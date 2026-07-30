using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SkillMyScreen;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly AiProviderService _provider = new();
    private readonly RawInputObserver _input = new();
    private IReadOnlyList<WindowInfo> _windows = [];
    private RecordingController? _recording;
    private RecordingController? _completedRecording;
    private SkillDraft? _draft;
    private string? _savedPath;
    private AiSettings _settings;
    private DateTimeOffset _recordingStarted;
    private readonly List<string> _recentEvents = [];

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        PopulateSettings();
        Loaded += (_, _) => RefreshWindows();
        Closed += (_, _) => { _input.Dispose(); _recording?.Dispose(); _completedRecording?.Dispose(); };
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
        _windows = WindowCatalog.GetVisibleWindows();
        WindowBox.ItemsSource = _windows;
        if (_windows.Count > 0) WindowBox.SelectedIndex = 0;
    }

    private void SetPanel(UIElement visible)
    {
        foreach (var panel in new[] { HomePanel, SetupPanel, RecordingPanel, ReviewPanel, SettingsPanel }) panel.Visibility = panel == visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => SetPanel(HomePanel);
    private void BuildSkill_Click(object sender, RoutedEventArgs e) { RefreshWindows(); SetPanel(SetupPanel); }
    private void Settings_Click(object sender, RoutedEventArgs e) { PopulateSettings(); SetPanel(SettingsPanel); }
    private void SavedSkills_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Skills);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Skills) { UseShellExecute = true });
    }

    private void CaptureMode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (WindowBox is not null) WindowBox.IsEnabled = CaptureModeBox.SelectedIndex == 1;
    }

    private void Provider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ApplyProviderDefaults();

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
        else if (provider == "Cerebras") { EndpointBox.Text = "https://api.cerebras.ai/v1"; }
        else if (provider == "Together AI") { EndpointBox.Text = "https://api.together.xyz/v1"; }
        else if (provider == "Fireworks AI") { EndpointBox.Text = "https://api.fireworks.ai/inference/v1"; }
        else if (provider == "Ollama Cloud") { EndpointBox.Text = "https://api.ollama.com/v1"; }
        else if (provider == "Ollama") { EndpointBox.Text = "http://127.0.0.1:11434/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "llama3.2"; }
        else if (provider == "LM Studio") { EndpointBox.Text = "http://127.0.0.1:1234/v1"; if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text == "gpt-4o-mini") ModelBox.Text = "local-model"; }
        else if (provider != "None (local draft)" && string.IsNullOrWhiteSpace(EndpointBox.Text)) EndpointBox.Text = "https://api.openai.com/v1";
    }

    private void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var mode = CaptureModeBox.SelectedIndex == 1 ? CaptureMode.Window : CaptureMode.Display;
            var window = mode == CaptureMode.Window && WindowBox.SelectedItem is WindowInfo item ? item.Handle : IntPtr.Zero;
            _recording = new RecordingController(TitleBox.Text, mode, window);
            _recording.EventRecorded += OnRecordingEvent;
            var source = (HwndSource)PresentationSource.FromVisual(this)!;
            _input.Attach(source, _recording);
            _recording.Start();
            _recordingStarted = DateTimeOffset.Now;
            _recentEvents.Clear();
            _uiTimer.Start();
            RecordingStatus.Text = _recording.Trace.HasAudio ? "Recording screen, microphone, and interaction context…" : "Recording screen and interaction context. No microphone was available.";
            SetPanel(RecordingPanel);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not start recording", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnRecordingEvent(TraceEvent item)
    {
        Dispatcher.Invoke(() =>
        {
            _recentEvents.Add($"{item.ElapsedMilliseconds / 1000.0:0.0}s — {item.Kind}: {item.Detail}");
            while (_recentEvents.Count > 8) _recentEvents.RemoveAt(0);
            EventPreview.Text = string.Join(Environment.NewLine, _recentEvents);
        });
    }

    private void UpdateRecordingUi()
    {
        if (_recording is null) return;
        var elapsed = DateTimeOffset.Now - _recordingStarted;
        RecordingTimer.Text = elapsed.ToString(@"mm\:ss");
    }

    private void MarkStep_Click(object sender, RoutedEventArgs e) => _recording?.Mark();
    private void Redact_Click(object sender, RoutedEventArgs e) => _recording?.RedactRecent();

    private async void FinishRecording_Click(object sender, RoutedEventArgs e)
    {
        if (_recording is null) return;
        try
        {
            _uiTimer.Stop(); _input.Dispose(); _recording.Stop();
            var completed = _recording;
            var trace = completed.Trace;
            _recording = null;
            _completedRecording = completed;
            ReviewStatus.Text = "Using narration, interaction context, and visual evidence to understand the user's intent…";
            SetPanel(ReviewPanel);
            var frames = completed.ReadFrameEvidence();
            if (completed.AudioWav.Length > 0)
            {
                try
                {
                    var transcript = await _provider.TranscribeAsync(completed.AudioWav, _settings);
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        trace.Transcript.Add(new TranscriptSegment(0, trace.Events.LastOrDefault()?.ElapsedMilliseconds ?? 0, transcript, _settings.UseAi ? 0.85 : 0.65));
                        trace.Notes.Add("Narration was transcribed and included in intent analysis.");
                    }
                    else trace.Notes.Add("Narration was captured, but no transcript was available.");
                }
                catch (Exception ex) { trace.Notes.Add("Transcription warning: " + ex.Message); }
            }
            _draft = DraftFactory.FromTrace(trace);
            try
            {
                var aiDraft = await _provider.GenerateDraftAsync(trace, _settings, frames, completed.AudioWav);
                if (aiDraft is not null) _draft = aiDraft;
                ReviewStatus.Text = $"Used {trace.Events.Count} timeline events, {frames.Count} visual frames, and {(trace.Transcript.Count > 0 ? "narration" : "no transcript")}. Review every step before saving.";
            }
            catch (Exception ex)
            {
                ReviewStatus.Text = "AI draft unavailable; a deterministic local draft is ready. " + ex.Message;
            }
            LoadDraftIntoReview(_draft);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not finish recording", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void LoadDraftIntoReview(SkillDraft draft)
    {
        ReviewNameBox.Text = draft.Name; ReviewDescriptionBox.Text = draft.Description; ReviewIntentBox.Text = draft.Intent; ReviewGoalBox.Text = draft.Goal;
        ReviewInputsBox.Text = string.Join(Environment.NewLine, draft.Inputs.Select(i => $"{i.Name} | {i.Description} | secret={(i.Secret ? "true" : "false")}"));
        ReviewProcedureBox.Text = string.Join(Environment.NewLine, draft.Procedure.Select(s => s.Instruction));
        ReviewSafetyBox.Text = string.Join(Environment.NewLine, draft.Safety);
        ReviewVerificationBox.Text = string.Join(Environment.NewLine, draft.Verification);
        ReviewRecoveryBox.Text = string.Join(Environment.NewLine, draft.Recovery);
        UpdateMarkdownPreview();
    }

    private SkillDraft ReadDraftFromReview()
    {
        var draft = _draft ?? new SkillDraft();
        draft.Name = SkillName.Slugify(ReviewNameBox.Text);
        draft.Title = string.IsNullOrWhiteSpace(ReviewNameBox.Text) ? "Computer workflow" : ReviewNameBox.Text.Trim();
        draft.Description = ReviewDescriptionBox.Text.Trim(); draft.Intent = ReviewIntentBox.Text.Trim(); draft.Goal = ReviewGoalBox.Text.Trim();
        draft.Inputs = ReviewInputsBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(line =>
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            return new SkillInput(parts.ElementAtOrDefault(0) ?? "input", parts.ElementAtOrDefault(1) ?? "Value requested at run time", "text", true, string.Equals(parts.ElementAtOrDefault(2), "secret=true", StringComparison.OrdinalIgnoreCase));
        }).ToList();
        draft.Procedure = ReviewProcedureBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select((line, i) => new SkillStep(i + 1, line.Trim(), null, "The expected visible result is confirmed.", 0.7)).ToList();
        draft.Safety = Lines(ReviewSafetyBox.Text); draft.Verification = Lines(ReviewVerificationBox.Text); draft.Recovery = Lines(ReviewRecoveryBox.Text);
        return draft;
    }

    private static List<string> Lines(string value) => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private void UpdateMarkdownPreview()
    {
        if (_draft is null) return;
        MarkdownPreviewBox.Text = SkillRenderer.Render(ReadDraftFromReview());
    }

    private void SaveSkill_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _draft = ReadDraftFromReview();
            _savedPath = SkillStorage.Save(_draft, AppPaths.Skills);
            _completedRecording?.DeleteTemporarySession();
            _completedRecording?.Dispose();
            _completedRecording = null;
            MarkdownPreviewBox.Text = SkillRenderer.Render(_draft);
            ReviewStatus.Text = $"Saved {_savedPath}. Temporary encrypted recording data can now be deleted.";
            Clipboard.SetText(PromptBuilder.Build(_draft, _savedPath));
            MessageBox.Show(this, $"Saved SKILL.md:\n{_savedPath}\n\nThe agent prompt has been copied to the clipboard.", "Skill created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save skill", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings = new AiSettings { Provider = ProviderBox.SelectedItem as string ?? "None (local draft)", Endpoint = EndpointBox.Text.Trim(), Model = ModelBox.Text.Trim(), UseAi = UseAiBox.IsChecked == true, EncryptedApiKey = _settings.EncryptedApiKey };
        SettingsStore.Save(_settings, ApiKeyBox.Password);
        ApiKeyBox.Clear(); SettingsStatus.Text = "Settings saved. The key is stored encrypted for this Windows user.";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings_Click(sender, e);
        SettingsStatus.Text = "Testing provider connection…";
        try { SettingsStatus.Text = await _provider.TestAsync(_settings) ? "Provider connection succeeded." : "Provider connection failed."; }
        catch (Exception ex) { SettingsStatus.Text = "Provider connection failed: " + ex.Message; }
    }
}
