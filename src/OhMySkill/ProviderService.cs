using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OhMySkill;

public static class ProviderCatalog
{
    public static readonly string[] Providers =
    [
        "None (local draft)", "OpenAI", "Anthropic", "Google Gemini", "OpenRouter", "Nous Portal", "Groq", "Mistral", "xAI", "DeepSeek", "Cerebras", "Together AI", "Fireworks AI", "NovitaAI", "z.ai/GLM", "Kimi/Moonshot", "MiniMax", "Alibaba/Qwen", "Hugging Face", "NVIDIA NIM", "Azure AI Foundry", "OpenCode Zen", "OpenCode Go", "DeepInfra", "Ollama Cloud", "Ollama", "LM Studio", "Custom OpenAI-compatible"
    ];
}

public sealed class AiProviderService
{
    private const int ActionsPerRequest = 6;
    private const int MaxFrameCount = 120;
    private const int MaxInlineAudioBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private sealed class ActionBatchEnvelope { public List<ActionUnderstanding> Actions { get; set; } = []; }
    public string LastTranscriptionSource { get; private set; } = "Unavailable";

    public AiProviderService(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

    public async Task<SkillDraft?> GenerateDraftAsync(
        RecordingTrace trace,
        AiSettings settings,
        IReadOnlyList<FrameEvidence>? frames = null,
        IReadOnlyList<AudioWindowEvidence>? audioWindows = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!settings.UseAi || settings.Provider.StartsWith("None", StringComparison.OrdinalIgnoreCase)) return null;
        var key = SettingsStore.GetApiKey(settings);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("The selected provider has no saved API key.");

        var selectedFrames = frames?.Where(frame => frame.Png.Length > 0).Take(MaxFrameCount).ToArray() ?? [];
        trace.Evidence.Provider = settings.Provider;
        trace.Evidence.Model = settings.Model;
        trace.Evidence.VisionMode = selectedFrames.Length > 0 ? "Action-pair vision requested" : "Unavailable";
        if (trace.Transcript.Count > 0 && trace.Evidence.AudioMode == "Unavailable") trace.Evidence.AudioMode = "Timestamped transcript";
        if (frames is { Count: > MaxFrameCount }) trace.Evidence.Warnings.Add($"Only the first {MaxFrameCount} frames were attached because the provider request limit was reached.");
        trace.Interpretations.Clear();
        trace.Interpretations.AddRange(await InterpretActionsAsync(trace, settings, key, selectedFrames, audioWindows ?? [], progress, cancellationToken));

        progress?.Report("Synthesizing the reusable skill from the full trajectory and narration…");
        var trajectoryFrames = selectedFrames.Where(frame => frame.ActionId is null).Take(8).ToArray();
        var raw = await CompleteAsync(CompilerPrompt.Build(trace, trajectoryFrames), settings, key, trajectoryFrames, [], trace, cancellationToken);
        var draft = ParseDraft(raw);

        progress?.Report("Checking the draft against the evidence for omissions and invented steps…");
        try
        {
            var refined = await CompleteAsync(CriticPrompt.Build(trace, draft), settings, key, [], [], trace, cancellationToken);
            return ParseDraft(refined);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace.Evidence.Warnings.Add("The evidence critic was unavailable; the validated synthesis draft was retained. " + Redact(ex.Message));
            return draft;
        }
    }

    private async Task<IReadOnlyList<ActionUnderstanding>> InterpretActionsAsync(
        RecordingTrace trace,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        IReadOnlyList<AudioWindowEvidence> audioWindows,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var actions = trace.Actions.Where(action => !action.Redacted).OrderBy(action => action.Order).ToArray();
        var result = new List<ActionUnderstanding>(actions.Length);
        for (var offset = 0; offset < actions.Length; offset += ActionsPerRequest)
        {
            var batch = actions.Skip(offset).Take(ActionsPerRequest).ToArray();
            progress?.Report($"Understanding actions {offset + 1}–{offset + batch.Length} of {actions.Length} from screen, narration, and interaction evidence…");
            var ids = batch.Select(action => action.Id).ToHashSet();
            var batchFrames = frames.Where(frame => frame.ActionId is Guid id && ids.Contains(id) && frame.Role is FrameRole.Before or FrameRole.After).ToArray();
            var batchStart = batch.Min(action => action.StartMilliseconds);
            var batchEnd = batch.Max(action => action.EndMilliseconds);
            var audio = SupportsNativeAudio(settings.Provider)
                ? audioWindows.FirstOrDefault(window => window.EndMilliseconds >= batchStart && window.StartMilliseconds <= batchEnd)?.Wav ?? []
                : [];
            if (audio.Length > MaxInlineAudioBytes)
            {
                audio = [];
                trace.Evidence.Warnings.Add($"Native audio for actions {offset + 1}–{offset + batch.Length} exceeded the provider limit; nearby transcript was used.");
            }
            try
            {
                var raw = await CompleteAsync(ActionPrompt.Build(batch, batchFrames, audio.Length > 0), settings, key, batchFrames, audio, trace, cancellationToken);
                var parsed = ParseActions(raw).Where(item => ids.Contains(item.ActionId)).ToDictionary(item => item.ActionId);
                result.AddRange(batch.Select(action => parsed.TryGetValue(action.Id, out var item) ? Normalize(item, action) : ActionUnderstandingFactory.FromAction(action)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                trace.Evidence.Warnings.Add($"AI action interpretation fell back to local evidence for actions {offset + 1}–{offset + batch.Length}: {Redact(ex.Message)}");
                result.AddRange(batch.Select(ActionUnderstandingFactory.FromAction));
            }
        }
        return result;
    }

    private async Task<string> CompleteAsync(
        string prompt,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        byte[] audioWav,
        RecordingTrace trace,
        CancellationToken cancellationToken)
    {
        var raw = settings.Provider switch
        {
            "Anthropic" => await Anthropic(prompt, settings, key, frames, cancellationToken),
            "Google Gemini" => await Gemini(prompt, settings, key, frames, audioWav, cancellationToken),
            _ => await OpenAiCompatible(prompt, settings, key, frames, audioWav, trace, cancellationToken)
        };
        if (frames.Count > 0) trace.Evidence.VisionMode = "Action-pair vision used";
        if (audioWav.Length > 0) trace.Evidence.AudioMode = "Native audio and timestamped transcript used";
        return raw;
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
        LastTranscriptionSource = "Unavailable";
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
                if (!string.IsNullOrWhiteSpace(remote))
                {
                    LastTranscriptionSource = settings.Provider + " transcription";
                    return remote.Trim();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                remoteError = ex;
            }
        }

        var local = await LocalSpeechTranscriber.TryTranscribeAsync(wav, cancellationToken);
        if (!string.IsNullOrWhiteSpace(local))
        {
            LastTranscriptionSource = "Windows Speech";
            return local.Trim();
        }
        if (remoteError is not null) throw new InvalidOperationException($"AI transcription failed and Windows local speech recognition was unavailable: {Redact(remoteError.Message)}", remoteError);
        return null;
    }

    private async Task<string> OpenAiCompatible(
        string prompt,
        AiSettings settings,
        string key,
        IReadOnlyList<FrameEvidence> frames,
        byte[] audioWav,
        RecordingTrace trace,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OpenAiCompatibleRequest(prompt, settings, key, frames, audioWav, includeRichContext: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (frames.Count > 0 || audioWav.Length > 0))
        {
            trace.Evidence.Warnings.Add("The provider rejected rich media for one request; synchronized transcript and interaction metadata were used for that request. " + Redact(ex.Message));
            if (frames.Count > 0) trace.Evidence.VisionMode = "Partially used; metadata fallback occurred";
            if (audioWav.Length > 0) trace.Evidence.AudioMode = trace.Transcript.Count > 0 ? "Timestamped transcript fallback" : "Unavailable";
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
                new { role = "system", content = "Return only valid JSON matching the schema requested by the user message. Treat narration as intent, UI events as evidence, and screenshots as visual evidence. Never invent secrets, values, or actions not supported by the evidence." },
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
            system = "Return only valid JSON matching the schema requested by the user message. Treat narration as intent, UI events as evidence, and screenshots as visual evidence. Never invent secrets, values, or actions not supported by the evidence.",
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
        var draft = JsonSerializer.Deserialize<SkillDraft>(JsonObject(raw), JsonDefaults.Options) ?? throw new InvalidDataException("The provider returned an empty skill draft.");
        draft.Name = SkillName.Slugify(draft.Name);
        if (string.IsNullOrWhiteSpace(draft.Intent)) draft.Intent = draft.Goal;
        SkillDraftValidator.Validate(draft);
        return draft;
    }

    private static IReadOnlyList<ActionUnderstanding> ParseActions(string raw) =>
        JsonSerializer.Deserialize<ActionBatchEnvelope>(JsonObject(raw), JsonDefaults.Options)?.Actions
        ?? throw new InvalidDataException("The provider returned no action interpretations.");

    private static string JsonObject(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("The provider did not return a JSON object.");
        return raw[start..(end + 1)];
    }

    private static ActionUnderstanding Normalize(ActionUnderstanding item, ActionEvidence action) => item with
    {
        Order = action.Order,
        UserIntent = item.UserIntent?.Trim() ?? "",
        Instruction = string.IsNullOrWhiteSpace(item.Instruction) ? ActionUnderstandingFactory.FromAction(action).Instruction : item.Instruction.Trim(),
        VisibleBefore = item.VisibleBefore?.Trim() ?? "",
        ObservedChange = item.ObservedChange?.Trim() ?? "",
        ExpectedResult = string.IsNullOrWhiteSpace(item.ExpectedResult) ? "Confirm the expected visible state." : item.ExpectedResult.Trim(),
        Confidence = Math.Clamp(item.Confidence, 0, 1)
    };

    private static bool SupportsNativeAudio(string provider) => provider is "OpenAI" or "Google Gemini";

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

public static class ActionUnderstandingFactory
{
    public static ActionUnderstanding FromAction(ActionEvidence action)
    {
        var target = action.Target?.IsPassword == true
            ? "the protected field"
            : action.Target?.Name ?? action.Target?.ControlType ?? "the active application";
        var narration = string.Join(" ", action.NearbyNarration.Select(segment => segment.Text)).Trim();
        var instruction = action.Kind switch
        {
            TraceEventKind.Click => $"Select {target}.",
            TraceEventKind.DoubleClick => $"Open {target} by double-clicking it.",
            TraceEventKind.RightClick => $"Open the context menu for {target}.",
            TraceEventKind.Drag => $"Drag {target} to the demonstrated destination.",
            TraceEventKind.Scroll => $"Scroll in {target} until the demonstrated content is visible.",
            TraceEventKind.Shortcut => $"Use the demonstrated keyboard action ({action.Detail}).",
            TraceEventKind.TextEntry => $"Enter the required runtime value in {target} without exposing or storing secret text.",
            TraceEventKind.Marker => "Perform the meaningful step marked by the user.",
            _ => $"Repeat the demonstrated {action.Kind.ToString().ToLowerInvariant()} in {target}."
        };
        var possibleMistake = narration.Contains("wrong", StringComparison.OrdinalIgnoreCase) ||
                              narration.Contains("mistake", StringComparison.OrdinalIgnoreCase) ||
                              narration.Contains("actually", StringComparison.OrdinalIgnoreCase) ||
                              narration.Contains("instead", StringComparison.OrdinalIgnoreCase)
            ? "Nearby narration may describe a correction; review whether this action belongs in the final procedure."
            : null;
        return new ActionUnderstanding(
            action.Id,
            action.Order,
            action.IncludeInSkill,
            string.IsNullOrWhiteSpace(narration) ? action.Detail ?? "Intent was not narrated." : narration,
            instruction,
            action.Before is null ? "No before frame was available." : "The before frame shows the state immediately before the interaction.",
            action.After is null ? "No after frame was available." : "The after frame shows the settled state after the interaction.",
            "Confirm the expected visible state before continuing.",
            possibleMistake,
            action.Confidence,
            possibleMistake is null ? null : "The local fallback cannot determine whether the correction refers to this action or the next one.");
    }
}

public static class ActionPrompt
{
    public static string Build(IReadOnlyList<ActionEvidence> actions, IReadOnlyList<FrameEvidence> frames, bool audioAttached)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("Interpret each demonstrated computer action. Return only this JSON shape:");
        prompt.AppendLine("{\"actions\":[{\"actionId\":\"GUID\",\"order\":1,\"includeInSkill\":true,\"userIntent\":\"why\",\"instruction\":\"semantic reusable instruction\",\"visibleBefore\":\"observable state\",\"observedChange\":\"visible transition\",\"expectedResult\":\"specific observable result\",\"possibleMistake\":null,\"confidence\":0.0,\"uncertainty\":null}]}");
        prompt.AppendLine("Compare the before and after frame belonging to each action. Use nearby narration to explain intent, conditions, variable values, corrections, mistakes, and success criteria.");
        prompt.AppendLine("Exclude a demonstrated mistake only when the evidence supports it. Never expose or reconstruct typed text from a protected field. Never invent a label or successful change that is not visible.");
        prompt.AppendLine(audioAttached
            ? "A synchronized narration window is attached; use it together with the timestamped nearby transcript."
            : "No native audio is attached; use the timestamped nearby transcript.");
        prompt.AppendLine();
        foreach (var action in actions)
        {
            var target = action.Target?.IsPassword == true ? "[protected field]" : action.Target?.Name ?? action.Target?.ControlType ?? "unknown";
            prompt.AppendLine($"Action {action.Order}: actionId={action.Id}; time={action.StartMilliseconds}-{action.EndMilliseconds}ms; kind={action.Kind}; detail={action.Detail}; app={action.Target?.ProcessName}; window={action.Target?.WindowTitle}; target={target}; before={action.Before?.Id ?? "missing"}; after={action.After?.Id ?? "missing"}; narration={string.Join(" | ", action.NearbyNarration.Select(segment => segment.Text))}");
        }
        prompt.AppendLine();
        prompt.AppendLine("Attached frames, in order:");
        foreach (var frame in frames)
            prompt.AppendLine($"id={frame.Id}; actionId={frame.ActionId}; role={frame.Role}; time={frame.ElapsedMilliseconds}ms");
        return prompt.ToString();
    }
}

public static class CriticPrompt
{
    public static string Build(RecordingTrace trace, SkillDraft draft)
    {
        var evidence = string.Join('\n', trace.Interpretations.OrderBy(item => item.Order).Select(item =>
            $"{item.Order}. include={item.IncludeInSkill}; intent={item.UserIntent}; instruction={item.Instruction}; change={item.ObservedChange}; expected={item.ExpectedResult}; mistake={item.PossibleMistake}; uncertainty={item.Uncertainty}"));
        var transcript = string.Join('\n', trace.Transcript.Select(item => $"{item.StartMilliseconds}-{item.EndMilliseconds}ms: {item.Text}"));
        return $"""
Review the SkillDraft against the evidence, repair it, and return only the complete corrected SkillDraft JSON.
Keep only supported steps, preserve corrections and uncertainty, turn run-specific values into inputs, and make verification observable.
The description must clearly state both what the skill does and when an agent should use it.
Never add secrets, hidden actions, coordinates, unobserved labels, or unsupported success claims.

Current SkillDraft:
{JsonSerializer.Serialize(draft, JsonDefaults.Options)}

Ordered action interpretations:
{evidence}

Full narration:
{transcript}
""";
    }
}

public static class SkillDraftValidator
{
    public static void Validate(SkillDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Description))
            throw new InvalidDataException("The skill needs a name and a description.");
        if (string.IsNullOrWhiteSpace(draft.Intent) || string.IsNullOrWhiteSpace(draft.Goal))
            throw new InvalidDataException("The skill needs intent and goal fields.");
        if (draft.Procedure.Count == 0) throw new InvalidDataException("The skill procedure is empty.");
        if (draft.Safety.Count == 0 || draft.Verification.Count == 0 || draft.Recovery.Count == 0)
            throw new InvalidDataException("The skill needs safety, verification, and recovery guidance.");
    }
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
            ? string.Join('\n', frames.Select((frame, index) => $"Attached frame {index + 1}: id={frame.Id}, {frame.ElapsedMilliseconds}ms, role={frame.Role}, action={frame.ActionId}, reason={frame.Reason}. Use it to verify visible labels, state changes, and target identity; do not read secrets."))
            : "No frame attachments were available.";
        var actions = trace.Actions.Count == 0
            ? "No paired action evidence was available."
            : string.Join('\n', trace.Actions.Where(a => !a.Redacted).OrderBy(a => a.Order).Select(a =>
                 $"Action {a.Order} id={a.Id} {a.StartMilliseconds}-{a.EndMilliseconds}ms kind={a.Kind} detail={a.Detail}; before={a.Before?.Id ?? "none"}; after={a.After?.Id ?? "none"}; narration={string.Join(" | ", a.NearbyNarration.Select(n => n.Text))}; target={TargetContext(a.Target)}; include={a.IncludeInSkill}; confidence={a.Confidence:0.00}"));
        var interpretations = trace.Interpretations.Count == 0
            ? "No action interpretations were available."
            : string.Join('\n', trace.Interpretations.OrderBy(item => item.Order).Select(item =>
                $"Action {item.Order} id={item.ActionId}; include={item.IncludeInSkill}; userIntent={item.UserIntent}; instruction={item.Instruction}; visibleBefore={item.VisibleBefore}; observedChange={item.ObservedChange}; expectedResult={item.ExpectedResult}; possibleMistake={item.PossibleMistake}; confidence={item.Confidence:0.00}; uncertainty={item.Uncertainty}"));
        var audioContext = audioAttached ? "The recorded WAV narration is attached. Use it to resolve intent, values, corrections, conditions, and spoken success criteria; prefer explicit narration over guesses." : "No audio bytes are attached; use the transcript only if present.";
        var prompt = new StringBuilder();
        prompt.AppendLine("Create a reusable computer skill from the complete demonstration context below.");
        prompt.AppendLine("First infer the user's intent and desired outcome, then separate intent from the mechanical UI actions that implemented it.");
        prompt.AppendLine("Return only valid JSON matching SkillDraft with fields Name, Title, Description, Intent, Goal, Inputs (Name, Description, Type, Required, Secret), Preconditions, Procedure (Order, Instruction, Target, ExpectedResult, Confidence), DecisionRules, Safety, Verification, Recovery, Uncertainties.");
        prompt.AppendLine("Description must say what the skill does and when an agent should use it, using likely user trigger language.");
        prompt.AppendLine();
        prompt.AppendLine("Evidence rules:");
        prompt.AppendLine("- Narration is the strongest evidence of user intent, variable values, corrections, exceptions, and success criteria.");
        prompt.AppendLine("- UI Automation targets, process/window metadata, event timing, and screenshots ground the procedure in observable computer state.");
        prompt.AppendLine("- Use screenshots to resolve labels and state transitions, but never transcribe or reproduce passwords, API keys, OTPs, private messages, or other secrets.");
        prompt.AppendLine("- Preserve uncertainty explicitly. Do not invent missing values, hidden steps, application capabilities, or success.");
        prompt.AppendLine("- Convert demonstrations into semantic instructions that another agent can execute with its available tools; do not depend on screen coordinates when a semantic target is available.");
        prompt.AppendLine("- Mark inputs that vary between runs and mark secrets as Secret=true. Ask before external, destructive, publishing, purchasing, sending, or submitting actions.");
        prompt.AppendLine("- Include verification and recovery steps that are observable and specific.");
        prompt.AppendLine("- Every paired action is evidence: explain the visible transition between its before and after frames when meaningful.");
        prompt.AppendLine("- Treat nearby narration as the reason for an action, and spoken corrections as authority to exclude mistakes.");
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
        prompt.AppendLine("Paired action evidence:");
        prompt.AppendLine(actions);
        prompt.AppendLine();
        prompt.AppendLine("Action-level multimodal interpretations:");
        prompt.AppendLine(interpretations);
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
        var meaningful = trace.Events.Where(e => e.Kind is TraceEventKind.Click or TraceEventKind.DoubleClick or TraceEventKind.RightClick or TraceEventKind.Drag or TraceEventKind.Scroll or TraceEventKind.Shortcut or TraceEventKind.TextEntry or TraceEventKind.Marker).ToList();
        if (meaningful.Count == 0) meaningful = trace.Events.Where(e => e.Kind == TraceEventKind.KeyFrame).ToList();
        var index = 1;
        foreach (var item in meaningful.Take(40))
        {
            var target = item.Target?.Name ?? item.Target?.ControlType ?? item.Target?.WindowTitle;
            var instruction = item.Kind switch
            {
                TraceEventKind.Scroll => "Scroll in the demonstrated application until the next required content is visible.",
                TraceEventKind.Shortcut => $"Use the demonstrated keyboard shortcut ({item.Detail ?? "shortcut"}).",
                TraceEventKind.TextEntry => item.Target?.IsPassword == true ? "Enter the required secret in the protected field without displaying or storing it." : "Enter the required runtime value in the demonstrated field.",
                TraceEventKind.Drag => "Drag the demonstrated item to the target location.",
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
