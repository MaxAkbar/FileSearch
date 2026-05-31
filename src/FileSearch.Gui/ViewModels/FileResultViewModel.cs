using System.Collections.Generic;
using System.IO;
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
    private readonly List<Hit> _hits = new();
    private readonly IFileLauncher _launcher;

    public FileResultViewModel(string fullPath, IFileLauncher launcher)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        _launcher = launcher;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string Directory { get; }
    public IReadOnlyList<Hit> Hits => _hits;

    [ObservableProperty] private int _hitCount;
    [ObservableProperty] private string _firstMatch = string.Empty;

    public void AddHit(Hit hit)
    {
        _hits.Add(hit);
        HitCount = _hits.Count;
        if (_hits.Count == 1)
            FirstMatch = hit.LineContent.Trim();
    }

    // ----- row-level commands -----

    [RelayCommand] private void Open() => _launcher.Open(FullPath);
    [RelayCommand] private void RevealInExplorer() => _launcher.RevealInExplorer(FullPath);
    [RelayCommand] private void CopyPath() => _launcher.CopyToClipboard(FullPath);
    [RelayCommand] private void CopyFolderPath() => _launcher.CopyToClipboard(Directory);
}
