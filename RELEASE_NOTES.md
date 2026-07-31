# Oh My Skill v0.2.0

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/OhMySkill/Assets/Branding/p3.png">
    <source media="(prefers-color-scheme: light)" srcset="src/OhMySkill/Assets/Branding/p2.png">
    <img src="src/OhMySkill/Assets/Branding/p2.png" width="96" alt="Oh My Skill logo">
  </picture>
</p>

## Release status

This release candidate is prepared for x64 and ARM64 Windows. Public signing is
still pending; unsigned artifacts must not be presented as production-trusted
downloads. The signed release workflow in
`.github/workflows/signed-windows-release.yml` remains the production gate.

## Highlights

- Captures synchronized microphone narration, screen evidence, and high-level
  computer interaction context.
- Associates a settled before/after frame pair with each logical action.
- Uses nearby narration and the complete ordered trajectory to infer intent,
  procedure, safety, verification, and recovery.
- Supports deterministic local drafting and optional BYOK provider processing.
- Reviews evidence before saving one portable `SKILL.md` and an agent prompt.
- Deletes temporary encrypted audio, frames, and traces after a successful save.
- Ships the original Oh My Skill cyan mark and monochrome fallbacks throughout
  the desktop application and documentation.

## Artifacts

The release assets use these names:

- `OhMySkill-win-x64.exe`
- `OhMySkill-win-arm64.exe`
- `SHA256SUMS.txt`

Verify the SHA-256 values before running an artifact. See
[INSTALLATION.md](INSTALLATION.md), [SIGNING.md](SIGNING.md), and the
[code signing policy](CODE_SIGNING_POLICY.md) for distribution and trust
requirements.

## Known limitations

- This release is Windows-only and does not retain a full recording video.
- Windows Speech fallback depends on an installed recognition language.
- Provider capability and retention policies vary; AI processing is opt-in.
- Generated skills require human review and are not executed by this app.
