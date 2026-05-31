using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FileSearch.Gui.Services;

public sealed class FileLauncher : IFileLauncher
{
    public void Open(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
