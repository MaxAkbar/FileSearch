using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FileSearch.Gui.Settings;

public sealed class IndexedLocationSettings : INotifyPropertyChanged
{
    private bool _isIndexing;
    private bool _isQueued;
    private bool _isIndexingPaused;
    private int _queuedWorkCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Root { get; set; } = string.Empty;

    public bool Recursive { get; set; } = true;

    public bool IncludeHidden { get; set; } = false;

    public bool EnableDocumentExtraction { get; set; } = true;

    public bool SkipUnknownFileTypes { get; set; } = false;

    public bool WatchEnabled { get; set; } = true;

    public long LastIndexedUtcTicks { get; set; }

    public long FileCount { get; set; }

    public long LineCount { get; set; }

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
    public string RuntimeStatusSummary
    {
        get
        {
            if (IsIndexingPaused && (IsIndexing || IsQueued))
                return "Paused";
            if (IsIndexing)
                return "Indexing now";
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
    public string TypeSummary =>
        $"{(EnableDocumentExtraction ? "Documents on" : "Documents off")}, {(SkipUnknownFileTypes ? "Known types only" : "Unknown text allowed")}";

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
