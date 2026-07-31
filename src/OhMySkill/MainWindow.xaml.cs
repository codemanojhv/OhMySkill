using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OhMySkill;

public partial class MainWindow : Window
{
    private sealed class ActionPreview
    {
        public string Header { get; init; } = "";
        public BitmapImage? BeforeImage { get; init; }
        public BitmapImage? AfterImage { get; init; }
        public string Narration { get; init; } = "No nearby narration.";
        public string Interpretation { get; init; } = "Not interpreted.";
    }

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly AiProviderService _provider = new();
    private readonly RawInputObserver _input = new();
    private IReadOnlyList<WindowInfo> _windows = [];
    private RecordingController? _recording;
    private RecordingController? _completedRecording;
    private SkillDraft? _draft;
    private string? _savedPath;
    private string? _promptText;
    private AiSettings _settings;
    private DateTimeOffset _recordingStarted;
    private readonly List<string> _recentEvents = [];
    private CancellationTokenSource? _workflowCts;
    private bool _isBusy;
    private bool _loadingDraft;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        PopulateSettings();
        Loaded += (_, _) => RefreshWindows();
        Closed += (_, _) =>
        {
            _workflowCts?.Cancel();
            _input.Dispose();
            _recording?.Dispose();
            _completedRecording?.Dispose();
        };
        _uiTimer.Tick += (_, _) => UpdateRecordingUi();
    }

    private void PopulateSettings()
    {
        ProviderBox.ItemsSource = ProviderCatalog.Providers;
        ProviderBox.SelectedItem = _settings.Provider;
        if (ProviderBox.SelectedIndex < 0) ProviderBox.SelectedIndex = 0;
        EndpointBox.Text = _settings.Endpoint;
        ModelBox.Text = _settings.Model;
        UseAiBox.IsChecked = _settings.UseAi;
        ApplyProviderDefaults();
    }

    private void RefreshWindows()
    {
        try
        {
            _windows = WindowCatalog.GetVisibleWindows();
            WindowBox.ItemsSource = _windows;
            if (_windows.Count > 0) WindowBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            _windows = [];
            WindowBox.ItemsSource = Array.Empty<WindowInfo>();
            SetupPreflight.Text = "Window discovery is unavailable. Entire-display capture is still available. " + ex.Message;
        }
    }

    private void SetPanel(UIElement visible)
    {
        foreach (var panel in new[] { HomePanel, SetupPanel, RecordingPanel, ReviewPanel, SettingsPanel })
            panel.Visibility = panel == visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool busy = false)
    {
        StatusText.Text = text;
        StatusDot.Fill = TryFindResource(busy ? "AccentBrush" : "SuccessBrush") as Brush ?? Brushes.Gray;
    }

    private void SetBusy(bool busy, string title = "", string detail = "", bool canCancel = false)
    {
        _isBusy = busy;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            CancelBusyButton.Visibility = Visibility.Collapsed;
            SetStatus("Ready");
            return;
        }

        BusyTitle.Text = title;
        BusyDetail.Text = detail;
        CancelBusyButton.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelBusyButton.IsEnabled = canCancel;
        SetStatus(title, true);
    }

    private void CancelBusy_Click(object sender, RoutedEventArgs e)
    {
        _workflowCts?.Cancel();
        CancelBusyButton.IsEnabled = false;
        BusyDetail.Text = "Cancel requested. Finishing the current safe operation...";
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetPanel(HomePanel);
        SetStatus("Ready");
    }

    private void BuildSkill_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        RefreshWindows();
        _savedPath = null;
        _promptText = null;
        SavedPathText.Text = "";
        OpenOutputButton.IsEnabled = false;
        CopyPromptButton.IsEnabled = false;
        var microphone = MicrophoneRecorder.HasInputDevice ? "available" : "not detected (narration will be unavailable)";
        var windowText = _windows.Count == 0 ? "No titled windows were found; use entire-display capture." : $"{_windows.Count} selectable windows found.";
        SetupPreflight.Text = $"Windows capture ready. Microphone: {microphone}. {windowText} AI is optional and can be configured under AI Settings.";
        SetPanel(SetupPanel);
        SetStatus("Setup");
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        PopulateSettings();
        SetPanel(SettingsPanel);
        SetStatus("AI settings");
    }

    private void SavedSkills_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Skills);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Skills) { UseShellExecute = true });
    }

    private void CaptureMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WindowBox is not null) WindowBox.IsEnabled = CaptureModeBox.SelectedIndex == 1;
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e) => ApplyProviderDefaults();

    private void ApplyProviderDefaults()
    {
        if (EndpointBox is null || ProviderBox.SelectedItem is not string provider) return;
        if (provider == "Anthropic") { EndpointBox.Text = "https://api.anthropic.com"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "claude-3-5-sonnet-latest"; }
        else if (provider == "Google Gemini") { EndpointBox.Text = "https://generativelanguage.googleapis.com"; if (string.IsNullOrWhiteSpace(ModelBox.Text)) ModelBox.Text = "gemini-2.0-flash"; }
        else if (provider == "OpenRouter") { EndpointBox.Text = "https://openrouter.ai/api/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "openai/gpt-4o-mini"; }
        else if (provider == "Groq") { EndpointBox.Text = "https://api.groq.com/openai/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "llama-3.3-70b-versatile"; }
        else if (provider == "Mistral") { EndpointBox.Text = "https://api.mistral.ai/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "mistral-small-latest"; }
        else if (provider == "xAI") { EndpointBox.Text = "https://api.x.ai/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "grok-3-mini"; }
        else if (provider == "DeepSeek") { EndpointBox.Text = "https://api.deepseek.com/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "deepseek-chat"; }
        else if (provider == "Cerebras") EndpointBox.Text = "https://api.cerebras.ai/v1";
        else if (provider == "Together AI") EndpointBox.Text = "https://api.together.xyz/v1";
        else if (provider == "Fireworks AI") EndpointBox.Text = "https://api.fireworks.ai/inference/v1";
        else if (provider == "Ollama Cloud") EndpointBox.Text = "https://api.ollama.com/v1";
        else if (provider == "Ollama") { EndpointBox.Text = "http://127.0.0.1:11434/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "llama3.2"; }
        else if (provider == "LM Studio") { EndpointBox.Text = "http://127.0.0.1:1234/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "local-model"; }
        else if (provider != "None (local draft)" && string.IsNullOrWhiteSpace(EndpointBox.Text)) EndpointBox.Text = "https://api.openai.com/v1";
    }

    private async void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var mode = CaptureModeBox.SelectedIndex == 1 ? CaptureMode.Window : CaptureMode.Display;
        if (mode == CaptureMode.Window && WindowBox.SelectedItem is not WindowInfo selected)
        {
            SetupPreflight.Text = "Choose a visible window or switch capture source to Entire display.";
            return;
        }

        var window = mode == CaptureMode.Window && WindowBox.SelectedItem is WindowInfo item ? item.Handle : IntPtr.Zero;
        var recording = new RecordingController(TitleBox.Text, mode, window);
        _recording = recording;
        recording.EventRecorded += OnRecordingEvent;
        _recentEvents.Clear();
        EventPreview.Text = "Preparing the first screen frame...";
        SetPanel(RecordingPanel);
        SetBusy(true, "Starting recording", "Initializing the screen sampler and microphone...", false);
        try
        {
            var source = PresentationSource.FromVisual(this) as HwndSource ?? throw new InvalidOperationException("The application window is not ready for input capture.");
            _input.Attach(source, recording);
            await Task.Run(recording.Start);
            _recordingStarted = DateTimeOffset.Now;
            AudioStatus.Text = recording.Trace.HasAudio ? "Microphone: capturing narration" : "Microphone: unavailable (audio will be missing)";
            RecordingStatus.Text = recording.Trace.HasAudio ? "Recording screen, microphone, and interaction context..." : "Recording screen and interaction context. No microphone was available.";
            SetBusy(false);
            SetStatus("Recording");
            _uiTimer.Start();
        }
        catch (Exception ex)
        {
            _input.Dispose();
            _recording = null;
            await Task.Run(recording.Dispose);
            SetBusy(false);
            SetPanel(SetupPanel);
            SetupPreflight.Text = "Could not start recording: " + ex.Message;
            SetStatus("Setup");
        }
    }

    private void OnRecordingEvent(TraceEvent item)
    {
        void Apply()
        {
            _recentEvents.Add($"{item.ElapsedMilliseconds / 1000.0:0.0}s - {item.Kind}: {item.Detail}");
            while (_recentEvents.Count > 8) _recentEvents.RemoveAt(0);
            EventPreview.Text = string.Join(Environment.NewLine, _recentEvents);
        }

        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.BeginInvoke(Apply);
    }

    private void UpdateRecordingUi()
    {
        if (_recording is null) return;
        var elapsed = DateTimeOffset.Now - _recordingStarted;
        RecordingTimer.Text = elapsed.ToString(@"mm\:ss");
        RecordingStats.Text = $"{_recording.ActionCount} actions - {_recording.BufferedFrameCount} buffered frames - {_recording.CapturedAudioMilliseconds / 1000.0:0}s audio";
        if (_recording.Trace.HasAudio)
            AudioStatus.Text = $"Microphone: capturing narration - level {_recording.MicrophoneLevel:P0}";
    }

    private void MarkStep_Click(object sender, RoutedEventArgs e)
    {
        _recording?.Mark();
        RecordingStatus.Text = "Step marked. Continue narrating the reason and expected result.";
    }

    private void Redact_Click(object sender, RoutedEventArgs e)
    {
        _recording?.RedactRecent();
        RecordingStatus.Text = "The most recent 15 seconds were redacted from audio, frames, and interactions.";
    }

    private async void FinishRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null || _isBusy) return;
        var completed = _recording;
        _recording = null;
        _completedRecording = completed;
        _uiTimer.Stop();
        _input.Dispose();
        FinishRecordingButton.IsEnabled = false;
        MarkStepButton.IsEnabled = false;
        RedactButton.IsEnabled = false;
        SetPanel(ReviewPanel);
        SetBusy(true, "Finishing recording", "Waiting for stable after-action frames and closing the microphone...", true);
        _workflowCts = new CancellationTokenSource();
        var cancellationToken = _workflowCts.Token;
        var trace = completed.Trace;
        IReadOnlyList<FrameEvidence> frames = [];
        IReadOnlyList<ActionEvidence> actions = [];

        try
        {
            await Task.Run(completed.StopAsync);
            SetBusy(true, "Reading evidence", "Loading before/after frames and timestamped audio windows...", true);
            var evidenceTask = Task.Run(() => completed.ReadFrameEvidence());
            var audioTask = Task.Run(() => completed.ReadAudioWindows());
            await Task.WhenAll(evidenceTask, audioTask);
            frames = await evidenceTask;
            var audioWindows = await audioTask;
            var transcriptionSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (completed.AudioWav.Length > 44 && audioWindows.Count > 0)
            {
                try
                {
                    for (var index = 0; index < audioWindows.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var window = audioWindows[index];
                        SetBusy(true, "Transcribing narration", $"Processing audio window {index + 1} of {audioWindows.Count}...", true);
                        var transcript = await _provider.TranscribeAsync(window.Wav, _settings, cancellationToken);
                        transcriptionSources.Add(_provider.LastTranscriptionSource);
                        if (string.IsNullOrWhiteSpace(transcript)) continue;
                        var duration = Math.Max(1000, (window.Wav.Length - 44) / 32L);
                        var segments = TranscriptSegmenter.Split(transcript, duration, _settings.UseAi ? 0.85 : 0.65)
                            .Select(segment => segment with
                            {
                                StartMilliseconds = segment.StartMilliseconds + window.StartMilliseconds,
                                EndMilliseconds = segment.EndMilliseconds + window.StartMilliseconds
                            });
                        trace.Transcript.AddRange(segments);
                    }
                    completed.AttachTranscript(trace.Transcript);
                    if (trace.Transcript.Count > 0)
                    {
                        trace.Evidence.AudioMode = string.Join(" + ", transcriptionSources.Where(source => source != "Unavailable"));
                        trace.Notes.Add($"Narration was transcribed with {trace.Evidence.AudioMode} in timestamped windows and attached to nearby actions.");
                    }
                    else trace.Notes.Add("Narration was captured, but no transcript was available.");
                }
                catch (OperationCanceledException)
                {
                    trace.Notes.Add("Narration processing was canceled; the captured audio was not sent again.");
                }
                catch (Exception ex)
                {
                    trace.Notes.Add("Transcription warning: " + ex.Message);
                }
            }

            _draft = DraftFactory.FromTrace(trace);
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var progress = new Progress<string>(message =>
                    {
                        ReviewStatus.Text = message;
                        BusyDetail.Text = message;
                    });
                    SetBusy(true, "Building draft", "Interpreting every action pair with the available narration...", true);
                    var aiDraft = await _provider.GenerateDraftAsync(trace, _settings, frames, audioWindows, progress, cancellationToken);
                    if (aiDraft is not null) _draft = aiDraft;
                }
                catch (OperationCanceledException)
                {
                    trace.Notes.Add("AI processing was canceled; the local evidence draft is ready for review.");
                }
                catch (Exception ex)
                {
                    trace.Notes.Add("AI draft unavailable; the deterministic local draft was retained. " + ex.Message);
                }
            }

            var evidenceSummary = BuildEvidenceSummary(trace, frames);
            // Release the provider payload before loading UI thumbnails; this keeps long sessions bounded.
            frames = Array.Empty<FrameEvidence>();
            audioWindows = Array.Empty<AudioWindowEvidence>();
            SetBusy(true, "Preparing review", "Rendering the evidence list and live SKILL.md preview...", false);
            actions = await Task.Run(completed.ReadActionEvidence);
            EvidenceList.ItemsSource = actions.Select(action => BuildActionPreview(action, trace.Interpretations.FirstOrDefault(item => item.ActionId == action.Id))).ToArray();
            actions = Array.Empty<ActionEvidence>();
            EvidenceSummary.Text = evidenceSummary;
            LoadDraftIntoReview(_draft);
            ReviewStatus.Text = BuildReviewStatus(trace, frames, cancellationToken.IsCancellationRequested);
            SetBusy(false);
            SetStatus("Review");
        }
        catch (OperationCanceledException)
        {
            _draft ??= DraftFactory.FromTrace(trace);
            actions = await Task.Run(completed.ReadActionEvidence);
            EvidenceList.ItemsSource = actions.Select(action => BuildActionPreview(action, trace.Interpretations.FirstOrDefault(item => item.ActionId == action.Id))).ToArray();
            EvidenceSummary.Text = BuildEvidenceSummary(trace, frames);
            LoadDraftIntoReview(_draft);
            ReviewStatus.Text = "Processing was canceled. The local evidence draft is ready; review it before saving.";
            SetBusy(false);
            SetStatus("Review");
        }
        catch (Exception ex)
        {
            _draft ??= DraftFactory.FromTrace(trace);
            ReviewStatus.Text = "The recording closed with a warning. A local draft is available: " + ex.Message;
            LoadDraftIntoReview(_draft);
            SetBusy(false);
            SetStatus("Review");
        }
        finally
        {
            _workflowCts.Dispose();
            _workflowCts = null;
            FinishRecordingButton.IsEnabled = true;
            MarkStepButton.IsEnabled = true;
            RedactButton.IsEnabled = true;
        }
    }

    private static string BuildReviewStatus(RecordingTrace trace, IReadOnlyList<FrameEvidence> frames, bool canceled)
    {
        var audio = trace.Transcript.Count > 0 ? trace.Evidence.AudioMode : trace.HasAudio ? "captured but unavailable to transcribe" : "unavailable";
        return $"Used {trace.Actions.Count} paired actions, {frames.Count} visual frames, and {audio}. {(canceled ? "AI processing was canceled; " : "")}Review every step before saving.";
    }

    private static string BuildEvidenceSummary(RecordingTrace trace, IReadOnlyList<FrameEvidence> frames)
    {
        var pairCount = trace.Actions.Count(a => a.Before is not null && a.After is not null && !a.Redacted);
        var words = trace.Transcript.Sum(t => t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var audio = trace.Transcript.Count > 0 ? trace.Evidence.AudioMode : trace.HasAudio ? "captured but unavailable to transcribe" : "unavailable";
        var warnings = trace.Evidence.Warnings.Count == 0 ? "No evidence downgrade reported." : "Warnings: " + string.Join(" | ", trace.Evidence.Warnings);
        return $"Evidence captured: {trace.Actions.Count} actions - {pairCount} before/after pairs - {frames.Count} frames - {words} narrated words - {trace.Interpretations.Count} action interpretations\nAudio: {audio} - Vision: {trace.Evidence.VisionMode} - Provider: {trace.Evidence.Provider}\n{warnings}";
    }

    private static ActionPreview BuildActionPreview(ActionEvidence action, ActionUnderstanding? understanding)
    {
        var narration = action.NearbyNarration.Count == 0 ? "No nearby narration." : string.Join(" ", action.NearbyNarration.Select(n => n.Text));
        return new ActionPreview
        {
            Header = $"{action.Order}. {action.Kind} - {action.StartMilliseconds}-{action.EndMilliseconds} ms - confidence {action.Confidence:0.00}",
            BeforeImage = ToBitmap(action.Before?.Png),
            AfterImage = ToBitmap(action.After?.Png),
            Narration = narration,
            Interpretation = understanding is null
                ? "Local evidence only; edit the draft if this action was misunderstood."
                : $"{(understanding.IncludeInSkill ? "Include" : "Exclude")} - {understanding.Instruction} Expected: {understanding.ExpectedResult} Confidence {understanding.Confidence:0.00}"
        };
    }

    private static BitmapImage? ToBitmap(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void LoadDraftIntoReview(SkillDraft draft)
    {
        _loadingDraft = true;
        try
        {
            ReviewNameBox.Text = draft.Name;
            ReviewDescriptionBox.Text = draft.Description;
            ReviewIntentBox.Text = draft.Intent;
            ReviewGoalBox.Text = draft.Goal;
            ReviewInputsBox.Text = string.Join(Environment.NewLine, draft.Inputs.Select(i => $"{i.Name} | {i.Description} | secret={(i.Secret ? "true" : "false")}"));
            ReviewProcedureBox.Text = string.Join(Environment.NewLine, draft.Procedure.Select(s => s.Instruction));
            ReviewSafetyBox.Text = string.Join(Environment.NewLine, draft.Safety);
            ReviewVerificationBox.Text = string.Join(Environment.NewLine, draft.Verification);
            ReviewRecoveryBox.Text = string.Join(Environment.NewLine, draft.Recovery);
        }
        finally
        {
            _loadingDraft = false;
        }
        UpdateMarkdownPreview();
    }

    private void ReviewInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingDraft || _draft is null || _isBusy) return;
        UpdateMarkdownPreview();
    }

    private SkillDraft ReadDraftFromReview()
    {
        var draft = _draft ?? new SkillDraft();
        draft.Name = SkillName.Slugify(ReviewNameBox.Text);
        draft.Title = string.IsNullOrWhiteSpace(ReviewNameBox.Text) ? "Computer workflow" : ReviewNameBox.Text.Trim();
        draft.Description = ReviewDescriptionBox.Text.Trim();
        draft.Intent = ReviewIntentBox.Text.Trim();
        draft.Goal = ReviewGoalBox.Text.Trim();
        draft.Inputs = ReviewInputsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            return new SkillInput(parts.ElementAtOrDefault(0) ?? "input", parts.ElementAtOrDefault(1) ?? "Value requested at run time", "text", true, string.Equals(parts.ElementAtOrDefault(2), "secret=true", StringComparison.OrdinalIgnoreCase));
        }).ToList();
        draft.Procedure = ReviewProcedureBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select((line, i) => new SkillStep(i + 1, line.Trim(), null, "The expected visible result is confirmed.", 0.7)).ToList();
        draft.Safety = Lines(ReviewSafetyBox.Text);
        draft.Verification = Lines(ReviewVerificationBox.Text);
        draft.Recovery = Lines(ReviewRecoveryBox.Text);
        return draft;
    }

    private static List<string> Lines(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void UpdateMarkdownPreview()
    {
        if (_draft is null) return;
        MarkdownPreviewBox.Text = SkillRenderer.Render(ReadDraftFromReview());
    }

    private async void SaveSkill_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _isBusy) return;
        _draft = ReadDraftFromReview();
        if (_draft.Procedure.Count == 0)
        {
            ReviewStatus.Text = "Add at least one procedure step before saving.";
            return;
        }

        var draft = _draft;
        SetBusy(true, "Saving skill", "Writing SKILL.md and the universal agent prompt...", false);
        try
        {
            var result = await Task.Run(() =>
            {
                var path = SkillStorage.Save(draft, AppPaths.Skills);
                var promptPath = Path.Combine(Path.GetDirectoryName(path)!, "USE_THIS_SKILL.txt");
                var prompt = PromptBuilder.Build(draft, path);
                File.WriteAllText(promptPath, prompt, new System.Text.UTF8Encoding(false));
                return (path, promptPath, prompt);
            });
            _savedPath = result.path;
            _promptText = result.prompt;
            var completed = _completedRecording;
            if (completed is not null)
            {
                await Task.Run(() => completed.DeleteTemporarySession());
                await Task.Run(completed.Dispose);
                _completedRecording = null;
            }

            try { Clipboard.SetText(result.prompt); }
            catch { ReviewStatus.Text = "Saved successfully. Clipboard was unavailable; use Copy agent prompt."; }
            SavedPathText.Text = $"Saved: {result.path}\nPrompt: {result.promptPath}";
            OpenOutputButton.IsEnabled = true;
            CopyPromptButton.IsEnabled = true;
            ReviewStatus.Text = "Skill saved. Temporary encrypted recording evidence was deleted.";
        }
        catch (Exception ex)
        {
            ReviewStatus.Text = "Could not save the skill: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            SetStatus("Review");
        }
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_savedPath)) return;
        var folder = Path.GetDirectoryName(_savedPath);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_promptText)) return;
        try
        {
            Clipboard.SetText(_promptText);
            ReviewStatus.Text = "Universal agent prompt copied to the clipboard.";
        }
        catch (Exception ex) { ReviewStatus.Text = "Could not copy the prompt: " + ex.Message; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = new AiSettings
            {
                Provider = ProviderBox.SelectedItem as string ?? "None (local draft)",
                Endpoint = EndpointBox.Text.Trim(),
                Model = ModelBox.Text.Trim(),
                UseAi = UseAiBox.IsChecked == true,
                EncryptedApiKey = _settings.EncryptedApiKey
            };
            SettingsStore.Save(_settings, ApiKeyBox.Password);
            ApiKeyBox.Clear();
            SettingsStatus.Text = "Settings saved. The key is stored encrypted for this Windows user.";
            SetStatus("AI settings saved");
        }
        catch (Exception ex) { SettingsStatus.Text = "Could not save settings: " + ex.Message; }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SaveSettings_Click(sender, e);
        _workflowCts = new CancellationTokenSource();
        try
        {
            SetBusy(true, "Testing provider", "Checking the selected provider without sending recording evidence...", true);
            var success = await _provider.TestAsync(_settings, _workflowCts.Token);
            SettingsStatus.Text = success ? "Provider connection succeeded." : "Provider connection failed. Check the endpoint, model, and key.";
        }
        catch (OperationCanceledException) { SettingsStatus.Text = "Provider test canceled."; }
        catch (Exception ex) { SettingsStatus.Text = "Provider connection failed: " + ex.Message; }
        finally
        {
            SetBusy(false);
            _workflowCts.Dispose();
            _workflowCts = null;
        }
    }
}
