using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Volumes;
using FileSearch.Gui;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

internal static class QuickInteractionSmoke
{
    public static async Task RunAsync(string output)
    {
        output = Path.GetFullPath(output);
        string root = Path.Combine(Path.GetDirectoryName(output)!, "quick-interaction-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = Enumerable.Range(0, 70).Select(index => Path.Combine(root, $"ordinary-needle-{index:00}.txt")).ToArray();
        string best = Path.Combine(root, "needle.txt");
        foreach (string path in paths.Append(best)) File.WriteAllText(path, $"needle matching text from {Path.GetFileName(path)}");
        var settings = new FakeSettingsService();
        settings.Current.QuickSearchDefaultScope = QuickSearchScopeKind.CurrentFolder;
        settings.Current.QuickSearchLastScope = QuickSearchScopeKind.CurrentFolder;
        settings.Current.QuickSearchFolderPath = root;
        settings.Current.QuickSearchIncludeContent = true;
        var status = new StatusBarViewModel();
        var applicationSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, applicationSettings, status);
        var launcher = new RecordingLauncher();
        var plain = new PlainTextExtractor();
        var registry = new ExtractorRegistry([plain], plain);
        var preview = new FilePreviewService(registry);
        var source = new GatedSearcher(paths, best);
        await using var volume = new CoveredVolumeIndex();
        using var main = new SearchViewModel(source, registry, new QueryFactory(), preview, launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        main.SearchPath = root;
        using var quick = new QuickSearchViewModel(source, new QueryFactory(), preview, launcher, settings, main,
            new FakeFolderPicker(), volumeNameIndex: volume);
        var window = new QuickSearchWindow { DataContext = quick, Width = 760, Height = 600, Left = -32000, Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        int hides = 0;
        quick.RequestHide += (_, _) => hides++;
        var rows = new List<object>();
        try
        {
            window.Show(); quick.PrepareForShow(); quick.SearchText = "needle";
            await WaitUntil(() => quick.Results.Count == paths.Length);
            await Settle();
            var list = (ListBox)window.FindName("ResultsList");
            var query = (TextBox)window.FindName("SearchBox");
            var scope = (ComboBox)window.FindName("QuickLocationScope");
            var matches = (TextBox)window.FindName("QuickMatchesPreview");
            Require(AutomationProperties.GetName(query) == "Quick Search query" &&
                AutomationProperties.GetName(list) == "Quick Search results" &&
                AutomationProperties.GetName(matches) == "Quick Search matching text preview", "Core Quick Search controls have no accessible names.");
            rows.Add(new { check = "query-results-preview-accessible-names" });

            Require(Route(window, query, Key.Down), "Down from query did not focus results.");
            var initiallySelected = quick.SelectedResult;
            Require(initiallySelected is not null, "Down did not select a result.");
            list.SelectedItem = quick.Results[35];
            list.ScrollIntoView(list.SelectedItem); list.UpdateLayout();
            var selectedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem);
            Press(window, selectedRow, Key.Down);
            var chosen = quick.SelectedResult ?? throw new InvalidOperationException("Keyboard navigation lost its selection.");
            Require(!ReferenceEquals(chosen, initiallySelected), "Keyboard navigation did not change the selection.");
            Require(Route(window, selectedRow, Key.F4), "Preview shortcut was not handled.");
            await WaitUntil(() => quick.IsPreviewVisible && quick.PreviewContent.Contains(Path.GetFileName(chosen.FullPath), StringComparison.Ordinal));
            string previewBeforeArrival = quick.PreviewContent;
            source.Release.TrySetResult();
            await WaitUntil(() => !quick.IsSearching && quick.Results.Count == paths.Length + 1);
            Require(ReferenceEquals(quick.SelectedResult, chosen) && quick.PreviewContent == previewBeforeArrival,
                "A delayed higher-ranked arrival stole the explicit keyboard selection or preview.");
            rows.Add(new { check = "streaming-arrival-preserves-keyboard-selection-and-preview", selected = chosen.ResultKey,
                selectedIndex = quick.Results.IndexOf(chosen), first = quick.Results[0].ResultKey });

            int actions = launcher.ActionCount;
            Require(!Route(window, scope, Key.Enter) && !Route(window, (CheckBox)window.FindName("QuickContentToggle"), Key.Enter),
                "Native scope/content Enter was intercepted by the result shortcut.");
            Require(launcher.ActionCount == actions && hides == 0, "Native control Enter opened a result.");
            rows.Add(new { check = "scope-and-content-enter-remain-native" });

            var tabs = Descendants(window).OfType<TabControl>().Single(control => AutomationProperties.GetName(control) == "Quick Search preview views");
            tabs.SelectedIndex = 0; await Settle(); matches.SelectAll();
            Require(!Route(window, matches, Key.C, ModifierKeys.Control) && !Route(window, matches, Key.Enter),
                "Matches preview native copy/Enter was intercepted.");
            Require(!Route(window, query, Key.C, ModifierKeys.Control) && launcher.ActionCount == actions && hides == 0,
                "Text editing copied a path or hid Quick Search.");
            rows.Add(new { check = "query-and-matches-native-copy-enter", selectedCharacters = matches.SelectionLength });

            list.ScrollIntoView(chosen); list.UpdateLayout();
            selectedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(chosen);
            Require(UIElementAutomationPeer.CreatePeerForElement(selectedRow)?.GetName() == chosen.DisplayName, "Result row has no accessible filename.");
            var buttons = Descendants(selectedRow).OfType<Button>().Where(button => button.IsVisible).ToArray();
            foreach (var name in new[] { "Open result", "Reveal result in Explorer", "Copy result path", chosen.PinActionText })
                Require(buttons.Any(button => new ButtonAutomationPeer(button).GetName() == name), $"Result action lacks accessible name: {name}");
            var open = buttons.Single(button => AutomationProperties.GetName(button) == "Open result");
            Require(!Route(window, open, Key.Enter), "Enter on a row action was intercepted by selected-result open.");
            DoubleClick(window, list);
            DoubleClick(window, open);
            Require(launcher.ActionCount == actions, "Double-clicking list whitespace or an interactive child opened a result.");
            rows.Add(new { check = "row-accessibility-and-double-click-source-guards" });

            quick.SelectedResult = quick.Results.First(result => !ReferenceEquals(result, chosen));
            Invoke(buttons.Single(button => AutomationProperties.GetName(button) == "Copy result path"));
            await WaitUntil(() => launcher.Copied is not null);
            Require(launcher.Copied == chosen.FullPath && hides == 1, "Copy action targeted the wrong result or did not dismiss.");
            Show(window); Invoke(buttons.Single(button => AutomationProperties.GetName(button) == "Reveal result in Explorer"));
            await WaitUntil(() => launcher.Revealed is not null);
            Require(launcher.Revealed == chosen.FullPath && hides == 2, "Reveal action targeted the wrong result.");
            Show(window); Invoke(open); await WaitUntil(() => launcher.Opened is not null);
            Require(launcher.Opened == chosen.FullPath && hides == 3, "Open action targeted the wrong result.");
            rows.Add(new { check = "uia-copy-reveal-open-use-row-target", copied = launcher.Copied, revealed = launcher.Revealed, opened = launcher.Opened });

            Show(window); list.ScrollIntoView(chosen); list.UpdateLayout();
            selectedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(chosen);
            var pin = Descendants(selectedRow).OfType<Button>().Single(button => AutomationProperties.GetName(button) == chosen.PinActionText);
            Invoke(pin); await Settle();
            Require(settings.Current.QuickSearchPinnedPaths.Contains(chosen.FullPath) && chosen.IsPinned && hides == 4,
                "Pin action did not retain its target or expected dismissal.");
            Show(window); Invoke(pin); await Settle();
            Require(!settings.Current.QuickSearchPinnedPaths.Contains(chosen.FullPath) && !chosen.IsPinned && hides == 4 && window.IsVisible,
                "Unpin action changed target or unexpectedly dismissed.");
            rows.Add(new { check = "uia-pin-unpin-and-dynamic-accessible-name", pinName = new ButtonAutomationPeer(pin).GetName() });

            quick.SelectedResult = chosen;
            Require(Route(window, selectedRow, Key.C, ModifierKeys.Control), "Result copy shortcut was not handled.");
            Require(launcher.Copied == chosen.FullPath && hides == 5, "Result copy shortcut did not use the selected result.");
            Show(window); Require(Route(window, selectedRow, Key.R, ModifierKeys.Control), "Result reveal shortcut was not handled.");
            Require(launcher.Revealed == chosen.FullPath && hides == 6, "Result reveal shortcut did not use the selected result.");
            Show(window); Require(Route(window, query, Key.Enter), "Query Enter did not open the selected result.");
            await WaitUntil(() => hides == 7);
            Require(launcher.Opened == chosen.FullPath, "Query Enter opened the wrong result.");
            Show(window); Require(Route(window, selectedRow, Key.P, ModifierKeys.Control), "Result pin shortcut was not handled.");
            Require(chosen.IsPinned && hides == 8, "Result pin shortcut did not pin the selected result.");
            Show(window); Require(Route(window, selectedRow, Key.P, ModifierKeys.Control), "Result unpin shortcut was not handled.");
            Require(!chosen.IsPinned && hides == 8, "Result unpin shortcut dismissed unexpectedly.");
            rows.Add(new { check = "keyboard-copy-reveal-open-pin-unpin", selected = chosen.ResultKey });

            quick.IsPreviewVisible = false;
            list.SelectedItem = quick.Results[^1];
            var last = quick.SelectedResult!;
            list.ScrollIntoView(quick.Results[0]); list.UpdateLayout(); await Settle();
            Require(Route(window, query, Key.Down), "Down did not restore the selected virtualized result.");
            var lastRow = list.ItemContainerGenerator.ContainerFromItem(last) as ListBoxItem;
            Require(lastRow is not null && lastRow.IsVisible && ReferenceEquals(quick.SelectedResult, last),
                "Focus results failed to realize and retain the offscreen selected row.");
            rows.Add(new { check = "focus-results-realizes-offscreen-selection", selected = last.ResultKey });

            actions = launcher.ActionCount;
            quick.SearchText = "different query";
            Route(window, query, Key.Enter);
            Require(launcher.ActionCount == actions && hides == 8, "Immediate Enter after query change opened a stale result during debounce.");
            rows.Add(new { check = "query-change-immediate-enter-cannot-open-old-selection" });
            Save(window, Path.ChangeExtension(output, ".png"));
            File.WriteAllText(output, JsonSerializer.Serialize(new { verification = "Scripted WPF routes and UI Automation; fake shell, no physical input", rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            if (!window.IsVisible) window.Show();
            Save(window, Path.ChangeExtension(output, ".failed.png"));
            File.WriteAllText(output + ".partial.json", JsonSerializer.Serialize(new { rows }, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally { source.Release.TrySetResult(); window.Close(); }
    }

    private static bool Route(QuickSearchWindow window, object source, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        (bool)typeof(QuickSearchWindow).GetMethod("TryHandleQuickKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [source, key, modifiers])!;
    private static void Press(QuickSearchWindow window, UIElement target, Key key)
    {
        if (Route(window, target, key)) return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
    }
    private static void DoubleClick(QuickSearchWindow window, UIElement target)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Control.MouseDoubleClickEvent, Source = target };
        typeof(QuickSearchWindow).GetMethod("OnResultDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [target, args]);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    private static void Show(Window window) { if (!window.IsVisible) window.Show(); }
    private static async Task Settle() { await Task.Delay(80); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!condition()) await Task.Delay(20, deadline.Token);
        await Settle();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class RecordingLauncher : IFileLauncher
    {
        public string? Opened, Revealed, Copied;
        public int ActionCount;
        public void Open(string path) { Opened = path; ActionCount++; }
        public Task<bool> OpenAtLocationAsync(string path, Hit hit, CancellationToken cancellationToken) { Open(path); return Task.FromResult(true); }
        public void OpenImageOcrPreview(ImageOcrPreviewViewModel preview) => throw new InvalidOperationException("Unexpected OCR action.");
        public void RevealInExplorer(string path) { Revealed = path; ActionCount++; }
        public void CopyToClipboard(string text) { Copied = text; ActionCount++; }
    }
    private sealed class GatedSearcher(string[] paths, string best) : ISearcher
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<Hit> SearchAsync(SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string path in paths) { cancellationToken.ThrowIfCancellationRequested(); yield return new Hit(path, 1, "needle matching text", [], Score: 1); }
            await Release.Task.WaitAsync(cancellationToken);
            yield return new Hit(best, 1, "needle matching text", [], Score: 100);
        }
    }
    private sealed class CoveredVolumeIndex : IVolumeNameIndex
    {
        public event EventHandler<VolumeIndexStatus>? StatusChanged { add { } remove { } }
        public Task<VolumeTermSearchResult> SearchAsync(VolumeTermSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new VolumeTermSearchResult([], 0, request.ScopeRoots ?? [], [], TimeSpan.Zero));
        public IReadOnlyList<VolumeIndexCandidate> GetCandidateVolumes() => [];
        public IReadOnlyList<VolumeIndexStatus> GetStatuses() => [];
        public Task StartTrackingAsync(IReadOnlyCollection<string> volumeRoots, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<VolumeIndexStatus> BuildAsync(string volumeRoot, VolumeBuildMethod method, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RemoveAsync(string volumeRoot, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool IsReady(string path) => true;
        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
