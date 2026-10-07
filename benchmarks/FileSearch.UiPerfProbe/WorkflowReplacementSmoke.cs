using System.Diagnostics;
using System.Text.Json;
using System.Windows;
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
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Core.Workflows;
using FileSearch.Gui;
using FileSearch.Gui.Settings;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

internal static class WorkflowReplacementSmoke
{
    private static int _checks;
    public static async Task RunAsync(string output)
    {
        var root = Path.Combine(Path.GetTempPath(), "workflow-replace-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        _checks = 0;
        var first = Path.Combine(root, "needle.txt"); var second = Path.Combine(root, "needle2.txt");
        File.WriteAllText(first, "needle"); File.WriteAllText(second, "needle");
        var settings = new FakeSettingsService(); var status = new StatusBarViewModel(); var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status); var plain = new PlainTextExtractor(); var registry = new ExtractorRegistry([plain], plain);
        var launcher = new FakeFileLauncher(); var searcher = new Searcher(new FileWalker(), registry);
        using var search = new SearchViewModel(searcher, registry, new QueryFactory(), new FakePreviewService(), launcher, settings,
            new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        search.SearchPath = root; search.QueryText = "needle";
        using var service = new ReplacementService(new() { BackupDirectory = Path.Combine(root, "backups") });
        using var replacement = new ReplacementViewModel(service, search, settings, history, status);
        var runner = new WorkflowRunner(searcher, new QueryFactory(), registry, replacement: service);
        using var workflows = new WorkflowsViewModel(new JsonWorkflowStore(Path.Combine(root, "workflows")), runner, launcher, new FakeFolderPicker(), service, replacement, settings);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher, new InlineDispatcher(), search, status);
        var main = new MainViewModel(search, index, history, appSettings, status, workflows, new FakeThemeService(), new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var window = new WorkflowsWindow { DataContext = main, Width = 1440, Height = 950, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); await Settle(window);
            var definition = new WorkflowDefinition { Name = "Rename and clean up", Steps = [
                new ReplacementStep { Id = "names", Find = "needle", ReplaceWith = "thread", Target = ReplacementTarget.Names, Roots = [root] },
                new ReplacementStep { Id = "contents", Find = "needle", ReplaceWith = "thread", Roots = [root] }] };
            typeof(WorkflowsViewModel).GetMethod("LoadDefinition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(workflows, [definition, null]);
            workflows.SelectedStep = workflows.StepRows[0]; await Settle(window);
            Require(Descendants(window).OfType<TextBox>().Any(box => box.Text == "needle"), "Replacement editor is rendered.");
            var editor = Descendants(window).OfType<TextBox>().First(box => box.Text == "needle");
            Require(editor.Foreground is SolidColorBrush ink && editor.Background is SolidColorBrush paper && ink.Color != paper.Color, "Editor text has theme contrast.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            Render(window, Path.ChangeExtension(output, ".editor.png"));
            Invoke(FindButton(window, "Dry run")); await Until(() => !workflows.IsRunning);
            Require(File.ReadAllText(first) == "needle" && File.Exists(second), "Dry run leaves files untouched.");
            Require(workflows.RunLog.Any(line => line.Contains("[dry run]")) && await service.GetLastRecoveryGroupAsync(CancellationToken.None) is null, "Dry run logs previews and creates no recovery group.");
            Invoke(FindButton(window, "Run"));
            var review = await ReviewWindow(); await Settle(review);
            var model = (WorkflowReplacementReviewViewModel)review.DataContext;
            Require(model.Items.Count == 2 && model.CheckedChanges == 2, "First review shows both name changes.");
            model.Items.Single(item => item.Path == second).IsChecked = false;
            Require(model.CheckedChanges == 1 && model.CheckedIds.Count == 1, "Checkboxes control the approved item set.");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(review), 0, Key.Return) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            review.RaiseEvent(key); Require(key.Handled && File.Exists(first), "Enter does not apply replacements.");
            await Settle(review); Render(review, Path.ChangeExtension(output, ".review.png"));
            Invoke((Button)review.FindName("ApplyButton")); await Until(() => !review.IsVisible);
            var contentReview = await ReviewWindow(); await Settle(contentReview);
            var contentModel = (WorkflowReplacementReviewViewModel)contentReview.DataContext;
            contentModel.Items.Single(item => item.Path == second).IsChecked = false;
            Require(File.Exists(Path.Combine(root, "thread.txt")) && File.ReadAllText(second) == "needle", "Only the approved name changed before the next step.");
            Invoke((Button)contentReview.FindName("ApplyButton")); await Until(() => !workflows.IsRunning);
            Require(File.ReadAllText(Path.Combine(root, "thread.txt")) == "thread" && File.ReadAllText(second) == "needle", "Only approved content changed.");
            Require(workflows.RunStatusText.StartsWith("Completed") && workflows.CanUndoReplacementRun, "Completed workflow exposes grouped Undo.");
            Invoke(Descendants(window).OfType<Button>().Single(button => Equals(button.Content, "Undo workflow replacements")));
            await Until(() => !workflows.IsRunning);
            Require(File.Exists(first) && File.ReadAllText(first) == "needle" && !File.Exists(Path.Combine(root, "thread.txt")), "Undo restores content then the original name.");
            Require(!workflows.CanUndoReplacementRun, "Completed Undo clears recovery availability.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks = _checks, summary = workflows.RunStatusText }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }
    private static Button FindButton(Window window, string label) => Descendants(window).OfType<Button>().Single(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == label));
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task<WorkflowReplacementReviewWindow> ReviewWindow()
    {
        await Until(() => Application.Current.Windows.OfType<WorkflowReplacementReviewWindow>().Any(window => window.IsVisible));
        return Application.Current.Windows.OfType<WorkflowReplacementReviewWindow>().Single(window => window.IsVisible);
    }
    private static async Task Until(Func<bool> complete)
    {
        var clock = Stopwatch.StartNew(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        while (!complete()) { if (clock.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Workflow smoke check timed out."); await Task.Delay(20); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task Settle(Window window) { window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static void Render(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
