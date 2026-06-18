# Windows IFilter Validation Notes

Windows IFilter availability is machine-specific. Windows, Office, PDF readers, and other applications can register different filters, so manual validation should record the exact machine state instead of assuming one universal result.

## What To Record

- Windows version and architecture.
- Installed Office/PDF/document applications that may add IFilters.
- File extensions tested.
- Whether `FileSearch.ExtractorHost.exe` was used.
- Exported failure/diagnostic rows for files that did not extract as expected.

## Suggested Checks

1. Pick one known text document for each installed filter type, such as `.rtf`, `.doc`, `.docx`, `.pdf`, or vendor-specific formats.
2. Index the files with the primary extractor unavailable or returning no content when possible.
3. Confirm the indexed locations view shows `IFilter fallback used`.
4. Confirm diagnostics include useful `ifilter_*` issue codes for empty, blocked, password-protected, unsupported, or failed files.
5. Export failed index extractions as CSV or JSON and confirm `extractor_id`, `extractor_version`, `issue_code`, `severity`, attempt count, and timestamp are present.
6. Confirm blocked shell-oriented extensions such as `.lnk`, `.url`, `.search-ms`, and `.searchconnector-ms` do not invoke IFilter fallback.
7. Test a document with standard properties such as title, author, subject, or modified date and confirm those values are searchable when the installed filter exposes them.
8. For a slow or hanging third-party filter, lower the `filesearch.ifilter` timeout in `OutOfProcessExtractionOptions.ExtractorTimeouts` and confirm the extractor host times out without hanging the GUI or indexer.

## Validation Matrix

| Scenario | Expected result |
| --- | --- |
| Registered filter returns text | File content is indexed; diagnostics show `ifilter_fallback_used`. |
| Registered filter returns properties | Exposed property values are indexed as searchable lines. |
| No registered filter | Diagnostic includes `ifilter_not_registered`. |
| Password/encrypted document | Diagnostic includes `ifilter_password` when the filter reports it. |
| Unsupported document format | Diagnostic includes `ifilter_unknown_format` or another `ifilter_*` failure code. |
| Blocked extension | IFilter fallback is skipped by policy. |
| Slow/hung filter | Out-of-process host is killed after the configured extractor timeout. |

## Notes

IFilter property extraction currently supports common scalar `PROPVARIANT` values: strings, numbers, booleans, floating point values, and `FILETIME`. Unsupported property value shapes are skipped rather than failing the whole extraction.
