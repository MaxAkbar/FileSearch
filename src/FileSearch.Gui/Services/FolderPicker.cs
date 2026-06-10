using System;
using Microsoft.Win32;

namespace FileSearch.Gui.Services;

public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = string.IsNullOrEmpty(initialDirectory)
                ? Environment.CurrentDirectory
                : initialDirectory,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
