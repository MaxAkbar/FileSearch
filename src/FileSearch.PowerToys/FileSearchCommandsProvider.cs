using FileSearch.Core;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Core.Queries;
using FileSearch.WindowsOcr;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Extensions.DependencyInjection;

namespace FileSearch.PowerToys;

internal sealed partial class FileSearchCommandsProvider : CommandProvider, IDisposable
{
    private readonly ExtensionSettings _settings = new();
    private readonly Lazy<ServiceProvider> _services;
    private readonly FileSearchPage[] _pages;
    private int _disposed;

    public FileSearchCommandsProvider()
    {
        _services = new Lazy<ServiceProvider>(CreateServices);
        DisplayName = "FileSearch";
        Icon = new IconInfo("\uE721");
        Settings = _settings.Settings;
        _pages =
        [
            new FileSearchPage("FileSearch: file names", SearchTarget.FileNames, SearchAsync, _settings),
            new FileSearchPage("FileSearch: file contents", SearchTarget.Content, SearchAsync, _settings),
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _pages.Select(page => (ICommandItem)new CommandItem(page)
    {
        Title = page.Title,
        Subtitle = "Search existing FileSearch indexes",
        MoreCommands = [new CommandContextItem(_settings.Settings.SettingsPage)],
    }).ToArray();

    private Task<LauncherSearchResponse> SearchAsync(string text, SearchTarget target, CancellationToken token)
    {
        var services = _services.Value;
        var search = new IndexedLauncherSearch(services.GetRequiredService<IIndexSearch>(),
            services.GetRequiredService<IIndexMaintenance>(), services.GetRequiredService<IQueryFactory>());
        return search.SearchAsync(text, target, cancellationToken: token);
    }

    private ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        if (!string.IsNullOrWhiteSpace(_settings.IndexPath))
            services.AddSingleton(new FileIndexOptions { DatabasePath = _settings.IndexPath });
        services.AddFileSearchCore();
        // Extractor registration keeps coverage checks compatible with the GUI's
        // OCR-enabled indexes. The companion never runs extractors or indexers.
        services.AddWindowsImageOcr();
        return services.BuildServiceProvider();
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var page in _pages)
            page.Dispose();
        if (_services.IsValueCreated)
            _services.Value.Dispose();
        base.Dispose();
    }
}
