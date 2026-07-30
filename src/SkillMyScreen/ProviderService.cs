using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SkillMyScreen;

public static class ProviderCatalog
{
    public static readonly string[] Providers =
    [
        "None (local draft)", "OpenAI", "Anthropic", "Google Gemini", "OpenRouter", "Nous Portal", "Groq", "Mistral", "xAI", "DeepSeek", "Cerebras", "Together AI", "Fireworks AI", "NovitaAI", "z.ai/GLM", "Kimi/Moonshot", "MiniMax", "Alibaba/Qwen", "Hugging Face", "NVIDIA NIM", "Azure AI Foundry", "OpenCode Zen", "OpenCode Go", "DeepInfra", "Ollama Cloud", "Ollama", "LM Studio", "Custom OpenAI-compatible"
    ];
}

public sealed class AiProviderService
{
    private readonly HttpClient _http;
    public AiProviderService(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

    public async Task<SkillDraft?> GenerateDraftAsync(RecordingTrace trace, AiSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.UseAi || settings.Provider.StartsWith("None", StringComparison.OrdinalIgnoreCase)) return null;
        var key = SettingsStore.GetApiKey(settings);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("The selected provider has no saved API key.");
        var prompt = CompilerPrompt.Build(trace);
        var raw = settings.Provider switch
        {
            "Anthropic" => await Anthropic(prompt, settings, key, cancellationToken),
            "Google Gemini" => await Gemini(prompt, settings, key, cancellationToken),
            _ => await OpenAiCompatible(prompt, settings, key, cancellationToken)
        };
        return ParseDraft(raw);
    }

    public async Task<bool> TestAsync(AiSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.UseAi || settings.Provider.StartsWith("None", StringComparison.OrdinalIgnoreCase)) return true;
        var key = SettingsStore.GetApiKey(settings);
        if (string.IsNullOrWhiteSpace(key)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.Provider == "Google Gemini" ? "https://generativelanguage.googleapis.com/v1beta/models" : Endpoint(settings).TrimEnd('/') + "/models");
        AddAuth(request, settings.Provider, key);
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<string?> TranscribeAsync(byte[] wav, AiSettings settings, CancellationToken cancellationToken = default)
    {
        if (wav.Length == 0 || !settings.UseAi || settings.Provider is "None (local draft)" or "Anthropic" or "Google Gemini") return null;
        var key = SettingsStore.GetApiKey(settings);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var endpoint = Endpoint(settings).TrimEnd('/') + "/audio/transcriptions";
        using var form = new MultipartFormDataContent();
        using var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "recording.wav");
        form.Add(new StringContent(TranscriptionModel(settings)), "model");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        AddAuth(request, settings.Provider, key);
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Transcription returned {(int)response.StatusCode}: {Redact(text)}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.TryGetProperty("text", out var value) ? value.GetString() : null;
    }

    private async Task<string> OpenAiCompatible(string prompt, AiSettings settings, string key, CancellationToken ct)
    {
        var endpoint = Endpoint(settings).TrimEnd('/') + "/chat/completions";
        var body = new { model = settings.Model, messages = new[] { new { role = "system", content = "Return only valid JSON matching the requested SkillDraft." }, new { role = "user", content = prompt } }, response_format = new { type = "json_object" }, temperature = 0.1 };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent(body) };
        AddAuth(request, settings.Provider, key);
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {Redact(text)}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
    }

    private async Task<string> Anthropic(string prompt, AiSettings settings, string key, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? "https://api.anthropic.com" : settings.Endpoint.TrimEnd('/');
        var body = new { model = settings.Model, max_tokens = 4096, system = "Return only valid JSON matching the requested SkillDraft.", messages = new[] { new { role = "user", content = prompt } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/messages") { Content = JsonContent(body) };
        request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {Redact(text)}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
    }

    private async Task<string> Gemini(string prompt, AiSettings settings, string key, CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-2.0-flash" : settings.Model;
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(key)}";
        var body = new { contents = new[] { new { parts = new[] { new { text = "Return only valid JSON matching the requested SkillDraft.\n" + prompt } } } } };
        using var response = await _http.PostAsync(endpoint, JsonContent(body), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {Redact(text)}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
    }

    private static SkillDraft ParseDraft(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("The provider did not return a JSON skill draft.");
        var draft = JsonSerializer.Deserialize<SkillDraft>(raw[start..(end + 1)], JsonDefaults.Options) ?? throw new InvalidDataException("The provider returned an empty skill draft.");
        draft.Name = SkillName.Slugify(draft.Name);
        return draft;
    }

    private static string Endpoint(AiSettings settings) => string.IsNullOrWhiteSpace(settings.Endpoint) ? "https://api.openai.com/v1" : settings.Endpoint;
    private static string TranscriptionModel(AiSettings settings) => settings.Provider switch
    {
        "Groq" => "whisper-large-v3-turbo",
        "Mistral" => "voxtral-mini-latest",
        _ => "whisper-1"
    };
    private static void AddAuth(HttpRequestMessage request, string provider, string key)
    {
        if (provider == "Anthropic") return;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }
    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");
    private static string Redact(string text) => text.Length > 300 ? text[..300] : text;
}

public static class CompilerPrompt
{
    public static string Build(RecordingTrace trace)
    {
        var events = string.Join('\n', trace.Events.Take(120).Select(e => $"{e.ElapsedMilliseconds}ms {e.Kind}: {e.Detail}; target={e.Target?.Name}; app={e.Target?.ProcessName}; window={e.Target?.WindowTitle}"));
        var transcript = trace.Transcript.Count == 0 ? "No transcript was available." : string.Join('\n', trace.Transcript.Select(t => $"{t.StartMilliseconds}-{t.EndMilliseconds}ms: {t.Text}"));
        return $"Create a concise reusable computer skill from this demonstration. Return JSON with fields Name, Title, Description, Goal, Inputs (Name, Description, Type, Required, Secret), Preconditions, Procedure (Order, Instruction, Target, ExpectedResult, Confidence), DecisionRules, Safety, Verification, Recovery, Uncertainties. Do not invent values or secrets. Use semantic targets and mention when a user must confirm.\n\nTitle: {trace.Title}\n\nEvents:\n{events}\n\nNarration:\n{transcript}";
    }
}

public static class DraftFactory
{
    public static SkillDraft FromTrace(RecordingTrace trace)
    {
        var draft = new SkillDraft
        {
            Name = SkillName.Slugify(trace.Title),
            Title = trace.Title,
            Description = $"Follow the demonstrated {trace.Title.ToLowerInvariant()} workflow using the available computer tools.",
            Goal = $"Complete the demonstrated {trace.Title.ToLowerInvariant()} workflow and verify its result.",
            Preconditions = ["The required application or website is available.", "Ask the user for any value that changes between runs."],
            Safety = ["Never expose passwords, API keys, OTPs, or private recorded values.", "Ask before sending, publishing, deleting, purchasing, or submitting.", "Stop instead of guessing between ambiguous targets."],
            Verification = ["Confirm the expected visible result before reporting success.", "Report any step that could not be verified."],
            Recovery = ["If a target or application is missing, describe the mismatch and ask the user.", "Do not claim completion without verification."]
        };
        var meaningful = trace.Events.Where(e => e.Kind is TraceEventKind.Click or TraceEventKind.DoubleClick or TraceEventKind.RightClick or TraceEventKind.Scroll or TraceEventKind.Shortcut or TraceEventKind.Marker).ToList();
        if (meaningful.Count == 0) meaningful = trace.Events.Where(e => e.Kind == TraceEventKind.KeyFrame).ToList();
        var index = 1;
        foreach (var item in meaningful.Take(40))
        {
            var target = item.Target?.Name ?? item.Target?.ControlType ?? item.Target?.WindowTitle;
            var instruction = item.Kind switch
            {
                TraceEventKind.Scroll => "Scroll in the demonstrated application until the next required content is visible.",
                TraceEventKind.Shortcut => $"Use the demonstrated keyboard shortcut ({item.Detail ?? "shortcut"}).",
                TraceEventKind.Marker => "Perform the meaningful step demonstrated at this point.",
                _ => target is null ? $"Repeat the demonstrated {item.Kind.ToString().ToLowerInvariant()} in the active application." : $"{item.Kind switch { TraceEventKind.DoubleClick => "Double-click", TraceEventKind.RightClick => "Right-click", _ => "Click" }} the control named \"{target}\"."
            };
            draft.Procedure.Add(new SkillStep(index++, instruction, target, "The application visibly changes as expected.", item.Target is null ? 0.55 : 0.85));
        }
        if (draft.Procedure.Count == 0) draft.Uncertainties.Add("No semantic interaction events were detected; add steps during review.");
        if (trace.HasAudio) draft.Uncertainties.Add("Narration was captured. Configure transcription or add spoken intent during review if inputs or conditions are missing.");
        return draft;
    }
}
