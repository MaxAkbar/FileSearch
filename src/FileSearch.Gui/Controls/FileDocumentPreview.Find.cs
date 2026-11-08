using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileSearch.Core.Engine;
using FileSearch.Gui.Services;
using ICSharpCode.AvalonEdit;

namespace FileSearch.Gui.Controls;

internal sealed record DocumentPreviewFindState(string SourceKey, DocumentPreviewKind Kind, string? Part, int Page, PreviewSourceVersion? SourceVersion, string Text, string Query, bool MatchCase,
    bool IsOpen, bool HasCompletedFind, int MatchNumber);

public partial class FileDocumentPreview
{
    private TextEditor? _findEditor;
    private PreviewFindBackgroundRenderer? _findRenderer;
    private PreviewBrowserFind? _browserFind;
    private PreviewTextFindResult? _textFind;
    private CancellationTokenSource? _previewFindLoad;
    private DocumentPreviewFindState? _expandedPreviewFindState;
    private int _previewFindGeneration, _previewMatchIndex = -1;
    private bool _settingPreviewFind, _previewFinding, _previewFindEdited;
    private string _previewFindUnavailable = "Select a text or formatted document preview to find text.";
    public bool IsPreviewFindVisible => PreviewFindPanel.Visibility == Visibility.Visible;
    public string PreviewFindState { get; private set; } = "Idle";
    public string PreviewFindStatusText => PreviewFindStatus.Text;
    public int PreviewMatchCount => _pdfFind?.Matches.Count ?? _textFind?.Matches.Count ?? _browserFind?.MatchCount ?? 0;
    public int CurrentPreviewMatchNumber => _textFind is not null || _pdfFind is not null ? _previewMatchIndex + 1 : Math.Max(0, _browserFind?.ActiveMatchIndex ?? 0);
    public bool CanFindInPreview => (_document?.Kind is DocumentPreviewKind.Text or DocumentPreviewKind.Html &&
        (_findEditor is not null || _browserFind?.IsAvailable == true)) ||
        (_document?.Kind == DocumentPreviewKind.Pdf && _pdfText?.CanFind == true && _pdfOverlay is not null);
    public static readonly RoutedEvent PreviewFindVisibilityChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(PreviewFindVisibilityChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(FileDocumentPreview));
    public event RoutedEventHandler PreviewFindVisibilityChanged
    {
        add => AddHandler(PreviewFindVisibilityChangedEvent, value);
        remove => RemoveHandler(PreviewFindVisibilityChangedEvent, value);
    }

    private void SetPreviewFindVisible(bool visible)
    {
        var value = visible ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewFindPanel.Visibility == value) return;
        PreviewFindPanel.Visibility = value;
        RaiseEvent(new RoutedEventArgs(PreviewFindVisibilityChangedEvent, this));
    }

    public void ShowPreviewFind()
    {
        if (!CanFindInPreview) return;
        SetPreviewFindVisible(true);
        PreviewFindQuery.Focus(); PreviewFindQuery.SelectAll();
    }

    private void OnTogglePreviewFind(object sender, RoutedEventArgs args)
    {
        if (IsPreviewFindVisible) ClosePreviewFind(); else ShowPreviewFind();
    }
    private async void OnStartPreviewFind(object sender, RoutedEventArgs args) => await FindInPreviewAsync();
    private async void OnPreviousPreviewMatch(object sender, RoutedEventArgs args) => await MovePreviewMatchAsync(-1);
    private async void OnNextPreviewMatch(object sender, RoutedEventArgs args) => await MovePreviewMatchAsync(1);
    private void OnStopPreviewFind(object sender, RoutedEventArgs args) => CancelPreviewFind();
    private void OnClosePreviewFind(object sender, RoutedEventArgs args) => ClosePreviewFind();

    public void ClosePreviewFind()
    {
        CancelPreviewFind();
        ClearPreviewMatches();
        SetPreviewFindVisible(false);
        if (_findEditor is not null) _findEditor.Focus(); else if (_pdfScroll is not null) _pdfScroll.Focus(); else _browser?.Focus();
    }

    private void OnPreviewFindInputChanged(object sender, RoutedEventArgs args)
    {
        if (_settingPreviewFind || PreviewFindStatus is null) return;
        _previewFindEdited = true;
        CancelPreviewFind(); ClearPreviewMatches();
        PreviewFindState = "Idle";
        PreviewFindStatus.Text = FindScope() + " · Literal phrase. Choose Find to search.";
        UpdatePreviewFindControls();
    }

    public void CancelPreviewFind()
    {
        bool searching = _previewFinding;
        ++_previewFindGeneration;
        _previewFindLoad?.Cancel();
        _browserFind?.Stop();
        _previewFinding = false;
        ClearPreviewMatches();
        if (searching)
        {
            PreviewFindState = "Canceled";
            PreviewFindStatus.Text = "Preview Find canceled. Choose Find to start again.";
        }
        else
        {
            PreviewFindState = "Idle";
            PreviewFindStatus.Text = FindScope() + " · Literal phrase";
        }
        UpdatePreviewFindControls();
    }

    private void ClearPreviewMatches()
    {
        _textFind = null; _previewMatchIndex = -1;
        _pdfFind = null; _pdfOverlay?.Clear();
        _findRenderer?.Clear();
        if (_findEditor is not null) _findEditor.Select(_findEditor.CaretOffset, 0);
    }

    private void StopPreviewFindRendering()
    {
        CancelPreviewFind(); ClearPreviewMatches();
        if (_findEditor is not null && _findRenderer is not null)
            _findEditor.TextArea.TextView.BackgroundRenderers.Remove(_findRenderer);
        _findEditor = null; _findRenderer = null;
        _pdfText = null; _pdfFind = null;
        _pdfOverlay?.ClearOcrRegion(); _pdfOverlay = null; _pdfScroll = null; _pdfOcrNotice = null;
        if (_browserFind is not null) { _browserFind.Changed -= OnBrowserFindChanged; _browserFind.Dispose(); _browserFind = null; }
        PreviewFindState = "Idle";
        UpdatePreviewFindControls();
    }

    private void PreviewFindSourceChanged(DependencyPropertyChangedEventArgs args)
    {
        if (args.Property != ResultProperty || args.OldValue is ViewModels.FileResultViewModel old &&
            args.NewValue is ViewModels.FileResultViewModel next && old.ResultKey == next.ResultKey) return;
        _expandedPreviewFindState = null;
        _expandedPdfView = null;
        _previewFindEdited = false;
        _settingPreviewFind = true;
        PreviewFindQuery.Text = ""; PreviewFindCase.IsChecked = false;
        _settingPreviewFind = false;
        SetPreviewFindVisible(false);
    }

    private void AttachPreviewTextFind(TextEditor editor)
    {
        _findEditor = editor;
        _findRenderer = new PreviewFindBackgroundRenderer(editor);
        editor.TextArea.TextView.BackgroundRenderers.Add(_findRenderer);
        _previewFindUnavailable = "";
    }

    private void AttachPreviewBrowserFind()
    {
        if (_document?.Kind != DocumentPreviewKind.Html || _browser?.CoreWebView2 is not { } core) return;
        try
        {
            _browserFind = new PreviewBrowserFind(core);
            _browserFind.Changed += OnBrowserFindChanged;
            _previewFindUnavailable = "";
        }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { _previewFindUnavailable = "Find is unavailable in this WebView2 runtime. Use Matches or Open."; }
    }

    private async Task InitializePreviewFindAsync(Hit? hit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _previewFindUnavailable = _document?.Kind switch
        {
            DocumentPreviewKind.Pdf => "Preparing embedded-text Find for this PDF page.",
            DocumentPreviewKind.Database => "Use Find in table in the database preview.",
            DocumentPreviewKind.Spreadsheet => "Use the grid's cell navigation. Text Find is available in text and formatted previews.",
            DocumentPreviewKind.Image => "Images do not contain searchable preview text. Use Matches for available OCR hits.",
            _ => "Find is available in loaded text and formatted document previews.",
        };
        if (_document?.Kind == DocumentPreviewKind.Text && Body.Content is TextEditor editor) AttachPreviewTextFind(editor);
        if (_document?.Kind == DocumentPreviewKind.Html) AttachPreviewBrowserFind();
        if (_document?.Kind == DocumentPreviewKind.Pdf) await InitializePdfFindAsync(hit, token);
        token.ThrowIfCancellationRequested();
        if (!_previewFindEdited && _expandedPreviewFindState is null && hit?.Kind == HitKind.Content && DocumentPreviewService.MatchedText(hit) is { Length: > 0 } seed && seed.Length <= PreviewTextFind.MaxQueryCharacters)
        { _settingPreviewFind = true; PreviewFindQuery.Text = seed; _settingPreviewFind = false; }
        UpdatePreviewFindControls();
        if (_expandedPreviewFindState is { } restored)
        {
            _expandedPreviewFindState = null;
            if (restored.SourceKey == Result?.ResultKey)
            {
                _settingPreviewFind = true;
                PreviewFindQuery.Text = restored.Query; PreviewFindCase.IsChecked = restored.MatchCase;
                _settingPreviewFind = false; _previewFindEdited = true;
                SetPreviewFindVisible(restored.IsOpen && CanFindInPreview);
                if (CanFindInPreview && restored.Query.Length > 0 && restored.HasCompletedFind)
                {
                    var document = _document;
                    var browser = _browserFind;
                    var search = FindInPreviewAsync();
                    int generation = _previewFindGeneration;
                    await search;
                    token.ThrowIfCancellationRequested();
                    if (generation == _previewFindGeneration && ReferenceEquals(document, _document) &&
                        restored.Kind == document?.Kind && restored.Part == document.SelectedPart && restored.Page == document.Page &&
                        restored.SourceVersion == document.PdfSourceVersion &&
                        restored.Text == PreviewFindContent(document) &&
                        PreviewFindState is "Ready" or "Partial" && restored.MatchNumber > 0 && restored.MatchNumber <= PreviewMatchCount)
                    {
                        if (_textFind is not null) SelectPreviewTextMatch(restored.MatchNumber - 1);
                        else if (_pdfFind is not null) SelectPdfMatch(restored.MatchNumber - 1);
                        else if (browser is not null && ReferenceEquals(browser, _browserFind))
                        {
                            try { await browser.MoveToAsync(restored.MatchNumber, token); }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested && generation != _previewFindGeneration) { }
                            catch (Exception error) when (error is TimeoutException or NotImplementedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                            {
                                if (generation != _previewFindGeneration || !ReferenceEquals(browser, _browserFind) || token.IsCancellationRequested) return;
                                PreviewFindState = "Unavailable";
                                _previewFindUnavailable = "Find navigation is unavailable. Reopen this preview to retry.";
                                PreviewFindStatus.Text = _previewFindUnavailable;
                                UpdatePreviewFindControls();
                            }
                        }
                    }
                }
                else PreviewFindStatus.Text = CanFindInPreview ? FindScope() + " · Literal phrase. Choose Find to search." : _previewFindUnavailable;
                return;
            }
        }
        if (!CanFindInPreview) SetPreviewFindVisible(false);
        else if (IsPreviewFindVisible)
            RaiseEvent(new RoutedEventArgs(PreviewFindVisibilityChangedEvent, this));
        PreviewFindStatus.Text = CanFindInPreview ? FindScope() + " · Literal phrase" : _previewFindUnavailable;
        if (CanFindInPreview && PreviewFindQuery.Text.Length > 0)
        {
            var search = FindInPreviewAsync();
            int generation = _previewFindGeneration;
            await search;
            token.ThrowIfCancellationRequested();
            if (generation == _previewFindGeneration && !_previewFindEdited && hit?.Kind == HitKind.Content && _findEditor is { } text && _textFind is { } matches && hit.LineNumber > 0 && hit.LineNumber <= text.LineCount)
            {
                var line = text.Document.GetLineByNumber(hit.LineNumber);
                for (int index = 0; index < matches.Matches.Count; index++)
                    if (matches.Matches[index].Start >= line.Offset && matches.Matches[index].Start <= line.EndOffset)
                    { SelectPreviewTextMatch(index); break; }
            }
        }
    }

    private string FindScope() => (_document?.Kind == DocumentPreviewKind.Pdf ? $"Embedded text · This PDF page ({_document.Page} / {_document.PageCount})" :
        _document?.Parts is { Count: > 0 } ? "This displayed part" : "This loaded preview") +
        (_document?.IsTruncated == true ? " · Partial: only loaded text is searched" : "");

    private void UpdatePreviewFindControls()
    {
        if (PreviewFindToggle is null || PreviewFindStart is null) return;
        PreviewFindToggle.IsEnabled = CanFindInPreview;
        PreviewFindToggle.ToolTip = CanFindInPreview ? "Find literal text in this preview (Ctrl+F)" : _previewFindUnavailable;
        PreviewFindStart.IsEnabled = CanFindInPreview && !_previewFinding;
        PreviewFindStop.IsEnabled = _previewFinding;
        PreviewFindPrevious.IsEnabled = PreviewFindNext.IsEnabled = CanFindInPreview && !_previewFinding && PreviewMatchCount > 0;
    }

    public async Task FindInPreviewAsync()
    {
        if (!CanFindInPreview) return;
        CancelPreviewFind(); ClearPreviewMatches();
        string query = PreviewFindQuery.Text;
        if (query.Length == 0) { PreviewFindState = "Idle"; PreviewFindStatus.Text = FindScope() + " · Enter a literal phrase."; return; }
        int generation = ++_previewFindGeneration;
        using var search = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _previewFindLoad = search;
        var token = search.Token;
        _previewFinding = true; PreviewFindState = "Searching";
        PreviewFindStatus.Text = "Finding text in this preview…"; UpdatePreviewFindControls();
        bool matchCase = PreviewFindCase.IsChecked == true;
        var editor = _findEditor; var browser = _browserFind;
        var pdf = _pdfText; var document = _document; string? sourcePath = Result?.FullPath;
        try
        {
            if (pdf is not null && document?.Kind == DocumentPreviewKind.Pdf && sourcePath is not null)
            {
                var result = await Task.Run(() =>
                {
                    if (!PdfSourceIsCurrent(document, sourcePath)) throw new System.IO.IOException("PDF source changed.");
                    var found = PdfPreviewText.Find(pdf, query, matchCase, token);
                    if (!PdfSourceIsCurrent(document, sourcePath)) throw new System.IO.IOException("PDF source changed.");
                    return found;
                }, token);
                token.ThrowIfCancellationRequested();
                if (generation != _previewFindGeneration || !ReferenceEquals(document, _document) || !ReferenceEquals(pdf, _pdfText)) return;
                _pdfFind = result;
                _pdfOverlay?.Update(result.Matches.Select(match => match.Regions).ToArray(), result.Matches.Count > 0 ? 0 : -1);
                if (result.Matches.Count > 0) SelectPdfMatch(0);
            }
            else if (editor is not null)
            {
                string text = editor.Text;
                var result = await Task.Run(() => PreviewTextFind.Find(text, query, matchCase, token), token);
                token.ThrowIfCancellationRequested();
                if (generation != _previewFindGeneration || !ReferenceEquals(editor, _findEditor)) return;
                _textFind = result; _findRenderer?.Update(result.Matches);
                if (result.Matches.Count > 0) SelectPreviewTextMatch(0);
            }
            else if (browser is not null)
            {
                await browser.FindAsync(query, matchCase, token);
                token.ThrowIfCancellationRequested();
                if (generation != _previewFindGeneration || !ReferenceEquals(browser, _browserFind)) return;
            }
            PreviewFindState = _document?.IsTruncated == true || _textFind?.IsComplete == false || _pdfFind?.IsComplete == false ? "Partial" : PreviewMatchCount == 0 ? "NoMatches" : "Ready";
            UpdatePreviewFindStatus();
        }
        catch (OperationCanceledException)
        {
            if (generation != _previewFindGeneration) return;
            ClearPreviewMatches(); PreviewFindState = "Canceled";
            PreviewFindStatus.Text = "Preview Find canceled or timed out. Choose Find to try again.";
        }
        catch (System.IO.IOException) when (document?.Kind == DocumentPreviewKind.Pdf)
        {
            if (generation == _previewFindGeneration && ReferenceEquals(document, _document)) InvalidatePdfSource();
        }
        catch (Exception error) when (error is NotImplementedException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            if (generation != _previewFindGeneration) return;
            ClearPreviewMatches(); PreviewFindState = "Unavailable";
            _previewFindUnavailable = "Find is unavailable in this preview. The document remains read-only; use Matches or Open.";
            PreviewFindStatus.Text = _previewFindUnavailable;
        }
        finally
        {
            if (ReferenceEquals(_previewFindLoad, search)) _previewFindLoad = null;
            if (generation == _previewFindGeneration) { _previewFinding = false; UpdatePreviewFindControls(); }
        }
    }

    public async Task MovePreviewMatchAsync(int direction)
    {
        if (!CanFindInPreview || _previewFinding) return;
        if (PreviewMatchCount == 0) { await FindInPreviewAsync(); return; }
        if (_pdfFind is { Matches.Count: > 0 } pdf && _document is { } document && Result is { } source)
        {
            int generation = _previewFindGeneration;
            bool current = await Task.Run(() => PdfSourceIsCurrent(document, source.FullPath));
            if (generation != _previewFindGeneration || !ReferenceEquals(document, _document) || !ReferenceEquals(pdf, _pdfFind)) return;
            if (!current) { InvalidatePdfSource(); return; }
            SelectPdfMatch((_previewMatchIndex + (direction < 0 ? -1 : 1) + pdf.Matches.Count) % pdf.Matches.Count);
        }
        else if (_textFind is { Matches.Count: > 0 } result)
            SelectPreviewTextMatch((_previewMatchIndex + (direction < 0 ? -1 : 1) + result.Matches.Count) % result.Matches.Count);
        else
        {
            try { _browserFind?.Move(direction); }
            catch (Exception error) when (error is NotImplementedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                PreviewFindState = "Unavailable";
                _previewFindUnavailable = "Find navigation is unavailable. Reopen this preview to retry.";
                PreviewFindStatus.Text = _previewFindUnavailable;
                UpdatePreviewFindControls(); return;
            }
        }
        UpdatePreviewFindStatus();
    }

    private void SelectPreviewTextMatch(int index)
    {
        if (_textFind is not { } result || _findEditor is not { } editor || index < 0 || index >= result.Matches.Count) return;
        var match = result.Matches[index]; _previewMatchIndex = index;
        editor.Select(match.Start, match.Length);
        var location = editor.Document.GetLocation(match.Start);
        editor.ScrollTo(location.Line, location.Column);
        UpdatePreviewFindStatus();
    }

    private void OnBrowserFindChanged(object? sender, EventArgs args)
    {
        if (!ReferenceEquals(sender, _browserFind) || _previewFinding || PreviewFindState is not ("Ready" or "NoMatches" or "Partial")) return;
        if (_browserFind?.IsAvailable != true)
        {
            PreviewFindState = "Unavailable";
            _previewFindUnavailable = "Find is unavailable in this browser preview. Reopen it to retry.";
            PreviewFindStatus.Text = _previewFindUnavailable;
            UpdatePreviewFindControls(); return;
        }
        UpdatePreviewFindStatus(); UpdatePreviewFindControls();
    }

    private void UpdatePreviewFindStatus()
    {
        string count = PreviewMatchCount == 0 ? "No matches" : $"Match {CurrentPreviewMatchNumber:n0} of {PreviewMatchCount:n0}";
        PreviewFindStatus.Text = count + " · " + FindScope() +
            (_textFind?.LimitReason is { } reason ? " · Partial: " + reason : "") +
            (_pdfFind?.LimitReason is { } pdfReason ? " · Partial: " + pdfReason : "") +
            (_browserFind is not null ? " · Browser text matching" : "");
    }

    private DocumentPreviewFindState? CapturePreviewFindState() => CanFindInPreview && Result is { } result && _document is { } document
        ? new(result.ResultKey, document.Kind, document.SelectedPart, document.Page, document.PdfSourceVersion, PreviewFindContent(document), PreviewFindQuery.Text, PreviewFindCase.IsChecked == true,
            IsPreviewFindVisible, PreviewFindState is "Ready" or "NoMatches" or "Partial", CurrentPreviewMatchNumber) : null;

    private string PreviewFindContent(DocumentPreview document)
    {
        // Embedded image URLs are regenerated for each read-only HTML preview.
        // Normalize only these known URLs so equivalent rendered text can retain its occurrence.
        string text = document.Kind == DocumentPreviewKind.Pdf ? _pdfText?.Text ?? "" : document.Text;
        if (document.Kind == DocumentPreviewKind.Html && document.Resources is { } resources)
        {
            int index = 0;
            foreach (string url in resources.Keys)
                text = text.Replace(url, $"filesearch-preview-resource-{index++}", StringComparison.Ordinal);
        }
        return text;
    }

    internal static bool TryRoutePreviewFindKey(object? source, Key key, ModifierKeys modifiers, out bool handled)
    {
        handled = false;
        var current = source as DependencyObject;
        while (current is not null && current is not FileDocumentPreview) current = PreviewKeyParent(current);
        if (current is not FileDocumentPreview preview || !preview.CanFindInPreview) return false;
        if (key == Key.F && modifiers == ModifierKeys.Control) { preview.ShowPreviewFind(); handled = true; return true; }
        if (key == Key.F3 && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        { preview.SetPreviewFindVisible(true); preview.RoutePreviewFindAction(() => _ = preview.MovePreviewMatchAsync(modifiers == ModifierKeys.Shift ? -1 : 1)); handled = true; return true; }
        if (key == Key.Escape && modifiers == ModifierKeys.None && (preview._previewFinding || preview.IsPreviewFindVisible))
        { preview.RoutePreviewFindAction(() => { if (preview._previewFinding) preview.CancelPreviewFind(); else preview.ClosePreviewFind(); }); handled = true; return true; }
        current = source as DependencyObject;
        while (current is not null && !ReferenceEquals(current, preview.PreviewFindQuery)) current = PreviewKeyParent(current);
        if (current is not null && key == Key.Enter && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        { preview.RoutePreviewFindAction(() => { if (preview.PreviewMatchCount > 0) _ = preview.MovePreviewMatchAsync(modifiers == ModifierKeys.Shift ? -1 : 1); else _ = preview.FindInPreviewAsync(); }); handled = true; return true; }
        return key == Key.C && modifiers == ModifierKeys.Control || key == Key.Enter && modifiers == ModifierKeys.None;
    }

    private void RoutePreviewFindAction(Action action)
    {
        if (_browserFind is not { } browser) { action(); return; }
        int generation = _previewFindGeneration;
        // WebView2 forwards accelerators synchronously while its process is blocked.
        // Make native calls only after that input callback has returned.
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(browser, _browserFind) && generation == _previewFindGeneration) action();
        });
    }

    private static DependencyObject? PreviewKeyParent(DependencyObject child) => child is FrameworkContentElement content ? content.Parent :
        child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
}
