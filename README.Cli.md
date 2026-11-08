# FileSearch command line

Search from a terminal, export results for scripts, and manage the same local indexes used by the desktop app. Start with the [main README](README.md) to download FileSearch.

[Commands](#using-the-cli) · [PowerShell automation](#powershell-usage) · [Indexing guide](README.Indexing.md)

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

Directly loading `FileSearch.Core` from PowerShell is possible, but it is not the recommended path because PowerShell has to resolve the full dependency graph and consume async streams. For automation, use the CLI.

## More guides

- [Database search](README.Databases.md): table, row, and column locations and read-only limits.
- [Mail search](README.Mail.md): message fields, dates, and individual message identity.
- [Workflows](README.Workflows.md): saved steps and explicit replacement authorization.
- [Build and API reference](README.Development.md): run from source or embed the search engine.
