using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui;
using FileSearch.Gui.Highlighting;
using FileSearch.Gui.Services;
using FileSearch.Gui.Tests;
using FileSearch.Gui.ViewModels;

internal static class ResultRefinementSmoke
{
    public static async Task RunAsync(string output)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "RenewalDocuments"));
        string targetPath = Path.Combine(root, "annual-renewal.txt"), otherPath = Path.Combine(root, "unrelated.txt"), pathOnly = Path.Combine(root, "RenewalDocuments", "evidence.txt");
        var lines = Enumerable.Range(1, 7).Select(line => line <= 4 ? "invoice ordinary line " + line : "invoice annual renewal line " + line).ToArray();
        File.WriteAllLines(targetPath, lines); File.WriteAllText(otherPath, "invoice unrelated"); File.WriteAllText(pathOnly, "invoice evidence");
        var settings = new FakeSettingsService(); var status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        var history = new HistoryViewModel(settings, appSettings, status); var launcher = new FakeFileLauncher();
        var plain = new PlainTextExtractor(); var registry = new ExtractorRegistry([plain], plain);
        using var search = new SearchViewModel(new Searcher(new FileWalker(), registry), registry, new QueryFactory(), new FilePreviewService(registry),
            launcher, settings, new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
        using var index = new IndexViewModel(new FakeFileIndex(), new FakeIndexingService(), settings, appSettings, launcher, new InlineDispatcher(), search, status);
        using var replacements = new ReplacementService(new() { BackupDirectory = Path.Combine(root, "backups") });
        using var replacement = new ReplacementViewModel(replacements, search, settings, history, status);
        var main = new MainViewModel(search, index, history, appSettings, status, null!, new FakeThemeService(), new FakeStyleService(), new FakeShellIntegrationService(), replacement);
        var target = new FileResultViewModel(targetPath, launcher);
        target.AddHits(lines.Select((line, i) => new Hit(targetPath, i + 1, line, [new MatchSpan(0, 7)])).ToArray());
        var other = new FileResultViewModel(otherPath, launcher); other.AddHit(new Hit(otherPath, 1, "invoice unrelated", [new MatchSpan(0, 7)]));
        var pathFile = new FileResultViewModel(pathOnly, launcher); pathFile.AddHit(new Hit(pathOnly, 1, "invoice evidence", [new MatchSpan(0, 7)]));
        foreach (var file in new[] { target, other, pathFile }) search.Files.Add(file);
        search.QueryText = "invoice"; search.SearchPath = root; search.PreviewPaneWidth = 480;
        search.SelectedFile = target;
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        try
        {
            window.Show(); await Settle();
            search.SearchPath = root;
            var box = (TextBox)window.FindName("ResultRefinementBox");
            var list = (ListBox)window.FindName("ResultsList");
            if (System.Windows.Automation.AutomationProperties.GetName(box) != "Filter current results" ||
                !System.Windows.Automation.AutomationProperties.GetHelpText(box).Contains("match excerpts")) throw new InvalidOperationException("The result filter has no clear accessible purpose.");
            box.Text = "annual renewal"; await Settle();
            if (search.FilesVisible != 1 || !ReferenceEquals(search.FilesView.Cast<FileResultViewModel>().Single(), target)) throw new InvalidOperationException("Filtering did not narrow the loaded results.");
            list.UpdateLayout();
            var card = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(target);
            var hitBlocks = Descendants(card).OfType<TextBlock>().Where(block => HitHighlight.GetHit(block) is not null).ToArray();
            if (hitBlocks.Length != 3 || hitBlocks.Any(block => HitHighlight.GetHit(block)!.LineNumber < 5)) throw new InvalidOperationException("Refinement did not reveal the late matching excerpts.");
            if (hitBlocks.Any(block => !Marked(block).Contains("invoice") || !Marked(block).Contains("annual renewal"))) throw new InvalidOperationException("Original and refinement highlights were not rendered together.");
            var tabs = Descendants(window).OfType<TabControl>().Single(control => System.Windows.Automation.AutomationProperties.GetName(control) == "Result preview views");
            tabs.SelectedIndex = 0; await Settle();
            if (!Descendants(tabs).OfType<TextBlock>().Any(block => Marked(block).Contains("annual renewal"))) throw new InvalidOperationException("The Matches pane did not highlight the result filter.");
            var hint = Descendants(window).OfType<TextBlock>().Single(block => block.Text == "Exact text · current results only");
            var area = (FrameworkElement)window.FindName("ResultsArea");
            var hintBounds = hint.TransformToAncestor(window).TransformBounds(new Rect(hint.RenderSize));
            if (!hint.IsVisible || hint.ActualHeight < 8 || hintBounds.Bottom > area.TransformToAncestor(window).Transform(new Point()).Y + 1)
                throw new InvalidOperationException("The purpose hint is clipped or overlaps the result list.");
            Save(window, Path.ChangeExtension(output, ".png"));

            search.SelectedGroupOption = search.ResultGroupOptions.Single(option => option.Value == ResultGroupMode.Folder);
            box.Text = "RenewalDocuments"; await Settle();
            if (search.FilesVisible != 1 || !ReferenceEquals(search.FilesView.Cast<FileResultViewModel>().Single(), pathFile)) throw new InvalidOperationException("The advertised path filter did not match a folder.");
            if (!Descendants(list).OfType<TextBlock>().Any(block => Marked(block).Contains("RenewalDocuments"))) throw new InvalidOperationException("The folder-group path match was not highlighted.");
            search.SelectedGroupOption = search.ResultGroupOptions.Single(option => option.Value == ResultGroupMode.File);
            await Settle();
            if (!Descendants(list).OfType<TextBlock>().Any(block => Marked(block).Contains("RenewalDocuments"))) throw new InvalidOperationException("The card path match was not highlighted.");
            search.ClearRefinementCommand.Execute(null); await Settle();
            if (search.FilesVisible != 3 || target.VisibleHits.First().LineNumber != 1) throw new InvalidOperationException("Clearing refinement did not restore the original results.");
            if (!Descendants(list).OfType<TextBlock>().Any(block => Marked(block).Contains("invoice"))) throw new InvalidOperationException("Clearing refinement lost the primary search highlights.");
            if (Descendants(list).OfType<TextBlock>().Any(block => Marked(block).Contains("RenewalDocuments"))) throw new InvalidOperationException("Cleared refinement left stale path highlights.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { filterPurpose = true, literalMatchedText = true, lateExcerptVisible = true,
                originalHighlightRetained = true, matchesPaneHighlight = true, groupedFolderHighlight = true, cardPathHighlight = true, clearRestoresResults = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.Close(); }
    }

    private static async Task Settle()
    {
        await Task.Delay(550);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static string[] Marked(TextBlock block) => block.Inlines.OfType<Run>().Where(run => run.Background is not null).Select(run => run.Text).ToArray();
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
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
