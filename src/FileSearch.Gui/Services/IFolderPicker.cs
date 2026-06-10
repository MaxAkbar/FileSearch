namespace FileSearch.Gui.Services;

/// <summary>
/// Abstracts the folder-picker dialog so view models stay free of WPF
/// dialog types and can be exercised in tests.
/// </summary>
public interface IFolderPicker
{
    /// <summary>Shows the picker; returns the chosen folder or null if cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory);
}
