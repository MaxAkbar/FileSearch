using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileSearch.Gui.Services;

public sealed class ShellIntegrationService : IShellIntegrationService
{
    private const string CommandSubKeyName = "command";
    private const string IconValueName = "Icon";
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    public void Install()
    {
        var executablePath = GetExecutablePath();

        foreach (var registration in ShellIntegrationRegistration.BuildVerbRegistrations(executablePath))
            WriteVerb(registration, executablePath);

        CreateStartMenuShortcut(executablePath);
        NotifyShellAssociationsChanged();
    }

    public void Remove()
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            ShellIntegrationRegistration.FolderShellKeyPath,
            throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            ShellIntegrationRegistration.FolderBackgroundShellKeyPath,
            throwOnMissingSubKey: false);

        RemoveStartMenuShortcut();
        NotifyShellAssociationsChanged();
    }

    private static void WriteVerb(ShellVerbRegistration registration, string executablePath)
    {
        using var shellKey = Registry.CurrentUser.CreateSubKey(registration.ShellKeyPath);
        if (shellKey is null)
            throw new InvalidOperationException($"Could not create registry key: {registration.ShellKeyPath}");

        shellKey.SetValue(null, ShellIntegrationRegistration.MenuText, RegistryValueKind.String);
        shellKey.SetValue(IconValueName, executablePath, RegistryValueKind.String);

        using var commandKey = shellKey.CreateSubKey(CommandSubKeyName);
        if (commandKey is null)
            throw new InvalidOperationException($"Could not create command key: {registration.ShellKeyPath}\\{CommandSubKeyName}");

        commandKey.SetValue(null, registration.Command, RegistryValueKind.String);
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new InvalidOperationException("Could not resolve the FileSearch executable path.");

        return executablePath;
    }

    private static string GetStartMenuShortcutPath()
    {
        var programsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programsFolder))
            throw new InvalidOperationException("Could not resolve the Start Menu programs folder.");

        return Path.Combine(programsFolder, "FileSearch.lnk");
    }

    private static void CreateStartMenuShortcut(string executablePath)
    {
        var shortcutPath = GetStartMenuShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Could not create Windows shortcut shell object.");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create Windows shortcut shell object.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath },
                culture: CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("Could not create Start Menu shortcut.");

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { executablePath }, CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(executablePath) ?? string.Empty }, CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { executablePath }, CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Search files with FileSearch" }, CultureInfo.InvariantCulture);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>(), CultureInfo.InvariantCulture);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void RemoveStartMenuShortcut()
    {
        var shortcutPath = GetStartMenuShortcutPath();
        if (File.Exists(shortcutPath))
            File.Delete(shortcutPath);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void NotifyShellAssociationsChanged() =>
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
