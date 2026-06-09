# Security Policy

## Supported Versions

FileSearch is under active development. Security fixes should target the current default branch first unless a release branch is explicitly created later.

## Reporting a Vulnerability

Report security issues privately to the repository owner instead of opening a public issue. Include:

- affected version or commit,
- steps to reproduce,
- expected impact,
- any relevant files or sample inputs.

Avoid sharing sensitive local files in reports. If a proof of concept requires a file sample, create a minimal synthetic sample.

## Security Model

FileSearch is a local desktop search application. It reads files from folders selected by the user, extracts text locally, and may store extracted lines in the local CSharpDB index when indexing is enabled.

Important local-risk areas:

- document parsing for PDFs, Office files, archives, email, and markup formats,
- shell integration through per-user Explorer registry keys,
- MSIX packaging with `runFullTrust`,
- local index storage under `%LocalAppData%\FileSearch\Index`.

## Release Security

Release packaging should follow `docs/RELEASE_CHECKLIST.md`.

Before public release:

- run CI build/test workflows,
- review dependency updates,
- sign release packages when distributing outside Partner Center signing,
- verify generated artifacts before upload,
- document any security-sensitive behavior changes in release notes.
