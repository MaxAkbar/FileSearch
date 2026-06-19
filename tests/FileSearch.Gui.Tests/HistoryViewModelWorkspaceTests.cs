using FileSearch.Core.Queries;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

public sealed class HistoryViewModelWorkspaceTests
{
    [Fact]
    public void ExportWorkspaceWritesShareablePackage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.filesearch-workspace.json");
        var savePicker = new FakeFileSavePicker { PathToReturn = path };

        try
        {
            var history = CreateHistory(savePicker, out var status);
            history.SaveWorkspace(new WorkspaceSettings
            {
                Name = "Daily Source",
                Search = new SavedSearchSettings
                {
                    QueryText = "needle",
                    SearchPath = @"C:\src",
                    SearchMode = QueryMode.Regex,
                },
                ResultSort = "HitCount",
                ResultGroup = "Folder",
            });

            var workspace = Assert.Single(history.Workspaces);
            history.ExportWorkspaceCommand.Execute(workspace);

            Assert.True(File.Exists(path));
            var json = File.ReadAllText(path);
            Assert.Contains("FileSearch.Workspace", json);
            Assert.Contains("\"Workspace\"", json);
            Assert.Contains("Daily Source", json);
            Assert.Contains("Regex", json);
            Assert.Equal("Export workspace", savePicker.LastTitle);
            Assert.EndsWith(".filesearch-workspace.json", savePicker.LastDefaultFileName);
            Assert.StartsWith("Exported workspace", status.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ImportWorkspaceMergesPackageAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.filesearch-workspace.json");
        var savePicker = new FakeFileSavePicker { PathToReturn = path };

        try
        {
            var exportHistory = CreateHistory(savePicker, out _);
            exportHistory.SaveWorkspace(new WorkspaceSettings
            {
                Name = "Daily Source",
                Search = new SavedSearchSettings { QueryText = "needle", SearchPath = @"C:\src" },
            });
            exportHistory.SaveWorkspace(new WorkspaceSettings
            {
                Name = "Docs",
                Search = new SavedSearchSettings { QueryText = "policy", SearchPath = @"C:\docs" },
            });
            exportHistory.ExportAllWorkspacesCommand.Execute(null);

            var openPicker = new FakeFileOpenPicker { PathToReturn = path };
            var imported = CreateHistory(openPicker, out var status, out var settings);
            imported.ImportWorkspaceCommand.Execute(null);

            Assert.Equal("Import workspace", openPicker.LastTitle);
            Assert.Equal(2, imported.Workspaces.Count);
            Assert.Contains(imported.Workspaces, workspace => workspace.Name == "Daily Source");
            Assert.Contains(imported.Workspaces, workspace => workspace.Name == "Docs");
            Assert.Equal(2, settings.Current.Workspaces.Count);
            Assert.StartsWith("Imported 2 workspaces", status.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static HistoryViewModel CreateHistory(
        IFileOpenPicker? fileOpenPicker,
        out StatusBarViewModel status,
        out FakeSettingsService settings,
        IFileSavePicker? fileSavePicker = null)
    {
        settings = new FakeSettingsService();
        status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        return new HistoryViewModel(settings, appSettings, status, fileOpenPicker, fileSavePicker);
    }

    private static HistoryViewModel CreateHistory(
        IFileSavePicker? fileSavePicker,
        out StatusBarViewModel status)
    {
        var settings = new FakeSettingsService();
        status = new StatusBarViewModel();
        var appSettings = new ApplicationSettingsViewModel(settings, status);
        return new HistoryViewModel(settings, appSettings, status, fileSavePicker: fileSavePicker);
    }
}
