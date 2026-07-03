# FileSearch MCP Server

`FileSearch.Mcp` is a stdio [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI assistants (Claude Code, Claude Desktop, LM Studio, VS Code, and any other MCP client) **read-only** access to FileSearch's engine: live content search, fast search over the folders FileSearch has already indexed, document text extraction, and index introspection.

It is a thin host over `FileSearch.Core` — the same engine the GUI and CLI use — so results, query modes, and index coverage semantics match the other surfaces exactly. Phase 1 is deliberately read-only: the server never builds, refreshes, clears, or compacts an index, and never writes to disk. Index writes stay owned by the GUI and the tray indexer.

Building an AI app or agent on top of the server? [README.McpIntegration.md](README.McpIntegration.md) is the wire-level integration reference: the full launch contract, per-tool parameter and response schemas with captured example JSON, the error catalog, VS Code/Cursor recipes, and programmatic C# and Python clients.

## Quick start

```powershell
# one-time: build the server (and optionally index a folder with the GUI or CLI)
dotnet build .\FileSearch.slnx

# run it manually to sanity-check (Ctrl+C to stop; it speaks JSON-RPC on stdin/stdout)
dotnet run --project src\FileSearch.Mcp --no-build -- --root C:\Users\you\Documents
```

The repository ships a committed [.mcp.json](.mcp.json), so in Claude Code the server is available automatically when working in this repo (scoped to the repo itself).

For daily use outside the repo, you do not need to build anything: **release artifacts include the server**. The portable ZIP carries `FileSearch.Mcp.exe` next to the GUI and CLI, and the MSI installs it to a stable path — `C:\Program Files\FileSearch\FileSearch.Mcp.exe` — which is the best target for client configs because it survives upgrades. (The Microsoft Store package omits the server: MCP configs cannot point into versioned, ACL-restricted `WindowsApps` paths.) Every release runs an initialize/tools-list handshake against the published binary before artifacts are uploaded.

To build from source instead, publish a self-contained binary once and point your client at it:

```powershell
dotnet publish src\FileSearch.Mcp -c Release -o artifacts\mcp
```

## Tools

Every tool is read-only and annotated as such (`readOnlyHint: true`, `openWorldHint: false`). Each wraps one narrow `FileSearch.Core` contract:

| MCP tool | Core contract it wraps | What it does |
| --- | --- | --- |
| `search_content` | `ISearcher` (live scan path) | Full-text/regex/boolean/unified search that re-reads files under the given roots. Works on any allowed folder, indexed or not; also matches file/folder names via `target`. |
| `search_index` | `IIndexSearch` (+ `GetCoverageAsync`) | Content search served from the CSharpDB index. Reports per-root `coverage`; roots the index cannot serve are reported, never silently live-scanned. |
| `extract_text` | `IExtractorRegistry` → `ITextExtractor` | Streams numbered text lines from one file using the registered format extractors (text/code, PDF, DOCX, XLSX, PPTX, ODT/ODS/ODP, EPUB, RTF, HTML, EML, ICS/VCF, XML, ZIP). Line numbers match search hits; PDF pages, sheets, and archive members surface as `anchor`s. |
| `index_status` | `IIndexMaintenance` (`GetDatabaseInfoAsync`, `GetLocationsAsync`, `GetStatsAsync`) + `IExtractorRegistry.SupportedExtensions` | Index database health/size, indexed locations with freshness and parsed build profile (OCR flag, build-time exclusions), optional per-root stats, plus server capabilities: allowed roots and supported extensions. |
| `index_failures` | `IIndexMaintenance` (`GetFailedFilesAsync`) | Files that failed extraction during indexing, with extractor id/version, error, and attempt counts — the honest explanation for "why doesn't indexed search see this file". |

The write-side contracts (`IIndexWriter.BuildOrRefreshAsync`, `ClearAsync`, `IIndexMaintenance.CompactAsync`) are intentionally not mapped in Phase 1.

The intended agent flow: call `index_status` once to learn allowed roots, indexed locations, and supported file types → `search_index` for indexed folders → `search_content` for everything else → `extract_text` to read the interesting hits in context.

### Common parameters

`search_content` and `search_index` share the query surface of the CLI: `mode` (`plain` | `regex` | `boolean` | `unified`), `caseSensitive`, `includeExtensions`/`excludeExtensions`, `modifiedAfter`/`modifiedBefore` (ISO 8601, treated as UTC), `maxResults`, `maxResultsPerFile`, and `timeoutSeconds`. `search_content` adds `roots` (required), `target` (`content` | `files` | `folders` | `names`), `includeGlobs`/`excludeGlobs`, and `includeHidden`. `search_index` makes `roots` optional — omitted means "every indexed location inside the allowed roots" — and accepts content queries only.

## Result shaping

Tool output is JSON text: camelCase, `null`s omitted, compact (models pay per token; humans can pipe through a formatter). Field names line up with the CLI's one-shot automation DTOs (search hit, index location, index stats, index failure) so the two machine surfaces stay in lockstep; the MCP additions are the truncation flags, `coverage`, and the parsed profile fields that replace the raw options-hash string.

Every limit degrades to *partial results plus an honest flag*, never an error, because for an agent a partial answer beats a retry loop:

| Limit | Default | Ceiling | On overflow |
| --- | --- | --- | --- |
| `maxResults` (search) | 50 | 500 | stop enumerating, `truncated: true` |
| `maxResultsPerFile` (search) | unlimited | 100 | suppress extra hits from that file, `truncated: true` |
| `timeoutSeconds` (all streaming tools) | 30 | 120 (min 5) | return hits found so far, `timedOut: true` |
| hit line length | 320 chars | — | window around the first highlight (the match always survives), `lineTruncated: true`, `…` at the cut ends |
| `maxLines` (extract) | 200 | 2,000 | stop, `truncated: true`, `nextStartLine` for paging |
| extract line length / total | 500 chars / 100k chars | — | per-line `…` cap; total budget stops the stream with `nextStartLine` |
| `roots` per search call | — | 8 | rejected with a clear error |

Two shaping decisions worth knowing:

- **`totalMatches` counts what was enumerated, not the whole corpus.** Once `maxResults` is reached the scan stops (that is the point of the cap), so `truncated: true` means "there may be more", not "there are exactly N more".
- **`search_index` auto-adopts build-time exclusions.** `IndexProfile.Covers` requires a request to exclude at least what the index build excluded. The server merges each root's stored profile exclusions (for example image extensions when OCR was off, or excluded directories) into the request before the coverage check. Everything merged was never indexed, so results are unchanged — without this, default requests against GUI- or CLI-built indexes would report "Index does not cover this search".

## Safety rails

- **Read-only tool surface.** No tool maps to `IIndexWriter` or any mutating API, and all tools carry `readOnlyHint: true`, `destructiveHint: false` annotations.
- **No write side-effects from searching.** The searcher stack normally queues a background root refresh when it notices a stale index (which persists pending-change rows to the index database). The MCP host registers a no-op `IIndexingSearchCoordinator` ahead of `AddFileSearchCore()`, so searches never enqueue indexing work and the in-process indexing service is never constructed.
- **Root allow-list.** Every path argument is canonicalized (`Path.GetFullPath`, so `..` segments cannot escape) and must be an absolute path under an allowed root. The allow-list is, in order of precedence: repeated `--root <folder>` arguments → the `FILESEARCH_MCP_ROOTS` environment variable (`;`-separated) → the default of *user profile + all indexed locations*. `--allow-any-root` disables the check for machine-wide use. Denials name the allowed roots so the model can recover or relay the fix.
- **No data leaks around the allow-list.** `index_status` hides indexed locations outside the allowed roots (reporting only `hiddenLocationCount`), and `index_failures` filters failure paths the same way.
- **Protocol-channel hygiene.** stdout carries JSON-RPC only. Console logging is removed and diagnostics go to the daily files the GUI/CLI already use: `%LocalAppData%\FileSearch\logs\filesearch-mcp-*.log`. Startup argument errors go to stderr.
- **Bounded work.** Deadlines above, plus Core's own guards: regex queries compile with a 2-second match timeout, live scans skip files over 100 MB by default, hidden files stay excluded unless asked for, and `.git`/`.vs`/`node_modules` are pruned.
- **Local-only.** The server makes no network calls; it reads the filesystem and the local index database, nothing else. Image files are excluded from live scans (the server exposes no OCR switch), while OCR text already stored in the index remains searchable.

One consequence of index-coverage semantics: the coverage check hashes the full extractor registry, so this host registers the same extractor set as the GUI, CLI, and tray indexer — including the Windows OCR extractors — and therefore targets Windows like every other FileSearch host. A host with a different extractor set could never serve an index those hosts built.

## Client configuration

All snippets assume Windows paths; escape backslashes in JSON (`C:\\Users\\you`).

### Claude Code

The repository's committed [.mcp.json](.mcp.json) (project scope — applies when Claude Code runs inside this repo, after `dotnet build .\FileSearch.slnx`):

```json
{
  "mcpServers": {
    "filesearch": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/FileSearch.Mcp", "--no-build", "--", "--root", "."]
    }
  }
}
```

`--no-build` matters: a build kicked off by `dotnet run` would print MSBuild output to stdout and corrupt the protocol stream. `--root .` sandboxes the server to the repository; relative roots are resolved against the working directory, which for a project-scope server is the repo root.

For any other project (or user-wide), register the installed or published binary with wider roots:

```powershell
claude mcp add filesearch --scope user -- "C:\Program Files\FileSearch\FileSearch.Mcp.exe" --root C:\Users\you\Documents --root C:\Users\you\source
```

(Use `artifacts\mcp\FileSearch.Mcp.exe` from the publish step above if you built from source, or the unzipped portable folder.)

### Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json` (Settings → Developer → Edit Config), then restart Claude Desktop:

```json
{
  "mcpServers": {
    "filesearch": {
      "command": "C:\\Program Files\\FileSearch\\FileSearch.Mcp.exe",
      "args": ["--root", "C:\\Users\\you\\Documents", "--root", "C:\\Users\\you\\source"]
    }
  }
}
```

Claude Desktop provides no working directory, so use absolute paths for both the executable and the roots. The command shown is the MSI install path; substitute your unzipped portable folder or `artifacts\mcp` publish output if you are not using the installer. Omitting every `--root` gives the default allow-list (user profile + indexed locations), which is a sensible desktop setup.

### LM Studio

Open the **Program** tab in the right sidebar → **Install** → **Edit mcp.json**, and add the same shape:

```json
{
  "mcpServers": {
    "filesearch": {
      "command": "C:\\Program Files\\FileSearch\\FileSearch.Mcp.exe",
      "args": ["--root", "C:\\Users\\you\\Documents"]
    }
  }
}
```

LM Studio surfaces tool-call confirmations per tool; everything here is read-only, so allowing the `filesearch` tools is safe. Local models vary in tool-calling quality — the tool descriptions are written to steer them, but if a small model flounders, start it with a single `--root` so even clumsy calls stay scoped.

## Troubleshooting

- **Client says the server disconnected immediately** — the project probably is not built (`--no-build` requires a prior `dotnet build`), or a startup argument was rejected; both are printed to stderr, which most clients show in their MCP logs.
- **`search_index` reports `covered: false`** — the `coverage` entry says why: `Missing` (root never indexed — index it in the GUI, or use `search_content`), `Incompatible` with "extractor versions are out of date" (index built by an older/newer FileSearch — refresh it from the GUI/CLI), or "Index does not cover this search" (the request asked for something the build excluded, for example `includeHidden` on a non-hidden index).
- **A path is refused** — the error names the allowed roots; restart the server with more `--root` arguments or `--allow-any-root`.
- **Diagnostics** — `%LocalAppData%\FileSearch\logs\filesearch-mcp-*.log`, alongside the GUI/CLI logs. The server also honors `FILESEARCH_INDEX_DATABASE_PATH` for pointing at a non-default index database (useful for testing).

## Not in Phase 1 (planned)

- Index write tools (`index_build`, `index_refresh`, `index_clear`) behind an explicit `--allow-write` opt-in, mapping `IIndexWriter`.
- A workflow tool wrapping `IWorkflowRunner` for saved multi-step searches.
- Failed-file retry, tracking the roadmap's `filesearch index retry-failures` CLI work.
