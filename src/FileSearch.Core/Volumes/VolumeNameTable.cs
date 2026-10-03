using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileSearch.Core.Volumes;

[Flags]
internal enum VolumeEntryFlags : byte
{
    None = 0,
    InUse = 1,
    Directory = 2,
    Hidden = 4,
    System = 8,
    ReparsePoint = 16,

    /// <summary>Storage detail: the name is kept as UTF-16 rather than ASCII.</summary>
    WideName = 32,
}

/// <summary>
/// In-memory name table for one NTFS volume (or, in tests, one subtree),
/// keyed by MFT record number. NTFS record numbers are dense, so every
/// column is a plain array indexed by record number instead of a hash map;
/// that keeps the table compact and makes parent lookups O(1). Names live
/// in one shared byte pool: pure-ASCII names (nearly all of them) take one
/// byte per character, anything else is stored as UTF-16. The full 64-bit
/// file reference number is the record number in the low 48 bits plus a
/// sequence number in the high 16 bits; the sequence is kept so a stale
/// delete or rename for a reused record never touches the newer file that
/// now owns it.
/// </summary>
/// <remarks>
/// Not thread-safe. The owning <see cref="VolumeIndexState"/> serializes
/// writers and lets readers share a lock. Readers pass a scratch buffer of
/// <see cref="MaxNameLength"/> characters to <see cref="NameOf"/>.
/// </remarks>
internal sealed class VolumeNameTable
{
    public const int NtfsRootRecord = 5;

    /// <summary>NTFS limits a name component to 255 UTF-16 code units.</summary>
    public const int MaxNameLength = byte.MaxValue;

    private const int MaxPathDepth = 512;
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;
    private const int InitialCapacity = 1024;

    private ushort[] _sequence;
    private int[] _parent;
    private int[] _nameStart;
    private byte[] _nameLength;
    private byte[] _flags;
    private byte[] _names;
    private int _namesLength;
    private int _garbageBytes;

    public VolumeNameTable(int rootRecord = NtfsRootRecord, int capacity = InitialCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rootRecord);

        RootRecord = rootRecord;
        capacity = Math.Max(capacity, rootRecord + 1);
        _sequence = new ushort[capacity];
        _parent = new int[capacity];
        _nameStart = new int[capacity];
        _nameLength = new byte[capacity];
        _flags = new byte[capacity];
        _names = new byte[Math.Max(capacity * 16, 4096)];
    }

    /// <summary>Record number of the directory every path is built from.</summary>
    public int RootRecord { get; }

    /// <summary>Number of live entries (the root included).</summary>
    public int Count { get; private set; }

    /// <summary>One past the highest record number that has ever been stored.</summary>
    public int HighWater { get; private set; }

    public int NameBytesInUse => _namesLength - _garbageBytes;

    public long EstimatedBytes =>
        (long)_sequence.Length * (sizeof(ushort) + sizeof(int) + sizeof(int) + 2) +
        _names.Length;

    /// <summary>
    /// Record number of a file reference, or -1 when it does not fit the
    /// array-indexed table (NTFS record numbers stay far below that limit).
    /// </summary>
    public static int RecordOf(ulong fileReference)
    {
        var record = fileReference & RecordMask;
        return record >= int.MaxValue ? -1 : (int)record;
    }

    public static ushort SequenceOf(ulong fileReference) => (ushort)(fileReference >> 48);

    public static ulong ComposeReference(int record, ushort sequence) =>
        ((ulong)sequence << 48) | (uint)record;

    public bool IsInUse(int record) =>
        (uint)record < (uint)HighWater && (_flags[record] & (byte)VolumeEntryFlags.InUse) != 0;

    public bool Contains(ulong fileReference)
    {
        var record = RecordOf(fileReference);
        return IsInUse(record) && _sequence[record] == SequenceOf(fileReference);
    }

    public int ParentOf(int record) => _parent[record];

    public VolumeEntryFlags FlagsOf(int record) => (VolumeEntryFlags)_flags[record];

    public ushort SequenceAt(int record) => _sequence[record];

    public bool IsDirectory(int record) => (_flags[record] & (byte)VolumeEntryFlags.Directory) != 0;

    public int NameLengthOf(int record) => _nameLength[record];

    /// <summary>
    /// The entry's name. ASCII names are widened into <paramref name="buffer"/>
    /// (at least <see cref="MaxNameLength"/> characters); UTF-16 names are
    /// returned straight from the pool. Valid until the table changes.
    /// </summary>
    public ReadOnlySpan<char> NameOf(int record, Span<char> buffer)
    {
        var length = _nameLength[record];
        var start = _nameStart[record];
        if ((_flags[record] & (byte)VolumeEntryFlags.WideName) != 0)
            return MemoryMarshal.Cast<byte, char>(_names.AsSpan(start, length * sizeof(char)));

        var destination = buffer[..length];
        Ascii.ToUtf16(_names.AsSpan(start, length), destination, out _);
        return destination;
    }

    public string NameString(int record)
    {
        Span<char> buffer = stackalloc char[MaxNameLength];
        return new string(NameOf(record, buffer));
    }

    public ulong ReferenceOf(int record) => ComposeReference(record, _sequence[record]);

    /// <summary>
    /// Inserts or replaces the entry for <paramref name="fileReference"/>.
    /// Returns false when the name cannot be stored (empty or longer than the
    /// NTFS component limit) or the entry would make the root its own child.
    /// </summary>
    public bool Set(ulong fileReference, ulong parentReference, ReadOnlySpan<char> name, FileAttributes attributes)
    {
        var record = RecordOf(fileReference);
        var parent = RecordOf(parentReference);
        if (record < 0 || parent < 0 || name.Length == 0 || name.Length > MaxNameLength)
            return false;

        if (record == RootRecord)
            return SetRoot(fileReference, attributes);

        if (parent == record)
            return false;

        EnsureCapacity(Math.Max(record, parent) + 1);
        var flags = ToFlags(attributes);
        var wasInUse = IsInUse(record);
        if (wasInUse)
        {
            Span<char> buffer = stackalloc char[MaxNameLength];
            if (NameOf(record, buffer).SequenceEqual(name))
            {
                _parent[record] = parent;
                _sequence[record] = SequenceOf(fileReference);
                _flags[record] = (byte)(flags | (FlagsOf(record) & VolumeEntryFlags.WideName));
                return true;
            }

            _garbageBytes += StoredBytes(record);
        }
        else
        {
            Count++;
        }

        var (start, wide) = AppendName(name);
        _nameStart[record] = start;
        _nameLength[record] = (byte)name.Length;
        _parent[record] = parent;
        _sequence[record] = SequenceOf(fileReference);
        _flags[record] = (byte)(wide ? flags | VolumeEntryFlags.WideName : flags);
        HighWater = Math.Max(HighWater, record + 1);
        return true;
    }

    /// <summary>
    /// Updates parent and attributes of an existing entry without touching its
    /// name. Used when a change is visible in the journal but the new name
    /// cannot be resolved (for example, access denied).
    /// </summary>
    public bool UpdatePlacement(ulong fileReference, ulong parentReference, FileAttributes attributes)
    {
        if (!Contains(fileReference))
            return false;

        var record = RecordOf(fileReference);
        if (record == RootRecord)
            return true;

        var parent = RecordOf(parentReference);
        if (parent < 0 || parent == record)
            return false;

        EnsureCapacity(parent + 1);
        _parent[record] = parent;
        _flags[record] = (byte)(ToFlags(attributes) | (FlagsOf(record) & VolumeEntryFlags.WideName));
        return true;
    }

    /// <summary>
    /// Removes the entry when its sequence number still matches. A record
    /// that was already reused by another file is left alone.
    /// </summary>
    public bool Remove(ulong fileReference)
    {
        var record = RecordOf(fileReference);
        if (record == RootRecord || !IsInUse(record) || _sequence[record] != SequenceOf(fileReference))
            return false;

        _garbageBytes += StoredBytes(record);
        _flags[record] = 0;
        _nameLength[record] = 0;
        Count--;
        return true;
    }

    /// <summary>
    /// Writes the full path of <paramref name="record"/> into
    /// <paramref name="builder"/>, starting with <paramref name="rootPath"/>.
    /// Returns false when the parent chain does not reach the root (an
    /// orphan whose ancestor is unknown) or loops.
    /// </summary>
    public bool TryBuildPath(int record, string rootPath, StringBuilder builder) =>
        TryBuildPathFrom(record, RootRecord, rootPath, builder);

    /// <summary>
    /// Builds the path of <paramref name="record"/> relative to
    /// <paramref name="ancestor"/>, prefixed with <paramref name="ancestorPath"/>
    /// exactly as the caller spelled it, so results read like a live walk
    /// rooted at that folder. Returns false when the record is not below it.
    /// </summary>
    public bool TryBuildPathFrom(int record, int ancestor, string ancestorPath, StringBuilder builder)
    {
        builder.Clear();
        if (!IsInUse(record))
            return false;

        if (record == ancestor)
        {
            builder.Append(ancestorPath);
            return true;
        }

        Span<int> chain = stackalloc int[MaxPathDepth];
        var depth = 0;
        var current = record;
        while (current != ancestor)
        {
            if (depth == MaxPathDepth || current == RootRecord || !IsInUse(current))
                return false;

            chain[depth++] = current;
            current = _parent[current];
        }

        builder.Append(ancestorPath);
        if (builder.Length > 0 && builder[^1] != Path.DirectorySeparatorChar)
            builder.Append(Path.DirectorySeparatorChar);

        Span<char> buffer = stackalloc char[MaxNameLength];
        for (var i = depth - 1; i >= 0; i--)
        {
            builder.Append(NameOf(chain[i], buffer));
            if (i > 0)
                builder.Append(Path.DirectorySeparatorChar);
        }

        return true;
    }

    public string? TryGetPath(int record, string rootPath)
    {
        var builder = new StringBuilder(128);
        return TryBuildPath(record, rootPath, builder) ? builder.ToString() : null;
    }

    /// <summary>
    /// Returns the depth of <paramref name="record"/> below
    /// <paramref name="ancestor"/> (1 for a direct child), or -1 when it is
    /// not a descendant.
    /// </summary>
    public int DepthBelow(int record, int ancestor)
    {
        var depth = 0;
        var current = record;
        while (current != ancestor)
        {
            if (current == RootRecord || depth == MaxPathDepth || !IsInUse(current))
                return -1;

            current = _parent[current];
            depth++;
        }

        return depth;
    }

    /// <summary>
    /// Rewrites the name pool without the bytes left behind by renames and
    /// deletes. Cheap enough to call on every snapshot save.
    /// </summary>
    public void CompactIfFragmented(double garbageRatio = 0.25)
    {
        if (_namesLength == 0 || _garbageBytes < _namesLength * garbageRatio)
            return;

        RewriteNames(NameBytesInUse + NameBytesInUse / 8);
    }

    /// <summary>
    /// Drops the growth headroom left by building: columns shrink to the
    /// highest record plus a little room for new files, and the name pool to
    /// the live names. Building doubles arrays as it goes, so a fresh table
    /// can otherwise carry up to twice the memory it needs.
    /// </summary>
    public void TrimExcess()
    {
        RewriteNames(NameBytesInUse + NameBytesInUse / 64);

        var columns = (int)Math.Min(Math.Max((long)HighWater + HighWater / 64, RootRecord + 1), Array.MaxLength);
        if (columns < _sequence.Length)
        {
            Array.Resize(ref _sequence, columns);
            Array.Resize(ref _parent, columns);
            Array.Resize(ref _nameStart, columns);
            Array.Resize(ref _nameLength, columns);
            Array.Resize(ref _flags, columns);
        }
    }

    public static VolumeEntryFlags ToFlags(FileAttributes attributes)
    {
        var flags = VolumeEntryFlags.InUse;
        if ((attributes & FileAttributes.Directory) != 0)
            flags |= VolumeEntryFlags.Directory;
        if ((attributes & FileAttributes.Hidden) != 0)
            flags |= VolumeEntryFlags.Hidden;
        if ((attributes & FileAttributes.System) != 0)
            flags |= VolumeEntryFlags.System;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            flags |= VolumeEntryFlags.ReparsePoint;
        return flags;
    }

    public static FileAttributes ToAttributes(VolumeEntryFlags flags)
    {
        var attributes = (FileAttributes)0;
        if ((flags & VolumeEntryFlags.Directory) != 0)
            attributes |= FileAttributes.Directory;
        if ((flags & VolumeEntryFlags.Hidden) != 0)
            attributes |= FileAttributes.Hidden;
        if ((flags & VolumeEntryFlags.System) != 0)
            attributes |= FileAttributes.System;
        if ((flags & VolumeEntryFlags.ReparsePoint) != 0)
            attributes |= FileAttributes.ReparsePoint;
        return attributes == 0 ? FileAttributes.Normal : attributes;
    }

    private int StoredBytes(int record) =>
        (_flags[record] & (byte)VolumeEntryFlags.WideName) != 0
            ? _nameLength[record] * sizeof(char) + 1
            : _nameLength[record];

    private bool SetRoot(ulong fileReference, FileAttributes attributes)
    {
        EnsureCapacity(RootRecord + 1);
        if (!IsInUse(RootRecord))
            Count++;

        _sequence[RootRecord] = SequenceOf(fileReference);
        _parent[RootRecord] = RootRecord;
        _nameLength[RootRecord] = 0;
        _flags[RootRecord] = (byte)(ToFlags(attributes) | VolumeEntryFlags.Directory);
        HighWater = Math.Max(HighWater, RootRecord + 1);
        return true;
    }

    private (int Start, bool Wide) AppendName(ReadOnlySpan<char> name)
    {
        var wide = !Ascii.IsValid(name);

        // Wide names start on an even offset so the pool can be viewed as chars.
        var padding = wide ? _namesLength & 1 : 0;
        var bytes = wide ? name.Length * sizeof(char) : name.Length;
        EnsureNameCapacity((long)_namesLength + padding + bytes);

        var start = _namesLength + padding;
        if (wide)
            MemoryMarshal.AsBytes(name).CopyTo(_names.AsSpan(start));
        else
            Ascii.FromUtf16(name, _names.AsSpan(start, bytes), out _);

        _namesLength = start + bytes;
        _garbageBytes += padding;
        return (start, wide);
    }

    private void EnsureNameCapacity(long required)
    {
        if (required <= _names.Length)
            return;

        var size = (int)Math.Min(Math.Max((long)_names.Length * 2, required), Array.MaxLength);
        if (size < required)
            throw new InvalidOperationException("Volume name pool is full.");

        Array.Resize(ref _names, size);
    }

    private void RewriteNames(long capacity)
    {
        var names = new byte[Math.Max(capacity, 4096)];
        var length = 0;
        for (var record = 0; record < HighWater; record++)
        {
            if (!IsInUse(record) || record == RootRecord)
                continue;

            var wide = (_flags[record] & (byte)VolumeEntryFlags.WideName) != 0;
            var bytes = wide ? _nameLength[record] * sizeof(char) : _nameLength[record];
            if (wide)
                length += length & 1;

            if (length + bytes > names.Length)
                Array.Resize(ref names, (int)Math.Min(Math.Max((long)names.Length * 2, (long)length + bytes), Array.MaxLength));

            _names.AsSpan(_nameStart[record], bytes).CopyTo(names.AsSpan(length));
            _nameStart[record] = length;
            length += bytes;
        }

        _names = names;
        _namesLength = length;
        _garbageBytes = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _sequence.Length)
            return;

        var size = (int)Math.Min(Math.Max((long)_sequence.Length * 2, required), Array.MaxLength);
        Array.Resize(ref _sequence, size);
        Array.Resize(ref _parent, size);
        Array.Resize(ref _nameStart, size);
        Array.Resize(ref _nameLength, size);
        Array.Resize(ref _flags, size);
    }

    /// <summary>Enumerates live records in record-number order.</summary>
    public IEnumerable<int> EnumerateRecords()
    {
        for (var record = 0; record < HighWater; record++)
        {
            if (IsInUse(record))
                yield return record;
        }
    }
}
