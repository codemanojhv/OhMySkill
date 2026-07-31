# Oh My Skill installation and first run

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/OhMySkill/Assets/Branding/p3.png">
    <source media="(prefers-color-scheme: light)" srcset="src/OhMySkill/Assets/Branding/p2.png">
    <img src="src/OhMySkill/Assets/Branding/p2.png" width="96" alt="Oh My Skill logo">
  </picture>
</p>

## Prerequisites

- Windows 10 version 19041 or later, or Windows 11.
- Intel/AMD 64-bit Windows: use the `x64` package. Windows on ARM: use the `arm64` package.
- No administrator permission and no .NET runtime installation are required for a published package.
- A microphone is optional. It is required only when you want narrated instructions; screen and interaction evidence still work without one.
- An internet connection and a provider key are optional. AI is BYOK; the local draft works without a network connection.
- Allow desktop microphone access in **Settings > Privacy & security > Microphone** if narration is needed.

## Download the application

1. Open the repository [Releases](https://github.com/codemanojhv/OhMySkill/releases) page.
2. Download the ZIP that matches the computer:
   - `OhMySkill-v0.2.1-win-x64.zip` for Intel/AMD 64-bit Windows.
   - `OhMySkill-v0.2.1-win-arm64.zip` for ARM64 Windows.
3. Extract the ZIP to a folder you control, for example `E:\Apps\Oh My Skill`.
4. Optional but recommended: verify the ZIP or EXE hash against `SHA256SUMS.txt` from the same release.
5. Run `OhMySkill.exe` from the extracted folder.

The ZIP is the recommended download because it keeps the executable, instructions, license, and privacy notice together. The standalone EXE is also published as a portable option; it does not install a service or register startup tasks.

### Windows warning about an unsigned preview

The public open-source preview is not Authenticode-signed, so Windows SmartScreen may show **Unknown publisher**. This is a reputation warning, not an administrator request. Verify the release checksum and source before choosing **More info > Run anyway**. A future signed release will still need reputation to build; see [`SIGNING.md`](SIGNING.md).

## First-use setup

1. Launch **Oh My Skill** and choose **Build a Skill**.
2. Confirm the setup status shows the capture source and microphone state. A missing microphone is safe but means narration cannot be transcribed.
3. Keep the target application visible. Close unrelated windows if you are using entire-display capture.
4. Choose **Entire display** or **Selected window**, enter a descriptive title, and click **Start Recording**.
5. Perform the task at a normal pace. Narrate the goal, changing values, decisions, optional branches, and what success looks like.
6. Use **Mark Step** for a meaningful checkpoint. Use **Redact Last 15 Seconds** immediately if private content appears.
7. Click **Finish & Build Draft**. The app shows progress while it closes the microphone, waits for stable after-action frames, reads evidence, transcribes narration, and calls the optional provider. The window remains responsive and the review screen appears as soon as the local draft is ready.
8. In **Review and save**, inspect every before/after pair, nearby narration, confidence, and warning. Edit the fields; the `SKILL.md` preview updates as you type.
9. Click **Save SKILL.md**. The app writes one local skill, writes `USE_THIS_SKILL.txt`, copies the universal prompt to the clipboard, and deletes temporary encrypted audio, screenshots, and traces only after the save succeeds.

## Configure BYOK AI (optional)

1. Open **AI Settings**.
2. Choose one of the listed providers, or use a local OpenAI-compatible server such as Ollama or LM Studio.
3. Enter the endpoint, model, and API key, then click **Save Settings**.
4. Click **Test Connection**. The test sends no recording evidence.
5. Enable **Use this provider to create the draft** before recording the next skill.

Provider capability and retention vary. The review summary reports whether the result used native audio, provider transcription, Windows Speech fallback, vision, or metadata-only fallback. The app never claims that rejected media was used.

## Troubleshooting

- **The window seems busy:** wait for the visible status card; recording stop, evidence reads, and provider calls are deliberately off the UI thread. During AI processing, choose **Cancel** to keep the local draft.
- **No microphone:** allow desktop microphone access, select the correct Windows input device, and restart the app. The evidence summary will explicitly show narration as unavailable when it cannot be recovered.
- **No selected window:** switch to **Entire display**, or leave the target window open and refresh the setup screen.
- **Provider failure:** use **Cancel** or wait for the local draft, then check the endpoint/model/key under AI Settings. The review screen preserves warnings and does not silently invent steps.
- **SmartScreen warning:** verify the checksum and use the source release. For enterprise distribution, use an Authenticode-signed build from the release process.

## Remove the application

Oh My Skill does not install a Windows service, startup entry, browser extension, or registry component. Close the app and delete the extracted folder. Settings are stored under `%LOCALAPPDATA%\Oh My Skill`; generated skills are stored under `%USERPROFILE%\Documents\Oh My Skill\skills`. Delete those folders separately only after exporting skills you want to keep.

## Build from source

Requirements:

- Windows 10/11.
- .NET 10 SDK (the repository includes a local SDK under `.tools\dotnet` for the maintainer build).

From the repository root in PowerShell:

```powershell
dotnet restore .\OhMySkill.sln
dotnet build .\OhMySkill.sln -c Release
dotnet run --project .\tests\OhMySkill.SelfCheck\OhMySkill.SelfCheck.csproj -c Release
dotnet publish .\src\OhMySkill\OhMySkill.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-x64
```

Run the packaged diagnostic with `.\artifacts\win-x64\OhMySkill.exe --self-check`. See [`README.md`](README.md), [`ARCHITECTURE.md`](ARCHITECTURE.md), and [`PRIVACY.md`](PRIVACY.md) for the product boundary and evidence handling details.
