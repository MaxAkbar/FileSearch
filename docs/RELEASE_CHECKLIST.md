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
- For Store packages, run the **Store package** workflow manually with the intended version, runtime, package identity, publisher, and publisher display name.
- Use one runtime per workflow run: `win-x64`, `win-x86`, or `win-arm64`.

## Signing

The Store package workflow supports optional signing through repository secrets:

- `WINDOWS_SIGNING_PFX_BASE64`: base64-encoded `.pfx` certificate.
- `WINDOWS_SIGNING_PFX_PASSWORD`: password for the `.pfx` certificate.

If these secrets are missing, the workflow still creates unsigned package artifacts. Unsigned artifacts are useful for CI validation, but they are not ready for sideload distribution.

## Artifact Review

- Download the workflow artifact named `filesearch-store-<version>-<runtime>`.
- Confirm it contains:
  - `.msix`,
  - `.appxsym` when symbols are available,
  - `.msixupload`.
- Confirm the packaged app includes `Help\index.html`.
- Confirm the package identity and publisher match the Partner Center reservation.

## Submission

- Submit the `.msixupload` file to Partner Center.
- Keep the workflow run URL with the release notes.
- Record whether the package was signed by GitHub Actions or later by Partner Center.
