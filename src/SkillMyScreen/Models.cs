using System.Text.Json.Serialization;

namespace SkillMyScreen;

public enum CaptureMode
{
    Display,
    Window
}

public enum TraceEventKind
{
    SessionStarted,
    SessionPaused,
    SessionResumed,
    SessionStopped,
    Click,
    DoubleClick,
    RightClick,
    Drag,
    Scroll,
    Shortcut,
    TextEntry,
    FocusChanged,
    WindowChanged,
    KeyFrame,
    Marker,
    Redaction
}

public sealed record UiTarget(
    string ProcessName,
    string WindowTitle,
    string? Name,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    string? Description,
    IReadOnlyList<string> Ancestors,
    double X,
    double Y,
    double Width,
    double Height,
    bool IsPassword,
    bool IsEnabled);

public sealed record TraceEvent(
    long ElapsedMilliseconds,
    TraceEventKind Kind,
    string? Detail,
    string? ProcessName,
    string? WindowTitle,
    UiTarget? Target,
    string? FramePath,
    bool Redacted = false);

public sealed record TranscriptSegment(long StartMilliseconds, long EndMilliseconds, string Text, double Confidence = 1);

public enum FrameRole
{
    Initial,
    Before,
    After,
    Marker,
    Final,
    Periodic
}

public sealed record FrameEvidence(
    long ElapsedMilliseconds,
    string Reason,
    byte[] Png,
    string? Id = null,
    FrameRole Role = FrameRole.Periodic,
    Guid? ActionId = null);

public sealed record ActionEvidence(
    Guid Id,
    int Order,
    long StartMilliseconds,
    long EndMilliseconds,
    TraceEventKind Kind,
    string? Detail,
    UiTarget? Target,
    FrameEvidence? Before,
    FrameEvidence? After,
    IReadOnlyList<TranscriptSegment> NearbyNarration,
    bool Redacted = false,
    bool IncludeInSkill = true,
    double Confidence = 0.7);

public sealed record AudioChunkEvidence(
    long StartMilliseconds,
    long EndMilliseconds,
    string Path,
    bool Redacted = false);

public sealed record AudioWindowEvidence(long StartMilliseconds, long EndMilliseconds, byte[] Wav);

public sealed record ActionUnderstanding(
    Guid ActionId,
    int Order,
    bool IncludeInSkill,
    string UserIntent,
    string Instruction,
    string VisibleBefore,
    string ObservedChange,
    string ExpectedResult,
    string? PossibleMistake,
    double Confidence,
    string? Uncertainty);

public sealed class EvidenceCoverage
{
    public int TotalActions { get; set; }
    public int ActionsWithFramePairs { get; set; }
    public int ActionsWithNarration { get; set; }
    public string AudioMode { get; set; } = "Unavailable";
    public string VisionMode { get; set; } = "Unavailable";
    public string Provider { get; set; } = "Local draft";
    public string Model { get; set; } = "";
    public List<string> Warnings { get; } = [];
}

public sealed class RecordingTrace
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Untitled computer workflow";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? EndedAt { get; set; }
    public CaptureMode CaptureMode { get; init; }
    public string? CaptureTarget { get; init; }
    public List<TraceEvent> Events { get; } = [];
    public List<ActionEvidence> Actions { get; } = [];
    public List<ActionUnderstanding> Interpretations { get; } = [];
    public List<AudioChunkEvidence> AudioChunks { get; } = [];
    public List<TranscriptSegment> Transcript { get; } = [];
    public List<string> Notes { get; } = [];
    public bool HasAudio { get; set; }
    public bool KeepRecording { get; set; }
    public EvidenceCoverage Evidence { get; } = new();
}

public sealed record SkillInput(string Name, string Description, string Type = "text", bool Required = true, bool Secret = false);

public sealed record SkillStep(
    int Order,
    string Instruction,
    string? Target,
    string ExpectedResult,
    double Confidence = 1);

public sealed class SkillDraft
{
    public string Name { get; set; } = "computer-workflow";
    public string Title { get; set; } = "Computer workflow";
    public string Description { get; set; } = "Follow the demonstrated computer workflow.";
    public string Intent { get; set; } = "Complete the user's demonstrated computer workflow.";
    public string Goal { get; set; } = "Complete the demonstrated computer workflow.";
    public List<SkillInput> Inputs { get; set; } = [];
    public List<string> Preconditions { get; set; } = [];
    public List<SkillStep> Procedure { get; set; } = [];
    public List<string> DecisionRules { get; set; } = [];
    public List<string> Safety { get; set; } = [];
    public List<string> Verification { get; set; } = [];
    public List<string> Recovery { get; set; } = [];
    public List<string> Uncertainties { get; set; } = [];
}

public sealed class AiSettings
{
    public string Provider { get; set; } = "None (local draft)";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string? EncryptedApiKey { get; set; }
    public bool UseAi { get; set; }
}

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId);
