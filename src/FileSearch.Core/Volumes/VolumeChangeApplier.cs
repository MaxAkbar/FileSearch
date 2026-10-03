using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FileSearch.Core.Indexing;

namespace FileSearch.Core.Volumes;

/// <summary>One file reference's net change across a batch of journal records.</summary>
internal sealed class PendingVolumeChange
{
    public PendingVolumeChange(ulong fileReference)
    {
        FileReference = fileReference;
    }

    public ulong FileReference { get; }
    public ulong ParentReference { get; set; }
    public FileAttributes Attributes { get; set; }
    public uint Reasons { get; set; }
    public string? Name { get; set; }
    public bool Deleted => (Reasons & VolumeChangeApplier.ReasonFileDelete) != 0;
    public VolumeFileIdLookup Lookup { get; set; }
}

/// <summary>
/// Streams journal records into one <see cref="PendingVolumeChange"/> per
/// file reference, so memory grows with the number of distinct files
/// touched rather than the number of records read.
/// </summary>
internal sealed class VolumeChangeCoalescer
{
    private readonly Dictionary<ulong, PendingVolumeChange> _byReference = new();

    public List<PendingVolumeChange> Changes { get; } = new();

    public void Add(UsnChangeRecord record)
    {
        if (!VolumeChangeApplier.IsNameRelevant(record.Reason) ||
            !VolumeChangeApplier.TryParseReference(record.FileReferenceNumber, out var reference) ||
            !VolumeChangeApplier.TryParseReference(record.ParentFileReferenceNumber, out var parent))
        {
            return;
        }

        if (!_byReference.TryGetValue(reference, out var change))
        {
            change = new PendingVolumeChange(reference);
            _byReference[reference] = change;
            Changes.Add(change);
        }

        change.ParentReference = parent;
        change.Attributes = record.FileAttributes;
        change.Reasons |= record.Reason;
        if (!string.IsNullOrEmpty(record.Name))
            change.Name = record.Name;
    }
}

/// <summary>
/// Turns USN journal records into name-table edits. The journal is read
/// unprivileged, so records carry references, parents, reasons, and
/// attributes but no names; names are looked up by file ID before the
/// batch is applied under the table's write lock.
/// </summary>
internal static class VolumeChangeApplier
{
    public const uint ReasonFileCreate = 0x00000100;
    public const uint ReasonFileDelete = 0x00000200;
    public const uint ReasonRenameOldName = 0x00001000;
    public const uint ReasonRenameNewName = 0x00002000;
    public const uint ReasonBasicInfoChange = 0x00008000;
    public const uint ReasonHardLinkChange = 0x00010000;
    public const uint ReasonReparsePointChange = 0x00100000;

    private const uint NameRelevantReasons =
        ReasonFileCreate |
        ReasonFileDelete |
        ReasonRenameOldName |
        ReasonRenameNewName |
        ReasonBasicInfoChange |
        ReasonHardLinkChange |
        ReasonReparsePointChange;

    /// <summary>
    /// Collapses records to one entry per file reference, in the order each
    /// reference first appeared so a new folder is applied before the files
    /// created inside it. Data-only changes are dropped: the table holds
    /// names and placement, not sizes.
    /// </summary>
    public static List<PendingVolumeChange> Coalesce(IEnumerable<UsnChangeRecord> records)
    {
        var coalescer = new VolumeChangeCoalescer();
        foreach (var record in records)
            coalescer.Add(record);
        return coalescer.Changes;
    }

    internal static bool IsNameRelevant(uint reason) => (reason & NameRelevantReasons) != 0;

    /// <summary>
    /// Drops changes the table cannot use: anything not already in the
    /// table whose parent is neither in the table nor created earlier in
    /// the same batch. On a full volume this skips folders the user cannot
    /// read; in a subtree table it skips everything outside the subtree.
    /// Caller holds at least a read lock.
    /// </summary>
    public static List<PendingVolumeChange> FilterRelevant(VolumeNameTable table, List<PendingVolumeChange> changes)
    {
        var batchReferences = new HashSet<int>();
        var relevant = new List<PendingVolumeChange>(changes.Count);
        foreach (var change in changes)
        {
            var record = VolumeNameTable.RecordOf(change.FileReference);
            var parent = VolumeNameTable.RecordOf(change.ParentReference);
            var known = table.Contains(change.FileReference);
            var parentKnown = parent == table.RootRecord || table.IsInUse(parent) || batchReferences.Contains(parent);
            if (!known && (change.Deleted || !parentKnown))
                continue;

            relevant.Add(change);
            if (!change.Deleted && record >= 0)
                batchReferences.Add(record);
        }

        return relevant;
    }

    /// <summary>Looks up names for surviving changes. Runs outside any lock.</summary>
    public static void ResolveNames(List<PendingVolumeChange> changes, IVolumeFileIdResolver resolver)
    {
        foreach (var change in changes)
        {
            if (change.Deleted)
                continue;

            change.Lookup = change.Name is { Length: > 0 } name
                ? new VolumeFileIdLookup(VolumeFileIdLookupStatus.Found, name, change.Attributes)
                : resolver.Resolve(change.FileReference);
        }
    }

    /// <summary>Applies resolved changes. Caller holds the write lock.</summary>
    public static int Apply(VolumeNameTable table, List<PendingVolumeChange> changes)
    {
        var applied = 0;
        foreach (var change in changes)
        {
            if (change.Deleted)
            {
                if (table.Remove(change.FileReference))
                    applied++;
                continue;
            }

            var parent = VolumeNameTable.RecordOf(change.ParentReference);
            var parentKnown = parent == table.RootRecord || table.IsInUse(parent);
            switch (change.Lookup.Status)
            {
                case VolumeFileIdLookupStatus.Found when parentKnown:
                    var attributes = change.Lookup.Attributes != 0 ? change.Lookup.Attributes : change.Attributes;
                    if (table.Set(change.FileReference, change.ParentReference, change.Lookup.Name, attributes))
                        applied++;
                    break;
                case VolumeFileIdLookupStatus.Found:
                case VolumeFileIdLookupStatus.Gone:
                    // Moved somewhere the table cannot see, or deleted since
                    // the record was written; either way the old path is stale.
                    if (table.Remove(change.FileReference))
                        applied++;
                    break;
                default:
                    // Name unknowable right now (access denied or a transient
                    // failure). Keep the old name but follow the move.
                    if (parentKnown && table.UpdatePlacement(change.FileReference, change.ParentReference, change.Attributes))
                        applied++;
                    break;
            }
        }

        return applied;
    }

    /// <summary>
    /// Parses the reference strings produced by <see cref="UsnRecordParser"/>:
    /// decimal for USN_RECORD_V2 and 32 hex digits (little-endian bytes) for
    /// V3, whose low 64 bits are the NTFS file reference.
    /// </summary>
    public static bool TryParseReference(string value, out ulong reference)
    {
        reference = 0;
        if (string.IsNullOrEmpty(value))
            return false;

        if (value.Length == 32)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (Convert.FromHexString(value, bytes, out _, out var written) != System.Buffers.OperationStatus.Done || written != 16)
                return false;

            reference = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            return true;
        }

        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out reference);
    }
}
