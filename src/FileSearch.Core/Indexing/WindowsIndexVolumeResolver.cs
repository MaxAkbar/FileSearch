using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileSearch.Core.Indexing;

internal sealed class WindowsIndexVolumeResolver : IIndexVolumeResolver
{
    private const int MaxPathBuffer = 32768;
    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileSupportsUsnJournal = 0x02000000;
    private const int FileIdType = 0;
    private const uint VolumeNameDos = 0;

    public bool TryResolveVolume(string root, out IndexVolumeInfo volume, out string fallbackReason)
    {
        volume = null!;
        fallbackReason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            fallbackReason = "USN replay is only available on Windows.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fallbackReason = $"Invalid root path: {ex.Message}";
            return false;
        }

        if (IsUncPath(fullPath))
        {
            volume = new IndexVolumeInfo(
                NormalizeRemoteKey(fullPath),
                Path.GetPathRoot(fullPath) ?? fullPath,
                string.Empty,
                null,
                "remote",
                IsRemote: true,
                UsnSupported: false);
            fallbackReason = "USN replay is unavailable for remote roots.";
            return true;
        }

        var rootPath = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            fallbackReason = "Root path has no volume.";
            return false;
        }

        try
        {
            var drive = new DriveInfo(rootPath);
            if (drive.DriveType == DriveType.Network)
            {
                volume = new IndexVolumeInfo(
                    rootPath.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(),
                    rootPath,
                    string.Empty,
                    null,
                    "remote",
                    IsRemote: true,
                    UsnSupported: false);
                fallbackReason = "USN replay is unavailable for mapped network drives.";
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            fallbackReason = $"Could not inspect drive type: {ex.Message}";
            return false;
        }

        var volumeRoot = GetVolumeRoot(fullPath, rootPath);
        if (!TryGetVolumeInformation(volumeRoot, out var serial, out var filesystem, out var flags, out fallbackReason))
            return false;

        var volumeKey = TryGetVolumeGuidPath(volumeRoot, out var guidPath)
            ? guidPath.TrimEnd('\\').ToUpperInvariant()
            : volumeRoot.TrimEnd('\\').ToUpperInvariant();

        var devicePath = ToVolumeDevicePath(volumeRoot, guidPath);
        var usnSupported = ((flags & FileSupportsUsnJournal) != 0) &&
            (filesystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
             filesystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase));

        volume = new IndexVolumeInfo(
            volumeKey,
            volumeRoot,
            devicePath,
            serial.ToString(CultureInfo.InvariantCulture),
            filesystem,
            IsRemote: false,
            usnSupported);
        fallbackReason = usnSupported
            ? string.Empty
            : $"Filesystem {filesystem} does not expose a supported local USN journal.";
        return true;
    }

    public bool TryGetFileIdentity(string path, out ResolvedFileIdentity identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows())
            return false;

        using var handle = CreateFileW(
            path,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return false;

        if (!GetFileInformationByHandle(handle, out var info))
            return false;

        var fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        identity = new ResolvedFileIdentity(
            fileId.ToString(CultureInfo.InvariantCulture),
            ParentFileReferenceNumber: null);
        return true;
    }

    public bool TryResolvePathFromFileId(
        IndexVolumeInfo volume,
        string fileReferenceNumber,
        out string path,
        out string fallbackReason)
    {
        path = string.Empty;
        fallbackReason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            fallbackReason = "File ID path resolution is only available on Windows.";
            return false;
        }

        if (volume.IsRemote || string.IsNullOrWhiteSpace(volume.DevicePath))
        {
            fallbackReason = "File ID path resolution requires a local volume handle.";
            return false;
        }

        if (!ulong.TryParse(fileReferenceNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed > long.MaxValue)
        {
            fallbackReason = "Only 64-bit file reference numbers can be resolved in V1.";
            return false;
        }

        using var volumeHandle = CreateFileW(
            volume.DevicePath,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
        {
            fallbackReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var descriptor = new FileIdDescriptor
        {
            Size = (uint)Marshal.SizeOf<FileIdDescriptor>(),
            Type = FileIdType,
            FileId = unchecked((long)parsed),
            ExtendedFileId = 0,
        };

        using var fileHandle = OpenFileById(
            volumeHandle,
            ref descriptor,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics);

        if (fileHandle.IsInvalid)
        {
            fallbackReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        var builder = new StringBuilder(MaxPathBuffer);
        var length = GetFinalPathNameByHandleW(fileHandle, builder, builder.Capacity, VolumeNameDos);
        if (length == 0 || length >= builder.Capacity)
        {
            fallbackReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        path = StripExtendedPrefix(builder.ToString());
        return true;
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) &&
        !path.StartsWith(@"\\?\", StringComparison.Ordinal);

    private static string NormalizeRemoteKey(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrWhiteSpace(root)
            ? path.ToUpperInvariant()
            : root.TrimEnd('\\').ToUpperInvariant();
    }

    private static string GetVolumeRoot(string path, string fallback)
    {
        var builder = new StringBuilder(MaxPathBuffer);
        return GetVolumePathNameW(path, builder, builder.Capacity)
            ? EnsureTrailingSlash(builder.ToString())
            : EnsureTrailingSlash(fallback);
    }

    private static bool TryGetVolumeInformation(
        string volumeRoot,
        out uint serial,
        out string filesystem,
        out uint flags,
        out string fallbackReason)
    {
        var fsBuilder = new StringBuilder(64);
        var nameBuilder = new StringBuilder(256);
        var ok = GetVolumeInformationW(
            volumeRoot,
            nameBuilder,
            nameBuilder.Capacity,
            out serial,
            out _,
            out flags,
            fsBuilder,
            fsBuilder.Capacity);

        if (!ok)
        {
            filesystem = string.Empty;
            fallbackReason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        filesystem = fsBuilder.ToString();
        fallbackReason = string.Empty;
        return true;
    }

    private static bool TryGetVolumeGuidPath(string volumeRoot, out string guidPath)
    {
        var builder = new StringBuilder(MaxPathBuffer);
        if (GetVolumeNameForVolumeMountPointW(volumeRoot, builder, builder.Capacity))
        {
            guidPath = builder.ToString();
            return true;
        }

        guidPath = string.Empty;
        return false;
    }

    private static string ToVolumeDevicePath(string volumeRoot, string guidPath)
    {
        if (!string.IsNullOrWhiteSpace(guidPath))
            return guidPath.TrimEnd('\\');

        var trimmed = volumeRoot.TrimEnd('\\');
        return trimmed.EndsWith(':')
            ? @"\\.\" + trimmed
            : trimmed;
    }

    private static string EnsureTrailingSlash(string path) =>
        path.EndsWith('\\') ? path : path + "\\";

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

#pragma warning disable CA1838
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumePathNameW(
        string fileName,
        StringBuilder volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint,
        StringBuilder volumeName,
        int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        int filePathSize,
        uint flags);
#pragma warning restore CA1838

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor
    {
        public uint Size;
        public int Type;
        public long FileId;
        public long ExtendedFileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeParts
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTimeParts CreationTime;
        public FileTimeParts LastAccessTime;
        public FileTimeParts LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
