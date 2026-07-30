# SkillMyScreen native Windows EXE

SkillMyScreen records a user-demonstrated computer workflow and stops after creating a reviewed local `SKILL.md` and a copyable prompt for another agent.

```mermaid
flowchart TB
    USER["User performs and narrates a task"] --> APP["WPF SkillMyScreen.exe"]
    APP --> SCREEN["Display or window keyframes"]
    APP --> AUDIO["Microphone narration"]
    APP --> INPUT["Raw Input and UI Automation context"]
    SCREEN --> TRACE["Synchronized local trace"]
    AUDIO --> TRACE
    INPUT --> TRACE
    TRACE --> REDACT["Password, private-region, and correction redaction"]
    REDACT --> TEMP["Encrypted temporary session artifacts"]
    REDACT --> CONTEXT["Intent context: narration, timing, UI semantics, notes, representative frames"]
    CONTEXT --> AUDIOAI["Provider or Windows speech transcription"]
    AUDIOAI --> CONTEXT
    CONTEXT --> AI["Multimodal BYOK provider or deterministic local draft"]
    AI --> DRAFT["Validated SkillDraft with inferred intent"]
    DRAFT --> REVIEW["User review and clarification"]
    REVIEW --> OUTPUT["Documents/SkillMyScreen/skills/<slug>/SKILL.md"]
    OUTPUT --> PROMPT["Copyable prompt for Codex, OpenCode, Claude Code, Hermes, or another agent"]
```

## Technology

- C# and WPF on .NET 10.
- Native Windows screen keyframes, microphone capture, Raw Input, Win32 window context, and Windows UI Automation.
- AES-GCM temporary artifacts with a DPAPI-protected session key.
- `HttpClient` BYOK adapters for OpenAI-compatible providers, Anthropic, and Gemini.
- Provider transcription, Gemini inline audio, and Windows speech fallback feed narration into intent analysis.
- Representative encrypted frames and UI semantics are passed to multimodal providers when supported.
- Self-contained single-file `win-x64` and `win-arm64` EXEs; code signing remains a distribution task.

## Product boundary

The only durable product artifact is `SKILL.md`. SkillMyScreen does not run the skill, host an MCP server, inject input, start a background service, require administrator access, install a browser, or create a database. The receiving agent must already have the tools needed to execute the instructions.
