using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

public sealed record LocationScopeOption(LocationScopeKind Value, string DisplayName);

public sealed partial class LocationSelectionOption : ObservableObject
{
    public LocationSelectionOption(string root, bool registered, bool selected)
    { Root = root; StatusText = registered ? "Indexed location" : "No longer indexed"; _isSelected = selected; }
    public string Root { get; }
    public string DisplayName => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Root)) is { Length: > 0 } name ? name : Root;
    public string StatusText { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class SearchViewModel
{
    private bool _updatingLocationSelections;
    private int _locationInputUpdateDepth;
    private string[] _registeredLocationRoots = [];
    private string _locationRunNotice = "";
    private string[] _lastSearchRoots = [];
    private string ExportFolder => _lastSearchFolder ?? SearchPath;
    private string? _lastSearchFolder;
    private LocationScopeKind? _lastSearchLocationScope;
    private string? _lastSearchText;
    private bool _lastUseQuickSearchMatching;
    private FileResultViewModel? _handoffSelection;
    private Hit? _handoffPreviewHit;
    [ObservableProperty] private LocationScopeKind _selectedLocationScope;
    [ObservableProperty] private bool _useQuickSearchMatching;
    public ObservableCollection<string> SelectedIndexedRoots { get; } = [];
    public ObservableCollection<LocationSelectionOption> LocationSelections { get; } = [];
    public IReadOnlyList<LocationScopeOption> LocationScopeOptions { get; } =
    [
        new(LocationScopeKind.SelectedFolder, "Selected folder"),
        new(LocationScopeKind.SelectedIndexedLocations, "Selected indexed locations"),
        new(LocationScopeKind.AllIndexedLocations, "All indexed locations"),
    ];
    public bool IsFolderLocationScope => SelectedLocationScope == LocationScopeKind.SelectedFolder;
    public bool IsSelectedIndexedScope => SelectedLocationScope == LocationScopeKind.SelectedIndexedLocations;
    public string LocationSelectionSummary => $"{SelectedIndexedRoots.Count:n0} selected";
    public string LocationScopeNotice => _locationRunNotice.Length > 0 ? _locationRunNotice : GetLocationScopeNotice(SelectedLocationScope);
    public string HandoffFailureText { get; private set; } = "";

    private void InitializeLocationSelections()
    {
        SelectedIndexedRoots.CollectionChanged += OnSelectedIndexedRootsChanged;
        RefreshLocationSelections();
    }

    [RelayCommand]
    public void RefreshLocationSelections()
    {
        var registered = SearchLocationResolver.NormalizeRoots(_settingsService.Current.IndexedLocations.Select(location => location.Root));
        var selected = SearchLocationResolver.NormalizeRoots(_settingsService.Current.QuickSearchSelectedIndexedRoots);
        if (_registeredLocationRoots.SequenceEqual(registered, StringComparer.OrdinalIgnoreCase) &&
            SelectedIndexedRoots.SequenceEqual(selected, StringComparer.OrdinalIgnoreCase) &&
            LocationSelections.Select(row => row.Root).SequenceEqual(registered.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return;
        _registeredLocationRoots = registered;
        _updatingLocationSelections = true;
        try
        {
            if (!SelectedIndexedRoots.SequenceEqual(selected, StringComparer.OrdinalIgnoreCase))
            {
                SelectedIndexedRoots.Clear();
                foreach (string root in selected) SelectedIndexedRoots.Add(root);
            }
            foreach (var row in LocationSelections) row.PropertyChanged -= OnLocationSelectionChanged;
            LocationSelections.Clear();
            foreach (string root in _registeredLocationRoots.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var row = new LocationSelectionOption(root, _registeredLocationRoots.Contains(root, StringComparer.OrdinalIgnoreCase),
                    selected.Contains(root, StringComparer.OrdinalIgnoreCase));
                row.PropertyChanged += OnLocationSelectionChanged;
                LocationSelections.Add(row);
            }
        }
        finally { _updatingLocationSelections = false; }
        OnPropertyChanged(nameof(LocationSelections));
        NotifyLocationScopeChanged();
    }

    private void OnLocationSelectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_updatingLocationSelections || args.PropertyName != nameof(LocationSelectionOption.IsSelected) || sender is not LocationSelectionOption row) return;
        if (row.IsSelected)
        { if (!SelectedIndexedRoots.Contains(row.Root, StringComparer.OrdinalIgnoreCase)) SelectedIndexedRoots.Add(row.Root); }
        else
        {
            foreach (string root in SelectedIndexedRoots.Where(root => string.Equals(SearchLocationResolver.Normalize(root), row.Root, StringComparison.OrdinalIgnoreCase)).ToArray())
                SelectedIndexedRoots.Remove(root);
        }
    }

    private void OnSelectedIndexedRootsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_updatingLocationSelections) return;
        _updatingLocationSelections = true;
        try
        {
            var roots = SearchLocationResolver.NormalizeRoots(SelectedIndexedRoots);
            foreach (var row in LocationSelections) row.IsSelected = roots.Contains(row.Root, StringComparer.OrdinalIgnoreCase);
            _settingsService.Update(settings => settings.QuickSearchSelectedIndexedRoots = roots.ToList());
        }
        finally { _updatingLocationSelections = false; }
        NotifyLocationScopeChanged();
    }

    public void ApplyLocationScope(LocationScopeKind scope, IEnumerable<string> roots)
    {
        var selected = SearchLocationResolver.NormalizeRoots(roots);
        _settingsService.Update(settings => settings.QuickSearchSelectedIndexedRoots = selected.ToList());
        RefreshLocationSelections();
        SelectedLocationScope = Enum.IsDefined(scope) ? scope : LocationScopeKind.SelectedFolder;
    }

    public SearchLocationResolution ResolveLocationScope(LocationScopeKind scope, string folder, IEnumerable<string> selectedRoots) =>
        SearchLocationResolver.Resolve(scope, folder, selectedRoots, _registeredLocationRoots);

    public Task<SearchLocationResolution> ResolveLocationScopeAsync(LocationScopeKind scope, string folder,
        IEnumerable<string> selectedRoots, CancellationToken token) =>
        SearchLocationResolver.ResolveAsync(scope, folder, selectedRoots, _registeredLocationRoots, token);

    public string GetLocationScopeNotice(LocationScopeKind scope)
    {
        if (scope == LocationScopeKind.SelectedFolder) return "Search the folder above. Choosing or typing another folder uses Selected folder scope.";
        if (_registeredLocationRoots.Length == 0 && SelectedIndexedRoots.Count == 0)
            return "No indexed locations are registered. Add a location from Index, or choose Selected folder.";
        if (scope == LocationScopeKind.SelectedIndexedLocations && SelectedIndexedRoots.Count == 0)
            return "No indexed locations selected. Choose locations or select All indexed locations.";
        int removed = SelectedIndexedRoots.Count(root => !_registeredLocationRoots.Contains(root, StringComparer.OrdinalIgnoreCase));
        return (scope == LocationScopeKind.SelectedIndexedLocations && removed > 0 ? $"{removed:n0} selected location(s) are no longer indexed and will be skipped. " : "") +
            "The folder above is unused in this scope. Offline or inaccessible locations are reported when searching.";
    }

    partial void OnSelectedLocationScopeChanged(LocationScopeKind value)
    {
        if (!Enum.IsDefined(value)) { SelectedLocationScope = LocationScopeKind.SelectedFolder; return; }
        RefreshLocationSelections();
        NotifyLocationScopeChanged();
    }

    private void NotifyLocationScopeChanged()
    {
        _locationRunNotice = "";
        OnPropertyChanged(nameof(IsFolderLocationScope)); OnPropertyChanged(nameof(IsSelectedIndexedScope));
        OnPropertyChanged(nameof(LocationSelectionSummary)); OnPropertyChanged(nameof(LocationScopeNotice));
        NotifySearchGuidanceChanged();
    }

    partial void OnUseQuickSearchMatchingChanged(bool value) => NotifySearchGuidanceChanged();

    [RelayCommand]
    private void UseStandardSearchMatching() => UseQuickSearchMatching = false;

    public bool ApplyQuickSearchHandoff(string text, LocationScopeKind scope, IReadOnlyList<string> selectedRoots,
        string folder, bool includeContent, FileResultViewModel? selectedResult)
    {
        if (!CanSearch()) { HandoffFailureText = "Finish or cancel the current main-window operation before opening full search."; return false; }
        try { _ = new QuickSearchNameQuery(text); }
        catch (ArgumentException error) { HandoffFailureText = _status.Text = error.Message; return false; }
        HandoffFailureText = "";
        SearchPath = folder;
        ApplyLocationScope(scope, selectedRoots);
        QueryText = text;
        SearchMode = QueryMode.PlainText;
        UseQuickSearchMatching = true;
        MatchCase = false; UseIndex = true; IncludeSubfolders = true;
        FileNamePattern = ExcludeFileNamePattern = "";
        MinSizeKB = MaxSizeKB = 0; ModifiedAfterEnabled = ModifiedBeforeEnabled = false;
        SetSearchTargets(includeContent ? [SearchTarget.FileNames, SearchTarget.FolderNames, SearchTarget.Content] : [SearchTarget.FileNames, SearchTarget.FolderNames]);
        _handoffSelection = selectedResult;
        return true;
    }

    private void RestoreHandoffSelection()
    {
        if (_handoffSelection is not { } selection) return;
        _handoffSelection = null;
        var result = Files.FirstOrDefault(file => string.Equals(file.ResultKey, selection.ResultKey, StringComparison.OrdinalIgnoreCase));
        if (result is null) return;
        SelectedFile = result;
        var hit = selection.Hits.FirstOrDefault(hit => hit.Kind == HitKind.Content) ?? (selection.Hits.Count > 0 ? selection.Hits[0] : null);
        _handoffPreviewHit = hit is null ? null : result.Hits.FirstOrDefault(candidate => SearchHitIdentity.Create(candidate) == SearchHitIdentity.Create(hit));
        var line = _handoffPreviewHit is { } preferred ? _previewMatchLines.FirstOrDefault(line => ReferenceEquals(line.SourceHit, preferred)) : null;
        if (line is not null) CurrentPreviewLine = line;
        OnPropertyChanged(nameof(CurrentPreviewHit));
    }
}
