# Oh My Skill installation

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/OhMySkill/Assets/Branding/p3.png">
    <source media="(prefers-color-scheme: light)" srcset="src/OhMySkill/Assets/Branding/p2.png">
    <img src="src/OhMySkill/Assets/Branding/p2.png" width="96" alt="Oh My Skill logo">
  </picture>
</p>

## Download the portable EXE

1. Open the repository Releases page.
2. Download the file matching Windows architecture:
   - `OhMySkill-win-x64.exe` for Intel/AMD 64-bit Windows.
   - `OhMySkill-win-arm64.exe` for ARM64 Windows.
3. Place the EXE in a folder you control, such as `E:\Apps\Oh My Skill`.
4. Verify the SHA-256 value against `SHA256SUMS.txt`.
5. Run the EXE. No .NET runtime installation is required.

The application stores settings under `%LOCALAPPDATA%\Oh My Skill` and generated skills under `%USERPROFILE%\Documents\Oh My Skill\skills`.

## Remove the portable app

Oh My Skill does not install a service, startup entry, or registry component.
To remove the application, close it and delete the downloaded `OhMySkill.exe`.
If you also want to remove local settings and saved skills, delete these
folders separately:

```text
%LOCALAPPDATA%\Oh My Skill
%USERPROFILE%\Documents\Oh My Skill
```

Delete the second folder only after exporting any `SKILL.md` files you want to
keep.

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

Unsigned development artifacts may show a Windows SmartScreen reputation warning. This does not mean the app requests administrator privileges. Production releases are built by the fail-closed workflow in [`SIGNING.md`](SIGNING.md), which requires a valid Authenticode signature and RFC 3161 timestamp before it can publish either EXE. Review the [code signing policy](CODE_SIGNING_POLICY.md) before distributing a release candidate.

Signing removes the **Unknown publisher** state. Microsoft may still classify a new signed publisher or new binary as unrecognized until reputation accumulates. Microsoft Store distribution is the only path Microsoft identifies as consistently avoiding first-download SmartScreen warnings.

## Build from source

The repository targets .NET 10 and Windows 10 build 19041 or later. From PowerShell:

```powershell
dotnet build .\OhMySkill.sln -c Release
dotnet run --project .\tests\OhMySkill.SelfCheck\OhMySkill.SelfCheck.csproj -c Release
dotnet publish .\src\OhMySkill\OhMySkill.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-x64
dotnet publish .\src\OhMySkill\OhMySkill.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-arm64
```

The generated branding resources are embedded from `src/OhMySkill/Assets/Branding`.
