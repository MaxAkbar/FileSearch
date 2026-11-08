using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Gui.Services;
using Size = System.Windows.Size;

namespace FileSearch.Gui.Controls;

internal sealed record PdfImageViewState(bool Fit, double Zoom, double CenterX, double CenterY,
    int Page, PreviewSourceVersion? SourceVersion);

public partial class FileDocumentPreview
{
    private static readonly SemaphoreSlim PdfTextReaders = new(1, 1);
    private PdfPreviewPage? _pdfText;
    private PdfPreviewFindResult? _pdfFind;
    private PdfPreviewOverlay? _pdfOverlay;
    private ScrollViewer? _pdfScroll;
    private PdfImageViewState? _expandedPdfView;
    private string? _pdfOcrNotice;

    private void CreatePdfSurface(DocumentPreview document, Grid surface)
    {
        _pdfOverlay = new PdfPreviewOverlay();
        surface.Children.Add(_pdfOverlay);
        System.Windows.Automation.AutomationProperties.SetName(surface, "Whole read-only PDF page");
        if (_expandedPdfView is { Fit: false, SourceVersion: not null } state && state.Page == document.Page && state.SourceVersion == document.PdfSourceVersion)
        {
            _fitImage = false; _zoom = state.Zoom;
            _imageBox!.Width = document.Image!.PixelWidth * _zoom;
            _imageBox.Height = document.Image.PixelHeight * _zoom;
        }
    }

    private async Task InitializePdfFindAsync(Hit? hit, CancellationToken token)
    {
        if (_document is not { Kind: DocumentPreviewKind.Pdf } document || _pdfOverlay is null || Result is not { } result) return;
        var overlay = _pdfOverlay;
        _pdfOcrNotice = null;
        if (document.PdfSourceVersion is not { } version)
        {
            _previewFindUnavailable = "The source changed while this page loaded. Reload the preview before using Find.";
            return;
        }
        Status.Text = "Whole PDF page · Preparing embedded-text Find…";
        using var extraction = CancellationTokenSource.CreateLinkedTokenSource(token);
        extraction.CancelAfter(TimeSpan.FromSeconds(15));
        var extractionToken = extraction.Token;
        SetPdfOcrRegion(hit, document, overlay);
        try
        {
            var read = Task.Run(async () =>
            {
                await PdfTextReaders.WaitAsync(extractionToken).ConfigureAwait(false);
                try
                {
                    if (PreviewSourceVersion.Read(result.FullPath) != version) throw new IOException("PDF source changed.");
                    var page = PdfPreviewText.Read(result.FullPath, document.Page, document.PdfPageSize.Width, document.PdfPageSize.Height, extractionToken);
                    if (PreviewSourceVersion.Read(result.FullPath) != version) throw new IOException("PDF source changed.");
                    return page;
                }
                finally { PdfTextReaders.Release(); }
            }, extractionToken);
            // A synchronous parser can finish after cancellation. Its semaphore remains
            // held until it exits; no later selection can start unbounded parallel reads.
            _ = read.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var text = await read.WaitAsync(extractionToken);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(document, _document) || !ReferenceEquals(overlay, _pdfOverlay)) return;
            _pdfText = text;
            _previewFindUnavailable = text.UnavailableReason ?? (text.CanFind ? "" : "This page has no supported embedded text. Use Matches for available OCR hits.");
            SetPdfOcrRegion(hit, document, overlay);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _previewFindUnavailable = "Embedded-text Find took too long. The whole PDF page is still available; reopen the preview to retry.";
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            if (!ReferenceEquals(document, _document)) return;
            if (!PdfSourceIsCurrent(document, result.FullPath)) { overlay.ClearOcrRegion(); _pdfOcrNotice = null; }
            _previewFindUnavailable = "Embedded-text Find is unavailable for this page. The whole PDF page is still available.";
        }
    }

    private void SetPdfOcrRegion(Hit? hit, DocumentPreview document, PdfPreviewOverlay overlay)
    {
        if (hit is null) return;
        SourceLocator? location = new[] { hit.Snippet?.Locator, hit.Locator, SourceLocator.FromAnchor(hit.Anchor) }
            .FirstOrDefault(candidate => candidate?.X is not null && candidate.Y is not null && candidate.Width is > 0 && candidate.Height is > 0);
        if (location is null || location.Page != document.Page) return;
        if (document.PdfSourceVersion is not { } version || hit.SizeBytes != version.Length || hit.ModifiedUtc?.Ticks != version.ModifiedUtc.Ticks)
        { _pdfOcrNotice = "OCR region unavailable: refresh the search to validate its source"; return; }
        var region = PdfOcrRegion(location, document.PdfPageSize);
        if (region is null) { _pdfOcrNotice = "OCR region unavailable: its page dimensions could not be verified"; return; }
        overlay.UpdateOcrRegion(region);
        _pdfOcrNotice = "Selected OCR line region · Existing search hit";
    }

    internal static Rect? PdfOcrRegion(SourceLocator location, Size pageSize)
    {
        if (location is not { X: >= 0, Y: >= 0, Width: > 0, Height: > 0, SourceWidth: > 0, SourceHeight: > 0 } ||
            !double.IsFinite(pageSize.Width) || !double.IsFinite(pageSize.Height) || pageSize.Width <= 0 || pageSize.Height <= 0 ||
            (long)location.X + location.Width > location.SourceWidth || (long)location.Y + location.Height > location.SourceHeight) return null;
        double width = location.SourceWidth.Value, height = location.SourceHeight.Value;
        if (Math.Abs((width / height) / (pageSize.Width / pageSize.Height) - 1) > Math.Max(0.001, 2 / Math.Min(width, height))) return null;
        // OCR coordinates already refer to the rendered, cropped and rotated page.
        return new Rect(location.X.Value / width, location.Y.Value / height, location.Width.Value / width, location.Height.Value / height);
    }

    private static bool PdfSourceIsCurrent(DocumentPreview document, string path) => document.PdfSourceVersion is { } version &&
        PreviewSourceVersion.Read(path) == version;

    private void InvalidatePdfSource()
    {
        CancelPreviewFind();
        _pdfText = null; _pdfFind = null;
        _pdfOverlay?.ClearOcrRegion();
        _previewFindUnavailable = "This PDF changed. Reload the preview before using Find or OCR regions.";
        _pdfOcrNotice = null;
        PreviewFindState = "Unavailable"; PreviewFindStatus.Text = _previewFindUnavailable;
        UpdatePreviewFindControls();
        Status.Text = PdfStatus();
    }

    private void SelectPdfMatch(int index, bool scrollToMatch = true)
    {
        if (_pdfFind is not { } find || _pdfOverlay is not { } overlay || index < 0 || index >= find.Matches.Count) return;
        _previewMatchIndex = index;
        overlay.SetCurrentMatch(index);
        if (scrollToMatch && !_fitImage && _pdfScroll is { } scroll && _imageBox is not null && !overlay.ActiveBounds.IsEmpty)
        {
            var bounds = overlay.ActiveBounds;
            overlay.BringIntoView(new Rect(bounds.X * overlay.ActualWidth, bounds.Y * overlay.ActualHeight,
                bounds.Width * overlay.ActualWidth, bounds.Height * overlay.ActualHeight));
        }
        UpdatePreviewFindStatus();
    }

    private PdfImageViewState? CapturePdfView() => _document is { Kind: DocumentPreviewKind.Pdf } document && _pdfScroll is { } scroll
        ? new(_fitImage, _zoom, scroll.ExtentWidth <= 0 ? 0.5 : (scroll.HorizontalOffset + scroll.ViewportWidth / 2) / scroll.ExtentWidth,
            scroll.ExtentHeight <= 0 ? 0.5 : (scroll.VerticalOffset + scroll.ViewportHeight / 2) / scroll.ExtentHeight,
            document.Page, document.PdfSourceVersion) : null;

    private async Task RestorePdfViewportAsync(CancellationToken token)
    {
        if (_expandedPdfView is not { } state) return;
        _expandedPdfView = null;
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        token.ThrowIfCancellationRequested();
        if (state.SourceVersion is null || _document is not { Kind: DocumentPreviewKind.Pdf } document || state.Page != document.Page ||
            state.SourceVersion != document.PdfSourceVersion || _pdfScroll is not { } scroll) return;
        scroll.UpdateLayout();
        if (state.Fit) { _fitImage = true; FitImage(scroll); }
        else
        {
            scroll.ScrollToHorizontalOffset(state.CenterX * scroll.ExtentWidth - scroll.ViewportWidth / 2);
            scroll.ScrollToVerticalOffset(state.CenterY * scroll.ExtentHeight - scroll.ViewportHeight / 2);
        }
    }

    private string PdfStatus() => "Whole PDF page · Read-only" + (_pdfOcrNotice is { } notice ? " · " + notice : "") +
        (_pdfText?.CanFind == true ? " · Find searches embedded text on this page" : " · " + _previewFindUnavailable);
}
