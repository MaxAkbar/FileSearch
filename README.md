# FileSearch

FileSearch is a Windows desktop application for searching text across files and folders. It combines a reusable .NET search engine with a WPF user interface, document text extractors, query parsing, and packaging support for Microsoft Store distribution.

## Features

- Search for text in files from a selected folder.
- Use either the WPF desktop app or the Spectre.Console-powered interactive CLI.
- Include or exclude subfolders.
- Filter by file name glob patterns such as `*.cs;*.txt`.
- Choose query modes:
  - **Plain text**: treats the whole search box as one literal substring.
  - **Regular expression**: treats the whole search box as a regex.
  - **Boolean**: supports Boolean-style expressions with `AND`, `OR`, `NOT`, and parentheses.
- Optional case-sensitive matching.
- Filter by file size and modified date range.
- Preview matching lines and context for selected results.
- Refine, facet, sort, group, favorite, pin, drag, export, and save or share workspace bundles, with optional workspace run-on-load.
- Optional CSharpDB-backed indexing for faster repeat searches across multiple locations, with GUI or tray-indexer background updates.
- Open matched files, reveal them in Explorer, copy file or folder paths, rename files, or move files to the Recycle Bin.
- Configure the Quick Search global hotkey, Quick Search action shortcuts, and main-window keyboard shortcuts.
- Light, dark, system, and Visual Studio-inspired themes.
- Optional Windows shell integration from the app menu.
- MSI and MSIX packaging scripts for Microsoft Store submission.

## Supported file types

The core project includes extractors for many common text and document formats, including:

- Plain text and unknown extensions via a plain-text fallback.
- PDF files.
- Microsoft Word documents.
- Microsoft Excel workbooks.
- Microsoft PowerPoint presentations.
- OpenDocument files.
- EPUB files.
- RTF files.
- HTML/XML-style text files.
- Email/calendar/contact-style files.
- ZIP-based formats.

Document extraction can be disabled in the UI for Office and PDF documents when you only want to scan simpler text files.

## Repository layout

```text
.
├── FileSearch.slnx
├── src
│   ├── FileSearch.Core
│   │   ├── Engine       # Search request/options/result models and search execution
│   │   ├── Extractors   # File text extraction by extension/content type
│   │   ├── Queries      # Query parsing and matching
│   │   └── Walker       # File traversal and filtering
│   ├── FileSearch.Cli   # Interactive Spectre.Console REPL
│   └── FileSearch.Gui   # WPF desktop application
├── tests
│   ├── FileSearch.Core.Tests
│   └── FileSearch.Gui.Tests
├── installer           # WiX MSI installer project
├── packaging           # MSIX manifest template and packaging assets
└── scripts             # Build/package automation scripts
```

## Projects

### `FileSearch.Core`

Reusable .NET library that contains the search pipeline:

1. Walk the selected directory tree.
2. Apply include/exclude, size, date, hidden-file, and recursion filters.
3. Select the best text extractor for each file.
4. Parse the query into the selected query mode.
5. Stream matching hits back to the caller.

Key namespaces/folders:

- `Engine`: `ISearcher`, `Searcher`, `SearchRequest`, `SearchOptions`, and hit/result models.
- `Walker`: `IFileWalker`, `FileWalker`, and `WalkerOptions`.
- `Queries`: plain text, regex, and Boolean query parsing/matching.
- `Extractors`: `ITextExtractor` implementations and the extractor registry.

### `FileSearch.Gui`

WPF front end for the core search library. It targets `net10.0-windows` and uses:

- `CommunityToolkit.Mvvm` for MVVM helpers.
- `Microsoft.Extensions.Hosting` and dependency injection for app composition.
- `ModernWpfUI` for modern Windows styling.

The app provides the main search form, result grid, match preview panel, result refinement, settings, themes, and Windows integration menu actions.

### `FileSearch.Cli`

Interactive REPL front end for the core search library. It targets `net10.0` and uses:

- `Spectre.Console` for rich terminal prompts, status spinners, tables, panels, and highlighted match output.
- `Microsoft.Extensions.DependencyInjection` for the same core service registration used by the GUI.

The CLI supports live search, covered indexed search, query mode switching, file filters, one-shot CSV/JSON/JSON Lines/Markdown search output, structured one-shot index reporting, CSharpDB index build/clear/stats commands, and direct query entry from the prompt.

### `FileSearch.Core.Tests`

Unit tests for the search core, including query parsing/matching, file walking, extractor behavior, and search behavior.

### `FileSearch.Gui.Tests`

Unit tests for GUI-adjacent services such as file preview, startup folder resolution, and shell integration registration.

## Requirements

- Windows, for running the WPF desktop app.
- .NET 10 SDK.
- PowerShell, for the packaging script.

## Getting started

Clone the repository:

```powershell
git clone https://github.com/MaxAkbar/FileSearch.git
cd FileSearch
```

Restore dependencies:

```powershell
dotnet restore .\FileSearch.slnx
```

Build the solution:

```powershell
dotnet build .\FileSearch.slnx
```

Run the WPF app:

```powershell
dotnet run --project .\src\FileSearch.Gui\FileSearch.Gui.csproj
```

Run the interactive CLI:

```powershell
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj
```

Run tests:

```powershell
dotnet test .\FileSearch.slnx
```

## Using the app

1. Enter the text or expression to find in **Containing text**.
2. Optionally enter one or more file name patterns in **File name pattern**. Separate multiple globs with semicolons, for example:

   ```text
   *.cs;*.xaml;*.md
   ```

3. Choose a folder in **Look in**.
4. Decide whether to include subfolders.
5. Adjust options:
   - Search mode: plain text, regex, or Boolean.
   - Match case.
   - Enable or disable Office/PDF document extraction.
   - Minimum and maximum file size.
   - Modified date filters.
6. Click **Start** or press Enter.
7. Select a result to preview matching lines.
8. Use the **Filter** tab to narrow the result set without rescanning.

## Using the CLI

Run the CLI without arguments to open the REPL. Type `help` to see available commands, or type a query directly to search the current folder.

Useful commands:

```text
path C:\src\project
mode plain
mode regex
mode boolean
case on
recursive off
include *.cs;*.xaml
exclude bin;obj
ext .cs,.md
limit 25
search connection string
index build
index on
index stats
index locations
exit
```

For automation, use the one-shot search command. Results stream to stdout unless `--output` is provided:

```powershell
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- search "connection string" --path C:\src --mode plain --json
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- search needle --path C:\src --csv --output .\results.csv
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- search needle --path C:\src --jsonl --limit 500
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- search needle --path C:\src --markdown --index
```

One-shot search accepts the same common filters as the REPL, including `--include`, `--exclude`, `--ext`, `--exclude-ext`, `--exclude-dir`, `--min-size`, `--max-size`, `--after`, `--before`, `--recursive`, `--no-recursive`, `--hidden`, `--case`, `--index`, and `--no-index`.

Index maintenance and reporting also have one-shot structured output. Use these commands when scripts need to build or clear an index, inspect configured roots, inspect per-root stats, or list failed extraction details without parsing the interactive tables:

```powershell
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index build C:\src --json
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index rebuild C:\src --csv --output .\index-rebuild.csv
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index clear C:\src --json
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index locations --json
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index stats C:\src --csv
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index failures --jsonl --output .\index-failures.jsonl
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index locations --markdown --output .\index-locations.md
```

`index build [FOLDER]`, `index rebuild [FOLDER]`, `index clear [FOLDER]`, `index locations`, `index stats [FOLDER]`, and `index failures` support `--json`, `--jsonl`, `--csv`, `--markdown`, `--format`, and `--output`. `index build` and `index rebuild` also accept the common traversal filters such as `--include`, `--exclude`, `--ext`, `--exclude-ext`, `--exclude-dir`, `--min-size`, `--max-size`, `--after`, `--before`, `--recursive`, `--no-recursive`, and `--hidden`. If no folder is passed to folder-specific index commands, the current working directory is used.

The CLI uses the same `FileSearch.Core` search pipeline and CSharpDB index database as the desktop app. Indexed search is opt-in with `index on`; when the current search is not covered by the index, the CLI falls back to live scan.

## Indexed search

Use **Index > Manage indexed locations...** to opt folders into background indexing. FileSearch watches indexed locations while either the GUI or the per-user tray indexer is running, batches file changes, and updates the local CSharpDB index without blocking searches. **Use index** controls whether a search may use covered indexed locations; live scan remains the fallback when the index is missing, stale, or does not cover the current options.

The Settings window can keep the tray indexer running after the search window closes and can register it to start after Windows sign-in. Local NTFS roots can catch up from USN journal checkpoints after restart; ReFS, network, cloud, removable, and unsupported roots use snapshot validation. Background indexing can be paused or resumed from the **Index** menu. Details are documented in [README.Indexing.md](README.Indexing.md).

## Query modes

### Plain text

Searches for the exact text entered as a literal substring.

Example:

```text
connection string
```

### Regular expression

Treats the entire query as a .NET regular expression.

Example:

```text
TODO|FIXME
```

### Boolean

Parses the query with Boolean operators and parentheses.

Examples:

```text
error AND timeout
```

```text
(invoice OR receipt) AND NOT draft
```

## Core library usage

`FileSearch.Core` can be used independently from the WPF app. Register the core services with dependency injection:

```csharp
using FileSearch.Core;
using FileSearch.Core.Engine;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddFileSearchCore();

var provider = services.BuildServiceProvider();
var searcher = provider.GetRequiredService<ISearcher>();
```

### Basic live search

```csharp
using FileSearch.Core;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddFileSearchCore();

await using var provider = services.BuildServiceProvider();
var searcher = provider.GetRequiredService<ISearcher>();
var queryFactory = provider.GetRequiredService<IQueryFactory>();

var root = @"C:\src\project";
var query = queryFactory.Build(
    "connection string",
    QueryMode.PlainText,
    caseSensitive: false);

var request = new SearchRequest(
    query,
    [root],
    new WalkerOptions
    {
        Recursive = true,
        IncludeHidden = false,
        IncludeGlobs = ["*.cs", "*.json"],
        ExcludeGlobs = ["bin", "obj"],
        MaxFileSizeBytes = 25L * 1024 * 1024,
    },
    Progress: progress =>
    {
        Console.WriteLine(
            $"Processed {progress.FilesProcessed:n0}/{progress.FilesEnumerated:n0} files");
    });

await foreach (var hit in searcher.SearchAsync(request, CancellationToken.None))
{
    Console.WriteLine($"{hit.Path}:{hit.LineNumber}: {hit.LineContent}");
}
```

### Regex or Boolean search

```csharp
var regexQuery = queryFactory.Build(
    "TODO|FIXME",
    QueryMode.Regex,
    caseSensitive: false);

var booleanQuery = queryFactory.Build(
    "(invoice OR receipt) AND NOT draft",
    QueryMode.Boolean,
    caseSensitive: false);
```

### Indexed search

```csharp
using FileSearch.Core.Indexing;

var index = provider.GetRequiredService<IFileIndex>();
var root = @"C:\src\project";
var walkerOptions = new WalkerOptions
{
    Recursive = true,
    IncludeHidden = false,
    IncludeGlobs = ["*.cs", "*.xaml", "*.md"],
};

await index.RefreshRootAsync(
    new IndexRequest(
        root,
        walkerOptions,
        Progress: progress =>
        {
            Console.WriteLine(
                $"Indexed {progress.FilesIndexed:n0}, skipped {progress.FilesSkippedUnchanged:n0}, lines {progress.LinesIndexed:n0}");
        }),
    IndexRefreshMode.Incremental,
    CancellationToken.None);

var query = queryFactory.Build("FileSearch", QueryMode.PlainText, caseSensitive: false);
var request = new SearchRequest(
    query,
    [root],
    walkerOptions,
    UseIndex: true,
    Status: Console.WriteLine);

await foreach (var hit in searcher.SearchAsync(request, CancellationToken.None))
{
    Console.WriteLine($"{hit.Path}:{hit.LineNumber}: {hit.LineContent}");
}
```

`UseIndex: true` is safe to use with the registered `ISearcher`. The coordinating searcher uses the CSharpDB index when the current root and filters are covered, and falls back to live scan when the index is missing or incompatible.

### Custom extractors

You can register additional `ITextExtractor` implementations before resolving `ISearcher` if you need support for custom file types.

```csharp
using FileSearch.Core.Extractors;

services.AddSingleton<ITextExtractor, MyCustomExtractor>();
services.AddFileSearchCore();
```

## PowerShell usage

No PowerShell extension is required for normal use. The CLI can be launched directly from PowerShell:

```powershell
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj
```

PowerShell can also drive the REPL non-interactively by piping commands:

```powershell
@(
  'path C:\src\project'
  'mode boolean'
  'case off'
  'limit 25'
  'search error AND timeout'
  'exit'
) | dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj
```

For structured search automation, prefer one-shot output:

```powershell
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- search TODO --path C:\src\project --json |
  ConvertFrom-Json
```

For structured index automation, call the one-shot index commands directly:

```powershell
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index build C:\src\project --json |
  ConvertFrom-Json
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index locations --json |
  ConvertFrom-Json
dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj -- index failures --csv --output .\failures.csv
```

Index maintenance works the same way:

```powershell
@(
  'path C:\src\project'
  'index build'
  'index on'
  'search TODO'
  'exit'
) | dotnet run --project .\src\FileSearch.Cli\FileSearch.Cli.csproj
```

Directly loading `FileSearch.Core` from PowerShell is possible, but it is not the recommended path because PowerShell has to resolve the full dependency graph and consume async streams. For automation, use the CLI. A future PowerShell module would mainly be useful for pipeline-native commands such as `Search-FileContent` and `Update-FileSearchIndex`.

## Packaging for Microsoft Store

FileSearch can produce both MSI and MSIX artifacts for Store distribution. The MSI path is useful for the Microsoft Store MSI/EXE submission flow; the MSIX path remains available when a Partner Center package identity is reserved.

CI builds, tests, CLI smoke tests, published sidecar smoke tests, dependency review, manual Store packaging, portable tag-based releases, MSI artifacts, and optional signed Store artifacts are configured with GitHub Actions. Release steps are documented in [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md).

Create an MSI installer from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-MsiInstaller.ps1 `
  -Version 1.0.0.0 `
  -RuntimeIdentifier win-x64
```

Outputs are written to `artifacts\msi`:

- `.msi` installer containing the GUI, background indexer, extractor host, and help bundle.
- `SHA256SUMS-<runtime>.txt` for checksum review.

For Store submission through the MSI/EXE path, sign the MSI with a certificate that chains to a Microsoft Trusted Root Program certificate authority. For local testing, an unsigned MSI can still validate the installer layout.

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-StorePackage.ps1 `
  -Version 1.0.0.0 `
  -PackageIdentityName "MaxAkbar.WindowsFileSearch" `
  -Publisher "CN=CE11F335-A232-43CF-824D-292CFB1D1A12" `
  -DisplayName "Windows File Search" `
  -PublisherDisplayName "Max Akbar"
```

MSIX outputs are written to `artifacts\store`:

- `.msix` packaged app.
- `.appxsym` public symbols.
- `.msixupload` file for Microsoft Store submission.

For sideload testing, pass `-CertificateThumbprint` with a certificate trusted on the test machine. For GitHub releases, the **Release** workflow creates a portable ZIP and MSI even when Store signing is not configured. When Windows signing secrets exist, it signs the MSI. When Store variables and signing secrets exist in the protected `release-signing` environment, the same workflow also creates signed MSIX Store artifacts. For MSIX Store submission, upload the generated `.msixupload` file using the reserved identity, publisher, and display name values for the app in Partner Center, and make sure `PublisherDisplayName` exactly matches Partner Center.

## Development notes

- The repository uses nullable reference types and implicit usings.
- The solution file is `FileSearch.slnx`.
- The GUI project references the core project directly.
- Tests use xUnit and `Microsoft.NET.Test.Sdk`.
- The core search pipeline is designed around dependency injection so extractors and options can be swapped or extended.
- Indexed search, tray-indexer lifecycle, USN catch-up, snapshot fallback, and index health behavior are documented in [README.Indexing.md](README.Indexing.md).
- Workflow search and the workflow JSON file format are documented in [README.Workflows.md](README.Workflows.md).
- The competitive roadmap refresh is documented in [README.Roadmap.md](README.Roadmap.md).
- Security and privacy posture are documented in [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).

## Recent changes

Work landed from `bbd4b73` (*Add background indexed search*, June 2026) to the current head, grouped by area.

### Search engine and indexing

- **Background indexed search** (`bbd4b73`): CSharpDB-backed file index with background refresh, coverage checks, and live-scan fallback — the baseline for the changes below. Documented in [README.Indexing.md](README.Indexing.md).
- **Indexing correctness, threading, and performance overhaul** (`13b1f9c`): removed the watcher feedback loop that caused double indexing, skipped upserts for unchanged files, ran schema setup once per process, and serialized cross-process writers with a lock file. Heavy parsing and preview extraction moved off the UI thread, UTF-16/32 files are detected correctly, overlapping indexed roots are supported, and search-time filters no longer shrink index coverage. Performance work included directory-exclude pruning, cached per-file verdicts, batched UI updates, and throttled progress callbacks, plus centralized file logging and safer settings management.
- **Index storage layering** (`1b08535`): split the index implementation into `IndexDatabase` (schema, versioning, write exclusion) and `IndexTables` (DML), extracted a tested `SearchHistory` MRU helper, and moved folder picking and UI dispatch behind injectable services.
- **Worker and queue fixes** (`2317524`): queue priority, worker edge cases, and small races found in review.
- **Compiler-enforced SQL escaping** (`6778331`): index queries are built through an interpolated-string handler (`Sql.Format`), so a value that bypasses escaping no longer compiles.
- **Workflow search** (unreleased): saved multi-step searches with if/else conditions on hit or file counts, retry and for-each loops, sub-searches over previous results, JSON/CSV/Markdown export, and confirmed copy/move/run-program actions — stored as hand-editable JSON files, one per workflow. The file format is documented in [README.Workflows.md](README.Workflows.md).

### Desktop app

- **Indexed locations management** (`0d94748`): dedicated window for per-location options (subfolders, hidden files, document extraction, change watching) with live status badges and queue counts in the sidebar.
- **Shutdown deadlock fix** (`5a1fa5c`): the app could hang forever on exit while stopping background indexing.
- **View-model split** (`41f59e7`): `MainViewModel` split into focused `Search`, `Index`, `History`, and `StatusBar` view models, backed by a binding-path audit test that verifies every XAML binding resolves.
- **Sidebar scrolling** (`13402db`): the collapsible sections (Scopes, Recent folders, Indexed locations, Saved searches) scroll as one surface when they outgrow the window instead of pushing the lower sections out of view, and the nav lists no longer swallow mouse-wheel events.

### Command line

- **Interactive search REPL** (`b9c2ec5`): Spectre.Console front end with live and indexed search, query-mode/case/filter/limit commands, and index build/clear/stats — sharing the same core pipeline and index database as the GUI.

### Extractors

- **Hardening pass** (`362b18e`): zip-bomb entry and size caps, bounded file reads, charset-correct EML decoding, and RTF/markup fixes.

### CI, tests, and release

- **CI and release foundation** (`a8e8965`): GitHub Actions build/test workflows, dependency review, Store packaging workflow, Dependabot, `SECURITY.md`, `PRIVACY.md`, the release checklist, and the competitive roadmap.
- **Coverage and pipeline hardening** (`71a616d`): new searcher, indexed-searcher, and indexing-service test suites and a stricter CI pipeline.

## Contributing

Contributions are welcome. A typical workflow is:

1. Create a feature branch.
2. Make your changes.
3. Run `dotnet build .\FileSearch.slnx`.
4. Run `dotnet test .\FileSearch.slnx`.
5. Open a pull request with a clear description of the change.

## License

FileSearch is licensed under the MIT License. See [LICENSE](LICENSE) for details.
