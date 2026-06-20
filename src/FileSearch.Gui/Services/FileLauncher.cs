using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Services;

public sealed class FileLauncher : IFileLauncher
{
    public void Open(string path)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenImageOcrPreview(ImageOcrPreviewViewModel preview)
    {
        if (preview is null || string.IsNullOrWhiteSpace(preview.ImagePath) || !File.Exists(preview.ImagePath))
            return;

        var window = new ImageOcrPreviewWindow
        {
            DataContext = preview,
        };

        var owner = System.Windows.Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(openWindow => openWindow.IsActive && openWindow is not QuickSearchWindow);
        if (owner is not null)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.Show();
        window.Activate();
    }

    public void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            Process.Start("explorer.exe", $"\"{path}\"");
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch (Exception)
        {
            // Clipboard can throw OpenClipboardFailedException on rare
            // races with other apps. Silently swallow — the user can retry.
        }
    }
}
