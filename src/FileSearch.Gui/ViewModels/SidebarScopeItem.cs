using CommunityToolkit.Mvvm.ComponentModel;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.ViewModels;

public sealed partial class SidebarScopeItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    public string FileNamePattern { get; init; } = string.Empty;

    public string Glyph { get; init; } = "\uE8A5";

    public bool IsCustom { get; init; }

    public SearchScope? CustomScope { get; init; }

    [ObservableProperty] private bool _isActive;

    public string Summary =>
        string.IsNullOrWhiteSpace(FileNamePattern) ? "All files" : FileNamePattern;
}
