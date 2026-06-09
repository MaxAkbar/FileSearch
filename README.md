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
- Refine/filter results in memory without rescanning files.
- Optional CSharpDB-backed indexing for faster repeat searches across multiple watched locations.
- Open matched files, reveal them in Explorer, and copy file or folder paths.
- Light, dark, system, and Visual Studio-inspired themes.
- Optional Windows shell integration from the app menu.
- MSIX packaging script for Microsoft Store submission.

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

The CLI supports live search, covered indexed search, query mode switching, file filters, CSharpDB index build/clear/stats commands, and direct query entry from the prompt.

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

The CLI opens as a REPL. Type `help` to see available commands, or type a query directly to search the current folder.

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

The CLI uses the same `FileSearch.Core` search pipeline and CSharpDB index database as the desktop app. Indexed search is opt-in with `index on`; when the current search is not covered by the index, the CLI falls back to live scan.

## Indexed search

Use **Index > Manage indexed locations...** to opt folders into background indexing. The app watches indexed locations while it is open, batches file changes, and updates the local CSharpDB index without blocking searches. **Use index** controls whether a search may use covered indexed locations; live scan remains the fallback when the index is missing, stale, or does not cover the current options.

Background indexing can be paused or resumed from the **Index** menu. Details are documented in [README.Indexing.md](README.Indexing.md).

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

Directly loading `FileSearch.Core` from PowerShell is possible, but it is not the recommended path because PowerShell has to resolve the full dependency graph and consume async streams. For automation, use the CLI. A future PowerShell module would mainly be useful if we want structured objects, JSON output, pipeline input, or native commands such as `Search-FileContent` and `Update-FileSearchIndex`.

## Packaging for Microsoft Store

FileSearch is packaged as an MSIX for Store distribution. The repository includes a PowerShell packaging script that publishes the app self-contained, creates the MSIX layout, generates tile assets, and emits a Partner Center upload file.

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-StorePackage.ps1 `
  -Version 1.0.0.0 `
  -PackageIdentityName "<Partner Center package identity name>" `
  -Publisher "<Partner Center publisher, for example CN=...>" `
  -PublisherDisplayName "<Publisher display name>"
```

Outputs are written to `artifacts\store`:

- `.msix` packaged app.
- `.appxsym` public symbols.
- `.msixupload` file for Microsoft Store submission.

For sideload testing, pass `-CertificateThumbprint` with a certificate trusted on the test machine. For Microsoft Store submission, upload the generated `.msixupload` file using the reserved identity and publisher values for the app in Partner Center.

## Development notes

- The repository uses nullable reference types and implicit usings.
- The solution file is `FileSearch.slnx`.
- The GUI project references the core project directly.
- Tests use xUnit and `Microsoft.NET.Test.Sdk`.
- The core search pipeline is designed around dependency injection so extractors and options can be swapped or extended.
- Indexed search and background watcher behavior are documented in [README.Indexing.md](README.Indexing.md).

## Contributing

Contributions are welcome. A typical workflow is:

1. Create a feature branch.
2. Make your changes.
3. Run `dotnet build .\FileSearch.slnx`.
4. Run `dotnet test .\FileSearch.slnx`.
5. Open a pull request with a clear description of the change.

## License

FileSearch is licensed under the MIT License. See [LICENSE](LICENSE) for details.
