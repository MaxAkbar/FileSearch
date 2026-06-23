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
    private long? _sizeBytes;
    private DateTime? _modifiedUtc;
    private bool _hasIndexedHits;
    private bool _hasLiveHits;

    public FileResultViewModel(
        string fullPath,
        IFileLauncher launcher,
        Func<string, CancellationToken, Task>? recordOpenedAsync = null,
        int searchRank = 0)
    {
        FullPath = fullPath;
        IsDirectory = System.IO.Directory.Exists(fullPath) && !File.Exists(fullPath);
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

    /// <summary>Lower-cased extension without the leading dot (e.g. "cs").</summary>
    public string Extension { get; private set; }

    public string ExtensionPattern =>
        string.IsNullOrWhiteSpace(Extension) ? string.Empty : $"*.{Extension}";

    public string ExcludeExtensionPatternMenuText =>
        string.IsNullOrWhiteSpace(ExtensionPattern) ? "Exclude extension" : $"Exclude {ExtensionPattern}";

    public IReadOnlyList<Hit> Hits => _hits;

    public bool HasImageOcrPreview => ImageOcrPreviewViewModel.HasPreviewAnchor(_hits);

    public bool HasStructuredSnippets => _hits.Any(hit => hit.Snippet is not null);

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

    public string PinActionText => IsPinned ? "Unpin result" : "Pin result";

    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";

    public string FavoriteActionText => IsFavorite ? "Remove favorite" : "Add favorite";

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

    /// <summary>Human-readable file size, loaded lazily on first access.</summary>
    public string SizeText => _sizeText ??= ComputeSizeText();

    /// <summary>Last-modified timestamp, loaded lazily on first access.</summary>
    public string ModifiedText => _modifiedText ??= ComputeModifiedText();

    public long? SizeBytes
    {
        get
        {
            EnsureMetadataLoaded();
            return _sizeBytes;
        }
    }

    public DateTime? ModifiedUtc
    {
        get
        {
            EnsureMetadataLoaded();
            return _modifiedUtc;
        }
    }

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

    public void AddHit(Hit hit)
    {
        var hadImageOcrPreview = HasImageOcrPreview;
        _hits.Add(hit);
        HitCount = _hits.Count;
        if (_hits.Count == 1)
            FirstMatch = hit.LineContent.Trim();
        if (hit.Score > BestScore)
            BestScore = hit.Score;
        if (hit.SizeBytes is { } size)
        {
            _sizeBytes = size;
            _sizeText = null;
            OnPropertyChanged(nameof(SizeBytes));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(SizeGroup));
            OnPropertyChanged(nameof(SizeFacet));
        }
        if (hit.ModifiedUtc is { } modified)
        {
            _modifiedUtc = modified;
            _modifiedText = null;
            OnPropertyChanged(nameof(ModifiedUtc));
            OnPropertyChanged(nameof(ModifiedSortTicks));
            OnPropertyChanged(nameof(ModifiedText));
            OnPropertyChanged(nameof(ModifiedDateGroup));
            OnPropertyChanged(nameof(ModifiedDateFacet));
        }

        var oldSource = SourceGroup;
        _hasIndexedHits |= hit.Route == HitRoute.Indexed;
        _hasLiveHits |= hit.Route == HitRoute.Live;
        if (!string.Equals(oldSource, SourceGroup, StringComparison.Ordinal))
            OnPropertyChanged(nameof(SourceGroup));

        // Refresh the rendered lines only while they can still change:
        // collapsed cards freeze at the first few, expanded cards keep growing.
        if (IsExpanded || _hits.Count <= CollapsedHitLimit)
            OnPropertyChanged(nameof(VisibleHits));
        if (_hits.Count <= PreviewHitLimit + 1)
            OnPropertyChanged(nameof(PreviewHits));
        if (!hadImageOcrPreview && HasImageOcrPreview)
        {
            OnPropertyChanged(nameof(HasImageOcrPreview));
            OpenImageOcrPreviewCommand.NotifyCanExecuteChanged();
        }

        if (hit.Snippet is not null)
            OnPropertyChanged(nameof(HasStructuredSnippets));

        OnPropertyChanged(nameof(HasMoreHits));
        OnPropertyChanged(nameof(ExtraHitCount));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(HasPreviewHits));
        OnPropertyChanged(nameof(ExtraPreviewHitCount));
        OnPropertyChanged(nameof(HasMorePreviewHits));
        OnPropertyChanged(nameof(PreviewMoreText));
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
        _sizeText = null;
        _modifiedText = null;
        _sizeBytes = null;
        _modifiedUtc = null;

        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(FileName));
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
    private async Task OpenAsync()
    {
        var hit = GetBestSourceHit();
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

    private void EnsureMetadataLoaded()
    {
        if (_metadataLoaded)
            return;

        _metadataLoaded = true;
        try
        {
            if (IsDirectory)
            {
                var directory = new DirectoryInfo(FullPath);
                if (directory.Exists)
                    _modifiedUtc ??= directory.LastWriteTimeUtc;
                return;
            }

            var info = new FileInfo(FullPath);
            if (info.Exists)
            {
                _sizeBytes ??= info.Length;
                _modifiedUtc ??= info.LastWriteTimeUtc;
            }
        }
        catch
        {
        }
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
