using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Databases;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;

namespace FileSearch.Gui.ViewModels;

public sealed partial class SearchViewModel
{
    private SearchRouteSummary? _searchRouting;

    public string QueryMeaningText
    {
        get
        {
            var mode = SearchMode == QueryMode.PlainText ? "Plain text" : SearchMode.ToString();
            var meaning = SearchQueryGuidance.Describe(QueryText, SearchMode, MatchCase);
            return (meaning.StartsWith(mode + ":", StringComparison.Ordinal) ? meaning : $"{mode} · {meaning}") +
                (MatchCase ? " · Case sensitive" : string.Empty);
        }
    }

    public string SearchScopeText =>
        $"{SearchTargetSummary} · {(IncludeSubfolders ? "Folder and subfolders" : "This folder only")}" +
        (string.IsNullOrWhiteSpace(FileNamePattern) ? string.Empty : $" · File pattern: {FileNamePattern}") +
        (CurrentSearchTargets.Contains(SearchTarget.Content) && EnableDocumentExtraction
            ? " · Database values match within one cell."
            : string.Empty);

    public string SearchCoverageText => _searchRouting is not null
        ? $"{(IsSearching ? "This search" : "Last search")}: {_searchRouting.Describe(IsSearching)}"
        : IsSemanticMode
            ? "Smart Search needs a ready local model and vectors for this folder. Availability is checked when you search."
            : UseIndex
                ? "Index requested · Coverage and availability are checked when you search."
                : "Use index off · Ordinary text and name searches run live; Smart Search still requires the index.";

    public string SearchCoverageDetails => _searchRouting is not null
        ? $"Folder for this search: {_searchRouting.Root}\n{SearchCoverageText}"
        : SearchCoverageText;

    private void NotifySearchGuidanceChanged()
    {
        OnPropertyChanged(nameof(QueryMeaningText));
        OnPropertyChanged(nameof(SearchScopeText));
        NotifySearchCoverageChanged();
    }

    private void NotifySearchCoverageChanged()
    {
        OnPropertyChanged(nameof(SearchCoverageText));
        OnPropertyChanged(nameof(SearchCoverageDetails));
    }

    partial void OnIsSearchingChanged(bool value) => NotifySearchSetupCommandsChanged();

    private void NotifySearchSetupCommandsChanged()
    {
        FindFileCommand.NotifyCanExecuteChanged();
        FindContentCommand.NotifyCanExecuteChanged();
        FindDatabaseValuesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void FindFile() => ApplySearchSetup(SearchTarget.FileNames, databasesOnly: false);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void FindContent() => ApplySearchSetup(SearchTarget.Content, databasesOnly: false);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private void FindDatabaseValues() => ApplySearchSetup(SearchTarget.Content, databasesOnly: true);

    private void ApplySearchSetup(SearchTarget target, bool databasesOnly)
    {
        if (!CanSearch())
            return;

        SearchMode = QueryMode.PlainText;
        SetSearchTargets([target]);
        FileNamePattern = databasesOnly
            ? string.Join("; ", DatabaseFiles.Extensions.Select(extension => "*" + extension))
            : string.Empty;
        if (databasesOnly)
            EnableDocumentExtraction = true;
    }
}

/// <summary>Retains actual per-target routing evidence, including searches with no hits.</summary>
internal sealed class SearchRouteSummary
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SearchTarget, Route> _routes;
    private string? _outcome;

    public SearchRouteSummary(string root, IEnumerable<SearchTarget> targets)
    {
        Root = root;
        _routes = targets.ToDictionary(target => target, _ => new Route());
    }

    public string Root { get; }

    public void ReportStatus(SearchTarget target, string message)
    {
        lock (_gate)
        {
            var messages = _routes[target].Messages;
            if (!string.IsNullOrWhiteSpace(message) && !messages.Contains(message, StringComparer.Ordinal) && messages.Count < 8)
                messages.Add(message);
        }
    }

    public void ReportHit(SearchTarget target, HitRoute route)
    {
        lock (_gate)
        {
            if (route == HitRoute.Indexed)
                _routes[target].HasIndexed = true;
            else
                _routes[target].HasLive = true;
        }
    }

    public void ReportOutcome(string outcome)
    {
        lock (_gate)
            _outcome = outcome;
    }

    public string Describe(bool isSearching = false)
    {
        lock (_gate)
        {
            var targets = string.Join(" · ", _routes.Select(pair =>
            {
                var route = pair.Value;
                var evidence = route.HasIndexed && route.HasLive ? "Indexed and live results"
                    : route.HasIndexed ? "Indexed results"
                    : route.HasLive ? "Live results" : isSearching ? "No results yet" : "No results";
                return $"{TargetName(pair.Key)}: {evidence}" +
                    (route.Messages.Count == 0 ? string.Empty : $"; {string.Join("; ", route.Messages)}");
            }));
            return _outcome is null ? targets : $"{_outcome} · {targets}";
        }
    }

    private static string TargetName(SearchTarget target) => target switch
    {
        SearchTarget.Content => "Contents",
        SearchTarget.FileNames => "File names",
        SearchTarget.FolderNames => "Folder names",
        _ => "Names",
    };

    private sealed class Route
    {
        public List<string> Messages { get; } = new();
        public bool HasIndexed { get; set; }
        public bool HasLive { get; set; }
    }
}
