using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using FileSearch.Core.Engine;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Services;

public sealed class FileLauncher : IFileLauncher
{
    public void Open(string path)
    {
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public async Task<bool> OpenAtLocationAsync(
        string path,
        Hit hit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var ocrPreview = await ImageOcrPreviewViewModel
            .TryCreateAsync(path, new[] { hit }, cancellationToken)
            .ConfigureAwait(true);
        if (ocrPreview is not null)
        {
            OpenImageOcrPreview(ocrPreview);
            return true;
        }

        var target = SourceOpenTargetBuilder.TryCreate(path, hit, FindVisualStudioCodePath());
        return target is not null && TryLaunch(target);
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

    private static bool TryLaunch(SourceOpenTarget target)
    {
        try
        {
            ProcessStartInfo startInfo;
            if (target.Kind == SourceOpenTargetKind.PdfPageUri)
            {
                if (string.IsNullOrWhiteSpace(target.Uri))
                    return false;
                startInfo = new ProcessStartInfo(target.Uri) { UseShellExecute = true };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(target.FileName))
                    return false;
                startInfo = new ProcessStartInfo(target.FileName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var argument in target.Arguments)
                    startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindVisualStudioCodePath()
    {
        foreach (var path in GetVisualStudioCodeCandidates())
        {
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> GetVisualStudioCodeCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Microsoft VS Code", "Code.exe");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe");

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(entry, "code.exe");
        }
    }
}
