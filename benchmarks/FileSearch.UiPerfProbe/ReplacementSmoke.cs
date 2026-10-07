using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui;
using FileSearch.Gui.Controls;
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

internal static class ReplacementSmoke
{
    public static async Task RunAsync(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "filesearch-replacement-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "needle.txt");
        File.WriteAllText(path, "A needle in the first line.\r\nAnother needle here.\r\n");
        var status = new StatusBarViewModel(); var settings = new FakeSettingsService();
        var appSettings = new ApplicationSettingsViewModel(settings, status); var history = new HistoryViewModel(settings, appSettings, status);
        var launcher = new FakeFileLauncher(); var plain = new PlainTextExtractor(); var registry = new ExtractorRegistry([plain], plain);
        using var search = new SearchViewModel(new Searcher(new FileWalker(), registry), registry, new QueryFactory(), new FakePreviewService(), launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        search.QueryText = "type:txt needle"; search.SearchPath = directory;
        using var service = new ReplacementService(new() { BackupDirectory = Path.Combine(directory, "backups") });
        using var replacement = new ReplacementViewModel(service, search, settings, history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher, new InlineDispatcher(), search, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(), new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); await Settle(window);
            var shortcut = typeof(MainWindow).GetMethod("TryExecuteShortcut", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Require(shortcut.Invoke(window, [AppShortcutAction.FindAndReplace, main]) is true && replacement.IsOpen, "Find and Replace shortcut opens the panel.");
            var query = (TextBox)window.FindName("QueryBox");
            query.Text = "needle"; query.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            var replace = (TextBox)window.FindName("ReplacementTextBox");
            replace.Text = "thread"; replace.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            // Invoke the actual WPF preview-key handler. Enter must never apply, including when a button has focus.
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Return) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            typeof(MainWindow).GetMethod("OnMainWindowPreviewKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, key]);
            Require(key.Handled, "Enter is handled as Preview.");
            while (replacement.IsBusy) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Settle(window);
            Require(File.ReadAllText(path).Contains("needle"), "Enter preview does not write.");
            Require(replacement.Items.Count == 1 && replacement.CheckedChanges == 2, "Preview contains the exact count.");
            var results = Descendants(window).OfType<ReplacementResultsView>().Single();
            Require(results.IsVisible && results.ActualHeight > 250, "Replacement results are rendered in the results area.");
            var grid = (DataGrid)results.FindName("ReplacementGrid");
            Require(grid.Items.Count == 1 && Descendants(grid).OfType<DataGridRow>().Any(), "Preview row is realized.");
            Require(replacement.SelectedPreview.Contains("BEFORE") && replacement.SelectedPreview.Contains("thread"), "Before/after preview is populated.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var screenshot = Path.ChangeExtension(output, ".png");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(screenshot)) encoder.Save(stream);
            var apply = Descendants(results).OfType<Button>().Single(button => Equals(button.Content, "Apply checked"));
            var undo = Descendants(results).OfType<Button>().Single(button => Equals(button.Content, "Undo last batch"));
            Require(apply.IsEnabled, "Apply is enabled only after preview.");
            ((IInvokeProvider)new ButtonAutomationPeer(apply).GetPattern(PatternInterface.Invoke)).Invoke();
            await Settle(window);
            while (replacement.IsBusy) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Settle(window);
            Require(File.ReadAllText(path).Contains("thread") && !File.ReadAllText(path).Contains("needle"), "Apply changes the file.");
            Require(!apply.IsEnabled && undo.IsEnabled, "A completed batch disables Apply and enables Undo.");
            ((IInvokeProvider)new ButtonAutomationPeer(undo).GetPattern(PatternInterface.Invoke)).Invoke();
            await Settle(window);
            while (replacement.IsBusy) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Settle(window);
            Require(File.ReadAllText(path).Contains("needle") && !File.ReadAllText(path).Contains("thread"), "Undo restores the original file.");
            shortcut.Invoke(window, [AppShortcutAction.FindAndReplace, main]); await Settle(window);
            Require(query.Text == "type:txt needle" && search.SearchMode == QueryMode.Unified, "Closing restores normal search state.");
            Require(!results.IsVisible, "Replacement results collapse on close.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks = 14, screenshot, summary = replacement.Summary }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Settle(Window window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
