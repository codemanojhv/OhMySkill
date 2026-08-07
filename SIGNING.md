# Trusted Windows signing

Oh My Skill releases are built on GitHub and signed through Microsoft Artifact Signing. The workflow refuses to create a release unless both EXEs have a valid Authenticode signature, an RFC 3161 timestamp, and the correct PE architecture.

## One-time Microsoft setup

1. In an Azure subscription, create an Artifact Signing account using the Basic SKU.
2. Complete **Public Trust** identity validation for the legal publisher.
3. Create a **Public Trust** certificate profile.
4. Create a Microsoft Entra application or managed identity for GitHub Actions.
5. Give that identity the **Artifact Signing Certificate Profile Signer** role on the certificate profile.
6. Add a federated credential for this repository and the `main` branch:

   ```text
   repo:codemanojhv/OhMySkill:ref:refs/heads/main
   ```

No certificate private key is placed in GitHub. GitHub uses a short-lived OIDC token to authenticate to Azure for each release.

## GitHub configuration

Add these repository secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Add these repository variables:

- `ARTIFACT_SIGNING_ENDPOINT`, such as `https://eus.codesigning.azure.net/`
- `ARTIFACT_SIGNING_ACCOUNT`
- `ARTIFACT_SIGNING_PROFILE`

Do not put these values in source files. The account and profile are not private keys, but keeping deployment configuration in repository variables avoids hard-coding the release environment.

## Create a signed release

1. Merge the release commit into `main`.
2. Open **Actions → Signed Windows release → Run workflow**.
3. Select `main`, enter a semantic version such as `v0.3.0`, and choose whether it is a prerelease.
4. Download only the EXEs attached by the completed workflow.

The workflow builds and tests the application, publishes x64 and ARM64 binaries, signs and timestamps them, rejects invalid output, generates SHA-256 checksums, and then creates the GitHub release.

## Verify a downloaded EXE

Run:

```powershell
$signature = Get-AuthenticodeSignature .\OhMySkill-win-x64.exe
$signature | Format-List Status,StatusMessage
$signature.SignerCertificate | Format-List Subject,Issuer,NotBefore,NotAfter,Thumbprint
$signature.TimeStamperCertificate | Format-List Subject,NotBefore,NotAfter
```

`Status` must be `Valid`, the publisher must match the validated Artifact Signing identity, and a timestamp certificate must be present. Compare the file hash with `SHA256SUMS.txt` after downloading.

## SmartScreen expectations

A trusted signature fixes the **Unknown publisher** problem and allows publisher reputation to carry across signed releases. It does not guarantee that a brand-new publisher or binary immediately avoids every SmartScreen reputation prompt. Microsoft currently identifies Microsoft Store distribution as the only path that reliably avoids first-download SmartScreen warnings; public Authenticode releases accumulate reputation over time.

Self-signed certificates are not used for public releases because Windows treats them like unsigned files unless each destination machine explicitly trusts the private certificate.
