using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Gui.Services;
using FileSearch.Gui.ViewModels;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;

namespace FileSearch.Gui.Controls;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF controls release their preview resources when hidden or unloaded.")]
public partial class FileDocumentPreview : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ResultProperty = DependencyProperty.Register(nameof(Result), typeof(FileResultViewModel),
        typeof(FileDocumentPreview), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty PreferredHitProperty = DependencyProperty.Register(nameof(PreferredHit), typeof(Hit),
        typeof(FileDocumentPreview), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(nameof(FallbackText), typeof(string),
        typeof(FileDocumentPreview), new PropertyMetadata("", (owner, _) => ((FileDocumentPreview)owner).RefreshFallback()));
    private CancellationTokenSource? _load;
    private DocumentPreview? _document;
    private WebView2? _browser;
    private NativePreviewSession? _native;
    private WindowsFormsHost? _nativeHost;
    private Viewbox? _imageBox;
    private double _zoom = 1;
    private double _fitScale = 1;
    private bool _fitImage = true;
    private int? _requestedPage;
    private string? _selectedPart;
    private bool _settingPart;
    private bool _animate = true;
    private readonly List<Stream> _resourceStreams = [];
    private ArchivePreviewLease? _archiveLease;
    private FileDocumentPreview? _archiveChild;
    internal int ArchiveDepth { get; set; }
    internal string? DisplayNameOverride { get; set; }
    private SpreadsheetPreview? _expandedWorkbook;
    private DatabasePreviewControl? _databaseView;
    private DatabaseLocation? _expandedDatabaseLocation;

    public FileDocumentPreview()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = ReloadAsync();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = ReloadAsync(); else Stop(); };
    }

    public FileResultViewModel? Result { get => (FileResultViewModel?)GetValue(ResultProperty); set => SetValue(ResultProperty, value); }
    public Hit? PreferredHit { get => (Hit?)GetValue(PreferredHitProperty); set => SetValue(PreferredHitProperty, value); }
    public string FallbackText { get => (string)GetValue(FallbackTextProperty); set => SetValue(FallbackTextProperty, value); }
    public string PreviewState { get; private set; } = "Empty";

    private static void OnSourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var preview = (FileDocumentPreview)owner;
        preview._requestedPage = null;
        preview._selectedPart = null;
        preview._animate = true;
        preview._expandedWorkbook = null;
        preview._expandedDatabaseLocation = null;
        _ = preview.ReloadAsync();
    }

    private void Stop()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
        _native?.Dispose();
        _native = null;
        _nativeHost?.Dispose();
        _nativeHost = null;
        _browser?.Dispose();
        _browser = null;
        foreach (var stream in _resourceStreams) stream.Dispose();
        _resourceStreams.Clear();
        _archiveChild?.Stop();
        _databaseView?.Dispose();
        _databaseView = null;
        _archiveChild = null;
        _archiveLease?.Dispose();
        _archiveLease = null;
        Body.Content = null;
        _imageBox = null;
        _document = null;
        PartSelector.ItemsSource = null;
    }

    private async Task ReloadAsync(int? page = null)
    {
        if (!IsLoaded || !IsVisible) return;
        Stop();
        _document = null;
        PageControls.Visibility = ZoomControls.Visibility = PartSelector.Visibility = AnimationButton.Visibility = Visibility.Collapsed;
        var result = Result;
        if (result is null) { PreviewState = "Empty"; FileNameLabel.Text = Summary.Text = ""; Status.Text = "Select a result to preview the file."; return; }
        _load = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var load = _load;
        var token = load.Token;
        PreviewState = "Loading";
        FileNameLabel.Text = DisplayNameOverride ?? result.DisplayName;
        FileNameLabel.ToolTip = DisplayNameOverride ?? result.FullPath;
        Summary.Text = result.DisplayName;
        Status.Text = "Loading file preview…";
        try
        {
            var hit = PreferredHit ?? result.Hits.FirstOrDefault(hit => hit.Kind == HitKind.Content);
            var document = await DocumentPreviewService.LoadAsync(result.FullPath, token, hit, _selectedPart,
                page ?? _requestedPage ?? DocumentPreviewService.FindInitialPdfPage(result.Hits, PreferredHit), result.MailMessage).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            _document = document;
            Summary.Text = document.Summary;
            Summary.ToolTip = DisplayNameOverride ?? result.FullPath;
            if (document.Parts is { Count: > 0 })
            {
                _settingPart = true;
                PartSelector.ItemsSource = document.Parts;
                PartSelector.SelectedValue = _selectedPart = document.SelectedPart;
                _settingPart = false;
                PartSelector.Visibility = Visibility.Visible;
            }
            switch (document.Kind)
            {
                case DocumentPreviewKind.Database:
                    _databaseView = new DatabasePreviewControl();
                    Body.Content = _databaseView;
                    await _databaseView.InitializeAsync(result.FullPath, _expandedDatabaseLocation ?? hit?.Snippet?.Locator?.Database ?? hit?.Locator?.Database ?? hit?.Anchor?.Database, token);
                    _expandedDatabaseLocation = null;
                    break;
                case DocumentPreviewKind.Image:
                case DocumentPreviewKind.Pdf:
                    _zoom = 1;
                    _fitImage = true;
                    _imageBox = new Viewbox { Stretch = Stretch.Uniform, Child = new System.Windows.Controls.Image { Source = document.Image } };
                    var scroll = new ScrollViewer { Content = _imageBox, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    scroll.SizeChanged += (_, _) => FitImage(scroll);
                    scroll.ScrollChanged += (_, _) => FitImage(scroll);
                    Body.Content = scroll;
                    ZoomControls.Visibility = Visibility.Visible;
                    if (document.PageCount > 1)
                    {
                        _requestedPage = document.Page;
                        PageControls.Visibility = Visibility.Visible;
                        PageLabel.Text = $"{document.Page} / {document.PageCount}";
                        PreviousPage.IsEnabled = document.Page > 1;
                        NextPage.IsEnabled = document.Page < document.PageCount;
                    }
                    if (document.Animation is not null)
                    {
                        AnimationButton.Visibility = Visibility.Visible;
                        AnimationButton.Content = _animate ? "Pause animation" : "Play animation";
                        if (_animate)
                        {
                            ZoomControls.Visibility = Visibility.Collapsed;
                            var content = new HtmlPreviewContent();
                            var url = content.AddResource(document.Animation);
                            await ShowHtmlAsync(DocumentPreviewService.WrapHtml("<img alt='Animated GIF' src='" + url + "'>"), token, content.Resources);
                        }
                    }
                    break;
                case DocumentPreviewKind.Html:
                    await ShowHtmlAsync(document.Text, token, document.Resources).WaitAsync(token);
                    break;
                case DocumentPreviewKind.Spreadsheet:
                    Body.Content = new SpreadsheetPreviewControl(_expandedWorkbook ?? document.Workbook!);
                    _expandedWorkbook = null;
                    break;
                case DocumentPreviewKind.Archive:
                    if (document.SelectedPart is null) ShowFallback("This ZIP contains no previewable file entries.");
                    else if (ArchiveDepth >= 3) ShowFallback("Nested archive previews are limited to three levels.");
                    else
                    {
                        var lease = await ArchivePreviewService.OpenMemberAsync(result.FullPath, document.SelectedPart, token);
                        if (token.IsCancellationRequested) { lease.Dispose(); token.ThrowIfCancellationRequested(); }
                        _archiveLease = lease;
                        _archiveChild = new FileDocumentPreview
                        {
                            Result = new FileResultViewModel(lease.Path, new PreviewOnlyLauncher()),
                            PreferredHit = hit is null ? null : hit with { Path = lease.Path, LineNumber = hit.Anchor?.Line ?? 1 },
                            ArchiveDepth = ArchiveDepth + 1,
                            DisplayNameOverride = document.SelectedPart,
                        };
                        _archiveChild.ExpandButton.Visibility = Visibility.Collapsed;
                        Body.Content = _archiveChild;
                    }
                    break;
                case DocumentPreviewKind.Shell:
                    var panel = new Forms.Panel { BackColor = System.Drawing.Color.White };
                    _nativeHost = new WindowsFormsHost { Child = panel };
                    Body.Content = _nativeHost;
                    _native = new NativePreviewSession();
                    if (!await _native.StartAsync(panel.Handle, result.FullPath, token))
                    {
                        token.ThrowIfCancellationRequested();
                        _native.Dispose();
                        _nativeHost.Dispose();
                        _native = null;
                        _nativeHost = null;
                        ShowFallback("A visual preview is unavailable for this file. Use Matches or Open.");
                    }
                    else _ = WatchNativeExitAsync(_native, token);
                    break;
                case DocumentPreviewKind.Text:
                    ShowText(document.Text, Path.GetExtension(result.FullPath));
                    if (Body.Content is TextEditor textEditor && hit?.LineNumber is > 0 && hit.LineNumber <= textEditor.LineCount)
                        textEditor.ScrollToLine(hit.LineNumber);
                    break;
                default:
                    ShowFallback(document.Text);
                    break;
            }
            token.ThrowIfCancellationRequested();
            _load?.CancelAfter(Timeout.InfiniteTimeSpan);
            PreviewState = document.Kind == DocumentPreviewKind.Shell && _native is null ? "Unavailable" : document.Kind.ToString();
            Status.Text = document.IsTruncated ? "Showing the first 250,000 characters. Use Open to view the rest." :
                document.Kind == DocumentPreviewKind.Pdf ? "Whole PDF page · Read-only" :
                document.Kind == DocumentPreviewKind.Details || PreviewState == "Unavailable" ? "File details / text fallback" :
                document.Kind == DocumentPreviewKind.Image ? $"{document.Image!.PixelWidth:n0} × {document.Image.PixelHeight:n0} preview pixels · Read-only" : "Read-only file preview";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(load, _load) && IsVisible && load.IsCancellationRequested)
            {
                Stop();
                ShowFallback("The preview took too long. Use Matches or Open.");
                PreviewState = "Unavailable";
                Status.Text = "Preview unavailable";
            }
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested) return;
            Stop();
            ShowFallback(error is InvalidDataException ? error.Message + " Use Matches or Open." : "A visual preview is unavailable for this file. Use Matches or Open.");
            PreviewState = "Unavailable";
            Status.Text = "Preview unavailable";
        }
    }

    private void ShowText(string text, string extension = "")
    {
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(extension);
        if (definition is null && extension is ".json" or ".jsonc" or ".ts" or ".tsx" or ".jsx")
            definition = HighlightingManager.Instance.GetDefinition("JavaScript");
        var editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 13,
            WordWrap = true,
            Text = text,
            SyntaxHighlighting = definition,
            Background = System.Windows.Media.Brushes.WhiteSmoke,
            Foreground = System.Windows.Media.Brushes.Black,
            Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(editor, "Read-only file contents");
        Body.Content = editor;
    }

    private void ShowFallback(string message) => ShowText(message +
        (string.IsNullOrWhiteSpace(FallbackText) ? "" : "\n\n" + FallbackText));

    private void RefreshFallback()
    {
        if (_document?.Kind == DocumentPreviewKind.Details && IsLoaded && IsVisible) ShowFallback(_document.Text);
    }

    private async Task WatchNativeExitAsync(NativePreviewSession session, CancellationToken token)
    {
        try
        {
            await session.WaitForExitAsync(token);
            if (token.IsCancellationRequested || !ReferenceEquals(_native, session)) return;
            session.Dispose();
            _native = null;
            _nativeHost?.Dispose();
            _nativeHost = null;
            ShowFallback("The visual preview closed. Use Matches or Open, or select the file again to retry.");
            PreviewState = "Unavailable";
            Status.Text = "Preview unavailable";
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task ShowHtmlAsync(string html, CancellationToken token, IReadOnlyDictionary<string, PreviewResource>? resources = null)
    {
        var browser = new WebView2();
        _browser = browser;
        Body.Content = browser;
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSearch", "PreviewWebView2"));
        token.ThrowIfCancellationRequested();
        await browser.EnsureCoreWebView2Async(environment);
        token.ThrowIfCancellationRequested();
        var core = browser.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Serve generated HTML and approved embedded images entirely from memory, avoiding NavigateToString's 2 MB cap.
        var generatedUrl = "https://filesearch-preview.invalid/" + Guid.NewGuid().ToString("N") + "/document.html";
        var approved = resources is null ? new Dictionary<string, PreviewResource>(StringComparer.Ordinal) : new Dictionary<string, PreviewResource>(resources, StringComparer.Ordinal);
        approved.Add(generatedUrl, new PreviewResource(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8"));
        ulong navigationId = 0;
        core.NavigationStarting += (_, args) =>
        {
            args.Cancel = args.Uri.Split('#')[0] != generatedUrl && args.Uri != "about:blank";
            if (!args.Cancel) navigationId = args.NavigationId;
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (args.NavigationId == navigationId) navigation.TrySetResult(args.IsSuccess);
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (approved.TryGetValue(args.Request.Uri, out var resource))
            {
                var stream = new MemoryStream(resource.Bytes, writable: false);
                _resourceStreams.Add(stream);
                args.Response = environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + resource.ContentType + "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            else if (!args.Request.Uri.StartsWith("about:", StringComparison.Ordinal) && !args.Request.Uri.StartsWith("data:", StringComparison.Ordinal))
                args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        core.Navigate(generatedUrl);
        if (!await navigation.Task.WaitAsync(token)) throw new IOException("The document preview could not be rendered.");
    }

    private async void OnPreviousPage(object sender, RoutedEventArgs e) { _animate = false; if (_document is { Page: > 1 } doc) await ReloadAsync(doc.Page - 1); }
    private async void OnNextPage(object sender, RoutedEventArgs e) { _animate = false; if (_document is { } doc && doc.Page < doc.PageCount) await ReloadAsync(doc.Page + 1); }
    private async void OnToggleAnimation(object sender, RoutedEventArgs e) { _animate = !_animate; await ReloadAsync(); }
    private async void OnPartChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingPart || PartSelector.SelectedValue is not string selected || selected == _selectedPart) return;
        _selectedPart = selected;
        await ReloadAsync();
    }
    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void OnFit(object sender, RoutedEventArgs e)
    {
        _fitImage = true;
        if (Body.Content is ScrollViewer scroll) FitImage(scroll);
    }
    private void FitImage(ScrollViewer scroll)
    {
        if (!_fitImage || _imageBox is null || _document?.Image is not { } image || !ReferenceEquals(Body.Content, scroll)) return;
        double width = Math.Max(1, scroll.ViewportWidth - 2), height = Math.Max(1, scroll.ViewportHeight - 2);
        _imageBox.Width = width;
        _imageBox.Height = height;
        _zoom = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
        _fitScale = _zoom;
    }
    private void SetZoom(double value)
    {
        if (_imageBox is null || _document?.Image is not { } image) return;
        _fitImage = false;
        _zoom = Math.Clamp(value, 0.01, Math.Max(4, _fitScale * 4));
        _imageBox.Width = image.PixelWidth * _zoom;
        _imageBox.Height = image.PixelHeight * _zoom;
    }

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        if (Result is null) return;
        var window = new Window
        {
            Title = Result.DisplayName + " — FileSearch preview",
            Width = 1000,
            Height = 720,
            MinWidth = 600,
            MinHeight = 400,
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new FileDocumentPreview
            {
                Result = Result,
                FallbackText = FallbackText,
                PreferredHit = PreferredHit,
                _requestedPage = _document?.PageCount > 0 ? _document.Page : null,
                _selectedPart = _selectedPart,
                _animate = _animate,
                _expandedWorkbook = (Body.Content as SpreadsheetPreviewControl)?.CurrentPosition,
                _expandedDatabaseLocation = _databaseView?.CurrentLocation,
                ArchiveDepth = ArchiveDepth,
                DisplayNameOverride = DisplayNameOverride
            },
        };
        ((FileDocumentPreview)window.Content).ExpandButton.Visibility = Visibility.Collapsed;
        window.Show();
    }

    private sealed class PreviewOnlyLauncher : IFileLauncher
    {
        public void Open(string path) { }
        public Task<bool> OpenAtLocationAsync(string path, Hit hit, CancellationToken cancellationToken) => Task.FromResult(false);
        public void OpenImageOcrPreview(ImageOcrPreviewViewModel preview) { }
        public void RevealInExplorer(string path) { }
        public void CopyToClipboard(string text) { }
    }
}
