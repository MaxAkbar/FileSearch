# Workflows

Open **Search → Workflows** or **Workflows** in the sidebar. Each saved workflow is a JSON file under `%AppData%\FileSearch\Workflows`.

Use **Add step → Find and replace** to configure content or name replacement. The step can scan its own root folders and filters afresh, or use the exact items matched by an earlier Search step. A source search stopped by `maxHits`, or one that failed to read files, cannot supply a replacement scope; rerun it completely or choose a folder scope. A hit-display or buffering cap does not restrict the distinct file set.

**Dry run** performs searches and replacement previews without writing files. **Run** pauses at each replacement step for before/after review and item checkboxes. Click **Apply checked** to continue with the approved items, or **Cancel** to skip that step. Enter does not apply changes. Cancelling the workflow or closing its window stops the run; completed changes remain recoverable.

Replacement uses the same text/Office/name adapters, stale-file checks, encoding preservation, backups, and protected-root rules as the main Search screen. See [Find and replace](README.md#find-and-replace) for supported formats and defaults. Names changed by a step are remapped in earlier search results and active for-each variables so later steps and exports use their current paths. Exports still contain the original search hits; use a new Search step to verify the modified contents.

The run log records applied change counts, skipped items, failures, and batch IDs. Durable group manifests and batch journals are stored under `%LOCALAPPDATA%\FileSearch\ReplacementBackups`. **Undo workflow replacements**, beneath the run log, restores every replacement batch from the last workflow run in reverse order, including after restarting the app. Conflicts stop earlier dependent batches from being undone. Copies, moves, exports, and program launches are separate workflow actions and are outside replacement Undo.

## JSON example

The format is version 1, uses camelCase fields and string enums, and requires a unique `id` and `type` for every step. This example searches for candidate files, replaces within that exact set, then searches the folder again to verify the result:

```json
{
  "version": 1,
  "name": "Update product name",
  "steps": [
    {
      "type": "search",
      "id": "candidates",
      "query": "OldProduct",
      "roots": ["C:\\Documents\\Project"],
      "filters": { "includeGlobs": ["*.txt", "*.docx", "*.xlsx", "*.pptx"] }
    },
    {
      "type": "replace",
      "id": "update",
      "scopeStepId": "candidates",
      "find": "OldProduct",
      "replaceWith": "NewProduct",
      "target": "contents",
      "useRegex": false,
      "matchCase": false,
      "includeFormulas": false
    },
    {
      "type": "search",
      "id": "verify",
      "query": "OldProduct",
      "roots": ["C:\\Documents\\Project"]
    }
  ]
}
```

A replacement step without `scopeStepId` requires `roots` and uses its own `filters`. Filters support include/exclude globs, excluded directory names, recursion, hidden files, size limits in bytes, and UTC modified-date bounds. An omitted excluded-directory list uses engine defaults; `[]` walks all folders. `maxFileSizeBytes: 0` removes the size cap.

Replacement options:

| Field | Default | Meaning |
| --- | --- | --- |
| `target` | `contents` | `contents` or `names` |
| `find` | required | Literal text or a regex; supports workflow variables |
| `replaceWith` | empty | Empty removes matches; regex supports substitutions such as `$1` |
| `useRegex` | `false` | Interpret Find as a regular expression |
| `matchCase` | `false` | Case-sensitive matching |
| `nameTarget` | `both` | `files`, `folders`, or `both` |
| `includeExtensions` | `false` | Permit file extension edits during name replacement |
| `includeFormulas` | `false` | Edit ordinary Excel formula expressions; shared/array/spill formulas are skipped |
| `additionalTextExtensions` | `[]` | Extra strict Unicode text formats, e.g. `[".custom"]` |

Other step kinds are `search`, `if`, `retry`, `forEach`, `export`, `fileOperation`, `runProgram`, and `stop`. Conditions measure earlier search hit or file counts. Retry parameters and for-each variables such as `${file}`, `${fileName}`, and `${directory}` are substituted when a step executes. Unknown variables remain literal.

## CLI

```powershell
FileSearch.Cli.exe workflow validate "Update product name"
FileSearch.Cli.exe workflow run "Update product name" --dry-run
FileSearch.Cli.exe workflow run "Update product name" --apply-replacements
FileSearch.Cli.exe workflow run "Update product name" --apply-replacements --yes --no-hits
```

CLI replacement requires `--apply-replacements`. Interactive runs then ask for confirmation; unattended runs also need `--yes`. Preview output appears before applying. Registered index roots are protected, and affected locations are refreshed using their saved indexing profiles. The CLI prints the recovery group ID; grouped Undo is available in the desktop Workflows window.
