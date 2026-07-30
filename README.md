# SkillMyScreen

SkillMyScreen is a local-first Windows desktop application that turns a demonstrated computer workflow into a reviewed, portable <code>SKILL.md</code> file for an AI agent.

It is designed for work that happens anywhere on a Windows computer—not only in a browser. Demonstrate a task in a desktop application, file manager, terminal, browser, or mixed workflow; explain the intent through the microphone; review the generated instructions; then save the skill locally and paste the included agent prompt into Codex, OpenCode, Claude Code, Hermes, or another agent that can use the required computer tools.

> **Status:** early pre-release build. The vertical slice is implemented and runnable, but provider compatibility, capture fidelity, packaging, signing, and broad hardware coverage still need production hardening.

> **Installation:** See the [installation guide](INSTALLATION.md) for release downloads, architecture selection, checksum verification, first-run setup, troubleshooting, updates, removal, and source builds.

## Product boundary

SkillMyScreen deliberately stops at the portable skill document.

- It records evidence from a user demonstration.
- It creates a deterministic local draft or an optional BYOK AI draft.
- It gives the user a review and correction step.
- It saves one durable <code>SKILL.md</code> and copies a prompt that tells another agent to read it.
- It does **not** execute the skill, inject mouse or keyboard actions, host an MCP server, install a browser, run a background service, or create a database.

The receiving agent must already have the tools and permissions needed to execute the instructions. This keeps the builder small, inspectable, and safer to run on a personal Windows machine.

## How it works

~~~mermaid
flowchart TB
    USER["User demonstrates a repeatable computer task"] --> UI["WPF desktop app"]
    UI --> CAPTURE["Display or selected-window keyframes"]
    UI --> MIC["Microphone narration"]
    UI --> INPUT["Raw Input and UI Automation context"]
    CAPTURE --> TRACE["Timestamped local recording trace"]
    MIC --> TRACE
    INPUT --> TRACE
    TRACE --> PROTECT["User redaction + encrypted temporary files"]
    PROTECT --> DRAFT["Deterministic local draft"]
    PROTECT --> BYOK["Optional BYOK provider"]
    BYOK --> DRAFT
    DRAFT --> REVIEW["User reviews and edits the draft"]
    REVIEW --> SKILL["Documents/SkillMyScreen/skills/<slug>/SKILL.md"]
    REVIEW --> PROMPT["Copyable prompt for the receiving agent"]
~~~

### Recording

1. Choose **Entire display** or a visible window.
2. Start recording and perform the task normally.
3. SkillMyScreen captures periodic keyframes, frames after click events, microphone PCM audio, and high-level interaction context.
4. Use **Mark Step** when an action is especially meaningful.
5. Use **Redact Last 15 Seconds** if the recent portion should not be retained.
6. Finish the recording to build a draft.

The recorder intentionally does not persist ordinary typed characters. It records selected keyboard shortcut categories (for example, Enter, Tab, Escape, and modifier keys) and semantic target information when Windows UI Automation exposes it. Never type passwords, API keys, OTPs, or private values during a recording; redact immediately if one is entered accidentally.

### Drafting

With AI disabled, the app converts the captured event timeline into a deterministic draft with explicit safety, verification, recovery, and uncertainty sections.

With AI enabled, the app sends the captured compiler prompt to the selected provider using the key saved for the current Windows user. The response must be JSON matching the <code>SkillDraft</code> contract. If the provider fails or returns invalid data, SkillMyScreen keeps the deterministic local draft available instead of claiming that AI succeeded.

### Review and output

The review page exposes the skill name, description, goal, inputs, procedure, safety, verification, recovery, and a Markdown preview. Saving creates a new folder under:

~~~text
%USERPROFILE%\Documents\SkillMyScreen\skills\<skill-slug>\SKILL.md
~~~

The prompt copied to the clipboard tells the receiving agent to read that exact file before acting, follow its safety and verification rules, ask about missing inputs, and report the final verification result.

## Current capabilities

| Area | Current implementation |
| --- | --- |
| Computer scope | Windows desktop workflows across browsers, desktop applications, file managers, terminals, and mixed workflows |
| Screen evidence | Native GDI-compatible PNG keyframes for the entire display or a selected visible window |
| Audio | Native <code>winmm</code> microphone capture to a temporary 16 kHz mono WAV buffer |
| Interaction context | Raw Input mouse events, selected keyboard shortcut categories, foreground process/window, and Windows UI Automation metadata at the cursor |
| Local drafting | Deterministic <code>DraftFactory</code> that produces a reviewable <code>SkillDraft</code> without a network call |
| BYOK drafting | OpenAI-compatible chat completions, Anthropic Messages, and Google Gemini <code>generateContent</code> request paths |
| BYOK transcription | OpenAI-compatible <code>/audio/transcriptions</code> path; model defaults include <code>whisper-1</code>, Groq Whisper, and Mistral Voxtral options |
| Provider catalog | OpenAI, Anthropic, Gemini, OpenRouter, Nous Portal, Groq, Mistral, xAI, DeepSeek, Cerebras, Together, Fireworks, NovitaAI, GLM, Moonshot, MiniMax, Qwen, Hugging Face, NVIDIA NIM, Azure AI Foundry, OpenCode Zen/Go, DeepInfra, Ollama Cloud, Ollama, LM Studio, and custom OpenAI-compatible endpoints |
| Privacy controls | Explicit recording warning, recent-window redaction, encrypted temporary artifacts, and automatic cleanup after a successful save |
| Output | One Markdown skill with YAML frontmatter plus a copyable agent prompt |
| Distribution | Self-contained single-file <code>win-x64</code> and <code>win-arm64</code> builds; no .NET runtime required for end users |

The provider catalog is broader than the number of protocol adapters. Providers that expose an OpenAI-compatible API use the generic adapter and may require a provider-specific endpoint and model. Anthropic and Gemini have dedicated request formats. Each provider/model combination still needs live validation before being described as production-ready.

## Technology stack

| Layer | Technology | Why it is used |
| --- | --- | --- |
| Desktop UI | C# and WPF on .NET 10 | Native Windows windowing and a small distributable surface |
| Screen capture | Win32 <code>user32.dll</code>, GDI-compatible <code>System.Drawing</code> | Captures display/window evidence without a browser or extension |
| Microphone | Win32 <code>winmm.dll</code> wave-in API | Low-dependency microphone capture with no third-party recorder service |
| Interaction | Win32 Raw Input | Receives high-level mouse/shortcut events while the recorder is active |
| UI semantics | Windows UI Automation | Adds control name, automation id, control type, class, bounds, process, and window context when available |
| Temporary protection | AES-GCM plus Windows DPAPI (<code>CurrentUser</code>) | Protects session material locally and binds the saved key to the Windows user |
| Provider calls | <code>HttpClient</code>, JSON, multipart HTTP | Keeps BYOK integrations direct and avoids a hosted backend |
| Serialization | <code>System.Text.Json</code> | Serializes traces, settings, and provider drafts |
| Packaging | .NET single-file self-contained publish | Users can run the EXE without installing .NET |
| Validation | Built-in <code>--self-check</code> executable path | Verifies slugging, Markdown rendering, prompt paths, file save, and AES-GCM round-trip |

The requested execution level is <code>asInvoker</code>; the application does not request administrator rights.

## Privacy and data lifecycle

SkillMyScreen is local-first, but screen and microphone capture are inherently sensitive. The user remains responsible for what appears during a demonstration.

### Temporary session data

While recording, encrypted temporary data is written under:

~~~text
%LOCALAPPDATA%\SkillMyScreen\sessions\<recording-id>\
  key.dpapi
  trace.json.enc
  audio.wav.enc              (when a microphone is available)
  frames\*.enc
~~~

Each session uses a random AES-GCM key. The key is protected with Windows DPAPI for the current user. After <code>SKILL.md</code> is saved, the temporary session folder is deleted.

### Settings and API keys

Provider settings are stored at:

~~~text
%LOCALAPPDATA%\SkillMyScreen\settings.json
~~~

The API key is stored as DPAPI-protected ciphertext, not as clear text. Keys are never written to the repository, logs, generated Markdown, or the clipboard prompt. Leaving the API key field blank keeps the existing protected value.

### What is and is not sent to a provider

- No provider call is made when AI is disabled.
- When AI is enabled, the captured compiler prompt and optional transcript are sent to the selected endpoint.
- Temporary PNG/audio files are not uploaded by the current provider service.
- Provider retention, logging, and training policies are outside this application; choose a provider and account appropriate for the data being demonstrated.

## Windows permissions and compatibility

The app is designed to run without elevation and without a background service. Windows may still require user-level privacy approval for microphone access. UI Automation metadata can be unavailable when the target application is elevated or exposes limited accessibility information.

Supported targets:

- Windows 10 version 19041 or later, and Windows 11.
- Intel/AMD 64-bit Windows: use <code>win-x64</code>.
- ARM64 Windows: use <code>win-arm64</code>.
- A microphone is optional; screen and interaction recording continue when no microphone is available.

The published binaries are currently unsigned. SmartScreen or enterprise policy may therefore show a reputation warning even though the executable is self-contained and does not request administrator access. Code signing is a distribution hardening task, not a runtime dependency.

## Getting started

### Run a published build

Download the architecture-appropriate executable from the repository Releases page and run it. For most PCs, choose the x64 asset:

~~~text
SkillMyScreen-win-x64.exe
~~~

The app does not need the .NET runtime when using a self-contained release asset.

### Build from source

Requirements:

- Windows 10/11
- .NET 10 SDK
- A microphone only if narration is required

From the repository root:

~~~powershell
dotnet --version
dotnet restore .\SkillMyScreen.sln
dotnet build .\SkillMyScreen.sln -c Release
dotnet run --project .\tests\SkillMyScreen.SelfCheck\SkillMyScreen.SelfCheck.csproj -c Release
~~~

The self-check should print:

~~~text
SkillMyScreen self-check passed.
~~~

The development build can also run its diagnostic path directly:

~~~powershell
dotnet run --project .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -- --self-check
~~~

### Publish a self-contained EXE

~~~powershell
dotnet publish .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-x64
dotnet publish .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-arm64
~~~

Run the packaged diagnostic:

~~~powershell
.\artifacts\win-x64\SkillMyScreen.exe --self-check
~~~

Build outputs and local SDK/cache folders are ignored by Git. Release assets should be attached to a GitHub Release rather than committed to the source tree.

## Project layout

~~~text
SkillMyScreen/
├── README.md                         Product, architecture, build, and privacy documentation
├── ARCHITECTURE.md                   Short architecture overview and Mermaid flow
├── SkillMyScreen.sln                 Solution containing the app and self-check project
├── src/SkillMyScreen/
│   ├── MainWindow.xaml(.cs)          WPF UI and recording/review workflow
│   ├── CaptureService.cs             Window catalog, screen capture, microphone, recorder
│   ├── RawInputService.cs            Mouse and shortcut event observation
│   ├── UiAutomationService.cs        Semantic target lookup at the cursor
│   ├── ProviderService.cs             BYOK adapters, transcription, compiler prompt, draft factory
│   ├── StorageService.cs              DPAPI/AES-GCM storage, Markdown renderer, prompt builder
│   ├── Models.cs                     Trace, draft, provider, and output contracts
│   ├── SelfCheck.cs                  In-process diagnostic checks
│   └── app.manifest                  asInvoker and Windows compatibility declaration
├── tests/SkillMyScreen.SelfCheck/     Minimal executable validation project
└── artifacts/                         Local publish output; ignored from source commits
~~~

## Output contract

Every saved skill is a Markdown file with YAML frontmatter and predictable sections:

~~~markdown
---
name: "example-skill"
description: "A short description of the demonstrated workflow."
---

# Example skill

## Goal
...

## Required inputs
...

## Preconditions
...

## Procedure
...

## Decision rules
...

## Safety
...

## Verification
...

## Recovery
...
~~~

The generated prompt is intentionally tool-agnostic. It tells an agent to read the file, use it as the procedural source of truth, ask for missing inputs, avoid guessing, request confirmation for sensitive external actions, and report verification. It does not assume that Codex, OpenCode, Claude Code, Hermes, or another agent has the same tool names.

## Provider configuration

1. Open **AI Settings**.
2. Select a provider.
3. Confirm or edit the endpoint and model.
4. Enter the API key and select **Use this provider to create the draft**.
5. Save settings and use **Test Connection** before recording a valuable workflow.

The generic adapter expects an OpenAI-compatible <code>/chat/completions</code> endpoint and, for transcription, <code>/audio/transcriptions</code>. Custom providers can be configured by selecting **Custom OpenAI-compatible** and entering the complete base endpoint. Do not place secrets in issue reports, screenshots, README files, or commits.

## Validation evidence

The current local build has been validated with:

- Release solution build on the x64 development machine.
- Release solution build on the ARM64 publish target.
- Built-in self-check for Markdown rendering, prompt path conversion, atomic skill save, and AES-GCM encryption/decryption.
- Packaged x64 EXE launch smoke test.
- Packaged x64 <code>--self-check</code> execution.
- PE-header verification of both architecture outputs (<code>0x8664</code> x64 and <code>0xAA64</code> ARM64).

This is local evidence, not a claim that every provider, Windows edition, microphone driver, accessibility provider, or end-to-end AI workflow has been verified.

## Known limitations

- Capture is currently event- and keyframe-based; it is not a full video recorder.
- Narration is captured as audio, but transcription requires a configured compatible provider. There is no bundled offline speech model yet.
- OCR, clipboard semantics, drag paths, application-specific APIs, and rich text entry are not yet modeled as first-class events.
- Raw Input intentionally records only high-level mouse actions and selected shortcut keys; it does not reconstruct every keystroke.
- Provider catalog entries do not guarantee identical API semantics. Live model and endpoint tests are still required per provider.
- The current compiler prompt sends a bounded event list and transcript, not all binary frames, to the model.
- Generated skills require human review. A recording is evidence, not proof that every inferred instruction is correct.
- The application creates instructions only; it does not execute or verify the workflow on the user's behalf.
- Published binaries are not code-signed and there is no automatic update channel yet.

## Roadmap

The next improvements should follow the evidence boundary rather than add an agent runtime:

1. **Capture fidelity:** pause/resume, better window lifecycle tracking, OCR/clipboard signals, richer keyboard and drag semantics, and reliable multi-monitor capture.
2. **Trust and review:** confidence-aware editing, frame/event review, transcript segments, secret-region masking, and stronger validation of generated Markdown.
3. **Provider hardening:** provider-specific model defaults, compatible response parsing, retries/timeouts, endpoint diagnostics, and optional offline transcription.
4. **Distribution:** signed x64/ARM64 installers or portable packages, release automation, update guidance, and Windows compatibility testing across common hardware.
5. **Documentation and examples:** representative skills for file management, spreadsheet work, desktop forms, terminal workflows, and mixed browser/desktop tasks.

## Contributing

Before opening a change:

1. Keep secrets, API keys, recordings, and personal screenshots out of the repository.
2. Make the smallest change that improves the capture-to-<code>SKILL.md</code> path.
3. Run the solution build and self-check.
4. If provider behavior changes, document the endpoint/model assumptions and test a real request with a locally supplied key.
5. Keep the product boundary explicit: no hidden automation executor, background service, MCP server, or telemetry without a separate design decision.

## Security and privacy reports

Do not publish captured recordings, API keys, or sensitive traces in a public issue. For a suspected security problem, use a private GitHub security report or contact the repository owner through GitHub with the minimum reproducible details.

## License

No open-source license has been selected yet. Until a license is added, the repository should be treated as all-rights-reserved source for evaluation and development.
