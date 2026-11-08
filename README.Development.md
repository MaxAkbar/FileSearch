# Developing FileSearch

Build FileSearch from source, embed its .NET search engine, or prepare a release. For downloading and using the desktop app, start with the [main README](README.md). Published portable packages are self-contained; the .NET SDK is needed for source builds, not for running those packages.

[Build](#build-from-source) · [Repository](#repository-layout) · [Projects](#projects) · [.NET API](#core-library-usage) · [Packaging](#packaging-and-release)

## Source-build requirements

- Windows, for running the WPF desktop app.
- .NET 10 SDK (see `global.json` for SDK selection).
- PowerShell for packaging scripts; Node.js 18 or later only for Help search tests.

## Build from source

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

Run the desktop app from source:

```powershell
dotnet run --project .\src\FileSearch.Gui\FileSearch.Gui.csproj
```

Run tests:

```powershell
dotnet test --solution .\FileSearch.slnx
```

Create a portable release folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-PortableRelease.ps1 `
  -Version 1.11.0.0 `
  -RuntimeIdentifier win-x64
```

Run the release executables:

```powershell
.\artifacts\release\publish\win-x64\FileSearch.Gui.exe
.\artifacts\release\publish\win-x64\FileSearch.Cli.exe
```

## Repository layout

```text
.
├── FileSearch.slnx
├── src
│   ├── FileSearch.Core          # Search pipeline, extractors, queries, and indexes
│   ├── FileSearch.Gui           # WPF desktop app and bundled Help
│   ├── FileSearch.Cli           # Terminal and one-shot automation
│   ├── FileSearch.Indexer       # Per-user background index worker
│   ├── FileSearch.ExtractorHost # Isolated document extraction
│   ├── FileSearch.Mcp           # Read-only MCP server
│   ├── FileSearch.PowerToys     # Separately packaged optional companion
│   └── FileSearch.WindowsOcr    # Optional Windows OCR integration
├── tests                       # Core, GUI, MCP, PowerToys, and benchmark tests
├── benchmarks                  # Controlled benchmarks and native preview probes
├── installer                   # WiX MSI installer project
├── packaging                   # MSIX manifest template and packaging assets
├── docs                        # Release, benchmark, and supporting documentation
└── scripts                     # Build/package automation
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
- `ModernWpfUI` 1.x for modern Windows styling and .NET 10 Fluent control resources.

The app provides the main search form, result grid, match preview panel, result refinement, settings, themes, and Windows integration menu actions.

### `FileSearch.Cli`

Interactive REPL front end for the core search library. It targets `net10.0-windows` and uses:

- `Spectre.Console` for rich terminal prompts, status spinners, tables, panels, and highlighted match output.
- `Microsoft.Extensions.DependencyInjection` for the same core service registration used by the GUI.
- `FileSearch.WindowsOcr` so command-line searches can opt into the same Windows OCR extractors as the desktop app.

The CLI supports live search, covered indexed search, content/file/folder-name targets, document/OCR/known-type filters, one-shot CSV/JSON/JSON Lines/Markdown search output, structured one-shot index reporting, CSharpDB index build/clear/stats commands, workflow execution, background indexer control, and direct query entry from the prompt.

### `FileSearch.Core.Tests`

Unit tests for the search core, including query parsing/matching, file walking, extractor behavior, and search behavior.

### `FileSearch.Gui.Tests`

Unit tests for GUI-adjacent services such as file preview, startup folder resolution, and shell integration registration.

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

## Terminal and PowerShell automation

See the [CLI guide](README.Cli.md) for interactive commands, one-shot JSON/CSV output, and [PowerShell examples](README.Cli.md#powershell-usage).

## Development notes

- The repository uses nullable reference types and implicit usings.
- The solution file is `FileSearch.slnx`.
- The GUI project references the core project directly.
- Bundled Help search is indexed automatically from `Help/*.html` when building or publishing the GUI. Add content to those pages without maintaining a separate index. Run `node --test tests/HelpSearch/help-search.test.cjs` to check the search logic (Node.js 18 or later is needed only for these development tests).
- Tests use xUnit and `Microsoft.NET.Test.Sdk`.
- The core search pipeline is designed around dependency injection so extractors and options can be swapped or extended.
- Indexed search, tray-indexer lifecycle, USN catch-up, snapshot fallback, and index health behavior are documented in [README.Indexing.md](README.Indexing.md).
- Workflow search and the workflow JSON file format are documented in [README.Workflows.md](README.Workflows.md).
- The read-only MCP server for AI assistants (Claude Code, Claude Desktop, LM Studio), its tool surface, and client configuration are documented in [README.Mcp.md](README.Mcp.md). The wire-level reference for integrating it into AI apps and tools — tool schemas, error catalog, OpenAI Codex/VS Code/Cursor setup, and C#/Python client code including the OpenAI Agents SDK — is [README.McpIntegration.md](README.McpIntegration.md).
- Security and privacy posture are documented in [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).
- Reproduce controlled real-file search/database measurements and WPF preview/OCR checks with the [task baseline tools](benchmarks/FileSearch.Benchmarks/README.md#real-file-task-baseline). The [benchmark methodology](docs/BENCHMARKS.md#task-baseline-and-comparison-protocol) separates local component timings, held-out relevance, and installed-product comparisons.

## Packaging and release

MSI, portable ZIP, and Microsoft Store packaging instructions live in the [packaging guide](packaging/README.md). Follow the [release checklist](docs/RELEASE_CHECKLIST.md) when preparing a release. The [PowerToys companion](README.PowerToys.md) is built and packaged separately.

## Contributing

Contributions are welcome. A typical workflow is:

1. Create a feature branch.
2. Make your changes.
3. Run `dotnet build .\FileSearch.slnx`.
4. Run `dotnet test --solution .\FileSearch.slnx`.
5. Open a pull request with a clear description of the change.
