# Optional PowerToys integration

For the bundled user guide, see [PowerToys Integration](src/FileSearch.Gui/Help/powertoys.html)
in the app's Help pages.

FileSearch for PowerToys is a **separate, optional companion** for PowerToys Command
Palette. The FileSearch GUI, CLI, MSI, portable ZIP, and main MSIX do not include or
load the PowerToys SDK. They do not detect PowerToys at startup, prompt to install
it, register extra hotkeys, or start an integration process. Installing or removing
PowerToys does not change normal FileSearch operation.

## Commands

After installing the companion, open Command Palette and choose:

- **FileSearch: file names** for literal filename search.
- **FileSearch: file contents** for indexed document text, including existing OCR
  text. Unified queries such as `type:pdf invoice` are supported.

Results include paths and matching text. Enter opens the file. The result's action
menu offers **Open in FileSearch**, **Show in File Explorer**, **Copy path**, and,
for content searches, **Copy matching text**. Open in FileSearch transfers the
query, indexed folder, search target, and selected file to the desktop app,
including when the app is already running.

The companion searches existing, compatible **folder indexes**. It does not scan
the disk, build or refresh indexes, start watchers, download models, or enable OCR.
Drive-wide MFT filename snapshots and semantic search are not used by this first
version. Each query is debounced, cancelled when typing continues, limited to 50
distinct files, and given a three-second deadline. Incompatible or incomplete
indexes are skipped; refresh them in FileSearch. No indexed folders or unavailable
indexes produce an explanatory empty state inside Command Palette.

## Install and configure

Use Windows 11 with PowerToys Command Palette enabled. Install FileSearch 1.10.0 or later normally,
then install the separately signed `FileSearch.PowerToys_*.msix` companion. Reload
Command Palette extensions if the commands do not appear immediately.

Releases without signing credentials provide an unsigned MSIX and a separate
`FileSearch.PowerToys_*-development.zip` for local testing. The unsigned MSIX
requires signing with a trusted certificate before normal installation. If
Developer Mode is already enabled, extract the development ZIP to a permanent
folder and run `Add-AppxPackage -Register 'C:\YourPermanentFolder\AppxManifest.xml'`
from PowerShell. Keep the extracted folder in place until the companion is
uninstalled. The ZIP includes these instructions and does not change Windows
security settings.

Standard MSI installations under `Program Files\FileSearch` are detected when an
Open in FileSearch action is invoked. For a portable, custom, or main MSIX
installation, set **FileSearch application** in the extension's settings to the
full path of `FileSearch.Gui.exe`. Settings belong only to the companion and are
saved under `%LOCALAPPDATA%\FileSearch\PowerToys\settings.json`.

The default index is `%LOCALAPPDATA%\FileSearch\Index\filesearch.db`. The existing
`FILESEARCH_INDEX_DATABASE_PATH` environment override is respected. The companion
also offers a **Custom FileSearch index** setting for installations using another
database file; reload the extension after changing it. The companion
only uses the index's read interfaces and preserves each indexed location's
extension, directory, recursion, hidden-file, and OCR filters.

Uninstall **FileSearch for PowerToys** from Windows Installed apps to remove the
integration. FileSearch and its settings and indexes remain available.

## Build the optional package

```powershell
.\scripts\New-PowerToysPackage.ps1 -RuntimeIdentifier win-x64 -Version 1.11.4.0
```

The script requires the .NET 10 SDK and Windows SDK packaging tools, but does not
require PowerToys. It creates a self-contained MSIX and checksum under
`artifacts\powertoys`; unsigned builds also include the development ZIP. Checksums
are in `SHA256SUMS-PowerToys-<runtime>.txt`. Pass `-CertificateThumbprint` for signing; the certificate
must be trusted by the target computer and match `-Publisher`. ARM64 can be built
with `-RuntimeIdentifier win-arm64`.

Verify the result with `scripts\Test-PowerToysPackageArtifact.ps1`. Tag releases
build and verify the companion for x64 or ARM64 as a separate release download;
the normal installers do not include it. When signing credentials are configured,
the workflow signs and timestamps the companion using that certificate's publisher.

For local development with Developer Mode enabled, register the staged package:

```powershell
Add-AppxPackage -Register .\artifacts\powertoys\package\win-x64\AppxManifest.xml
```

This registration is opt-in and is never performed by the build or the main app.

## Desktop query handoff

The desktop app also accepts these arguments independently of PowerToys:

```powershell
.\FileSearch.Gui.exe --query 'type:pdf invoice' --path 'C:\Documents' --target content
.\FileSearch.Gui.exe --query 'report' --path 'C:\Documents' --target files --select 'C:\Documents\report.txt'
```

Existing positional-folder and `--background` launches continue to work. An
explicit query brings the app forward. A running search or replacement is not
interrupted by a second query handoff.
