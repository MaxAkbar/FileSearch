# Microsoft Store packaging

FileSearch is a WPF desktop app, so the repository supports both Store-friendly
MSI output and MSIX packaging. The MSI artifact is intended for the Microsoft
Store MSI/EXE submission path. The MSIX artifact remains available when a
Partner Center package identity is reserved.

## Create an MSI installer

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-MsiInstaller.ps1 `
  -Version 1.0.0.0 `
  -RuntimeIdentifier win-x64
```

The script writes outputs to `artifacts\msi`:

- `.msi` installer.
- `SHA256SUMS-<runtime>.txt`.

The MSI publishes the GUI, `FileSearch.Indexer.exe`, `FileSearch.ExtractorHost.exe`,
and `Help\index.html`, then packages them with WiX. Pass `-CertificateThumbprint`
to Authenticode-sign the MSI. For final Microsoft Store MSI/EXE submission, use
a signing certificate that chains to a Microsoft Trusted Root Program certificate
authority.

The GitHub Actions **Release** workflow always creates the MSI for tag releases.
If signing secrets are configured in the protected `release-signing` environment,
the workflow signs and timestamps the MSI before attaching it to the draft GitHub
Release.

## Create a Store upload package

The MSIX packaging script publishes the app self-contained, creates the MSIX
layout, generates required tile assets, and emits a Partner Center upload file.

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-StorePackage.ps1 `
  -Version 1.0.0.0 `
  -PackageIdentityName "MaxAkbar.WindowsFileSearch" `
  -Publisher "CN=CE11F335-A232-43CF-824D-292CFB1D1A12" `
  -DisplayName "Windows File Search" `
  -PublisherDisplayName "Max Akbar"
```

The script writes outputs to `artifacts\store`:

- `.msix` for the packaged app.
- `.appxsym` for public symbols.
- `.msixupload` for Microsoft Store submission.

The app help bundle is published from `src\FileSearch.Gui\Help` and is included
in the package root as `Help\index.html`. The packaging script also publishes
the companion `FileSearch.Indexer.exe` background worker and
`FileSearch.ExtractorHost.exe` out-of-process extractor host beside
`FileSearch.Gui.exe`; all three are verified before creating the MSIX.

For local sideload testing, sign the `.msix` by passing `-CertificateThumbprint`
with a certificate trusted on the test machine. The script timestamps signed
packages by default with `http://timestamp.digicert.com`; pass `-TimestampUrl`
to use a different RFC3161 timestamp server.

Use `scripts\Test-StorePackageArtifact.ps1` to verify package contents,
signature state, Store upload contents, and checksums.

For Microsoft Store submission, upload the `.msixupload` file generated with
the identity, publisher, and display name values reserved for the app in Partner Center. The
`DisplayName` value must exactly match a reserved app display name in Partner Center, and the
`PublisherDisplayName` value must exactly match the publisher display name shown
in Partner Center.

The MSIX manifest declares the restricted `runFullTrust` capability because
FileSearch is packaged as a WPF desktop app with `Windows.FullTrustApplication`
entry point and sidecar desktop processes. Microsoft Store submission may ask
for approval or justification for this restricted capability. Removing it would
require changing the app to an AppContainer-compatible architecture or using the
MSI/EXE Store submission path instead of the MSIX path.

The GitHub Actions **Release** workflow always creates a portable ZIP for version
tags and an MSI for the Store MSI/EXE path. It also creates signed MSIX Store
artifacts when the Store variables and signing secrets are configured in the
protected `release-signing` environment.
