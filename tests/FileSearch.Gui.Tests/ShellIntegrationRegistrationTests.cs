using FileSearch.Gui.Services;

namespace FileSearch.Gui.Tests;

public sealed class ShellIntegrationRegistrationTests
{
    [Fact]
    public void BuildFolderItemCommand_QuotesExecutableAndFolderArgument()
    {
        const string executable = @"C:\Program Files\FileSearch\FileSearch.Gui.exe";

        var command = ShellIntegrationRegistration.BuildFolderItemCommand(executable);

        Assert.Equal(@"""C:\Program Files\FileSearch\FileSearch.Gui.exe"" ""%1""", command);
    }

    [Fact]
    public void BuildFolderBackgroundCommand_QuotesExecutableAndBackgroundArgument()
    {
        const string executable = @"C:\Program Files\FileSearch\FileSearch.Gui.exe";

        var command = ShellIntegrationRegistration.BuildFolderBackgroundCommand(executable);

        Assert.Equal(@"""C:\Program Files\FileSearch\FileSearch.Gui.exe"" ""%V""", command);
    }

    [Fact]
    public void BuildVerbRegistrations_TargetsFolderAndBackgroundKeys()
    {
        const string executable = @"C:\FileSearch\FileSearch.Gui.exe";

        var registrations = ShellIntegrationRegistration.BuildVerbRegistrations(executable);

        Assert.Collection(
            registrations,
            folder =>
            {
                Assert.Equal(@"Software\Classes\Directory\shell\FileSearch", folder.ShellKeyPath);
                Assert.Equal(@"""C:\FileSearch\FileSearch.Gui.exe"" ""%1""", folder.Command);
            },
            background =>
            {
                Assert.Equal(@"Software\Classes\Directory\Background\shell\FileSearch", background.ShellKeyPath);
                Assert.Equal(@"""C:\FileSearch\FileSearch.Gui.exe"" ""%V""", background.Command);
            });
    }

    [Fact]
    public void MenuText_IsStableExplorerLabel()
    {
        Assert.Equal("Search with FileSearch", ShellIntegrationRegistration.MenuText);
    }

    [Fact]
    public void BuildBackgroundStartupCommand_QuotesExecutableAndBackgroundArgument()
    {
        const string executable = @"C:\Program Files\FileSearch\FileSearch.Indexer.exe";

        var command = StartupRegistration.BuildBackgroundStartupCommand(executable);

        Assert.Equal(@"""C:\Program Files\FileSearch\FileSearch.Indexer.exe"" --background", command);
    }

    [Fact]
    public void IsExpectedBackgroundStartupCommand_MatchesExpectedRunCommand()
    {
        const string executable = @"C:\Program Files\FileSearch\FileSearch.Indexer.exe";
        var command = StartupRegistration.BuildBackgroundStartupCommand(executable);

        Assert.True(StartupRegistration.IsExpectedBackgroundStartupCommand(command, executable));
        Assert.False(StartupRegistration.IsExpectedBackgroundStartupCommand(@"""C:\Other\FileSearch.Indexer.exe"" --background", executable));
    }

    [Fact]
    public void BackgroundIndexerPathResolver_UsesSiblingIndexerWhenPresent()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "filesearch-indexer-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);
        var indexerPath = Path.Combine(baseDirectory, "FileSearch.Indexer.exe");
        File.WriteAllText(indexerPath, string.Empty);

        try
        {
            Assert.Equal(indexerPath, BackgroundIndexerProcessService.ResolveDefaultExecutablePath(baseDirectory));
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Fact]
    public void StartupRegistrationConstants_TargetCurrentUserRunValue()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.RunKeyPath);
        Assert.Equal("FileSearch", StartupRegistration.ValueName);
    }
}
