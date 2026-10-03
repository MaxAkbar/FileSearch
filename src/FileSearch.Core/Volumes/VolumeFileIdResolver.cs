using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileSearch.Core.Volumes;

internal enum VolumeFileIdLookupStatus
{
    Found,
    Gone,
    AccessDenied,
    Failed,
}

internal readonly record struct VolumeFileIdLookup(
    VolumeFileIdLookupStatus Status,
    string? Name,
    FileAttributes Attributes);

/// <summary>
/// Resolves the current name of a file reference. Unprivileged journal
/// reads (FSCTL_READ_UNPRIVILEGED_USN_JOURNAL) strip file names from every
/// record, so the tracker asks the file system directly instead.
/// </summary>
internal interface IVolumeFileIdResolver : IDisposable
{
    VolumeFileIdLookup Resolve(ulong fileReference);
}

internal interface IVolumeFileIdResolverFactory
{
    IVolumeFileIdResolver Create(string rootDirectory);
}

internal sealed class WindowsVolumeFileIdResolverFactory : IVolumeFileIdResolverFactory
{
    public IVolumeFileIdResolver Create(string rootDirectory) => new WindowsVolumeFileIdResolver(rootDirectory);
}

internal sealed class WindowsVolumeFileIdResolver : IVolumeFileIdResolver
{
    private const int PathBufferChars = 32768;
    private readonly SafeFileHandle _hint;
    private readonly char[] _buffer = new char[PathBufferChars];

    public WindowsVolumeFileIdResolver(string rootDirectory)
    {
        _hint = VolumeNativeMethods.OpenForMetadata(rootDirectory, 0, openReparsePoint: false);
        if (_hint.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            _hint.Dispose();
            throw new IOException($"Could not open {rootDirectory} to resolve file IDs (error {error}).");
        }
    }

    public VolumeFileIdLookup Resolve(ulong fileReference)
    {
        var descriptor = new VolumeNativeMethods.FileIdDescriptor
        {
            Size = (uint)Marshal.SizeOf<VolumeNativeMethods.FileIdDescriptor>(),
            Type = 0,
            FileId = unchecked((long)fileReference),
        };

        using var handle = VolumeNativeMethods.OpenFileById(
            _hint,
            ref descriptor,
            VolumeNativeMethods.FileReadAttributes,
            VolumeNativeMethods.FileShareReadWriteDelete,
            IntPtr.Zero,
            VolumeNativeMethods.FileFlagBackupSemantics | VolumeNativeMethods.FileFlagOpenReparsePoint);
        if (handle.IsInvalid)
            return new VolumeFileIdLookup(Classify(Marshal.GetLastWin32Error()), null, 0);

        if (!VolumeNativeMethods.GetFileInformationByHandle(handle, out var info))
            return new VolumeFileIdLookup(Classify(Marshal.GetLastWin32Error()), null, 0);

        var length = VolumeNativeMethods.GetFinalPathNameByHandleW(
            handle,
            _buffer,
            _buffer.Length,
            VolumeNativeMethods.VolumeNameNone);
        if (length == 0 || length >= _buffer.Length)
            return new VolumeFileIdLookup(Classify(Marshal.GetLastWin32Error()), null, 0);

        var path = _buffer.AsSpan(0, (int)length).TrimEnd('\\');
        var separator = path.LastIndexOf('\\');
        var name = separator >= 0 ? path[(separator + 1)..] : path;
        return name.IsEmpty
            ? new VolumeFileIdLookup(VolumeFileIdLookupStatus.Failed, null, 0)
            : new VolumeFileIdLookup(VolumeFileIdLookupStatus.Found, new string(name), (FileAttributes)info.FileAttributes);
    }

    public void Dispose() => _hint.Dispose();

    private static VolumeFileIdLookupStatus Classify(int error) => error switch
    {
        VolumeNativeMethods.ErrorFileNotFound or
            VolumeNativeMethods.ErrorPathNotFound or
            VolumeNativeMethods.ErrorNotFound or
            VolumeNativeMethods.ErrorInvalidParameter => VolumeFileIdLookupStatus.Gone,
        VolumeNativeMethods.ErrorAccessDenied => VolumeFileIdLookupStatus.AccessDenied,
        _ => VolumeFileIdLookupStatus.Failed,
    };
}
