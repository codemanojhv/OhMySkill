# Oh My Skill v0.2.1 architecture

Oh My Skill records a narrated Windows workflow, pairs every logical interaction with before/after screen evidence, and stops after saving a reviewed local `SKILL.md` plus a universal agent prompt.

```mermaid
flowchart TD
    U["User starts recording and narrates"] --> P["WPF preflight"]
    P --> A["16 kHz microphone PCM"]
    P --> S["4 FPS rolling screen buffer"]
    P --> I["Raw Input + UI Automation"]
    A --> AC["Encrypted timestamped audio chunks"]
    S --> RB["Three-second in-memory buffer"]
    S --> TK["Bounded change-aware trajectory keyframes"]
    I --> AB["Action boundary detector"]
    RB --> BF["Frame before action"]
    AB --> ST["Capture until UI settles"]
    ST --> AF["Frame after action"]
    BF --> E["ActionEvidence"]
    AF --> E
    AB --> E
    AC --> T["Timestamped transcript"]
    T --> E
    E --> B["Action batches of at most six"]
    B --> AI1["Multimodal action interpretation"]
    B --> L["Deterministic local draft when AI is disabled"]
    TK --> AI2["Global skill synthesis"]
    AI1 --> AI2
    T --> AI2
    AI2 --> C["Evidence critic and refinement"]
    C --> R["Evidence review"]
    L --> R
    R --> O["Local SKILL.md + USE_THIS_SKILL.txt"]
    O --> D["Delete temporary evidence"]
    UI["WPF dispatcher: status, preview, and review only"] -.-> P
    AI2 -.-> UI
    O -.-> UI
```

## Technology

- C# and WPF on .NET 10.
- Native GDI-compatible screen sampling, Raw Input, Win32 window context, and Windows UI Automation.
- Native microphone capture at 16 kHz mono PCM with five-second encrypted chunks.
- AES-GCM temporary artifacts with a DPAPI-protected session key.
- `HttpClient` BYOK adapters for action-level vision/audio interpretation, complete-trajectory synthesis, critic refinement, transcription, and explicit capability fallbacks.
- Supplied cyan/monochrome branding assets embedded as WPF resources and the application icon.
- Self-contained single-file `win-x64` and `win-arm64` EXEs.
- Capture start, session finalization, evidence reads, transcription, provider calls, and saving run on background tasks. The WPF dispatcher is reserved for status updates, cancellation, thumbnails, and editing so long recordings do not freeze the window.

## Product boundary

The durable product output is a local `SKILL.md` and a universal prompt. Oh My Skill does not execute skills, host MCP, inject input, install a browser, create a database, run a background service, require administrator access, or collect telemetry.
