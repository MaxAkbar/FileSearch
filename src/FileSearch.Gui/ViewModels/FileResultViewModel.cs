using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly List<Hit> _hits = new();
    private readonly IFileLauncher _launcher;
    private readonly Func<string, CancellationToken, Task>? _recordOpenedAsync;

    private string? _sizeText;
    private string? _modifiedText;

    public FileResultViewModel(
        string fullPath,
        IFileLauncher launcher,
        Func<string, CancellationToken, Task>? recordOpenedAsync = null)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        Extension = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        _launcher = launcher;
        _recordOpenedAsync = recordOpenedAsync;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string Directory { get; }

    /// <summary>Lower-cased extension without the leading dot (e.g. "cs").</summary>
    public string Extension { get; }

    public string ExtensionPattern =>
        string.IsNullOrWhiteSpace(Extension) ? string.Empty : $"*.{Extension}";

    public string ExcludeExtensionPatternMenuText =>
        string.IsNullOrWhiteSpace(ExtensionPattern) ? "Exclude extension" : $"Exclude {ExtensionPattern}";

    public IReadOnlyList<Hit> Hits => _hits;

    [ObservableProperty] private int _hitCount;
    [ObservableProperty] private string _firstMatch = string.Empty;
    [ObservableProperty] private bool _isPinned;

    /// <summary>
    /// Whether the card is showing every hit or just the first
    /// <see cref="CollapsedHitLimit"/>. Toggled by <see cref="ToggleExpandCommand"/>.
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>The hit lines the card should currently render.</summary>
    public IEnumerable<Hit> VisibleHits =>
        IsExpanded ? _hits : _hits.Take(CollapsedHitLimit);

    public bool HasMoreHits => _hits.Count > CollapsedHitLimit;

    public int ExtraHitCount => Math.Max(0, _hits.Count - CollapsedHitLimit);

    public string MoreText =>
        IsExpanded
            ? "Show fewer"
            : $"+ {ExtraHitCount} more match{(ExtraHitCount == 1 ? string.Empty : "es")} in this file";

    public string PinActionText => IsPinned ? "Unpin result" : "Pin result";

    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";

    /// <summary>Human-readable file size, loaded lazily on first access.</summary>
    public string SizeText => _sizeText ??= ComputeSizeText();

    /// <summary>Last-modified timestamp, loaded lazily on first access.</summary>
    public string ModifiedText => _modifiedText ??= ComputeModifiedText();

    public void AddHit(Hit hit)
    {
        _hits.Add(hit);
        HitCount = _hits.Count;
        if (_hits.Count == 1)
            FirstMatch = hit.LineContent.Trim();

        // Refresh the rendered lines only while they can still change:
        // collapsed cards freeze at the first few, expanded cards keep growing.
        if (IsExpanded || _hits.Count <= CollapsedHitLimit)
            OnPropertyChanged(nameof(VisibleHits));
        OnPropertyChanged(nameof(HasMoreHits));
        OnPropertyChanged(nameof(ExtraHitCount));
        OnPropertyChanged(nameof(MoreText));
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

    // ----- row-level commands -----

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;
    [RelayCommand]
    private async Task OpenAsync()
    {
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
    [RelayCommand] private void RevealInExplorer() => _launcher.RevealInExplorer(FullPath);
    [RelayCommand] private void CopyPath() => _launcher.CopyToClipboard(FullPath);
    [RelayCommand] private void CopyFolderPath() => _launcher.CopyToClipboard(Directory);

    // ----- lazy file metadata (best-effort; never throws into the UI) -----

    private string ComputeSizeText()
    {
        try
        {
            return FormatSize(new FileInfo(FullPath).Length);
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
            return File.GetLastWriteTime(FullPath).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch
        {
            return "—";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }
}
