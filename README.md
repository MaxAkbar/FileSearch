# FileSearch

FileSearch is a Windows desktop application for searching text across files and folders. It combines a reusable .NET search engine with a WPF user interface, document text extractors, query parsing, and packaging support for Microsoft Store distribution.

## Features

- Search for text in files from a selected folder.
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

WPF front end for the core search library. It targets `net9.0-windows` and uses:

- `CommunityToolkit.Mvvm` for MVVM helpers.
- `Microsoft.Extensions.Hosting` and dependency injection for app composition.
- `ModernWpfUI` for modern Windows styling.

The app provides the main search form, result grid, match preview panel, result refinement, settings, themes, and Windows integration menu actions.

### `FileSearch.Core.Tests`

Unit tests for the search core, including query parsing/matching, file walking, extractor behavior, and search behavior.

### `FileSearch.Gui.Tests`

Unit tests for GUI-adjacent services such as file preview, startup folder resolution, and shell integration registration.

## Requirements

- Windows, for running the WPF desktop app.
- .NET 9 SDK.
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

You can register additional `ITextExtractor` implementations before resolving `ISearcher` if you need support for custom file types.

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

## Contributing

Contributions are welcome. A typical workflow is:

1. Create a feature branch.
2. Make your changes.
3. Run `dotnet build .\FileSearch.slnx`.
4. Run `dotnet test .\FileSearch.slnx`.
5. Open a pull request with a clear description of the change.

## License

FileSearch is licensed under the MIT License. See [LICENSE](LICENSE) for details.
