using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Volumes;

/// <summary>Where a scan starts: a whole volume, or (in tests) one folder.</summary>
internal sealed record VolumeScanTarget(string RootDirectory, string VolumeDevicePath);

internal interface IVolumeScanner
{
    /// <summary>
    /// Builds a name table for <paramref name="target"/> with an already
    /// resolved method (<see cref="VolumeBuildMethod.MasterFileTable"/> or
    /// <see cref="VolumeBuildMethod.DirectoryWalk"/>).
    /// </summary>
    VolumeNameTable Scan(
        VolumeScanTarget target,
        VolumeBuildMethod method,
        IProgress<long>? progress,
        CancellationToken cancellationToken);
}

internal sealed class WindowsVolumeScanner : IVolumeScanner
{
    public VolumeNameTable Scan(
        VolumeScanTarget target,
        VolumeBuildMethod method,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Volume name indexing is only available on Windows.");

        return method switch
        {
            VolumeBuildMethod.MasterFileTable => MasterFileTableScanner.Scan(target.VolumeDevicePath, progress, cancellationToken),
            VolumeBuildMethod.DirectoryWalk => DirectoryWalkScanner.Scan(target.RootDirectory, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Resolve Auto before scanning."),
        };
    }
}

/// <summary>
/// Enumerates every MFT record with FSCTL_ENUM_USN_DATA. Each output record
/// is a USN_RECORD carrying the file reference, parent reference,
/// attributes, and name, which is all the name table needs. Windows only
/// grants the volume handle this requires to administrators.
/// </summary>
internal static class MasterFileTableScanner
{
    private const int OutputBufferBytes = 1 << 20;
    private const int FirstUserRecord = 16;

    public static VolumeNameTable Scan(string volumeDevicePath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var volume = VolumeNativeMethods.CreateFileW(
            volumeDevicePath,
            VolumeNativeMethods.GenericRead,
            VolumeNativeMethods.FileShareReadWriteDelete,
            IntPtr.Zero,
            VolumeNativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (volume.IsInvalid)
            throw CreateException(Marshal.GetLastWin32Error(), volumeDevicePath);

        var table = new VolumeNameTable(VolumeNameTable.NtfsRootRecord, 1 << 16);
        var input = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8, 8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16, 8), long.MaxValue);
        var output = new byte[OutputBufferBytes];
        long seen = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VolumeNativeMethods.DeviceIoControl(
                    volume,
                    VolumeNativeMethods.FsctlEnumUsnData,
                    input,
                    input.Length,
                    output,
                    output.Length,
                    out var returned,
                    IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == VolumeNativeMethods.ErrorHandleEof)
                    break;

                throw CreateException(error, volumeDevicePath);
            }

            if (returned <= 8)
                break;

            seen += ParseRecords(output.AsSpan(0, returned), table);
            output.AsSpan(0, 8).CopyTo(input);
            progress?.Report(seen);
        }

        EnsureRoot(table, volumeDevicePath);
        progress?.Report(seen);
        table.TrimExcess();
        return table;
    }

    /// <summary>
    /// FSCTL_ENUM_USN_DATA does not report the root directory's own record,
    /// so record it explicitly; every path and snapshot is anchored on it.
    /// </summary>
    internal static void EnsureRoot(VolumeNameTable table, string volumeDevicePath)
    {
        if (table.IsInUse(table.RootRecord))
            return;

        var root = VolumeNativeMethods.TryGetFileReference(volumeDevicePath.TrimEnd('\\') + "\\", out var reference, out _) &&
            VolumeNameTable.RecordOf(reference) == table.RootRecord
            ? reference
            : VolumeNameTable.ComposeReference(table.RootRecord, (ushort)table.RootRecord);
        table.Set(root, root, ".", FileAttributes.Directory);
    }

    internal static int ParseRecords(ReadOnlySpan<byte> buffer, VolumeNameTable table)
    {
        var count = 0;
        var offset = 8;
        while (offset + 8 <= buffer.Length)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (length <= 0 || offset + length > buffer.Length)
                break;

            var record = buffer.Slice(offset, length);
            offset += length;
            if (!UsnRecordFields.TryRead(record, out var fileReference, out var parentReference, out var attributes, out var name))
                continue;

            var number = VolumeNameTable.RecordOf(fileReference);
            if (number < FirstUserRecord && number != table.RootRecord)
                continue;

            table.Set(fileReference, parentReference, name, attributes);
            count++;
        }

        return count;
    }

    private static Exception CreateException(int error, string volume) =>
        error == VolumeNativeMethods.ErrorAccessDenied
            ? new UnauthorizedAccessException(
                $"Reading the master file table of {volume} requires administrator rights.",
                new Win32Exception(error))
            : new IOException($"Master file table enumeration failed for {volume}: {new Win32Exception(error).Message}", new Win32Exception(error));
}

/// <summary>Reads the fields the name table needs from a USN_RECORD_V2/V3.</summary>
internal static class UsnRecordFields
{
    public static bool TryRead(
        ReadOnlySpan<byte> record,
        out ulong fileReference,
        out ulong parentReference,
        out FileAttributes attributes,
        out ReadOnlySpan<char> name)
    {
        fileReference = 0;
        parentReference = 0;
        attributes = 0;
        name = default;
        if (record.Length < 8)
            return false;

        var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int nameLength;
        int nameOffset;
        if (major == 2 && record.Length >= 60)
        {
            fileReference = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            parentReference = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
            attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
            nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
        }
        else if (major == 3 && record.Length >= 76)
        {
            // 128-bit identifiers; on NTFS the low 64 bits are the classic reference.
            fileReference = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
            parentReference = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]);
            attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
            nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[72..]);
            nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[74..]);
        }
        else
        {
            return false;
        }

        if (nameLength % 2 != 0 || nameOffset + nameLength > record.Length)
            return false;

        name = MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, nameLength));
        return true;
    }
}

/// <summary>
/// Unprivileged fallback: walks every directory the user can open and reads
/// entries with GetFileInformationByHandleEx(FileIdBothDirectoryInfo),
/// which returns each child's NTFS file reference. That yields the same
/// reference-keyed table as an MFT scan, so the journal can keep it current
/// the same way. Reparse points are recorded but never followed.
/// </summary>
internal static class DirectoryWalkScanner
{
    private const int DirectoryBufferBytes = 64 * 1024;
    private const int FileIdOffset = 96;
    private const int FileNameOffset = 104;

    public static VolumeNameTable Scan(string rootDirectory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!VolumeNativeMethods.TryGetFileReference(rootDirectory, out var rootReference, out _))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"Could not open {rootDirectory}: {new Win32Exception(error).Message}");
        }

        var table = new VolumeNameTable(VolumeNameTable.RecordOf(rootReference), 1 << 16);
        table.Set(rootReference, rootReference, ".", FileAttributes.Directory);

        var queue = new ConcurrentQueue<(string Path, ulong Reference)>();
        queue.Enqueue((rootDirectory, rootReference));
        var pending = 1;
        long seen = 0;
        var tableLock = new object();
        var parallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);

        void Worker()
        {
            var buffer = new byte[DirectoryBufferBytes];
            var batch = new List<(ulong Reference, FileAttributes Attributes, string Name)>(256);
            var spinner = default(SpinWait);
            while (Volatile.Read(ref pending) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!queue.TryDequeue(out var directory))
                {
                    spinner.SpinOnce(sleep1Threshold: 20);
                    continue;
                }

                spinner.Reset();
                try
                {
                    batch.Clear();
                    ListDirectory(directory.Path, buffer, batch);
                    lock (tableLock)
                    {
                        foreach (var (reference, attributes, name) in batch)
                            table.Set(reference, directory.Reference, name, attributes);
                    }

                    foreach (var (reference, attributes, name) in batch)
                    {
                        if ((attributes & FileAttributes.Directory) == 0 ||
                            (attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        Interlocked.Increment(ref pending);
                        queue.Enqueue((Path.Join(directory.Path, name), reference));
                    }

                    var total = Interlocked.Add(ref seen, batch.Count);
                    if (progress is not null && total / 20_000 != (total - batch.Count) / 20_000)
                        progress.Report(total);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }
        }

        var workers = new Task[parallelism];
        for (var i = 0; i < workers.Length; i++)
            workers[i] = Task.Factory.StartNew(Worker, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Task.WaitAll(workers, cancellationToken);
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException canceled)
        {
            throw canceled;
        }

        progress?.Report(Interlocked.Read(ref seen));
        table.TrimExcess();
        return table;
    }

    private static void ListDirectory(
        string path,
        byte[] buffer,
        List<(ulong Reference, FileAttributes Attributes, string Name)> batch)
    {
        using var handle = VolumeNativeMethods.OpenForMetadata(
            path,
            VolumeNativeMethods.FileListDirectory | VolumeNativeMethods.Synchronize,
            openReparsePoint: true);
        if (handle.IsInvalid)
            return;

        var infoClass = VolumeNativeMethods.FileIdBothDirectoryRestartInfoClass;
        while (VolumeNativeMethods.GetFileInformationByHandleEx(handle, infoClass, buffer, buffer.Length))
        {
            infoClass = VolumeNativeMethods.FileIdBothDirectoryInfoClass;
            ParseEntries(buffer, batch);
        }
    }

    internal static void ParseEntries(
        ReadOnlySpan<byte> buffer,
        List<(ulong Reference, FileAttributes Attributes, string Name)> batch)
    {
        var offset = 0;
        while (offset + FileNameOffset <= buffer.Length)
        {
            var entry = buffer[offset..];
            var next = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(entry[60..]);
            var reference = BinaryPrimitives.ReadUInt64LittleEndian(entry[FileIdOffset..]);
            if (nameBytes > 0 && nameBytes % 2 == 0 && FileNameOffset + nameBytes <= entry.Length)
            {
                var name = MemoryMarshal.Cast<byte, char>(entry.Slice(FileNameOffset, nameBytes));
                if (!name.SequenceEqual(".") && !name.SequenceEqual(".."))
                    batch.Add((reference, attributes, new string(name)));
            }

            if (next <= 0)
                break;

            offset += next;
        }
    }
}
