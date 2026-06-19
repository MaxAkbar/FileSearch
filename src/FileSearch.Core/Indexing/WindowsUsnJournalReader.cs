using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FileSearch.Core.Indexing;

internal sealed class WindowsUsnJournalReader : IUsnJournalReader
{
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FileTraverse = 0x00000020;
    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;
    private const int QueryOutputBytes = 256;
    private const int ReadInputBytes = 44;
    private const int ReadOutputBytes = 1024 * 1024;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorJournalEntryDeleted = 1177;

    public Task<UsnJournalSnapshot> QueryAsync(
        IndexVolumeInfo volume,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USN journal access is only available on Windows.");

        using var handle = OpenVolume(volume);
        var output = new byte[QueryOutputBytes];
        if (!DeviceIoControl(
                handle,
                FsctlQueryUsnJournal,
                IntPtr.Zero,
                0,
                output,
                output.Length,
                out _,
                IntPtr.Zero))
        {
            throw CreateWin32Exception();
        }

        return Task.FromResult(new UsnJournalSnapshot(
            BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16, 8))));
    }

    public async IAsyncEnumerable<UsnChangeRecord> ReadChangesAsync(
        IndexVolumeInfo volume,
        long startUsn,
        long stopAtUsn,
        ulong journalId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USN journal access is only available on Windows.");

        using var handle = OpenVolume(volume);
        var cursor = startUsn;

        while (cursor < stopAtUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            var input = BuildReadInput(cursor, journalId);
            var output = new byte[ReadOutputBytes];
            if (!DeviceIoControlPinned(
                    handle,
                    FsctlReadUnprivilegedUsnJournal,
                    input,
                    output,
                    out var bytesReturned))
            {
                var win32Error = Marshal.GetLastWin32Error();
                if (win32Error != ErrorInvalidFunction ||
                    !DeviceIoControlPinned(
                        handle,
                        FsctlReadUsnJournal,
                        input,
                        output,
                        out bytesReturned))
                {
                    throw CreateWin32Exception(Marshal.GetLastWin32Error());
                }
            }

            if (!UsnRecordParser.TryParseBuffer(output, bytesReturned, out var nextUsn, out var records, out var error))
                throw new InvalidOperationException(error);

            if (nextUsn <= cursor)
                throw new IOException($"USN journal read made no progress. Cursor={cursor}, NextUsn={nextUsn}.");

            foreach (var record in records)
            {
                if (record.Usn >= stopAtUsn)
                    yield break;

                yield return record;
            }

            cursor = nextUsn;
        }
    }

    private static SafeFileHandle OpenVolume(IndexVolumeInfo volume)
    {
        if (string.IsNullOrWhiteSpace(volume.VolumeDevicePath))
            throw new InvalidOperationException("Volume device path is required for USN replay.");

        var handle = CreateFileW(
            volume.VolumeDevicePath,
            FileTraverse,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        return handle.IsInvalid
            ? throw CreateWin32Exception()
            : handle;
    }

    private static byte[] BuildReadInput(long startUsn, ulong journalId)
    {
        var input = new byte[ReadInputBytes];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0, 8), startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32, 8), journalId);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42, 2), 3);
        return input;
    }

    private static bool DeviceIoControlPinned(
        SafeFileHandle handle,
        uint controlCode,
        byte[] input,
        byte[] output,
        out int bytesReturned)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            return DeviceIoControl(
                handle,
                controlCode,
                inputHandle.AddrOfPinnedObject(),
                input.Length,
                outputHandle.AddrOfPinnedObject(),
                output.Length,
                out bytesReturned,
                IntPtr.Zero);
        }
        finally
        {
            inputHandle.Free();
            outputHandle.Free();
        }
    }

    private static Exception CreateWin32Exception() =>
        CreateWin32Exception(Marshal.GetLastWin32Error());

    private static Exception CreateWin32Exception(int error)
    {
        var exception = new Win32Exception(error);
        return error == ErrorJournalEntryDeleted
            ? new IOException(exception.Message, exception)
            : exception;
    }

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
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        int inBufferSize,
        byte[] outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
