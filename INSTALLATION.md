# SkillMyScreen installation

## Download the portable EXE

1. Open the repository Releases page.
2. Download the file matching Windows architecture:
   - `SkillMyScreen-win-x64.exe` for Intel/AMD 64-bit Windows.
   - `SkillMyScreen-win-arm64.exe` for ARM64 Windows.
3. Place the EXE in a folder you control, such as `E:\Apps\SkillMyScreen`.
4. Verify the SHA-256 value against `SHA256SUMS.txt`.
5. Run the EXE. No .NET runtime installation is required.

The application stores settings under `%LOCALAPPDATA%\SkillMyScreen` and generated skills under `%USERPROFILE%\Documents\SkillMyScreen\skills`.

## Windows privacy access

Recording uses the selected display/window, Windows UI Automation, and the default microphone. If Windows blocks microphone access, open **Settings → Privacy & security → Microphone** and allow desktop applications to use the microphone. The app does not require administrator access.

## First recording

1. Choose **Build a Skill**.
2. Enter a descriptive title.
3. Choose the entire display or a visible target window.
4. Configure a BYOK provider under **AI Settings**, or leave AI disabled for a deterministic local draft.
5. Click **Start Recording** and narrate the intent while performing the task.
6. Use **Mark Step** for an important point and **Redact Last 15 Seconds** for private material.
7. Click **Finish & Build Draft**.
8. Review the audio transcript, action evidence, intent, procedure, safety, and verification sections.
9. Save the skill. The app writes `SKILL.md`, writes `USE_THIS_SKILL.txt`, copies the prompt to the clipboard, and deletes temporary encrypted evidence.

## SmartScreen and signing

Unsigned development artifacts may show a Windows SmartScreen reputation warning. This does not mean the app requests administrator privileges. A production release must be Authenticode-signed and verified before distribution; the local build artifacts are unsigned until a code-signing certificate is configured.

## Build from source

The repository targets .NET 10 and Windows 10 build 19041 or later. From PowerShell:

```powershell
dotnet build .\SkillMyScreen.sln -c Release
dotnet run --project .\tests\SkillMyScreen.SelfCheck\SkillMyScreen.SelfCheck.csproj -c Release
dotnet publish .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-x64
dotnet publish .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-arm64
```

The generated branding resources are embedded from `src/SkillMyScreen/Assets/Branding`.
