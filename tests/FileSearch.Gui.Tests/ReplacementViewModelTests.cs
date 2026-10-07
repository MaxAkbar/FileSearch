using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class ReplacementViewModelTests
{
    [Fact]
    public void UsesFreshFolderAndInvalidatesPreviewWhenInputsOrScopeChange()
    {
        Run((pump, search, replacement, settings, history) =>
        {
            replacement.IsOpen = true; replacement.FindText = "needle";
            Execute(pump, replacement.PreviewCommand.ExecuteAsync(null));
            Assert.Single(replacement.Items); Assert.True(replacement.ApplyCommand.CanExecute(null)); Assert.Equal(2, replacement.CheckedChanges);
            Assert.Empty(search.Files);
            replacement.ReplaceWith = "thread"; Assert.Empty(replacement.Items); Assert.False(replacement.ApplyCommand.CanExecute(null));
            Execute(pump, replacement.PreviewCommand.ExecuteAsync(null));
            search.IncludeSubfolders = false; Assert.Empty(replacement.Items);
            Execute(pump, replacement.PreviewCommand.ExecuteAsync(null));
            replacement.IncludeFormulas = true; Assert.Empty(replacement.Items);
        });
    }

    [Fact]
    public void ReplacementDefaultsStayIndependentOfNormalMatchingOptions()
    {
        Run((pump, search, replacement, settings, history) =>
        {
            search.SearchMode = QueryMode.Regex; search.MatchCase = true;
            replacement.ToggleCommand.Execute(null);
            Assert.Equal(ReplacementTarget.Contents, replacement.Target);
            Assert.False(replacement.UseRegex); Assert.False(replacement.MatchCase);
            Assert.False(replacement.IncludeFormulas); Assert.False(replacement.IncludeExtensions);
            replacement.ToggleCommand.Execute(null);
            Assert.Equal(QueryMode.Regex, search.SearchMode); Assert.True(search.MatchCase);
        });
    }

    [Fact]
    public void MainQueryProxyRestoresUnifiedQueryAndOnlyPreviews()
    {
        Run((pump, search, replacement, settings, history) =>
        {
            var appSettings = new ApplicationSettingsViewModel(settings, new());
            var main = new MainViewModel(search, null!, history, appSettings, new(), null!, new FakeThemeService(), new FakeStyleService(), new FakeShellIntegrationService(), replacement);
            search.QueryText = "type:txt needle"; search.SearchMode = QueryMode.Unified;
            replacement.ToggleCommand.Execute(null); Assert.Equal("", main.ActiveQueryText);
            main.ActiveQueryText = "needle"; main.ActiveRegex = true; main.ActiveMatchCase = true;
            Assert.Equal("type:txt needle", search.QueryText); Assert.Equal(QueryMode.Unified, search.SearchMode);
            Assert.Same(replacement.PreviewCommand, main.ActiveStartCommand);
            Execute(pump, replacement.PreviewCommand.ExecuteAsync(null));
            Assert.Equal("needle needle", File.ReadAllText(Path.Combine(search.SearchPath, "needle.txt")));
            replacement.ToggleCommand.Execute(null);
            Assert.Equal("type:txt needle", main.ActiveQueryText); Assert.Equal(QueryMode.Unified, search.SearchMode); Assert.False(main.ActiveRegex);
        });
    }

    [Fact]
    public void ApplyingAndUndoingRefreshResultsAndPersistPathReferences()
    {
        Run((pump, search, replacement, settings, history) =>
        {
            var oldPath = Path.Combine(search.SearchPath, "needle.txt"); var newPath = Path.Combine(search.SearchPath, "thread.txt");
            history.FavoriteResults.Add(new() { Path = oldPath });
            settings.Current.QuickSearchPinnedPaths.Add(oldPath);
            history.Workspaces.Add(new() { Name = "test", Search = new() { SearchPath = search.SearchPath, QueryText = "needle" }, PinnedPaths = [oldPath] });
            replacement.IsOpen = true; replacement.Target = ReplacementTarget.Names; replacement.FindText = "needle"; replacement.ReplaceWith = "thread";
            Execute(pump, replacement.PreviewCommand.ExecuteAsync(null)); Execute(pump, replacement.ApplyCommand.ExecuteAsync(null));
            Assert.True(File.Exists(newPath)); Assert.False(replacement.ApplyCommand.CanExecute(null)); Assert.True(replacement.CanUndo);
            Assert.Equal(newPath, Assert.Single(history.FavoriteResults).Path); Assert.Equal(newPath, Assert.Single(settings.Current.QuickSearchPinnedPaths));
            Assert.Equal(newPath, Assert.Single(history.Workspaces).PinnedPaths.Single());
            Execute(pump, replacement.UndoCommand.ExecuteAsync(null));
            Assert.True(File.Exists(oldPath)); Assert.Equal(oldPath, Assert.Single(history.FavoriteResults).Path); Assert.False(replacement.CanUndo);
        });
    }

    [Fact]
    public void UncheckingRowsPreventsApplyAndChangesExactCounts()
    {
        Run((pump, search, replacement, settings, history) =>
        {
            replacement.FindText = "needle"; replacement.ReplaceWith = "thread";
            Execute(pump, replacement.PreviewCommand.ExecuteAsync(null));
            replacement.Items[0].IsChecked = false; Assert.Equal(0, replacement.CheckedChanges); Assert.False(replacement.ApplyCommand.CanExecute(null));
        });
    }

    [Fact]
    public void CtrlHDefaultCanBeConfiguredAndPersists()
    {
        var settings = new FakeSettingsService(); var vm = new ApplicationSettingsViewModel(settings, new());
        Assert.Equal(AppShortcutGesture.CtrlH, vm.GetShortcut(AppShortcutAction.FindAndReplace));
        var binding = Assert.Single(vm.ShortcutBindings, binding => binding.Action == AppShortcutAction.FindAndReplace);
        binding.SetGesture(AppShortcutGesture.CtrlO);
        Assert.Equal(AppShortcutGesture.CtrlO, settings.Current.Shortcuts.FindAndReplace);
        var reloaded = new ApplicationSettingsViewModel(settings, new());
        Assert.Equal(AppShortcutGesture.CtrlO, reloaded.GetShortcut(AppShortcutAction.FindAndReplace));
    }

    private static void Execute(PumpingSynchronizationContext pump, Task task)
    {
        pump.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(20)); task.GetAwaiter().GetResult();
    }
    private static void Run(Action<PumpingSynchronizationContext, SearchViewModel, ReplacementViewModel, FakeSettingsService, HistoryViewModel> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "replacement-gui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previous = SynchronizationContext.Current; var pump = new PumpingSynchronizationContext(); SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            File.WriteAllText(Path.Combine(directory, "needle.txt"), "needle needle");
            var settings = new FakeSettingsService(); var status = new StatusBarViewModel(); var appSettings = new ApplicationSettingsViewModel(settings, status);
            var history = new HistoryViewModel(settings, appSettings, status); var plain = new PlainTextExtractor(); var registry = new ExtractorRegistry([plain], plain);
            using var search = new SearchViewModel(new Searcher(new FileWalker(), registry), registry, new QueryFactory(), new FakePreviewService(), new FakeFileLauncher(), settings,
                new FakeFileTypeOptionsStore(), new FakeFolderPicker(), history, status);
            search.SearchPath = directory; search.QueryText = "needle";
            using var service = new ReplacementService(new() { BackupDirectory = Path.Combine(directory, "backups") });
            using var replacement = new ReplacementViewModel(service, search, settings, history, status);
            action(pump, search, replacement, settings, history);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); Directory.Delete(directory, true); }
    }
}
