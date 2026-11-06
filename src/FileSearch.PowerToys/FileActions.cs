using System.ComponentModel;
using System.Diagnostics;
using FileSearch.Core.Engine;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace FileSearch.PowerToys;

internal sealed partial class FileActionCommand : InvokableCommand
{
    private readonly Action _action;

    public FileActionCommand(string name, Action action)
    {
        Name = name;
        _action = action;
    }

    public override ICommandResult Invoke()
    {
        try
        {
            _action();
            return CommandResult.Dismiss();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return CommandResult.ShowToast("Could not open the item. Check the FileSearch application path in this extension's settings.");
        }
    }
}

internal static class FileActions
{
    public static void OpenFileSearch(string configuredPath, string? query = null, string? root = null,
        SearchTarget target = SearchTarget.Content, string? selectedPath = null)
    {
        var path = ResolveAppPath(configuredPath) ??
            throw new FileNotFoundException("Set the path to FileSearch.Gui.exe in the extension's settings.");
        using var process = Process.Start(CreateStartInfo(path, query, root, target, selectedPath));
    }

    internal static ProcessStartInfo CreateStartInfo(string path, string? query, string? root,
        SearchTarget target, string? selectedPath)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        if (!string.IsNullOrWhiteSpace(query))
        {
            start.ArgumentList.Add("--query");
            start.ArgumentList.Add(query);
            start.ArgumentList.Add("--target");
            start.ArgumentList.Add(target == SearchTarget.FileNames ? "files" : "content");
        }
        if (!string.IsNullOrWhiteSpace(root))
        {
            start.ArgumentList.Add("--path");
            start.ArgumentList.Add(root);
        }
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            start.ArgumentList.Add("--select");
            start.ArgumentList.Add(selectedPath);
        }
        return start;
    }

    private static string? ResolveAppPath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? configuredPath : null;

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "FileSearch.Gui.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FileSearch", "FileSearch.Gui.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FileSearch", "FileSearch.Gui.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FileSearch", "FileSearch.Gui.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void Reveal(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true,
        });
    }
}
