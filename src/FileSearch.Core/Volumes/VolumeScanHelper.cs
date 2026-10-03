using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Volumes;

/// <summary>
/// Entry point for the elevated master file table scan. A host that is not
/// elevated starts <c>FileSearch.Indexer.exe --scan-volume C: --output
/// &lt;file&gt;</c> through UAC; the helper scans, writes one snapshot file,
/// and exits. It never overwrites an existing file and only writes files
/// with the scan extension into an existing, non-reparse-point folder, so
/// the elevated process cannot be steered into replacing other files.
/// </summary>
public static class VolumeScanHelper
{
    public const string Command = "--scan-volume";
    public const string OutputExtension = ".fsvol.scan";
    public const string ErrorExtension = ".error";

    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;
    public const int ExitAccessDenied = 3;
    public const int ExitUnsupported = 4;
    public const int ExitFailed = 5;

    public static bool IsScanCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], Command, StringComparison.OrdinalIgnoreCase);

    public static int Run(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out var volumeRoot, out var outputPath, out var usageError))
        {
            Console.Error.WriteLine(usageError);
            return ExitUsage;
        }

        if (!IsSafeOutputPath(outputPath, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return ExitUsage;
        }

        try
        {
            var resolver = new WindowsIndexVolumeResolver();
            if (!resolver.TryResolveVolume(volumeRoot, out var volume, out var reason) ||
                volume.IsRemote ||
                !volume.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(outputPath, string.IsNullOrWhiteSpace(reason) ? $"{volumeRoot} is not a local NTFS volume." : reason);
                return ExitUnsupported;
            }

            UsnJournalSnapshot? journal = null;
            try
            {
                journal = new WindowsUsnJournalReader().QueryAsync(volume, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
            {
                // Without a journal the snapshot is still useful; it just cannot be caught up.
            }

            var table = MasterFileTableScanner.Scan(volume.VolumeDevicePath, progress: null, CancellationToken.None);
            var header = new VolumeSnapshotHeader(
                volume.VolumeKey,
                volume.VolumeSerial,
                volumeRoot,
                journal?.JournalId ?? 0,
                journal?.NextUsn ?? 0,
                VolumeBuildMethod.MasterFileTable,
                DateTime.UtcNow,
                DateTime.UtcNow,
                table.RootRecord,
                table.SequenceAt(table.RootRecord),
                table.Count);

            using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            VolumeSnapshotSerializer.Write(stream, header, table);
            stream.Flush(flushToDisk: true);
            return ExitSuccess;
        }
        catch (UnauthorizedAccessException ex)
        {
            WriteError(outputPath, ex.Message);
            return ExitAccessDenied;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            WriteError(outputPath, ex.Message);
            return ExitFailed;
        }
    }

    internal static bool TryParse(
        IReadOnlyList<string> args,
        out string volumeRoot,
        out string outputPath,
        out string error)
    {
        volumeRoot = string.Empty;
        outputPath = string.Empty;
        error = $"Usage: {Command} <drive:> --output <file{OutputExtension}>";
        if (!IsScanCommand(args) || args.Count != 4 ||
            !string.Equals(args[2], "--output", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        volumeRoot = VolumeNameIndexService.NormalizeVolumeRoot(args[1]);
        outputPath = args[3];
        return volumeRoot.Length == 3 && volumeRoot[1] == ':';
    }

    internal static bool IsSafeOutputPath(string path, out string error)
    {
        error = string.Empty;
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(OutputExtension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"The output must be a full path ending in {OutputExtension}.";
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            error = "The output folder does not exist.";
            return false;
        }

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            error = "The output folder must not be a link.";
            return false;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            error = "The output file already exists.";
            return false;
        }

        return true;
    }

    private static void WriteError(string outputPath, string message)
    {
        try
        {
            using var stream = new FileStream(outputPath + ErrorExtension, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes(message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Starts <c>FileSearch.Indexer.exe</c> with the "runas" verb so Windows
/// shows its elevation prompt, then waits for the scan to finish.
/// </summary>
public sealed class ProcessVolumeScanLauncher : IVolumeScanLauncher
{
    public const string HelperExecutableName = "FileSearch.Indexer.exe";
    private readonly string? _helperPath;

    public ProcessVolumeScanLauncher(string? helperPath = null)
    {
        _helperPath = helperPath;
    }

    public async Task RunElevatedScanAsync(string volumeRoot, string outputPath, CancellationToken cancellationToken)
    {
        var helper = _helperPath ?? ResolveHelperPath(AppContext.BaseDirectory)
            ?? throw new FileNotFoundException($"{HelperExecutableName} was not found next to the application.");
        var drive = VolumeNameIndexService.NormalizeVolumeRoot(volumeRoot).TrimEnd('\\');
        var startInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"{VolumeScanHelper.Command} {drive} --output \"{outputPath}\"",
        };

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == VolumeNativeMethods.ErrorCancelled)
        {
            throw new OperationCanceledException("Administrator permission was declined.", ex);
        }
        catch (Win32Exception ex)
        {
            // For example, a packaged app without permission to elevate.
            throw new IOException($"The administrator scan could not start: {ex.Message}", ex);
        }

        if (process is null)
            throw new IOException($"Could not start {HelperExecutableName}.");

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode == VolumeScanHelper.ExitSuccess)
                return;

            var errorPath = outputPath + VolumeScanHelper.ErrorExtension;
            var message = File.Exists(errorPath)
                ? await File.ReadAllTextAsync(errorPath, cancellationToken).ConfigureAwait(false)
                : $"The elevated scan exited with code {process.ExitCode}.";
            throw process.ExitCode == VolumeScanHelper.ExitAccessDenied
                ? new UnauthorizedAccessException(message)
                : new IOException(message);
        }
    }

    /// <summary>
    /// Finds the helper next to the running app, or in a sibling project's
    /// build output when running from source.
    /// </summary>
    public static string? ResolveHelperPath(string baseDirectory)
    {
        var sameDirectory = Path.Combine(baseDirectory, HelperExecutableName);
        if (File.Exists(sameDirectory))
            return sameDirectory;

        try
        {
            var buildRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "FileSearch.Indexer", "bin"));
            if (!Directory.Exists(buildRoot))
                return null;

            return Directory
                .EnumerateFiles(buildRoot, HelperExecutableName, SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
