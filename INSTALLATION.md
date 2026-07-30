# SkillMyScreen installation guide

This guide covers the supported ways to install, verify, run, update, and remove SkillMyScreen on Windows. The recommended path is the portable self-contained EXE from the GitHub Release page.

## Choose the right installation path

| Path | Best for | Requirements | Admin rights |
| --- | --- | --- | --- |
| Portable release EXE | Most users | Windows 10/11 | Not required |
| Build from source | Developers and contributors | Windows 10/11 and .NET 10 SDK | Not required |
| Local draft mode | Privacy-sensitive workflows or no provider key | Portable EXE only | Not required |

SkillMyScreen does not use an installer, Windows service, browser extension, MCP server, database, or background agent. It is a user-launched desktop application that stops after creating a local <code>SKILL.md</code>.

## System requirements

### Operating system

- Windows 10 version 19041 or later, or Windows 11.
- Intel/AMD 64-bit Windows: use <code>SkillMyScreen-win-x64.exe</code>.
- ARM64 Windows: use <code>SkillMyScreen-win-arm64.exe</code>.

To identify the processor architecture in PowerShell:

~~~powershell
$env:PROCESSOR_ARCHITECTURE
~~~

Typical values are <code>AMD64</code> for Intel/AMD 64-bit Windows and <code>ARM64</code> for ARM-based Windows. You can also open **Settings > System > About** and check **System type**.

### Optional hardware and accounts

- A microphone is optional. Without one, screen and interaction capture still work.
- An internet connection and a provider API key are required only when using BYOK drafting or transcription.
- The receiving agent (Codex, OpenCode, Claude Code, Hermes, or another agent) must already have the computer-control, browser, shell, or file tools required by the generated skill.

## Install the published EXE

### 1. Download from GitHub

Open the [SkillMyScreen releases page](https://github.com/codemanojhv/SkillMyScreen/releases) and choose the release you intend to use. The current initial build is [v0.1.0](https://github.com/codemanojhv/SkillMyScreen/releases/tag/v0.1.0).

Download all three files if you want to verify the build:

~~~text
SkillMyScreen-win-x64.exe       Intel/AMD 64-bit Windows
SkillMyScreen-win-arm64.exe     ARM64 Windows only
SHA256SUMS.txt                  SHA-256 verification list
~~~

For a normal Intel or AMD PC, download <code>SkillMyScreen-win-x64.exe</code>. Do not choose ARM64 merely because Windows says “64-bit”; ARM64 is a different processor architecture.

### 2. Put the EXE in a stable folder

The release is portable. Create a folder you control, for example:

~~~text
E:\Apps\SkillMyScreen\
~~~

Move the downloaded EXE into that folder. You can also run it directly from Downloads, but a stable folder makes shortcuts and updates easier to manage. Do not place it in <code>Program Files</code> unless your organization manages that location.

### 3. Verify the download

The release includes <code>SHA256SUMS.txt</code>. Place it beside the EXE and run:

~~~powershell
Set-Location 'E:\Apps\SkillMyScreen'
$expected = (Select-String -Path '.\SHA256SUMS.txt' -Pattern 'SkillMyScreen-win-x64.exe').Line.Split()[0]
$actual = (Get-FileHash -LiteralPath '.\SkillMyScreen-win-x64.exe' -Algorithm SHA256).Hash
[pscustomobject]@{Expected=$expected; Actual=$actual; Match=($expected -eq $actual)}
~~~

The result must show <code>Match : True</code>. For an ARM64 download, replace the file name in both commands with <code>SkillMyScreen-win-arm64.exe</code>. Do not run a file when the hash does not match the release checksum.

### 4. Start the application

Double-click the architecture-appropriate EXE. The app runs as the current Windows user and does not request administrator access.

To run the built-in diagnostic from PowerShell:

~~~powershell
.\SkillMyScreen-win-x64.exe --self-check
~~~

The expected output is:

~~~text
SkillMyScreen self-check passed.
~~~

The diagnostic does not record the screen or microphone and does not call an AI provider.

## First-run setup

### Microphone access

The microphone is optional. If you want narration:

1. Open **Settings > Privacy & security > Microphone**.
2. Enable **Microphone access** and **Let desktop apps access your microphone**.
3. Confirm that the intended input device is available in Windows sound settings.
4. Start a short test recording and verify that the recording status reports microphone capture.

If no microphone is available, SkillMyScreen continues with screen and interaction context. Narration transcription is then unavailable.

### AI provider setup

AI is optional. To stay entirely local, leave AI disabled and use the deterministic draft.

To configure BYOK:

1. Open **AI Settings**.
2. Select a provider.
3. Confirm the endpoint and model, or enter a custom OpenAI-compatible endpoint.
4. Enter the API key.
5. Enable **Use this provider to create the draft**.
6. Select **Save Settings**.
7. Select **Test Connection** before recording an important workflow.

The key is stored with Windows DPAPI for the current user. It is not written into the repository, the generated <code>SKILL.md</code>, or the copied agent prompt.

The catalog includes OpenAI, Anthropic, Google Gemini, OpenRouter, Groq, Mistral, xAI, DeepSeek, Cerebras, Together AI, Fireworks AI, NovitaAI, GLM, Moonshot, MiniMax, Qwen, Hugging Face, NVIDIA NIM, Azure AI Foundry, OpenCode Zen/Go, DeepInfra, Ollama Cloud, Ollama, LM Studio, and custom OpenAI-compatible endpoints. The generic providers require an endpoint and model compatible with the selected protocol; catalog presence is not a guarantee that every model has been live-tested.

## Create your first skill

1. Select **Build a Skill**.
2. Enter a descriptive title, such as “Prepare the weekly sales report”.
3. Choose **Entire display** or **Selected window**.
4. Select **Start Recording**.
5. Perform the task slowly and narrate the intent, changing inputs, conditions, and what success looks like.
6. Select **Mark Step** at important transitions.
7. Avoid passwords, API keys, OTPs, private messages, and customer data. If sensitive data appears, select **Redact Last 15 Seconds** immediately.
8. Select **Finish & Build Draft**.
9. Review and edit every field, especially required inputs, safety rules, verification, and recovery.
10. Select **Save SKILL.md**.

The final file is saved under:

~~~text
%USERPROFILE%\Documents\SkillMyScreen\skills\<skill-slug>\SKILL.md
~~~

The prompt for the receiving agent is copied to the clipboard when the skill is saved. Paste it into the agent, or open the saved file directly and ask the agent to read it before acting.

## Where data is stored

| Data | Location | Retention |
| --- | --- | --- |
| Provider settings and DPAPI-protected key | <code>%LOCALAPPDATA%\SkillMyScreen\settings.json</code> | Kept until removed or replaced |
| Temporary trace, audio, and frame files | <code>%LOCALAPPDATA%\SkillMyScreen\sessions\</code> | Encrypted while recording; deleted after saving a skill |
| Durable skills | <code>%USERPROFILE%\Documents\SkillMyScreen\skills\</code> | Kept until the user deletes them |

If a recording is interrupted before saving, an encrypted session directory may remain. Review and remove only the specific abandoned session folders after closing the app.

## Windows security prompts

### “This app can’t run on your PC”

This almost always means the wrong architecture was selected:

- Normal Intel/AMD computer: run <code>SkillMyScreen-win-x64.exe</code>.
- ARM-based Windows computer: run <code>SkillMyScreen-win-arm64.exe</code>.

Do not rename an ARM64 binary and do not try to solve this by disabling Windows security. Download the correct asset and verify its SHA-256 hash first.

### SmartScreen says the publisher is unknown

The current pre-release binaries are not code-signed, so Windows may show a reputation warning. Before continuing:

1. Confirm that the file came from the project’s GitHub Release.
2. Verify the SHA-256 hash against <code>SHA256SUMS.txt</code>.
3. In the Windows warning, choose **More info** only if the verified file is the one you intended to run.

If company policy or antivirus blocks the file, do not bypass the policy. Use the source-build path or ask the administrator to review the release.

### The app asks for administrator permission

The application manifest requests <code>asInvoker</code> and should not need elevation. Cancel unexpected elevation prompts and verify that you launched the official EXE rather than a modified wrapper or shortcut.

## Troubleshooting

### Recording starts without audio

- Check Windows microphone privacy settings.
- Confirm that another application is not exclusively using the microphone.
- Select a default input device in **Settings > System > Sound**.
- Continue without audio if visual and interaction evidence is sufficient.

### A control name is missing from the event preview

Windows UI Automation is provided by the target application. Some applications expose little or no accessibility metadata, and elevated applications may not be inspectable from a non-elevated recorder. Use **Mark Step**, explain the action aloud, and correct the target during review.

### The provider test fails

- Confirm the endpoint includes the expected API base path.
- Confirm the model name is valid for the selected provider.
- Check the key permissions, quota, and account region.
- Try the deterministic local draft to verify that recording and Markdown output work independently of the provider.
- Do not paste the key into an issue or log.

### The generated skill is incomplete

The recording is evidence, not automatic proof of intent. Add explicit markers, narrate variable inputs and decision rules, and edit the generated procedure, safety, verification, and recovery sections before saving.

### A previous skill already has the same name

SkillMyScreen does not overwrite a saved skill folder. It creates a numbered folder such as <code>my-skill-2</code>. Rename or remove old copies manually after confirming that they are no longer needed.

## Update procedure

1. Read the release notes on the [releases page](https://github.com/codemanojhv/SkillMyScreen/releases).
2. Download the new architecture-specific EXE and checksum file.
3. Verify the new hash.
4. Close SkillMyScreen.
5. Replace the old EXE in your portable folder.
6. Run <code>--self-check</code>, then open the app normally.

Settings and saved skills are stored outside the EXE, so replacing the binary does not remove them. Keep a backup of important <code>SKILL.md</code> files before major pre-release updates.

## Uninstall and cleanup

SkillMyScreen has no installer registration, service, scheduled task, or background process.

To remove only the program, close it and delete the portable EXE and its folder.

To remove application settings and any abandoned encrypted sessions, delete these user-owned locations after closing the app:

~~~text
%LOCALAPPDATA%\SkillMyScreen\
~~~

To remove generated skills, delete the specific folders under:

~~~text
%USERPROFILE%\Documents\SkillMyScreen\skills\
~~~

Deleting the local application folder removes the protected provider key and any unsaved temporary sessions. Export or copy skills you want to keep first.

## Build from source

Use this path for development, provider integration work, or when your organization does not allow unsigned release binaries.

### Requirements

- Git for Windows.
- .NET 10 SDK.
- Windows 10/11 with the Windows desktop workload included in the SDK.
- A microphone only if narration is being tested.

### Clone and validate

~~~powershell
git clone https://github.com/codemanojhv/SkillMyScreen.git
Set-Location .\SkillMyScreen
dotnet --version
dotnet restore .\SkillMyScreen.sln
dotnet build .\SkillMyScreen.sln -c Release
dotnet run --project .\tests\SkillMyScreen.SelfCheck\SkillMyScreen.SelfCheck.csproj -c Release
~~~

The self-check must print <code>SkillMyScreen self-check passed.</code>.

### Publish a portable executable

~~~powershell
dotnet publish .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-x64
dotnet publish .\src\SkillMyScreen\SkillMyScreen.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\artifacts\win-arm64
~~~

The repository ignores <code>.tools</code>, <code>artifacts</code>, <code>bin</code>, and <code>obj</code> so SDK caches and generated binaries are not committed.

For an E: drive-only developer setup, point the NuGet cache at E: and invoke an SDK installed on E::

~~~powershell
$env:NUGET_PACKAGES = 'E:\SkillMyScreen\.tools\nuget'
$dotnet = 'E:\SkillMyScreen\.tools\dotnet\dotnet.exe'
& $dotnet build .\SkillMyScreen.sln -c Release
~~~

The public repository does not include that local SDK cache; install the .NET SDK using your organization’s approved method.

## Security checklist

- Download release files only from the project’s GitHub repository.
- Verify checksums before first launch and after every update.
- Do not record secrets or private data.
- Keep AI disabled for sensitive workflows unless the provider’s data policy is acceptable.
- Review every generated procedure before giving it to an agent.
- Treat the generated skill as instructions, not as proof of completion.
- Report security issues privately; never attach API keys, recordings, or private traces to a public issue.

For architecture and design details, see [ARCHITECTURE.md](ARCHITECTURE.md) and the [project README](README.md).
