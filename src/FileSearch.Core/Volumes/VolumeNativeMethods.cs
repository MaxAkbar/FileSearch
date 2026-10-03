using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileSearch.Core.Volumes;

internal static class VolumeNativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint FileListDirectory = 0x00000001;
    public const uint FileReadAttributes = 0x00000080;
    public const uint Synchronize = 0x00100000;
    public const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FsctlEnumUsnData = 0x000900B3;
    public const uint VolumeNameNone = 0x4;

    public const int FileIdBothDirectoryInfoClass = 10;
    public const int FileIdBothDirectoryRestartInfoClass = 11;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidHandle = 6;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorHandleEof = 38;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorCancelled = 1223;
    public const int ErrorNotFound = 1168;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inBuffer,
        int inBufferSize,
        [Out] byte[] outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        [Out] byte[] fileInformation,
        int bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[] filePath,
        int filePathSize,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct FileIdDescriptor
    {
        public uint Size;
        public int Type;
        public long FileId;
        public long ExtendedFileId;
    }

    // FILETIME is two DWORDs, so the native struct is 4-byte packed; plain
    // long fields would be 8-byte aligned and shift every later field.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// Opens a path for metadata access only. Paths are prefixed with
    /// <c>\\?\</c> so deep trees are not cut off at MAX_PATH.
    /// </summary>
    public static SafeFileHandle OpenForMetadata(string path, uint desiredAccess, bool openReparsePoint) =>
        CreateFileW(
            ToExtendedPath(path),
            desiredAccess,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | (openReparsePoint ? FileFlagOpenReparsePoint : 0),
            IntPtr.Zero);

    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }

    public static bool TryGetFileReference(string path, out ulong fileReference, out uint attributes)
    {
        fileReference = 0;
        attributes = 0;
        if (!OperatingSystem.IsWindows())
            return false;

        using var handle = OpenForMetadata(path, 0, openReparsePoint: false);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            return false;

        fileReference = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        attributes = info.FileAttributes;
        return true;
    }
}
