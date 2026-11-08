using FileSearch.Core.Databases;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.Controls;

public partial class FileDocumentPreview
{
    private sealed record ArchiveViewState(string Member, string Fingerprint, ArchiveChildState Child);
    private sealed record ArchiveChildState(int? Page, string? Part, bool Animate, SpreadsheetPreview? Workbook,
        DatabaseLocation? Database, DatabasePreviewFindState? DatabaseFind, DocumentPreviewFindState? Find,
        PdfImageViewState? Pdf, ArchiveViewState? Archive);

    private ArchiveViewState? _expandedArchiveView;
    private string? _archiveSourceFingerprint;

    private ArchiveViewState? CaptureArchiveView()
    {
        if (_document is not { Kind: DocumentPreviewKind.Archive, SelectedPart: { } member } ||
            _archiveChild is not { } child || _archiveSourceFingerprint is not { } fingerprint)
            return null;
        return new(member, fingerprint, new(
            child._document?.PageCount > 0 ? child._document.Page : child._requestedPage,
            child._selectedPart, child._animate,
            (child.Body.Content as SpreadsheetPreviewControl)?.CurrentPosition,
            child._databaseView?.CurrentLocation, child._databaseView?.CaptureFindState(),
            child.CapturePreviewFindState(), child.CapturePdfView(), child.CaptureArchiveView()));
    }

    private void RestoreArchiveChildState(string member, ArchivePreviewLease lease)
    {
        var saved = _expandedArchiveView;
        _expandedArchiveView = null;
        if (saved is null || _archiveChild is not { Result: { } result } child ||
            saved.Member != member || saved.Fingerprint != lease.SourceFingerprint)
            return;

        var state = saved.Child;
        var version = PreviewSourceVersion.Read(lease.Path);
        child._requestedPage = state.Page;
        child._selectedPart = state.Part;
        child._animate = state.Animate;
        child._expandedWorkbook = state.Workbook;
        child._expandedDatabaseLocation = state.Database;
        child._expandedDatabaseFindState = state.DatabaseFind;
        // A validated identical member has a fresh temporary path and timestamps.
        // Bind its retained view state to this new read-only copy only after the
        // original archive fingerprint and exact member name match.
        child._expandedPreviewFindState = state.Find is { } find
            ? find with { SourceKey = result.ResultKey, SourceVersion = find.SourceVersion is null ? null : version }
            : null;
        child._expandedPdfView = state.Pdf is { } pdf ? pdf with { SourceVersion = version } : null;
        child._expandedArchiveView = state.Archive;
    }
}
