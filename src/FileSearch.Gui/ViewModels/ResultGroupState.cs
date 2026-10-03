using CommunityToolkit.Mvvm.ComponentModel;

namespace FileSearch.Gui.ViewModels;

/// <summary>Search-local group state survives container recycling and view refreshes.</summary>
public sealed partial class ResultGroupState : ObservableObject
{
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _summaryText = string.Empty;

    internal void Update(int files, long matches) =>
        SummaryText = $"{files:n0} {(files == 1 ? "file" : "files")} · {matches:n0} {(matches == 1 ? "match" : "matches")}";
}
