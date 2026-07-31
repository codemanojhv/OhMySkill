# Oh My Skill v0.2.1

## Release status

This is an open-source Windows preview for x64 and ARM64. The binaries are
self-contained and intentionally unsigned; verify `SHA256SUMS.txt` before
running them. The fail-closed signing workflow remains available for a future
trusted release.

The public release assets are architecture-specific ZIPs. Each ZIP contains
the matching `OhMySkill.exe`, README, installation guide, license, and privacy
notice; no bare EXE is presented as the primary download.

## What changed

- Moved recording startup, stop/finalization, encrypted evidence reads, and
  skill saving away from the WPF dispatcher so the window remains responsive.
- Added a visible busy card with a progress bar, current operation, and a safe
  cancel path for transcription, provider calls, and connection tests.
- Added live recording status: elapsed time, action count, buffered frames,
  microphone level, and captured audio duration.
- Added setup preflight feedback for microphone and visible-window discovery.
- Added an ordered review flow with explicit steps, before/after thumbnails,
  narration, interpretation, confidence, and evidence warnings.
- Made the `SKILL.md` preview update as the user edits the draft.
- Replaced blocking save dialogs with inline success/error state and buttons to
  open the output folder or copy the universal agent prompt.
- Added architecture-specific ZIP downloads containing the executable,
  README, installation guide, license, and privacy notice.
- Kept the supplied Oh My Skill cyan mark throughout the app and release docs.

## Evidence behavior

The recorder keeps a bounded in-memory rolling frame buffer, stores a settled
after-action frame for each logical interaction, records timestamped encrypted
microphone chunks, and writes only the final `SKILL.md` plus prompt after a
successful save. No full video, MCP server, executor, telemetry, or database
is added.

## Known limitations

- Windows-only; no full recording video is retained.
- Windows Speech fallback depends on an installed recognition language.
- Provider capability and retention policies vary; AI processing is opt-in and
  BYOK.
- New unsigned downloads can show a SmartScreen reputation warning.
- Generated skills require human review and are not executed by this app.
