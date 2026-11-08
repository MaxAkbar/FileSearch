# FileSearch

FileSearch is a Windows desktop application for searching text across files and folders. It combines a reusable .NET search engine with a WPF user interface, document text extractors, query parsing, and packaging support for Microsoft Store distribution.

## Features

- Search for text, file names, folder names, or file and folder names from a selected folder, selected indexed locations, or all registered indexed locations.
- Use either the WPF desktop app or the Spectre.Console-powered interactive CLI.
- Include or exclude subfolders.
- Filter by file name glob patterns such as `*.cs;*.txt`.
- Choose query modes:
  - **Unified** (GUI default): combines content terms with structured fields such as `type:`, `name:`, and `modified:`.
  - **Plain text**: treats the whole search box as one literal substring.
  - **Regular expression**: treats the whole search box as a regex.
  - **Semantic** (desktop): treats the whole search box as a concept to find in the local Smart Search index; select the **Semantic** toggle next to `.*` without typing a query prefix. Adjust **Minimum score** and **Maximum files** to filter weaker matches and limit the result count.
  - **Boolean**: supports Boolean-style expressions with `AND`, `OR`, `NOT`, and parentheses.
- Optional case-sensitive matching.
- Filter by file size and modified date range.
- Preview matching lines and context for selected results.
- Use File preview for whole PDF pages, spreadsheet grids, ZIP members, complete selected messages, EPUB chapters, OpenDocument/RTF content, Markdown, source code, and images including WebP, animated GIF, and multipage TIFF. These viewers are shared with Quick Search and require no PowerToys installation.
- Find and highlight literal phrases in loaded text, source code, formatted documents, and embedded text on the displayed PDF page, with next/previous navigation shared by the main window, Quick Search, and expanded previews.
- Preview and apply find-and-replace batches across text files, Word/Excel/PowerPoint files, or file and folder names, with backups and Undo.
- Filter current results by exact text in names, paths, or returned match excerpts, with highlights that preserve the original search matches. Facet, sort, group, favorite, pin, drag, export, and save or share workspace bundles, with optional workspace run-on-load.
- Optional CSharpDB-backed indexing for faster repeat searches across multiple locations, with GUI or tray-indexer background updates.
- Optional local ONNX Smart Search over indexed vectors; see the [setup and search walkthrough](src/FileSearch.Gui/Help/smart-search.html) for model packs, the Semantic toggle, advanced `semantic:` queries, and current limitations.
- Smart Search applies indexed file eligibility before capped retrieval, including folder, type/date/size, visibility, and filename filters. Individual PST/OST messages retain separate result identities, and database/archive source locations survive result fusion.
- Optional whole-drive file-name index for NTFS drives, built from the master file table and kept live by the change journal, for instant name search across millions of files in Quick Search, indexed name searches, and the CLI.
- Local Outlook MSG/PST/OST content search with individual message results, previews, and folder indexing; see [Mail search](README.Mail.md).
- Read-only SQLite and CSharpDB content search with table/row/column locations and a paged matching-record preview in both windows; see [Database search](README.Databases.md).
- Open matched files, reveal them in Explorer, copy file or folder paths, rename files, or move files to the Recycle Bin.
- Configure the Quick Search global hotkey, Quick Search action shortcuts, and main-window keyboard shortcuts.
- Light, dark, system, and Visual Studio-inspired themes.
- Optional Windows shell integration from the app menu.
- Optional, separately installed [PowerToys Command Palette companion](README.PowerToys.md) for indexed filename and document search.
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
- Outlook MSG messages and local PST/OST stores, including headers, body text, and attachment names, without requiring Outlook or an Outlook IFilter.
- ZIP-based formats.
- Local SQLite and CSharpDB table contents (`.db`, `.sqlite`, `.sqlite3`, `.db3`, `.csharpdb`, `.csdb`, `.cdb`), detected by file header.
- Optional Windows OCR for PNG, JPEG, BMP, TIFF, scanned/image-only PDF files, and embedded images in Office, OpenDocument, EPUB, email, and ZIP/archive files.

Document extraction can be disabled in the UI for Office and PDF documents when you only want to scan simpler text files. Image OCR is a separate opt-in because OCR can be slower on large image folders, scanned PDFs, and image-heavy documents; OCR hits include image, PDF page, or embedded-member region labels. Previews can highlight the recognized region for standalone image files and rendered PDF pages.

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

Create a portable release folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-PortableRelease.ps1 `
  -Version 1.0.0.0 `
  -RuntimeIdentifier win-x64
```

Run the release executables:

```powershell
.\artifacts\release\publish\win-x64\FileSearch.Gui.exe
.\artifacts\release\publish\win-x64\FileSearch.Cli.exe
```

Run tests:

```powershell
dotnet test .\FileSearch.slnx
```

## Using the app

1. Choose **Search locations** below the search bar: **Selected folder**, **Selected indexed locations**, or **All indexed locations**. For a folder, type or browse its path; for selected indexed locations, use the checked-location popup shared with Quick Search. Clearing every check searches no indexed locations.
2. Open **Search options** (the sliders button). **Find a file**, **Find content**, and **Database values** choose a Plain text starting setup while keeping your query. You can also select targets and a matching mode directly.
3. Enter your query and check the explanation below it. In Unified/Boolean, `john smith` requires both terms, `john OR smith` accepts either, and `"john smith"` keeps the phrase together. Plain text treats the complete input literally, including operators and quotes.
4. Optionally restrict file names with a separate **File pattern**, such as `*.cs;*.xaml;*.md`. A file pattern controls which files are searched; it is separate from the text you want to find.
5. Adjust subfolders, case, document/database extraction, optional OCR, size and date limits. **Use index** checks coverage for the selected locations and options when searching. The route details retain live/indexed results and any fallback or pending-update reason; registered folders can still have incomplete coverage. Offline/inaccessible locations and removed registrations are explained rather than replaced with a broader scope.
6. Click **Search** or press Enter.
7. Select a result to view its matches or full-file preview. Database content matches within one cell, and hits identify the matching table, row and column. See the [database guide](README.Databases.md).
8. Use **Filter current results** to narrow names, paths and returned hit text without rescanning. It does not search the rest of a file or database.

### Search locations and Quick Search handoff

The selected indexed-location checkboxes are shared by main search, Quick Search, and the Quick Search settings page. **All indexed locations** uses the registered roots explicitly. Removed selected roots remain visible with a notice; uncheck them or register the location again. Overlapping locations do not duplicate the same file or source hit. The folder address is unused while an indexed-location scope is selected; choosing or typing another folder switches back to **Selected folder**.

In Quick Search, **Open full search** carries the query, location scope, content choice, and selected source into the main window. The query keeps Quick Search matching: every literal word must match a filename/path, while content uses the complete literal phrase. Main search explains this profile and offers **Use standard matching** to return to its ordinary query mode. Entire-machine metadata stays in Quick Search; choose a folder or indexed scope before handing off. Busy main-window searches or replacements must finish or be canceled first.

Saved searches and workspaces retain the location scope, selected roots, and Quick Search matching profile. Older saved entries continue to use their saved folder. See [Search locations](src/FileSearch.Gui/Help/search.html#search-locations) and [Quick Search handoff](src/FileSearch.Gui/Help/quick-search.html#full-search-handoff).

### Quick Search ranking and selection

Quick Search ranks arriving candidates with exact filenames and exact names without extensions ahead of weaker prefix or folder-word matches. A deeper or later exact match keeps that priority. Within the same ranking tier, complete name components separated by punctuation, spaces or case transitions get a bounded preference over substrings inside longer words. This keeps the existing exact-name and phrase-prefix priorities and literal matching rules. The automatic selection follows the current best result until you navigate results, click one, or choose **Preview**; later arrivals then preserve your choice. A query, content or scope change clears old results and preview so stale rows cannot be opened accidentally.

The quick window displays at most **80 results**. The content-enabled search stage collects at most **80 result identities** or **500 unique hits**, including any indexed filename hits it returns. Individual mail messages count separately even when they share a store file. Filename discovery finishes independently and can contribute later matches. Live metadata scans retain their **500 ms** folder/indexed-scope or **1,500 ms** entire-machine budget. Reached limits mean partial coverage, reported as a limited search; undiscovered files cannot be ranked. Use **Open full search** for broader coverage in a supported scope.

Enter and Ctrl+C retain focused controls' normal input/copy behavior; result shortcuts act on the selected result. Escape closes an open scope/location popup or preview Find before closing Quick Search. Literal query rules are unchanged; this adds no automatic typo/acronym matching or usage-history learning. See [ranking and keyboard selection](src/FileSearch.Gui/Help/quick-search.html#ranking-and-keyboard).

An inline database preview adapts the quick window's minimum height to the visible scope and database controls so records remain readable. Find reserves more room; closing Find returns to the current database minimum. Closing the preview restores the normal minimum height of 420 and the prior window height if it grew automatically. Manual resizes are preserved.

### Find in a preview

Open **File preview**, then choose **Find** or press **Ctrl+F** while the preview is focused. This searches the loaded text/source, formatted HTML view, or embedded text on the displayed PDF page. Formatted views include Markdown and supported mail/document previews. Enter the complete phrase and optionally enable **Match case**: spaces, quotes, `AND`, `OR`, and regex characters are literal text. Occurrences are highlighted without changing the source file.

Use **F3** for the next occurrence and **Shift+F3** for the previous one. In the Find box, **Enter** starts Find or advances to the next occurrence after a search. **Escape** stops a running Find or closes its panel. **Expand** retains the query, case setting, current occurrence, and whether Find is open or closed when the source content is unchanged. Expanded PDFs also retain the current page, Fit mode or custom zoom, and viewing position.

Find searches **this loaded preview** or **this displayed part**, such as a chapter, slide, message, or archive member; it does not search other parts or files. Long text and Markdown previews load the first 250,000 characters, and text/source Find retains at most 10,000 occurrences. The status declares partial coverage when text is truncated or a Find limit is reached. Formatted HTML uses the installed WebView2 runtime's text matching; if Find is unavailable in that runtime, the ordinary document preview remains usable.

PDF Find searches embedded text on **this PDF page**, with highlights that follow the supported page crop and rotation while keeping the whole original page visible. It does not search other pages or run OCR. PDF previews remain limited to 100 MB; page text retains at most 100,000 glyphs and 250,000 characters, and Find retains at most 10,000 occurrences. Find input is limited to 2,048 characters and each search has a 15-second deadline. An existing selected OCR hit can show its recognized line region after source and page-dimension checks; this separate outline is not a per-word Find highlight. Blank, image-only, incompatible, or unavailable text leaves the whole-page preview usable. Detected source changes discard Find and OCR overlays; refresh the search or reload the preview as indicated. See [Find within File preview](src/FileSearch.Gui/Help/results.html#preview-find).

## Search inside ZIP files

Choose **Contents**, enable **Document & database text**, and set the file pattern to `*.zip`. Supported text members and native text in PDF, DOCX, XLSX, and PPTX documents are searchable in both live and indexed content search. Refresh existing content indexes after upgrading. Physical-file filters still describe the ZIP: `type:pdf` does not select PDF members inside an archive.

Results retain the exact member, archive fingerprint, member-local line, and inner document location. Open uses the built-in viewer; PDF pages and XLSX sheets/cells are selected from the hit. Expand retains the active child's page, zoom, Find, and sheet position. DOCX/PPTX visual previews retain their existing installed-handler and navigation limitations. JSON/JSONL, CSV, and MCP results retain archive location metadata.

ZIP **Matches** use captured hits and same-member indexed context, preserving results when OCR changes extraction offsets. The whole-file excerpt toggle is unavailable for those hits; **File preview** reads the complete selected member.

Extraction retains the 16 MiB/member, 64 MiB/archive and 10,000-entry limits, with bounded Office-package expansion and XML depth. Text and metadata budgets are 2 Mi characters/50,000 lines per member and 8 Mi characters/200,000 lines per archive. New document parsing uses the bundled isolated host and generated temporary files; an unavailable host leaves ordinary text/image search usable and reports document skips. A 20-second cooperative archive budget and configured host hard deadline (normally 30 seconds) bound work; a terminated whole-archive host loses its buffered results. Partial notices identify skipped, corrupt/protected, duplicate/unsafe, truncated and timed-out content on live and indexed routes.

Exact duplicate member names and unsafe paths are skipped rather than resolving to the wrong member. Detected source size/time changes invalidate previews. Nested archives, embedded-image/scanned-PDF OCR inside Office/PDF members, legacy/macro-enabled Office files, and mail attachment contents are not included. Existing optional OCR of standalone ZIP image members remains available. See [Search Inside ZIP Files](src/FileSearch.Gui/Help/results.html#archive-content) for precise limits and behavior.

## Find and replace

In the desktop app, click **Replace** below the query row, select **Search → Find and replace**, or press **Ctrl+H** (configurable in Settings). The query box becomes **Find**; closing the panel restores your normal search query and mode.

Choose **Contents** or **Names**, enter **Replace with**, then click **Preview changes** or press Enter. Preview scans the current folder and filters directly. Review the before/after details, uncheck unwanted items, and click **Apply checked**. Empty replacement text removes matches; regex mode supports capture substitutions such as `$1`.

Replacement requires **Selected folder** scope. Choose that scope and an explicit folder, or use **Browse**, before previewing or applying changes. Indexed-location search scopes cannot use a remembered folder for replacement; changing scope invalidates the previous replacement preview.

Content replacement supports UTF-8 and BOM-marked Unicode text, `.docx` body/table/header/footer paragraphs, `.pptx` slide/notes paragraphs, and `.xlsx` text cells. **Also replace in formulas** explicitly enables textual editing of ordinary Excel formula expressions; Excel recalculates them on opening. Shared, array, and spill formulas are skipped. PDF, OCR, archives, mail, legacy/macro-enabled Office formats, signed/protected documents, binary files, and unsupported text encodings are not content replacement targets.

Name replacement supports files, folders, or both, and preserves file extensions unless **Include extensions** is selected. Collisions and invalid names are skipped. The scope root, indexed roots, their ancestors, and linked files/folders are protected.

**Undo last batch** survives app restarts. Originals and recovery journals are retained under `%LOCALAPPDATA%\FileSearch\ReplacementBackups`; changed files or occupied original names are reported as recovery conflicts rather than overwritten. Cancelled batches retain completed items for Undo.

Saved workflows also support **Add step → Find and replace**, with fresh previews, checked-item review, and grouped Undo after restart. CLI workflow runs require explicit `--apply-replacements` authorization. See [workflow instructions and JSON examples](README.Workflows.md).

## Using the CLI

Run `.\FileSearch.Cli.exe` from the installed or portable release folder without arguments to open the REPL. Type `help` to see available commands, or type a query directly to search the current folder.

Useful commands:

```text
path C:\src\project
mode plain
mode regex
mode boolean
target files
case on
recursive off
docs off
ocr on
known-only on
include *.cs;*.xaml
exclude bin;obj
ext .cs,.md
plain-ext .log,.cfg
limit 25
search connection string
index build
index on
index stats
index locations
workflow list
indexer status
exit
```

For automation, use the one-shot search command. Results stream to stdout unless `--output` is provided:

```powershell
.\FileSearch.Cli.exe search "connection string" --path C:\src --mode plain --json
.\FileSearch.Cli.exe search invoice --path C:\docs --target files --json
.\FileSearch.Cli.exe search "serial number" --path C:\scans --ocr --markdown
.\FileSearch.Cli.exe search needle --path C:\src --csv --output .\results.csv
.\FileSearch.Cli.exe search needle --path C:\src --jsonl --limit 500
.\FileSearch.Cli.exe search needle --path C:\src --markdown --index
```

One-shot search accepts the same common filters as the REPL, including `--target`, `--docs`, `--no-docs`, `--ocr`, `--no-ocr`, `--known-only`, `--all-types`, `--plain-ext`, `--include`, `--exclude`, `--ext`, `--exclude-ext`, `--exclude-dir`, `--min-size`, `--max-size`, `--after`, `--before`, `--recursive`, `--no-recursive`, `--hidden`, `--case`, `--index`, and `--no-index`.

Index maintenance and reporting also have one-shot structured output. Use these commands when scripts need to build or clear an index, inspect configured roots, inspect per-root stats, or list failed extraction details without parsing the interactive tables:

```powershell
.\FileSearch.Cli.exe index build C:\src --json
.\FileSearch.Cli.exe index rebuild C:\src --csv --output .\index-rebuild.csv
.\FileSearch.Cli.exe index clear C:\src --json
.\FileSearch.Cli.exe index locations --json
.\FileSearch.Cli.exe index stats C:\src --csv
.\FileSearch.Cli.exe index failures --jsonl --output .\index-failures.jsonl
.\FileSearch.Cli.exe index locations --markdown --output .\index-locations.md
```

`index build [FOLDER]`, `index rebuild [FOLDER]`, `index clear [FOLDER]`, `index locations`, `index stats [FOLDER]`, and `index failures` support `--json`, `--jsonl`, `--csv`, `--markdown`, `--format`, and `--output`. `index build` and `index rebuild` also accept traversal and extraction filters such as `--include`, `--exclude`, `--ext`, `--exclude-ext`, `--exclude-dir`, `--min-size`, `--max-size`, `--after`, `--before`, `--recursive`, `--no-recursive`, `--hidden`, `--ocr`, `--no-docs`, `--known-only`, and `--plain-ext`. If no folder is passed to folder-specific index commands, the current working directory is used.

Saved workflow JSON files can be inspected and run from the CLI:

```powershell
.\FileSearch.Cli.exe workflow list
.\FileSearch.Cli.exe workflow validate "Find TODOs"
.\FileSearch.Cli.exe workflow run "Find TODOs" --dry-run
.\FileSearch.Cli.exe workflow run "Find TODOs" --yes --no-hits
```

Background indexer controls are available as `indexer` commands. They use the same per-user named-pipe API as the GUI:

```powershell
.\FileSearch.Cli.exe indexer start
.\FileSearch.Cli.exe indexer status
.\FileSearch.Cli.exe indexer pause
.\FileSearch.Cli.exe indexer resume
.\FileSearch.Cli.exe indexer refresh C:\src
.\FileSearch.Cli.exe indexer validate C:\src
```

Whole-drive file-name indexes are managed with `volumes` commands. `build` reads the master file table when the CLI is elevated or with `--admin` (Windows asks for permission); otherwise it scans folders without elevation:

```powershell
.\FileSearch.Cli.exe volumes list
.\FileSearch.Cli.exe volumes build C: --admin
.\FileSearch.Cli.exe volumes search invoice 2026 --limit 20
.\FileSearch.Cli.exe volumes search config json --path C:\src --files --json
.\FileSearch.Cli.exe volumes remove C:
```

Once a drive is indexed, one-shot name searches with `--index` (for example `search report --target files --index`) are answered from it. Details are in [README.Indexing.md](README.Indexing.md#drive-file-name-index).

The CLI uses the same `FileSearch.Core` search pipeline, Windows OCR package, workflow runner, and CSharpDB index database as the desktop app. Indexed search is opt-in with `index on`; when the current search is not covered by the index, the CLI falls back to live scan.

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

No PowerShell extension is required for normal use. The CLI can be launched directly from PowerShell from the installed or portable release folder:

```powershell
.\FileSearch.Cli.exe
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
) | .\FileSearch.Cli.exe
```

For structured search automation, prefer one-shot output:

```powershell
.\FileSearch.Cli.exe search TODO --path C:\src\project --json |
  ConvertFrom-Json
```

For structured index automation, call the one-shot index commands directly:

```powershell
.\FileSearch.Cli.exe index build C:\src\project --json |
  ConvertFrom-Json
.\FileSearch.Cli.exe index locations --json |
  ConvertFrom-Json
.\FileSearch.Cli.exe index failures --csv --output .\failures.csv
```

Index maintenance works the same way:

```powershell
@(
  'path C:\src\project'
  'index build'
  'index on'
  'search TODO'
  'exit'
) | .\FileSearch.Cli.exe
```

Directly loading `FileSearch.Core` from PowerShell is possible, but it is not the recommended path because PowerShell has to resolve the full dependency graph and consume async streams. For automation, use the CLI. A future PowerShell module would mainly be useful for pipeline-native commands such as `Search-FileContent` and `Update-FileSearchIndex`.

## Packaging for Microsoft Store

FileSearch can produce both MSI and MSIX artifacts for Store distribution. The MSI path is useful for the Microsoft Store MSI/EXE submission flow; the MSIX path remains available when a Partner Center package identity is reserved.

CI builds, tests, CLI smoke tests, published executable smoke tests, dependency review, manual Store packaging, portable tag-based releases, MSI artifacts, and optional signed Store artifacts are configured with GitHub Actions. Release steps are documented in [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md).

Create an MSI installer from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\New-MsiInstaller.ps1 `
  -Version 1.0.0.0 `
  -RuntimeIdentifier win-x64
```

Outputs are written to `artifacts\msi`:

- `.msi` installer containing the GUI, CLI, background indexer, extractor host, and help bundle.
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
- Bundled Help search is indexed automatically from `Help/*.html` when building or publishing the GUI. Add content to those pages without maintaining a separate index. Run `node --test tests/HelpSearch/help-search.test.cjs` to check the search logic (Node.js 18 or later is needed only for these development tests).
- Tests use xUnit and `Microsoft.NET.Test.Sdk`.
- The core search pipeline is designed around dependency injection so extractors and options can be swapped or extended.
- Indexed search, tray-indexer lifecycle, USN catch-up, snapshot fallback, and index health behavior are documented in [README.Indexing.md](README.Indexing.md).
- Workflow search and the workflow JSON file format are documented in [README.Workflows.md](README.Workflows.md).
- The read-only MCP server for AI assistants (Claude Code, Claude Desktop, LM Studio), its tool surface, and client configuration are documented in [README.Mcp.md](README.Mcp.md). The wire-level reference for integrating it into AI apps and tools — tool schemas, error catalog, OpenAI Codex/VS Code/Cursor setup, and C#/Python client code including the OpenAI Agents SDK — is [README.McpIntegration.md](README.McpIntegration.md).
- Security and privacy posture are documented in [SECURITY.md](SECURITY.md) and [PRIVACY.md](PRIVACY.md).
- Reproduce controlled real-file search/database measurements and WPF preview/OCR checks with the [task baseline tools](benchmarks/FileSearch.Benchmarks/README.md#real-file-task-baseline). The [benchmark methodology](docs/BENCHMARKS.md#task-baseline-and-comparison-protocol) separates local component timings, held-out relevance, and installed-product comparisons.

## Benchmark results

Local development benchmarks cover searches over generated files, SQLite/CSharpDB previews, and filename ranking. Selected navigation and ranking cases improved, with mixed timing and resource results; broader performance qualification remains open. See the [benchmark methodology](docs/BENCHMARKS.md) and [runner guide](benchmarks/FileSearch.Benchmarks/README.md) to reproduce the measurements.

## Recent changes

Work landed from `bbd4b73` (*Add background indexed search*, June 2026) to the current head, grouped by area.

### Unreleased

- **Consistent search locations**: main search and Quick Search share selected indexed-location checkboxes, support explicit selected/all-indexed scopes, and explain empty, offline, and removed locations. Open full search preserves Quick Search query matching and source selection; saved searches and workspaces retain scope and matching intent. See [search location Help](src/FileSearch.Gui/Help/search.html#search-locations).
- **Find in file previews**: highlight and navigate literal phrases in loaded text/source, formatted HTML, and embedded text on the displayed PDF page across the main window, Quick Search, and Expand. Find shows its loaded-part/page scope and partial limits; unavailable text or browser Find leaves the ordinary preview available. PDF highlights follow supported page crop/rotation, validated existing OCR hits can show a separate line outline, and Expand retains Fit/custom zoom and position. See [preview Find Help](src/FileSearch.Gui/Help/results.html#preview-find).
- **Database loading and navigation**: preparation stages and snapshot copy progress, Stop loading, reuse of the visible session and grid for another hit in the same unchanged database, and bounded continuation bookmarks for supported sequential pages. Ordinary page changes update existing rows where possible; sorting and record locations remain distinct. Arbitrary deep jumps retain a correct offset fallback; read-only snapshot and size/row limits remain in effect.
- **Find in database previews**: search the selected SQLite or CSharpDB table beyond the loaded page, choose all/either literal terms or a phrase and a column scope, then navigate matching cells. Scans are cancellable and bounded, with explicit partial coverage; the database stays read-only. See [database Find and limits](README.Databases.md#find-in-a-table).
- **Search guidance**: live mode/target explanations, starting setups for files/content/database values, and persistent per-target routing details, including zero-hit fallback reasons. Plain text, AND/OR/phrases, filename filters and database cell scope are explained in bundled Help.
- **Built-in file previews**: switch between Matches and File preview in the main window or Quick Search. Preview images and SVG, navigate PDF pages, read formatted Markdown, and view syntax-highlighted source code, including filename-only results. Expand the same preview into a larger window. Installed Windows preview handlers extend format coverage in an isolated helper process, with file details or text as the fallback. PowerToys is not required.
- **Whole-page PDF context**: PDF file previews show the complete original page and start at the current match's page when available, including partial snippets and OCR-region hits. Expanded previews retain the current page; Matches remains an excerpt view, and text fallbacks are labeled separately.
- **Database search and preview**: search SQLite and CSharpDB scalar values with `type:database`, retain table/row/column/key locations in live and indexed results, and browse complete supported records in a read-only grid. Private database/WAL snapshots leave source files unchanged and reject stale hit locations. Refresh existing indexes to add database contents. See [coverage and limits](README.Databases.md).
- **More built-in viewers**: read CSV/TSV, XLSX, and ODS in a paged grid with sheet selection and matching-cell navigation; browse actual ZIP members; read complete EML/MSG and selected PST/OST messages with attachment names; preview WebP, animated GIF, and multipage TIFF; and read whole EPUB chapters plus reflowed ODT/ODP/RTF content without Office. Expand preserves the selected part or cell. Formulas and active/remote document content are disabled. Refresh old indexes for precise XLSX and ZIP locations. See [File preview Help](src/FileSearch.Gui/Help/results.html) for format limits and fallback behavior. PowerToys remains optional.
- **Offline Help search**: search the bundled guide by keyword or quoted phrase, view matching passages, and open the relevant section with highlighted matches. Press Ctrl+K from any Help page. The search index is generated automatically when building or publishing the GUI; no web service or additional application runtime is required.

### FileSearch 1.10.0

- **Optional PowerToys companion**: search indexed file names or document text from Command Palette, view snippets, open files, reveal them in Explorer, copy paths or matching text, and continue a query in FileSearch. The companion is installed separately; normal FileSearch startup, shortcuts, and dependencies do not require PowerToys. See the [PowerToys setup guide](README.PowerToys.md).
- **Desktop query handoff**: launch or activate the desktop app with a query, folder, target, and selected file. Active searches and replacements are preserved, and a missing folder never redirects the query to another location.
- **Bundled Help**: added the PowerToys guide, troubleshooting, and executable argument reference throughout the app's Help pages.
- **Release packaging**: separate companion MSIX, unsigned development ZIP when needed, and checksums, with automatic package verification and main-app dependency isolation checks.
- **Dependency maintenance**: updated the existing PKCS dependency to 10.0.13 to address its reported vulnerabilities.

### FileSearch 1.9.1

- **Find and replace**: preview and apply checked content or name changes from the main Search screen, with strict Unicode encoding preservation, native DOCX/XLSX/PPTX editing, collision checks, protected roots, durable backups, and Undo after restart.
- **Workflow replacement**: add Find and replace steps with fresh folder scopes or exact earlier-search scopes, before/after review, dry-run previews, and grouped Undo. CLI application requires `--apply-replacements`.
- **Semantic search**: desktop concept-search controls, relevance thresholds, result limits, and optional local EmbeddingGemma model support. See [Smart Search help](src/FileSearch.Gui/Help/smart-search.html).
- **Model-license packaging**: keep bundled license bytes stable across Windows clean checkouts so pinned model-pack checksums remain valid.

### Search engine and indexing

- **Background indexed search** (`bbd4b73`): CSharpDB-backed file index with background refresh, coverage checks, and live-scan fallback — the baseline for the changes below. Documented in [README.Indexing.md](README.Indexing.md).
- **Indexing correctness, threading, and performance overhaul** (`13b1f9c`): removed the watcher feedback loop that caused double indexing, skipped upserts for unchanged files, ran schema setup once per process, and serialized cross-process writers with a lock file. Heavy parsing and preview extraction moved off the UI thread, UTF-16/32 files are detected correctly, overlapping indexed roots are supported, and search-time filters no longer shrink index coverage. Performance work included directory-exclude pruning, cached per-file verdicts, batched UI updates, and throttled progress callbacks, plus centralized file logging and safer settings management.
- **Index storage layering** (`1b08535`): split the index implementation into `IndexDatabase` (schema, versioning, write exclusion) and `IndexTables` (DML), extracted a tested `SearchHistory` MRU helper, and moved folder picking and UI dispatch behind injectable services.
- **Worker and queue fixes** (`2317524`): queue priority, worker edge cases, and small races found in review.
- **Compiler-enforced SQL escaping** (`6778331`): index queries are built through an interpolated-string handler (`Sql.Format`), so a value that bypasses escaping no longer compiles.
- **Workflow search**: saved multi-step searches with if/else conditions on hit or file counts, retry and for-each loops, sub-searches over previous results, JSON/CSV/Markdown export, and confirmed replacement/copy/move/run-program actions — stored as hand-editable JSON files, one per workflow. The file format is documented in [README.Workflows.md](README.Workflows.md).

### Desktop app

- **Indexed locations management** (`0d94748`): dedicated window for per-location options (subfolders, hidden files, document extraction, change watching) with live status badges and queue counts in the sidebar.
- **Shutdown deadlock fix** (`5a1fa5c`): the app could hang forever on exit while stopping background indexing.
- **View-model split** (`41f59e7`): `MainViewModel` split into focused `Search`, `Index`, `History`, and `StatusBar` view models, backed by a binding-path audit test that verifies every XAML binding resolves.
- **Sidebar scrolling** (`13402db`): the collapsible sections (Scopes, Recent folders, Indexed locations, Saved searches) scroll as one surface when they outgrow the window instead of pushing the lower sections out of view, and the nav lists no longer swallow mouse-wheel events.

### Command line

- **Interactive search REPL** (`b9c2ec5`): Spectre.Console front end with live and indexed search, query-mode/case/filter/limit commands, target/document/OCR controls, workflow execution, background-indexer controls, and index build/clear/stats — sharing the same core pipeline and index database as the GUI.

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
