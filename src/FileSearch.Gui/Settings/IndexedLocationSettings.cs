using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using FileSearch.Core;

namespace FileSearch.Gui.Settings;

public sealed class IndexedLocationSettings : INotifyPropertyChanged
{
    private bool _isIndexing;
    private bool _isQueued;
    private bool _isIndexingPaused;
    private int _queuedWorkCount;
    private string _runtimeStatusDetail = string.Empty;
    private long _lastIndexedUtcTicks;
    private long _fileCount;
    private long _lineCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Root { get; set; } = string.Empty;

    public bool Recursive { get; set; } = true;

    public bool IncludeHidden { get; set; }

    public bool EnableDocumentExtraction { get; set; } = true;

    public bool SkipUnknownFileTypes { get; set; }

    public string IncludedExtensions { get; set; } = string.Empty;

    public string IncludedFolders { get; set; } = string.Empty;

    public string ExcludedExtensions { get; set; } = string.Empty;

    public string ExcludedFolders { get; set; } = string.Empty;

    public bool WatchEnabled { get; set; } = true;

    // Stats notify so the indexed-locations list can be updated in place
    // after a background refresh instead of being rebuilt.
    public long LastIndexedUtcTicks
    {
        get => _lastIndexedUtcTicks;
        set
        {
            if (SetProperty(ref _lastIndexedUtcTicks, value))
                OnPropertyChanged(nameof(LastIndexedSummary));
        }
    }

    public long FileCount
    {
        get => _fileCount;
        set
        {
            if (SetProperty(ref _fileCount, value))
                OnPropertyChanged(nameof(Summary));
        }
    }

    public long LineCount
    {
        get => _lineCount;
        set
        {
            if (SetProperty(ref _lineCount, value))
                OnPropertyChanged(nameof(Summary));
        }
    }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Root))
                return "Index";

            var trimmed = Root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Root : name;
        }
    }

    [JsonIgnore]
    public string Summary => $"{FileCount:n0} files, {LineCount:n0} lines";

    [JsonIgnore]
    public bool IsIndexing
    {
        get => _isIndexing;
        set
        {
            if (SetProperty(ref _isIndexing, value))
                OnRuntimeStatusChanged();
        }
    }

    [JsonIgnore]
    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            if (SetProperty(ref _isQueued, value))
                OnRuntimeStatusChanged();
        }
    }

    [JsonIgnore]
    public bool IsIndexingPaused
    {
        get => _isIndexingPaused;
        set
        {
            if (SetProperty(ref _isIndexingPaused, value))
                OnRuntimeStatusChanged();
        }
    }

    [JsonIgnore]
    public int QueuedWorkCount
    {
        get => _queuedWorkCount;
        set
        {
            if (SetProperty(ref _queuedWorkCount, value))
                OnRuntimeStatusChanged();
        }
    }

    [JsonIgnore]
    public string RuntimeStatusDetail
    {
        get => _runtimeStatusDetail;
        set
        {
            if (SetProperty(ref _runtimeStatusDetail, value ?? string.Empty))
                OnRuntimeStatusChanged();
        }
    }

    [JsonIgnore]
    public string RuntimeStatusSummary
    {
        get
        {
            if (IsIndexingPaused && (IsIndexing || IsQueued))
                return "Paused";
            if (IsIndexing)
                return string.IsNullOrWhiteSpace(RuntimeStatusDetail) ? "Indexing now" : RuntimeStatusDetail;
            if (IsQueued)
                return QueuedWorkCount <= 1 ? "Queued" : $"{QueuedWorkCount:n0} queued";
            return "Ready";
        }
    }

    [JsonIgnore]
    public string WatchSummary => WatchEnabled ? "Watching changes" : "Watch disabled";

    [JsonIgnore]
    public string RecursionSummary => Recursive ? "Includes subfolders" : "Top folder only";

    [JsonIgnore]
    public string TypeSummary
    {
        get
        {
            var parts = new List<string>
            {
                EnableDocumentExtraction ? "Documents on" : "Documents off",
                SkipUnknownFileTypes ? "Known types only" : "Unknown text allowed",
            };

            var includedExtensions = ExtensionList.Parse(IncludedExtensions);
            if (includedExtensions.Length > 0)
                parts.Add($"Includes {string.Join(", ", includedExtensions)}");

            var includedFolders = IndexFilterListSettings.ParseFolders(IncludedFolders);
            if (includedFolders.Length > 0)
                parts.Add($"Folders {string.Join(", ", includedFolders)}");

            var excludedExtensions = ExtensionList.Parse(ExcludedExtensions);
            if (excludedExtensions.Length > 0)
                parts.Add($"Excludes {string.Join(", ", excludedExtensions)}");

            var excludedFolders = IndexFilterListSettings.ParseFolders(ExcludedFolders);
            if (excludedFolders.Length > 0)
                parts.Add($"Skips folders {string.Join(", ", excludedFolders)}");

            return string.Join(", ", parts);
        }
    }

    [JsonIgnore]
    public string LastIndexedSummary =>
        LastIndexedUtcTicks <= 0
            ? "Not indexed yet"
            : $"Last indexed {new System.DateTime(LastIndexedUtcTicks, System.DateTimeKind.Utc).ToLocalTime():g}";

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnRuntimeStatusChanged()
    {
        OnPropertyChanged(nameof(RuntimeStatusSummary));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
