# Code signing policy

## Current status

Oh My Skill is applying for sponsored open-source signing. Until an external
signing program accepts the project, no SignPath Foundation certificate is
active. The current Azure Artifact Signing workflow remains fail-closed and
will not publish a release unless both Windows binaries are validly signed and
timestamped.

## Sponsored open-source policy

**Free code signing provided by SignPath.io, certificate by SignPath
Foundation** (application pending; not active until acceptance).

- **Committers and reviewers:** `codemanojhv` and any maintainers granted
  repository write access. Changes from outside committers require review
  before merge.
- **Release approver:** `codemanojhv`, the repository owner, until a separate
  release-approver team is established.
- **Build source:** release artifacts must be produced by the repository's
  version-controlled GitHub Actions workflow from the release source tree.
- **Release approval:** each sponsored signing request must be manually
  approved by the release approver.
- **Privacy policy:** see [PRIVACY.md](PRIVACY.md). The application does not
  transfer demonstration data unless the user explicitly enables a provider
  and requests AI processing.
- **Security:** repository and signing accounts must use multi-factor
  authentication. Private signing keys must never be stored in the repository
  or GitHub secrets.

If SignPath Foundation accepts the project, this document will be updated with
the active SignPath project link and the final team memberships.
