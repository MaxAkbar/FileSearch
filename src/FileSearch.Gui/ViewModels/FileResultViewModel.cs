using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// Display row for one matching file. Aggregates all <see cref="Hit"/>s
/// that came back from the searcher for the same path, exposes a live
/// hit count, and surfaces row-level commands (open, reveal, copy paths).
/// </summary>
public sealed partial class FileResultViewModel : ObservableObject
{
    /// <summary>How many hit lines a result card shows before "+N more".</summary>
    public const int CollapsedHitLimit = 3;

    /// <summary>How many hit lines the preview pane shows above the loaded context.</summary>
    public const int PreviewHitLimit = 5;

    private readonly List<Hit> _hits = new();
    private readonly IFileLauncher _launcher;
    private readonly Func<string, CancellationToken, Task>? _recordOpenedAsync;

    private string? _sizeText;
    private string? _modifiedText;
    private bool _metadataLoaded;
    private int _metadataVersion;
    private static readonly SemaphoreSlim MetadataWorkers = new(4);
    private long? _sizeBytes;
    private DateTime? _modifiedUtc;
    private bool _hasIndexedHits;
    private bool _hasLiveHits;
    private bool _hasImageOcrPreview;
    private bool _hasStructuredSnippets;

    public FileResultViewModel(
        string fullPath,
        IFileLauncher launcher,
        Func<string, CancellationToken, Task>? recordOpenedAsync = null,
        int searchRank = 0,
        bool? isDirectory = null)
    {
        FullPath = fullPath;

        // Callers that already know (the search pipeline works it out off the
        // UI thread) pass it in; probing the disk here ran once per new row
        // on the dispatcher.
        IsDirectory = isDirectory ?? (System.IO.Directory.Exists(fullPath) && !File.Exists(fullPath));
        FileName = GetDisplayName(fullPath, IsDirectory);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        Extension = IsDirectory ? string.Empty : Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        _launcher = launcher;
        _recordOpenedAsync = recordOpenedAsync;
        SearchRank = searchRank;
    }

    public string FullPath { get; private set; }
    public string FileName { get; private set; }
    public string Directory { get; private set; }
    public bool IsDirectory { get; private set; }
    public int SearchRank { get; }
    // Assigned before a grouped refresh; it preserves the order of groups'
    // first ranked files and is independent of the engine's score/rank.
    public int ResultGroupRank { get; internal set; }

    /// <summary>Lower-cased extension without the leading dot (e.g. "cs").</summary>
    public string Extension { get; private set; }

    public string ExtensionPattern =>
        string.IsNullOrWhiteSpace(Extension) ? string.Empty : $"*.{Extension}";

    public string ExcludeExtensionPatternMenuText =>
        string.IsNullOrWhiteSpace(ExtensionPattern) ? "Exclude extension" : $"Exclude {ExtensionPattern}";

    public IReadOnlyList<Hit> Hits => _hits;

    public MailMessageMetadata? MailMessage { get; private set; }

    public bool IsStoreMessage => MailMessage?.StoreFingerprint is not null;

    public bool HasMailMessage => MailMessage is not null;

    public string MailSummaryText => MailMessage is { } message
        ? $"{message.From} · {message.DateUtc?.ToLocalTime():yyyy-MM-dd HH:mm} · {message.Folder}"
        : string.Empty;

    public string ResultKey => _hits.Count > 0 ? _hits[0].ResultKey : FullPath;

    public string DisplayName => string.IsNullOrWhiteSpace(MailMessage?.Subject) ? FileName : MailMessage.Subject;

    public string DisplayDirectory => MailMessage is { } message
        ? $"{message.From} · {message.Folder} · {FileName}"
        : Directory;

    // Tracked as hits arrive: rescanning every hit on each add made streaming
    // a file with thousands of matches quadratic on the UI thread.
    public bool HasImageOcrPreview => _hasImageOcrPreview;

    public bool HasStructuredSnippets => _hasStructuredSnippets;

    [ObservableProperty] private int _hitCount;
    [ObservableProperty] private string _firstMatch = string.Empty;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private double _bestScore;

    /// <summary>
    /// Whether the card is showing every hit or just the first
    /// <see cref="CollapsedHitLimit"/>. Toggled by <see cref="ToggleExpandCommand"/>.
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The hit lines the card should currently render.</summary>
    public IEnumerable<Hit> VisibleHits =>
        IsExpanded ? _hits : _hits.Take(CollapsedHitLimit);

    public IEnumerable<Hit> PreviewHits => _hits.Take(PreviewHitLimit);

    public bool HasPreviewHits => _hits.Count > 0;

    public int ExtraPreviewHitCount => Math.Max(0, _hits.Count - PreviewHitLimit);

    public bool HasMorePreviewHits => ExtraPreviewHitCount > 0;

    public string PreviewMoreText =>
        ExtraPreviewHitCount <= 0
            ? string.Empty
            : $"+ {ExtraPreviewHitCount} more match{(ExtraPreviewHitCount == 1 ? string.Empty : "es")} in loaded preview";

    public bool HasMoreHits => _hits.Count > CollapsedHitLimit;

    public int ExtraHitCount => Math.Max(0, _hits.Count - CollapsedHitLimit);

    public string MoreText =>
        IsExpanded
            ? "Show fewer"
            : $"+ {ExtraHitCount} more match{(ExtraHitCount == 1 ? string.Empty : "es")} in this file";

    public string BuildStoredHitPreview()
    {
        var snippetPreview = BuildStructuredSnippetPreview();
        if (!string.IsNullOrWhiteSpace(snippetPreview))
            return snippetPreview;

        var contentHits = _hits
            .Where(hit => hit.Kind == HitKind.Content && hit.LineNumber > 0)
            .OrderBy(hit => hit.LineNumber)
            .ToList();
        if (contentHits.Count == 0)
        {
            var metadataHits = _hits.Where(hit => hit.Kind == HitKind.Metadata).ToList();
            if (metadataHits.Count == 0)
                return string.Empty;

            var metadataBuilder = new StringBuilder();
            foreach (var hit in metadataHits)
                metadataBuilder.Append('\u25ba').Append(' ').Append(hit.LineContent).AppendLine();
            return metadataBuilder.ToString();
        }

        var sb = new StringBuilder();
        foreach (var hit in contentHits)
        {
            sb.Append('\u25ba')
              .Append(' ')
              .Append(hit.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(6))
              .Append("  ")
              .Append(hit.LineContent);

            if (!string.IsNullOrWhiteSpace(hit.Anchor?.DisplayText))
                sb.Append("  [").Append(hit.Anchor.DisplayText).Append(']');
            else
            {
                var location = SourceLocationFormatter.Format(hit.Anchor, hit.Locator);
                if (!string.IsNullOrWhiteSpace(location))
                    sb.Append("  [").Append(location).Append(']');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string BuildStructuredSnippetPreview()
    {
        var snippetHits = _hits
            .Where(hit => hit.Snippet is not null)
            .OrderBy(hit => hit.Snippet?.Locator?.StartLine ?? hit.LineNumber)
            .ThenBy(hit => hit.Snippet?.ContentUnitId ?? long.MaxValue)
            .ToList();
        if (snippetHits.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        var seen = new HashSet<long>();
        foreach (var hit in snippetHits)
        {
            var snippet = hit.Snippet!;
            if (snippet.ContentUnitId is { } contentUnitId && !seen.Add(contentUnitId))
                continue;

            var location = SourceLocationFormatter.Format(hit.Anchor, snippet.Locator ?? hit.Locator);
            sb.Append('\u25ba').Append(' ');
            if (!string.IsNullOrWhiteSpace(location))
                sb.Append('[').Append(location).Append("] ");

            var text = string.IsNullOrWhiteSpace(snippet.Text)
                ? hit.LineContent
                : snippet.Text.Trim();
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Count pill on the result card ("2 matches").</summary>
    public string MatchCountText => $"{HitCount:n0} {(HitCount == 1 ? "match" : "matches")}";

    public string PinActionText => IsPinned ? "Unpin result" : "Pin result";

    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";

    public string FavoriteActionText => IsFavorite ? "Remove favorite" : "Add favorite";

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

    /// <summary>Human-readable file size, loaded lazily on first access.</summary>
    public string SizeText
    {
        get
        {
            EnsureMetadataLoading();
            return _sizeText ??= ComputeSizeText();
        }
    }

    /// <summary>Last-modified timestamp, loaded lazily on first access.</summary>
    public string ModifiedText
    {
        get
        {
            EnsureMetadataLoading();
            return _modifiedText ??= ComputeModifiedText();
        }
    }

    public long? SizeBytes => _sizeBytes;

    public DateTime? ModifiedUtc => _modifiedUtc;

    public long ModifiedSortTicks => ModifiedUtc?.Ticks ?? 0;

    public string FileTypeGroup =>
        IsDirectory ? "Folder" : string.IsNullOrWhiteSpace(Extension) ? "No extension" : $".{Extension}";

    public string ModifiedDateGroup => ToModifiedDateGroup(ModifiedUtc);

    public string ModifiedDateFacet => ToModifiedDateFacet(ModifiedUtc);

    public string SizeGroup => ToSizeGroup(SizeBytes);

    public string SizeFacet => ToSizeFacet(SizeBytes);

    public string SourceGroup
    {
        get
        {
            if (_hasIndexedHits && _hasLiveHits)
                return "Indexed + live scan";
            if (_hasIndexedHits)
                return "Indexed";
            if (_hasLiveHits)
                return "Live scan";
            return "Unknown";
        }
    }

    public void AddHit(Hit hit) => AddHits([hit]);

    /// <summary>
    /// Appends a batch of hits and raises each change notification at most
    /// once. Every notification re-runs bindings on the UI thread, so a
    /// streaming search applies hits per file per drain, not one at a time.
    /// </summary>
    public void AddHits(IReadOnlyList<Hit> hits)
    {
        if (hits.Count == 0)
            return;

        var previousCount = _hits.Count;
        if (previousCount == 0 && hits[0].MailMessage is { } message)
        {
            MailMessage = message;
            OnPropertyChanged(nameof(MailMessage));
            OnPropertyChanged(nameof(IsStoreMessage));
            OnPropertyChanged(nameof(HasMailMessage));
            OnPropertyChanged(nameof(MailSummaryText));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DisplayDirectory));
        }
        var hadImageOcrPreview = _hasImageOcrPreview;
        var hadStructuredSnippets = _hasStructuredSnippets;
        var oldSource = SourceGroup;
        var bestScore = BestScore;
        var sizeChanged = false;
        var modifiedChanged = false;
        foreach (var hit in hits)
        {
            _hits.Add(hit);
            if (hit.Score > bestScore)
                bestScore = hit.Score;
            if (hit.SizeBytes is { } size)
            {
                _sizeBytes = size;
                sizeChanged = true;
            }

            if (hit.ModifiedUtc is { } modified)
            {
                _modifiedUtc = modified;
                modifiedChanged = true;
            }

            _hasIndexedHits |= hit.Route == HitRoute.Indexed;
            _hasLiveHits |= hit.Route == HitRoute.Live;
            _hasImageOcrPreview |= !_hasImageOcrPreview && ImageOcrPreviewViewModel.IsPreviewAnchor(hit.Anchor);
            _hasStructuredSnippets |= hit.Snippet is not null;
        }

        HitCount = _hits.Count;
        if (previousCount == 0)
            FirstMatch = hits[0].LineContent.Trim();
        BestScore = bestScore;
        if (sizeChanged)
        {
            _sizeText = null;
            OnPropertyChanged(nameof(SizeBytes));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(SizeGroup));
            OnPropertyChanged(nameof(SizeFacet));
        }

        if (modifiedChanged)
        {
            _modifiedText = null;
            OnPropertyChanged(nameof(ModifiedUtc));
            OnPropertyChanged(nameof(ModifiedSortTicks));
            OnPropertyChanged(nameof(ModifiedText));
            OnPropertyChanged(nameof(ModifiedDateGroup));
            OnPropertyChanged(nameof(ModifiedDateFacet));
        }

        if (!string.Equals(oldSource, SourceGroup, StringComparison.Ordinal))
            OnPropertyChanged(nameof(SourceGroup));

        // Refresh the rendered lines only while they can still change:
        // collapsed cards freeze at the first few, expanded cards keep growing.
        if (IsExpanded || previousCount < CollapsedHitLimit)
            OnPropertyChanged(nameof(VisibleHits));
        if (previousCount <= PreviewHitLimit)
            OnPropertyChanged(nameof(PreviewHits));
        if (!hadImageOcrPreview && _hasImageOcrPreview)
        {
            OnPropertyChanged(nameof(HasImageOcrPreview));
            OpenImageOcrPreviewCommand.NotifyCanExecuteChanged();
        }

        if (!hadStructuredSnippets && _hasStructuredSnippets)
            OnPropertyChanged(nameof(HasStructuredSnippets));

        if (previousCount == 0)
            OnPropertyChanged(nameof(HasPreviewHits));

        if (_hits.Count > CollapsedHitLimit)
        {
            OnPropertyChanged(nameof(HasMoreHits));
            OnPropertyChanged(nameof(ExtraHitCount));
            OnPropertyChanged(nameof(MoreText));
        }

        if (_hits.Count > PreviewHitLimit)
        {
            OnPropertyChanged(nameof(ExtraPreviewHitCount));
            OnPropertyChanged(nameof(HasMorePreviewHits));
            OnPropertyChanged(nameof(PreviewMoreText));
        }
    }

    public void UpdatePath(string fullPath)
    {
        FullPath = fullPath;
        IsDirectory = System.IO.Directory.Exists(fullPath) && !File.Exists(fullPath);
        FileName = GetDisplayName(fullPath, IsDirectory);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        Extension = IsDirectory ? string.Empty : Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();

        for (var i = 0; i < _hits.Count; i++)
            _hits[i] = _hits[i] with { Path = fullPath };

        _metadataLoaded = false;
        _metadataVersion++;
        _sizeText = null;
        _modifiedText = null;
        _sizeBytes = null;
        _modifiedUtc = null;

        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayDirectory));
        OnPropertyChanged(nameof(Directory));
        OnPropertyChanged(nameof(IsDirectory));
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(ExtensionPattern));
        OnPropertyChanged(nameof(ExcludeExtensionPatternMenuText));
        OnPropertyChanged(nameof(VisibleHits));
        OnPropertyChanged(nameof(PreviewHits));
        OnPropertyChanged(nameof(HasPreviewHits));
        OnPropertyChanged(nameof(ExtraPreviewHitCount));
        OnPropertyChanged(nameof(HasMorePreviewHits));
        OnPropertyChanged(nameof(PreviewMoreText));
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeGroup));
        OnPropertyChanged(nameof(SizeFacet));
        OnPropertyChanged(nameof(ModifiedUtc));
        OnPropertyChanged(nameof(ModifiedSortTicks));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(ModifiedDateGroup));
        OnPropertyChanged(nameof(ModifiedDateFacet));
        OnPropertyChanged(nameof(FileTypeGroup));
        OnPropertyChanged(nameof(HasImageOcrPreview));
        OpenImageOcrPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(VisibleHits));
        OnPropertyChanged(nameof(MoreText));
    }

    partial void OnHitCountChanged(int value) =>
        OnPropertyChanged(nameof(MatchCountText));

    partial void OnIsPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(PinActionText));
        OnPropertyChanged(nameof(PinGlyph));
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(FavoriteGlyph));
    }

    // ----- row-level commands -----

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;
    [RelayCommand]
    private Task OpenAsync() => OpenHitAsync(GetBestSourceHit());

    /// <summary>
    /// Opens the file at <paramref name="lineNumber"/> (the preview's current
    /// match). Uses that line's hit when there is one so page/sheet anchors
    /// survive; any other line opens as a plain line location.
    /// </summary>
    public Task OpenAtLineAsync(int? lineNumber)
    {
        if (lineNumber is not > 0)
            return OpenAsync();

        var hit = _hits.FirstOrDefault(candidate => candidate.LineNumber == lineNumber)
            ?? new Hit(FullPath, lineNumber.Value, string.Empty, Array.Empty<FileSearch.Core.Queries.MatchSpan>(),
                Anchor: IsStoreMessage ? _hits.FirstOrDefault()?.Anchor : null,
                Locator: IsStoreMessage ? new SourceLocator(MailMessage: MailMessage) : null);
        return OpenHitAsync(hit);
    }

    private async Task OpenHitAsync(Hit? hit)
    {
        var opened = hit is not null &&
            await _launcher.OpenAtLocationAsync(FullPath, hit, CancellationToken.None).ConfigureAwait(true);
        if (!opened)
            _launcher.Open(FullPath);

        if (_recordOpenedAsync is null)
            return;

        try
        {
            await _recordOpenedAsync(FullPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Usage tracking should never block opening a result.
        }
    }

    private Hit? GetBestSourceHit() =>
        _hits
            .Where(HasSourceLocation)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.LineNumber <= 0 ? int.MaxValue : hit.LineNumber)
            .FirstOrDefault();

    private static bool HasSourceLocation(Hit hit) =>
        hit.Anchor is not null ||
        hit.Locator is not null ||
        hit.Snippet?.Locator is not null ||
        hit.LineNumber > 0;

    [RelayCommand(CanExecute = nameof(CanOpenImageOcrPreview))]
    private async Task OpenImageOcrPreviewAsync()
    {
        var preview = await ImageOcrPreviewViewModel
            .TryCreateAsync(FullPath, _hits, CancellationToken.None)
            .ConfigureAwait(true);
        if (preview is not null)
            _launcher.OpenImageOcrPreview(preview);
    }

    private bool CanOpenImageOcrPreview() => HasImageOcrPreview;

    [RelayCommand] private void RevealInExplorer() => _launcher.RevealInExplorer(FullPath);
    [RelayCommand] private void CopyPath() => _launcher.CopyToClipboard(FullPath);
    [RelayCommand] private void CopyFolderPath() => _launcher.CopyToClipboard(Directory);

    // ----- lazy file metadata (best-effort; never throws into the UI) -----

    private string ComputeSizeText()
    {
        try
        {
            return SizeBytes is { } size ? FormatSize(size) : "—";
        }
        catch
        {
            return "—";
        }
    }

    private string ComputeModifiedText()
    {
        try
        {
            return ModifiedUtc is { } modified
                ? modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture)
                : "—";
        }
        catch
        {
            return "—";
        }
    }

    internal event EventHandler? MetadataLoaded;

    internal void EnsureMetadataLoading()
    {
        if (_metadataLoaded || (_modifiedUtc.HasValue && (IsDirectory || _sizeBytes.HasValue)))
            return;

        _metadataLoaded = true;
        _ = LoadMetadataAsync(FullPath, IsDirectory, _metadataVersion);
    }

    private async Task LoadMetadataAsync(string path, bool isDirectory, int version)
    {
        await MetadataWorkers.WaitAsync().ConfigureAwait(true);
        (long? Size, DateTime? Modified) metadata;
        try
        {
            metadata = await Task.Run(() =>
            {
                try
                {
                    if (isDirectory)
                    {
                        var directory = new DirectoryInfo(path);
                        return ((long?)null, directory.Exists ? (DateTime?)directory.LastWriteTimeUtc : null);
                    }

                    var file = new FileInfo(path);
                    return file.Exists ? ((long?)file.Length, (DateTime?)file.LastWriteTimeUtc) : (null, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                    return ((long?)null, (DateTime?)null);
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            MetadataWorkers.Release();
        }

        if (version != _metadataVersion)
            return;
        if (metadata.Size is null && metadata.Modified is null)
            return;
        // A newer indexed hit remains authoritative if it arrived while the
        // best-effort filesystem read was in flight.
        _sizeBytes ??= metadata.Size;
        _modifiedUtc ??= metadata.Modified;
        _sizeText = null;
        _modifiedText = null;
        OnPropertyChanged(nameof(SizeBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeGroup));
        OnPropertyChanged(nameof(SizeFacet));
        OnPropertyChanged(nameof(ModifiedUtc));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(ModifiedSortTicks));
        OnPropertyChanged(nameof(ModifiedDateGroup));
        OnPropertyChanged(nameof(ModifiedDateFacet));
        MetadataLoaded?.Invoke(this, EventArgs.Empty);
    }

    private static string GetDisplayName(string path, bool isDirectory)
    {
        var trimmed = isDirectory
            ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : path;
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    private static string ToModifiedDateGroup(DateTime? modifiedUtc)
    {
        if (modifiedUtc is null)
            return "Modified date unknown";

        var modified = modifiedUtc.Value.ToLocalTime().Date;
        var today = DateTime.Today;
        if (modified == today)
            return "Modified today";
        if (modified >= today.AddDays(-7))
            return "Modified in last 7 days";
        if (modified >= today.AddDays(-30))
            return "Modified in last 30 days";
        return "Modified earlier";
    }

    private static string ToModifiedDateFacet(DateTime? modifiedUtc)
    {
        if (modifiedUtc is null)
            return "unknown";

        var modified = modifiedUtc.Value.ToLocalTime().Date;
        var today = DateTime.Today;
        if (modified == today)
            return "today";
        if (modified >= today.AddDays(-7))
            return "last7";
        if (modified >= today.AddDays(-30))
            return "last30";
        return "older";
    }

    private static string ToSizeGroup(long? sizeBytes)
    {
        if (sizeBytes is null)
            return "Size unknown";
        if (sizeBytes < 100 * 1024)
            return "Under 100 KB";
        if (sizeBytes < 10 * 1024 * 1024)
            return "100 KB to 10 MB";
        return "10 MB and larger";
    }

    private static string ToSizeFacet(long? sizeBytes)
    {
        if (sizeBytes is null)
            return "unknown";
        if (sizeBytes < 100 * 1024)
            return "small";
        if (sizeBytes < 10 * 1024 * 1024)
            return "medium";
        return "large";
    }
}
