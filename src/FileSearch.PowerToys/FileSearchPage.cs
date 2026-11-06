using FileSearch.Core.Engine;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace FileSearch.PowerToys;

internal sealed partial class FileSearchPage : DynamicListPage, IDisposable
{
    private readonly SearchTarget _target;
    private readonly Func<string, SearchTarget, CancellationToken, Task<LauncherSearchResponse>> _search;
    private readonly ExtensionSettings _settings;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private readonly List<Task> _pending = [];
    private IListItem[] _items = [];
    private bool _disposed;

    public FileSearchPage(string title, SearchTarget target,
        Func<string, SearchTarget, CancellationToken, Task<LauncherSearchResponse>> search,
        ExtensionSettings settings)
    {
        Title = title;
        Name = "Search";
        Icon = new IconInfo("\uE721");
        PlaceholderText = target == SearchTarget.FileNames ? "Find an indexed file by name" : "Search indexed text, for example type:pdf invoice";
        _target = target;
        _search = search;
        _settings = settings;
        SetEmpty("Search your FileSearch indexes", "Type to search. Add indexed folders in FileSearch to make them available here.");
    }

    public override IListItem[] GetItems()
    {
        lock (_gate)
            return _items;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _cancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _items = [];
            IsLoading = !string.IsNullOrWhiteSpace(newSearch);
            SetEmpty(IsLoading ? "Searching…" : "Search your FileSearch indexes",
                "Searches existing indexed folders only.");
            _pending.RemoveAll(task => task.IsCompleted);
            _pending.Add(Task.Run(() => SearchAsync(newSearch.Trim(), cancellation)));
        }
        RaiseItemsChanged();
    }

    private async Task SearchAsync(string text, CancellationTokenSource cancellation)
    {
        try
        {
            if (text.Length == 0)
                return;

            await Task.Delay(150, cancellation.Token).ConfigureAwait(false);
            var response = await _search(text, _target, cancellation.Token).ConfigureAwait(false);
            var items = response.Results.Select(result => (IListItem)CreateItem(result, text)).ToList();
            if (items.Count > 0 && (response.IsTruncated || response.SkippedLocations > 0))
            {
                items.Add(new ListItem(new NoOpCommand())
                {
                    Title = response.TimedOut ? "Search time limit reached" :
                        response.IsTruncated ? "Showing a limited set of matching files" : "Some indexed folders were skipped",
                    Subtitle = response.SkippedLocations > 0
                        ? "Refresh unavailable indexes in FileSearch, or refine your query."
                        : "Refine your query to see more specific matches.",
                });
            }
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_cancellation, cancellation) || cancellation.IsCancellationRequested)
                    return;

                _items = items.ToArray();
                var message = response.TimedOut ? "Search took too long. Try a more specific query." :
                    response.SkippedLocations > 0 ? "Some folders need an index refresh in FileSearch." :
                    "Try another query, or add indexed folders in FileSearch.";
                SetEmpty("No matching indexed files", message);
                HasMoreItems = false;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_cancellation, cancellation))
                    SetEmpty("Search is unavailable", "Open FileSearch to check your indexes, then try again.");
            }
        }
        finally
        {
            var changed = false;
            lock (_gate)
            {
                if (!_disposed && ReferenceEquals(_cancellation, cancellation))
                {
                    IsLoading = false;
                    _cancellation = null;
                    changed = true;
                }
            }
            cancellation.Dispose();
            if (changed)
                RaiseItemsChanged();
        }
    }

    private ListItem CreateItem(LauncherSearchResult result, string query)
    {
        var hit = result.Hit;
        var snippet = hit.LineContent.ReplaceLineEndings(" ");
        if (snippet.Length > 180)
            snippet = snippet[..180] + "…";

        var commands = new List<IContextItem>
        {
            new CommandContextItem(new FileActionCommand("Open in FileSearch", () =>
                FileActions.OpenFileSearch(_settings.AppPath, query, result.Root, _target, hit.Path))),
            new CommandContextItem(new FileActionCommand("Show in File Explorer", () => FileActions.Reveal(hit.Path))),
            new CommandContextItem(new CopyTextCommand(hit.Path) { Name = "Copy path" }),
        };
        if (_target == SearchTarget.Content)
            commands.Add(new CommandContextItem(new CopyTextCommand(hit.LineContent) { Name = "Copy matching text" }));

        return new ListItem(new OpenFileCommand(hit.Path))
        {
            Title = Path.GetFileName(hit.Path),
            Subtitle = _target == SearchTarget.Content ? $"{hit.Path} · {snippet}" : hit.Path,
            Icon = new IconInfo("\uE8A5"),
            MoreCommands = commands.ToArray(),
        };
    }

    private void SetEmpty(string title, string subtitle) => EmptyContent = new CommandItem(
        new FileActionCommand("Open FileSearch", () => FileActions.OpenFileSearch(_settings.AppPath)))
    {
        Title = title,
        Subtitle = subtitle,
    };

    public void Dispose()
    {
        Task[] pending;
        lock (_gate)
        {
            _disposed = true;
            _cancellation?.Cancel();
            pending = _pending.ToArray();
        }
        Task.WhenAll(pending).GetAwaiter().GetResult();
    }
}
