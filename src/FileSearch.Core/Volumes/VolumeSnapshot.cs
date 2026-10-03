using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace FileSearch.Core.Volumes;

/// <summary>How a volume name index was (or should be) built.</summary>
public enum VolumeBuildMethod
{
    /// <summary>
    /// Master file table enumeration when the process is already elevated;
    /// otherwise the unprivileged directory walk.
    /// </summary>
    Auto,

    /// <summary>
    /// Enumerate the NTFS master file table with FSCTL_ENUM_USN_DATA. Fast
    /// and complete, but Windows only allows it for administrators.
    /// </summary>
    MasterFileTable,

    /// <summary>
    /// Walk every directory the current user can open, collecting file IDs.
    /// Slower, needs no elevation, and skips folders the user cannot read.
    /// </summary>
    DirectoryWalk,
}

internal sealed record VolumeSnapshotHeader(
    string VolumeKey,
    string? VolumeSerial,
    string VolumeRoot,
    ulong JournalId,
    long CheckpointUsn,
    VolumeBuildMethod BuildMethod,
    DateTime BuiltUtc,
    DateTime SavedUtc,
    int RootRecord,
    ushort RootSequence,
    int EntryCount);

/// <summary>
/// Binary snapshot of a <see cref="VolumeNameTable"/> plus the journal
/// checkpoint it is consistent with. Layout: an uncompressed 8-byte magic
/// and format version, then a Brotli stream with the header and one record
/// per entry (delta-encoded record number, sequence, parent, flags, and the
/// raw UTF-16 name, since NTFS names may hold unpaired surrogates that a
/// UTF-8 round-trip would corrupt).
/// </summary>
internal static class VolumeSnapshotSerializer
{
    public const string FileExtension = ".fsvol";
    public const int FormatVersion = 2;
    private const int EndMarker = -1;
    private static readonly byte[] s_magic = "FSVOLIDX"u8.ToArray();

    public static void WriteFile(string path, VolumeSnapshotHeader header, VolumeNameTable table)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                Write(stream, header, table);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static void Write(Stream destination, VolumeSnapshotHeader header, VolumeNameTable table)
    {
        destination.Write(s_magic);
        Span<byte> version = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(version, FormatVersion);
        destination.Write(version);

        using var compressed = new BrotliStream(destination, CompressionLevel.Fastest, leaveOpen: true);
        using var buffered = new BufferedStream(compressed, 1 << 16);
        using var writer = new BinaryWriter(buffered, Encoding.UTF8, leaveOpen: true);

        writer.Write(header.VolumeKey);
        writer.Write(header.VolumeSerial ?? string.Empty);
        writer.Write(header.VolumeRoot);
        writer.Write(header.JournalId);
        writer.Write(header.CheckpointUsn);
        writer.Write((int)header.BuildMethod);
        writer.Write(header.BuiltUtc.ToUniversalTime().Ticks);
        writer.Write(header.SavedUtc.ToUniversalTime().Ticks);
        writer.Write(table.RootRecord);
        writer.Write(table.IsInUse(table.RootRecord) ? table.SequenceAt(table.RootRecord) : header.RootSequence);
        writer.Write(table.Count);
        writer.Write(table.HighWater);

        var previous = 0;
        var written = 0;
        Span<char> buffer = stackalloc char[VolumeNameTable.MaxNameLength];
        for (var record = 0; record < table.HighWater; record++)
        {
            if (!table.IsInUse(record) || record == table.RootRecord)
                continue;

            var name = table.NameOf(record, buffer);
            writer.Write7BitEncodedInt(record - previous);
            writer.Write(table.SequenceAt(record));
            writer.Write7BitEncodedInt(table.ParentOf(record));
            writer.Write((byte)(table.FlagsOf(record) & ~VolumeEntryFlags.WideName));
            writer.Write((byte)name.Length);
            writer.Write(MemoryMarshal.AsBytes(name));
            previous = record;
            written++;
        }

        // The trailer counts entry records, not Table.Count: the root is
        // never written as an entry and the reader always recreates it, so
        // whether the source table had it marked in use must not matter.
        writer.Write7BitEncodedInt(EndMarker);
        writer.Write(written);
        writer.Flush();
    }

    public static (VolumeSnapshotHeader Header, VolumeNameTable Table)? TryReadFile(string path, out string? error)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
            return TryRead(stream, readEntries: true, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    public static VolumeSnapshotHeader? TryReadHeaderFile(string path, out string? error)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096);
            return TryRead(stream, readEntries: false, out error)?.Header;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    public static (VolumeSnapshotHeader Header, VolumeNameTable Table)? TryRead(
        Stream source,
        bool readEntries,
        out string? error)
    {
        error = null;
        try
        {
            Span<byte> prefix = stackalloc byte[s_magic.Length + sizeof(int)];
            source.ReadExactly(prefix);
            if (!prefix[..s_magic.Length].SequenceEqual(s_magic))
            {
                error = "Not a FileSearch volume snapshot.";
                return null;
            }

            var version = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix[s_magic.Length..]);
            if (version != FormatVersion)
            {
                error = $"Unsupported volume snapshot format {version}.";
                return null;
            }

            using var decompressed = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
            using var buffered = new BufferedStream(decompressed, 1 << 16);
            using var reader = new BinaryReader(buffered, Encoding.UTF8, leaveOpen: true);

            var volumeKey = reader.ReadString();
            var serial = reader.ReadString();
            var volumeRoot = reader.ReadString();
            var journalId = reader.ReadUInt64();
            var checkpoint = reader.ReadInt64();
            var method = (VolumeBuildMethod)reader.ReadInt32();
            var builtUtc = ReadUtc(reader.ReadInt64());
            var savedUtc = ReadUtc(reader.ReadInt64());
            var rootRecord = reader.ReadInt32();
            var rootSequence = reader.ReadUInt16();
            var count = reader.ReadInt32();
            var highWater = reader.ReadInt32();

            if (string.IsNullOrWhiteSpace(volumeKey) ||
                string.IsNullOrWhiteSpace(volumeRoot) ||
                !Enum.IsDefined(method) ||
                rootRecord < 0 ||
                count < 0 ||
                highWater < 0 ||
                highWater > 1 << 30 ||
                count > highWater + 1)
            {
                error = "Volume snapshot header is invalid.";
                return null;
            }

            var header = new VolumeSnapshotHeader(
                volumeKey,
                string.IsNullOrEmpty(serial) ? null : serial,
                volumeRoot,
                journalId,
                checkpoint,
                method,
                builtUtc,
                savedUtc,
                rootRecord,
                rootSequence,
                count);

            var table = new VolumeNameTable(rootRecord, Math.Max(highWater, rootRecord + 1));
            table.Set(
                VolumeNameTable.ComposeReference(rootRecord, rootSequence),
                VolumeNameTable.ComposeReference(rootRecord, rootSequence),
                ".",
                FileAttributes.Directory);
            if (!readEntries)
                return (header, table);

            Span<char> name = stackalloc char[byte.MaxValue];
            var record = 0;
            var entries = 0;
            while (true)
            {
                var delta = reader.Read7BitEncodedInt();
                if (delta == EndMarker)
                    break;

                if (delta < 0)
                {
                    error = "Volume snapshot entry is invalid.";
                    return null;
                }

                record += delta;
                var sequence = reader.ReadUInt16();
                var parent = reader.Read7BitEncodedInt();
                var flags = (VolumeEntryFlags)reader.ReadByte();
                var length = reader.ReadByte();
                if (record >= highWater || parent < 0 || length == 0 || (flags & VolumeEntryFlags.InUse) == 0)
                {
                    error = "Volume snapshot entry is invalid.";
                    return null;
                }

                var nameBytes = MemoryMarshal.AsBytes(name[..length]);
                reader.BaseStream.ReadExactly(nameBytes);
                if (!table.Set(
                        VolumeNameTable.ComposeReference(record, sequence),
                        VolumeNameTable.ComposeReference(parent, 0),
                        name[..length],
                        VolumeNameTable.ToAttributes(flags)))
                {
                    error = $"Volume snapshot entry {record} is invalid.";
                    return null;
                }

                entries++;
            }

            var expected = reader.ReadInt32();
            if (expected != entries)
            {
                error = $"Volume snapshot entry count does not match ({entries:n0} read, {expected:n0} written).";
                return null;
            }

            table.TrimExcess();
            return (header with { EntryCount = table.Count }, table);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or FormatException)
        {
            error = $"Volume snapshot is corrupt: {ex.Message}";
            return null;
        }
    }

    private static DateTime ReadUtc(long ticks) =>
        ticks is >= 0 and <= 3155378975999999999
            ? new DateTime(ticks, DateTimeKind.Utc)
            : DateTime.MinValue;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
