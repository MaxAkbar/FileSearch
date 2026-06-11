using CommunityToolkit.Mvvm.ComponentModel;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// The shared status-bar line. Injected into the feature view models so any
/// of them can report without referencing each other or the shell.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "Ready.";
}
