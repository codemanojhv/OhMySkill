using System.Net.Http.Headers;
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
    private const int MaxFrameCount = 8;
    private const int MaxInlineAudioBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;

    public AiProviderService(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

    public async Task<SkillDraft?> GenerateDraftAsync(
        RecordingTrace trace,
        AiSettings settings,
        IReadOnlyList<FrameEvidence>? frames = null,
        byte[]? audioWav = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.UseAi || settings.Provider.StartsWith("None", StringComparison.OrdinalIgnoreCase)) return null;
        var key = SettingsStore.GetApiKey(settings);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("The selected provider has no saved API key.");

        var selectedFrames = frames?.Where(frame => frame.Png.Length > 0).Take(MaxFrameCount).ToArray() ?? [];
        var inlineAudio = audioWav is { Length: > 0 } && audioWav.Length <= MaxInlineAudioBytes ? audioWav : [];
        var prompt = CompilerPrompt.Build(trace, selectedFrames, inlineAudio.Length > 0);
        var raw = settings.Provider switch
        {
            "Anthropic" => await Anthropic(prompt, settings, key, selectedFrames, cancellationToken),
            "Google Gemini" => await Gemini(prompt, settings, key, selectedFrames, inlineAudio, cancellationToken),
            _ => await OpenAiCompatible(prompt, settings, key, selectedFrames, inlineAudio, cancellationToken)
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
        if (wav.Length == 0) return null;

        Exception? remoteError = null;
        if (settings.UseAi && !settings.Provider.StartsWith("None", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var remote = settings.Provider switch
                {
                    "Google Gemini" => await GeminiTranscribe(wav, settings, SettingsStore.GetApiKey(settings), cancellationToken),
                    "Anthropic" => null,
                    _ => await OpenAiCompatibleTranscribe(wav, settings, SettingsStore.GetApiKey(settings), cancellationToken)
                };
                if (!string.IsNullOrWhiteSpace(remote)) return remote.Trim();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                remoteError = ex;
            }
        }

        var local = await LocalSpeechTranscriber.TryTranscribeAsync(wav, cancellationToken);
        if (!string.IsNullOrWhiteSpace(local)) return local.Trim();
        if (remoteError is not null) throw new InvalidOperationException($"AI transcription failed and Windows local speech recognition was unavailable: {Redact(remoteError.Message)}", remoteError);
        return null;
    }

    private async Task<string> OpenAiCompatible(
        string prompt,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        byte[] audioWav,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OpenAiCompatibleRequest(prompt, settings, key, frames, audioWav, includeRichContext: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (frames.Count > 0 || audioWav.Length > 0))
        {
            return await OpenAiCompatibleRequest(prompt, settings, key, [], [], includeRichContext: false, cancellationToken);
        }
    }

    private async Task<string> OpenAiCompatibleRequest(
        string prompt,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        byte[] audioWav,
        bool includeRichContext,
        CancellationToken cancellationToken)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        if (includeRichContext)
        {
            foreach (var frame in frames)
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = "data:image/png;base64," + Convert.ToBase64String(frame.Png),
                        detail = "low"
                    }
                });
            }
            if (audioWav.Length > 0)
            {
                content.Add(new
                {
                    type = "input_audio",
                    input_audio = new { data = Convert.ToBase64String(audioWav), format = "wav" }
                });
            }
        }

        var body = new
        {
            model = settings.Model,
            messages = new object[]
            {
                new { role = "system", content = "Return only valid JSON matching the requested SkillDraft. Treat narration as the user's intent, UI events as evidence, and screenshots as visual evidence. Never invent secrets, values, or actions not supported by the evidence." },
                new { role = "user", content = includeRichContext ? (object)content.ToArray() : prompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.1
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings).TrimEnd('/') + "/chat/completions") { Content = JsonContent(body) };
        AddAuth(request, settings.Provider, key);
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {Redact(text)}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
    }

    private async Task<string> Anthropic(
        string prompt,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        CancellationToken cancellationToken)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var frame in frames)
        {
            content.Add(new
            {
                type = "image",
                source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(frame.Png) }
            });
        }

        var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? "https://api.anthropic.com" : settings.Endpoint.TrimEnd('/');
        var body = new
        {
            model = settings.Model,
            max_tokens = 4096,
            system = "Return only valid JSON matching the requested SkillDraft. Treat narration as the user's intent, UI events as evidence, and screenshots as visual evidence. Never invent secrets, values, or actions not supported by the evidence.",
            messages = new object[] { new { role = "user", content = content.ToArray() } }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/v1/messages") { Content = JsonContent(body) };
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {Redact(text)}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
    }

    private async Task<string> Gemini(
        string prompt,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        byte[] audioWav,
        CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-2.0-flash" : settings.Model;
        var parts = new List<object> { new { text = prompt } };
        foreach (var frame in frames)
            parts.Add(new { inlineData = new { mimeType = "image/png", data = Convert.ToBase64String(frame.Png) } });
        if (audioWav.Length > 0)
            parts.Add(new { inlineData = new { mimeType = "audio/wav", data = Convert.ToBase64String(audioWav) } });

        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(key)}";
        var body = new { contents = new[] { new { parts = parts.ToArray() } } };
        using var response = await _http.PostAsync(endpoint, JsonContent(body), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {Redact(text)}");
        return ExtractGeminiText(text);
    }

    private async Task<string?> GeminiTranscribe(byte[] wav, AiSettings settings, string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var model = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-2.0-flash" : settings.Model;
        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = "Transcribe this narration exactly. Return only the spoken text, preserving commands, values, conditions, and corrections. Do not summarize." },
                        new { inlineData = new { mimeType = "audio/wav", data = Convert.ToBase64String(wav) } }
                    }
                }
            }
        };
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(key)}";
        using var response = await _http.PostAsync(endpoint, JsonContent(body), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Gemini transcription returned {(int)response.StatusCode}: {Redact(text)}");
        return ExtractGeminiText(text);
    }

    private async Task<string?> OpenAiCompatibleTranscribe(byte[] wav, AiSettings settings, string? key, CancellationToken cancellationToken)
    {
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

    private static string ExtractGeminiText(string raw)
    {
        using var json = JsonDocument.Parse(raw);
        return json.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";
    }

    private static SkillDraft ParseDraft(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("The provider did not return a JSON skill draft.");
        var draft = JsonSerializer.Deserialize<SkillDraft>(raw[start..(end + 1)], JsonDefaults.Options) ?? throw new InvalidDataException("The provider returned an empty skill draft.");
        draft.Name = SkillName.Slugify(draft.Name);
        if (string.IsNullOrWhiteSpace(draft.Intent)) draft.Intent = draft.Goal;
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
    public static string Build(RecordingTrace trace, IReadOnlyList<FrameEvidence>? frames = null, bool audioAttached = false)
    {
        var events = string.Join('\n', trace.Events
            .OrderBy(e => e.ElapsedMilliseconds)
            .Take(300)
            .Select(e => $"{e.ElapsedMilliseconds}ms {e.Kind}: {e.Detail}; {TargetContext(e.Target)}; app={e.ProcessName ?? e.Target?.ProcessName}; window={e.WindowTitle ?? e.Target?.WindowTitle}; redacted={e.Redacted}"));
        var transcript = trace.Transcript.Count == 0
            ? "No transcript was available. Do not infer spoken intent from silence."
            : string.Join('\n', trace.Transcript.Select(t => $"{t.StartMilliseconds}-{t.EndMilliseconds}ms (confidence {t.Confidence:0.00}): {t.Text}"));
        var notes = trace.Notes.Count == 0 ? "No recorder notes." : string.Join('\n', trace.Notes);
        var frameContext = frames is { Count: > 0 }
            ? string.Join('\n', frames.Select((frame, index) => $"Attached frame {index + 1}: {frame.ElapsedMilliseconds}ms, reason={frame.Reason}. Use it to verify visible labels, state changes, and target identity; do not read secrets."))
            : "No frame attachments were available.";
        var audioContext = audioAttached ? "The recorded WAV narration is attached. Use it to resolve intent, values, corrections, conditions, and spoken success criteria; prefer explicit narration over guesses." : "No audio bytes are attached; use the transcript only if present.";
        var prompt = new StringBuilder();
        prompt.AppendLine("Create a reusable computer skill from the complete demonstration context below.");
        prompt.AppendLine("First infer the user's intent and desired outcome, then separate intent from the mechanical UI actions that implemented it.");
        prompt.AppendLine("Return only valid JSON matching SkillDraft with fields Name, Title, Description, Intent, Goal, Inputs (Name, Description, Type, Required, Secret), Preconditions, Procedure (Order, Instruction, Target, ExpectedResult, Confidence), DecisionRules, Safety, Verification, Recovery, Uncertainties.");
        prompt.AppendLine();
        prompt.AppendLine("Evidence rules:");
        prompt.AppendLine("- Narration is the strongest evidence of user intent, variable values, corrections, exceptions, and success criteria.");
        prompt.AppendLine("- UI Automation targets, process/window metadata, event timing, and screenshots ground the procedure in observable computer state.");
        prompt.AppendLine("- Use screenshots to resolve labels and state transitions, but never transcribe or reproduce passwords, API keys, OTPs, private messages, or other secrets.");
        prompt.AppendLine("- Preserve uncertainty explicitly. Do not invent missing values, hidden steps, application capabilities, or success.");
        prompt.AppendLine("- Convert demonstrations into semantic instructions that another agent can execute with its available tools; do not depend on screen coordinates when a semantic target is available.");
        prompt.AppendLine("- Mark inputs that vary between runs and mark secrets as Secret=true. Ask before external, destructive, publishing, purchasing, sending, or submitting actions.");
        prompt.AppendLine("- Include verification and recovery steps that are observable and specific.");
        prompt.AppendLine();
        prompt.AppendLine("Session:");
        prompt.AppendLine($"Title: {trace.Title}");
        prompt.AppendLine($"Capture mode: {trace.CaptureMode}");
        prompt.AppendLine($"Capture target: {trace.CaptureTarget}");
        prompt.AppendLine($"Has audio: {trace.HasAudio}");
        prompt.AppendLine(audioContext);
        prompt.AppendLine();
        prompt.AppendLine("Narration:");
        prompt.AppendLine(transcript);
        prompt.AppendLine();
        prompt.AppendLine("Interaction timeline:");
        prompt.AppendLine(events);
        prompt.AppendLine();
        prompt.AppendLine("Recorder notes:");
        prompt.AppendLine(notes);
        prompt.AppendLine();
        prompt.AppendLine("Visual evidence:");
        prompt.AppendLine(frameContext);
        return prompt.ToString();
    }

    private static string TargetContext(UiTarget? target)
    {
        if (target is null) return "target=none";
        var name = target.IsPassword ? "[password field]" : target.Name;
        var ancestors = target.Ancestors.Count == 0 ? "none" : string.Join(" > ", target.Ancestors.Take(4));
        return $"target={name}; automationId={target.AutomationId}; type={target.ControlType}; class={target.ClassName}; help={target.Description}; ancestors={ancestors}; bounds={target.X:0},{target.Y:0},{target.Width:0}x{target.Height:0}; password={target.IsPassword}; enabled={target.IsEnabled}";
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
            Intent = $"Complete the user's demonstrated {trace.Title.ToLowerInvariant()} workflow while preserving the user's stated intent and safety boundaries.",
            Goal = $"Complete the demonstrated {trace.Title.ToLowerInvariant()} workflow and verify its result.",
            Preconditions = ["The required application or website is available.", "Ask the user for any value that changes between runs."],
            Safety = ["Never expose passwords, API keys, OTPs, or private recorded values.", "Ask before sending, publishing, deleting, purchasing, or submitting.", "Stop instead of guessing between ambiguous targets."],
            Verification = ["Confirm the expected visible result before reporting success.", "Report any step that could not be verified."],
            Recovery = ["If a target or application is missing, describe the mismatch and ask the user.", "Do not claim completion without verification."]
        };
        var narration = string.Join(" ", trace.Transcript.Select(segment => segment.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        if (!string.IsNullOrWhiteSpace(narration))
        {
            var intent = narration.Length > 600 ? narration[..600].TrimEnd() + "…" : narration;
            draft.Intent = $"User narration indicates: {intent}";
            draft.Goal = $"Complete the demonstrated workflow to achieve the outcome described in the user's narration: {intent}";
            draft.Uncertainties.Add("The deterministic draft preserves the transcript as intent evidence; review it for transcription errors before saving.");
        }
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
        if (trace.HasAudio) draft.Uncertainties.Add("Narration was captured. Confirm the transcript and intent before saving.");
        return draft;
    }
}
