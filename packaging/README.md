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
in the package root as `Help\index.html`. The packaging script verifies this
file exists before creating the MSIX.

For local sideload testing, sign the `.msix` by passing `-CertificateThumbprint`
with a certificate trusted on the test machine. For Microsoft Store submission,
upload the `.msixupload` file generated with the identity and publisher values
reserved for the app in Partner Center.
