# Oh My Skill privacy statement

Oh My Skill is a local-first Windows application. It does not collect
telemetry, create a background service, or send demonstration data anywhere
unless the user explicitly enables a BYOK provider and starts an AI-assisted
draft.

When AI is disabled, recording evidence and generated `SKILL.md` files remain
on the user's computer. When AI is enabled, the application sends only the
evidence required for the selected processing step, which may include paired
screen frames, nearby narration, semantic interaction metadata, and a short
audio window when the selected provider supports native audio. The provider's
own retention and training policies apply to those requests.

API keys are protected with Windows DPAPI and are not committed to the
repository, written to generated skills, or copied into the agent prompt.
Temporary audio, images, and traces are removed after a successful save. The
user can redact recent evidence before processing and should avoid recording
secrets, passwords, or personal information.

This statement describes the application's behavior. It is not a substitute
for a provider's terms, a deployment-specific privacy notice, or legal advice.
