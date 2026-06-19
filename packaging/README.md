# Microsoft Store packaging

FileSearch is a WPF desktop app, so the Store distribution artifact is an MSIX package.
The repository includes a command-line packaging script that publishes the app self-contained,
creates the MSIX layout, generates required tile assets, and emits a Partner Center upload file.

## Create a Store upload package

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-StorePackage.ps1 `
  -Version 1.0.0.0 `
  -PackageIdentityName "<Partner Center package identity name>" `
  -Publisher "<Partner Center publisher, for example CN=...>" `
  -PublisherDisplayName "<Publisher display name>"
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
the identity and publisher values reserved for the app in Partner Center. The
GitHub Actions **Release** workflow always creates a portable ZIP for version
tags. It also creates signed Store artifacts when the Store variables and
signing secrets are configured in the protected `release-signing` environment.
