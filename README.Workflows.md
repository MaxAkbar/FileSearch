# Workflows

Open **Search → Workflows** or **Workflows** in the sidebar. Each saved workflow is a JSON file under `%AppData%\FileSearch\Workflows`.

Use **Add step → Find and replace** to configure content or name replacement. The step can scan its own root folders and filters afresh, or use the exact items matched by an earlier Search step. A source search stopped by `maxHits`, or one that failed to read files, cannot supply a replacement scope; rerun it completely or choose a folder scope. A hit-display or buffering cap does not restrict the distinct file set.

**Dry run** performs searches and replacement previews without writing files. **Run** pauses at each replacement step for before/after review and item checkboxes. Click **Apply checked** to continue with the approved items, or **Cancel** to skip that step. Enter does not apply changes. Cancelling the workflow or closing its window stops the run; completed changes remain recoverable.

Replacement uses the same text/Office/name adapters, stale-file checks, encoding preservation, backups, and protected-root rules as the main Search screen. See [Find and replace](#find-and-replace) for supported formats and defaults. Names changed by a step are remapped in earlier search results and active for-each variables so later steps and exports use their current paths. Exports still contain the original search hits; use a new Search step to verify the modified contents.

The run log records applied change counts, skipped items, failures, and batch IDs. Durable group manifests and batch journals are stored under `%LOCALAPPDATA%\FileSearch\ReplacementBackups`. **Undo workflow replacements**, beneath the run log, restores every replacement batch from the last workflow run in reverse order, including after restarting the app. Conflicts stop earlier dependent batches from being undone. Copies, moves, exports, and program launches are separate workflow actions and are outside replacement Undo.

## Find and replace

In the desktop app, click **Replace** below the query row, select **Search → Find and replace**, or press **Ctrl+H** (configurable in Settings). The query box becomes **Find**; closing the panel restores your normal search query and mode.

Choose **Contents** or **Names**, enter **Replace with**, then click **Preview changes** or press Enter. Preview scans the current folder and filters directly. Review the before/after details, uncheck unwanted items, and click **Apply checked**. Empty replacement text removes matches; regex mode supports capture substitutions such as `$1`.

Replacement requires **Selected folder** scope. Choose that scope and an explicit folder, or use **Browse**, before previewing or applying changes. Indexed-location search scopes cannot use a remembered folder for replacement; changing scope invalidates the previous replacement preview.

Content replacement supports UTF-8 and BOM-marked Unicode text, `.docx` body/table/header/footer paragraphs, `.pptx` slide/notes paragraphs, and `.xlsx` text cells. **Also replace in formulas** explicitly enables textual editing of ordinary Excel formula expressions; Excel recalculates them on opening. Shared, array, and spill formulas are skipped. PDF, OCR, archives, mail, legacy/macro-enabled Office formats, signed/protected documents, binary files, and unsupported text encodings are not content replacement targets.

Name replacement supports files, folders, or both, and preserves file extensions unless **Include extensions** is selected. Collisions and invalid names are skipped. The scope root, indexed roots, their ancestors, and linked files/folders are protected.

**Undo last batch** survives app restarts. Originals and recovery journals are retained under `%LOCALAPPDATA%\FileSearch\ReplacementBackups`; changed files or occupied original names are reported as recovery conflicts rather than overwritten. Cancelled batches retain completed items for Undo.

Saved workflows also support **Add step → Find and replace**, with fresh previews, checked-item review, and grouped Undo after restart. CLI workflow runs require explicit `--apply-replacements` authorization. See the workflow JSON examples below.

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
