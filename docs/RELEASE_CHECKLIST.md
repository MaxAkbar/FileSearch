# FileSearch Release Checklist

Use this checklist before creating a public FileSearch release or Microsoft Store submission.

## Before Packaging

- Confirm the working tree is clean except for intentional release changes.
- Confirm the version uses four integer parts for MSIX packaging, for example `1.2.3.0`.
- Run `dotnet restore .\FileSearch.slnx`.
- Run `dotnet build .\FileSearch.slnx --configuration Release`.
- Run `dotnet test .\FileSearch.slnx --configuration Release --no-build`.
- Smoke test the WPF app.
- Smoke test the CLI with `help`, `options`, `search`, and `index locations`.
- Review `README.md`, `README.Indexing.md`, and `README.Roadmap.md` for stale release notes.

## GitHub Actions

- Confirm the `CI` workflow is green for the target branch or pull request.
- Confirm dependency review is green for dependency-changing pull requests.
- Configure repository variables before using the signed Store package path in the release workflow:
  - `STORE_PACKAGE_IDENTITY_NAME`
  - `STORE_PUBLISHER`
  - `STORE_PUBLISHER_DISPLAY_NAME`
  - optional `WINDOWS_TIMESTAMP_URL`, defaulting to `http://timestamp.digicert.com`.
- Configure signing secrets in the protected `release-signing` environment for signed Store packages:
  - `WINDOWS_SIGNING_PFX_BASE64`
  - `WINDOWS_SIGNING_PFX_PASSWORD`
- Protect the `release-signing` environment with required reviewers before allowing public releases.
- Create release tags as `v<major>.<minor>.<patch>`, for example `v1.2.3`. The release workflow converts that to MSIX version `1.2.3.0`. Tags may also use four parts, such as `v1.2.3.4`.
- The **Release** workflow runs Release build/test gates, creates a portable ZIP, verifies sidecars, writes `SHA256SUMS-<runtime>.txt`, uploads artifacts, and creates a draft GitHub Release for tag pushes.
- When all Store variables and signing secrets are configured, the **Release** workflow also creates and verifies a signed Store package. When they are missing, it logs a warning and skips only the Store/MSIX artifacts.
- For Store packages, run the **Store package** workflow manually with the intended version, runtime, package identity, publisher, and publisher display name.
- Use one runtime per workflow run: `win-x64`, `win-x86`, or `win-arm64`.

## Signing

The manual Store package workflow supports optional signing through repository secrets:

- `WINDOWS_SIGNING_PFX_BASE64`: base64-encoded `.pfx` certificate.
- `WINDOWS_SIGNING_PFX_PASSWORD`: password for the `.pfx` certificate.

If these secrets are missing, the manual Store package workflow still creates unsigned package artifacts. Unsigned artifacts are useful for CI validation, but they are not ready for sideload distribution.

The Release workflow creates portable ZIP artifacts without signing secrets. Signed Store packages are produced only when the Store variables and signing secrets are configured. Signed release packages are timestamped through the configured RFC3161 timestamp URL.

## Artifact Review

- Download the workflow artifact named `filesearch-store-<version>-<runtime>`.
- For signed releases, download `filesearch-release-<version>-<runtime>` or the assets attached to the draft GitHub Release.
- Confirm it contains:
  - `.zip` portable app archive,
  - `SHA256SUMS*.txt`,
  - `.msix`,
  - `.appxsym` when symbols are available,
  - `.msixupload`.
- Store artifacts are present only when signed Store configuration was available.
- Confirm `SHA256SUMS*.txt` is present and matches the release assets.
- Confirm the packaged app includes `Help\index.html`.
- Confirm the packaged app includes `FileSearch.Indexer.exe`.
- Confirm the packaged app includes `FileSearch.ExtractorHost.exe`.
- Confirm the package identity and publisher match the Partner Center reservation.
- Confirm the `.msix` Authenticode signature is present and timestamped.

## Submission

- Submit the `.msixupload` file to Partner Center.
- Keep the workflow run URL with the release notes.
- Record whether the package was signed by GitHub Actions or later by Partner Center.
