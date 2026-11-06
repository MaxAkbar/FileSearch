using FileSearch.Core.Engine;
using Microsoft.CommandPalette.Extensions;

namespace FileSearch.PowerToys.Tests;

public sealed class CommandPaletteTests
{
    [Theory]
    [InlineData(new[] { "-RegisterProcessAsComServer" }, true)]
    [InlineData(new[] { "-RegisterProcessAsComServer", "-Embedding" }, true)]
    [InlineData(new[] { "-Embedding" }, false)]
    [InlineData(new string[0], false)]
    public void ComActivationAllowsBrokerArguments(string[] args, bool expected)
    {
        Assert.Equal(expected, Program.IsComServerActivation(args));
    }

    [Fact]
    public void ProviderLoadsWithoutPowerToysRunningAndExposesTwoCommands()
    {
        using var stopped = new ManualResetEvent(false);
        using (var extension = new FileSearchExtension(stopped))
        {
            var provider = Assert.IsAssignableFrom<ICommandProvider>(extension.GetProvider(ProviderType.Commands));
            var commands = provider.TopLevelCommands();
            Assert.Equal(2, commands.Length);
            Assert.Contains(commands, command => command.Title == "FileSearch: file names");
            Assert.Contains(commands, command => command.Title == "FileSearch: file contents");
            Assert.False(stopped.WaitOne(0));
        }
        Assert.True(stopped.WaitOne(0));
    }

    [Fact]
    public void QueryHandoffUsesLiteralArgumentValuesWithoutAShell()
    {
        const string query = "type:txt \"annual report\" & $(literal)";
        var start = FileActions.CreateStartInfo(@"C:\Apps\FileSearch.Gui.exe", query,
            @"C:\Work Files", SearchTarget.Content, @"C:\Work Files\report.txt");

        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "--query", query, "--target", "content", "--path", @"C:\Work Files",
            "--select", @"C:\Work Files\report.txt" }, start.ArgumentList);
    }

    [Fact]
    public async Task EmptySearchDoesNotInvokeTheSearchBackend()
    {
        var calls = 0;
        using var page = new FileSearchPage("test", SearchTarget.Content, (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new LauncherSearchResponse([]));
        }, new ExtensionSettings());

        page.UpdateSearchText("", " ");
        await WaitForAsync(() => !page.IsLoading);
        Assert.Equal(0, calls);
        Assert.Empty(page.GetItems());
    }

    [Fact]
    public async Task OlderSearchCannotOverwriteNewerResultsEvenIfItIgnoresCancellation()
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResult = new TaskCompletionSource<LauncherSearchResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var page = new FileSearchPage("test", SearchTarget.Content, (text, _, _) =>
        {
            if (text == "old")
            {
                oldStarted.SetResult();
                return oldResult.Task;
            }
            return Task.FromResult(Response("new.txt"));
        }, new ExtensionSettings());

        try
        {
            page.UpdateSearchText("", "old");
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            page.UpdateSearchText("old", "new");
            await WaitForAsync(() => !page.IsLoading);
            Assert.Equal("new.txt", Assert.Single(page.GetItems()).Title);
            oldResult.SetResult(Response("old.txt"));
        }
        finally
        {
            oldResult.TrySetResult(Response("old.txt"));
        }
        // Dispose waits for every pending search, including the older cancelled one.
        page.Dispose();
        Assert.Equal("new.txt", Assert.Single(page.GetItems()).Title);
    }

    [Fact]
    public async Task BackendFailureProducesAnEmptyStateInsteadOfAnUnhandledException()
    {
        using var page = new FileSearchPage("test", SearchTarget.Content,
            (_, _, _) => throw new IOException("index unavailable"), new ExtensionSettings());
        page.UpdateSearchText("", "needle");
        await WaitForAsync(() => !page.IsLoading);
        Assert.Empty(page.GetItems());
        Assert.Equal("Search is unavailable", page.EmptyContent!.Title);
    }

    [Fact]
    public async Task PartialResultsShowThatTheSearchWasLimited()
    {
        using var page = new FileSearchPage("test", SearchTarget.Content,
            (_, _, _) => Task.FromResult(Response("one.txt") with { IsTruncated = true }), new ExtensionSettings());
        page.UpdateSearchText("", "needle");
        await WaitForAsync(() => !page.IsLoading);
        Assert.Equal(2, page.GetItems().Length);
        Assert.Equal("Showing a limited set of matching files", page.GetItems()[1].Title);
    }

    private static LauncherSearchResponse Response(string name) =>
        new([new LauncherSearchResult(new Hit(name, 1, "needle", []), "root")]);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, deadline.Token);
    }
}
