using System.Buffers.Binary;
using System.Text;
using FileSearch.Core.Indexing;
using FileSearch.Core.Volumes;

namespace FileSearch.Core.Tests;

public sealed class VolumeNameTableTests
{
    private const string Root = @"C:\";

    private static ulong Ref(int record, ushort sequence = 1) => VolumeNameTable.ComposeReference(record, sequence);

    internal static VolumeNameTable CreateSampleTable()
    {
        var table = new VolumeNameTable();
        table.Set(Ref(5, 5), Ref(5, 5), ".", FileAttributes.Directory);
        table.Set(Ref(20), Ref(5, 5), "Users", FileAttributes.Directory);
        table.Set(Ref(21), Ref(20), "maxim", FileAttributes.Directory);
        table.Set(Ref(22), Ref(21), "projects", FileAttributes.Directory);
        table.Set(Ref(23), Ref(22), "report.docx", FileAttributes.Archive);
        table.Set(Ref(24), Ref(22), "Report Final.pdf", FileAttributes.Archive);
        table.Set(Ref(25), Ref(21), "AppData", FileAttributes.Directory | FileAttributes.Hidden);
        table.Set(Ref(26), Ref(25), "report-cache.tmp", FileAttributes.Archive);
        table.Set(Ref(27), Ref(22), "node_modules", FileAttributes.Directory);
        table.Set(Ref(28), Ref(27), "report.js", FileAttributes.Archive);
        table.Set(Ref(29), Ref(21), "notes", FileAttributes.Directory);
        table.Set(Ref(30), Ref(29), "projects-plan.txt", FileAttributes.Archive);
        table.Set(Ref(31), Ref(21), ".secret", FileAttributes.Hidden);
        return table;
    }

    [Fact]
    public void BuildsPathsFromTheParentChain()
    {
        var table = CreateSampleTable();

        Assert.Equal(@"C:\Users\maxim\projects\report.docx", table.TryGetPath(23, Root));
        Assert.Equal(@"C:\", table.TryGetPath(5, Root));
        Assert.Equal(13, table.Count);
        Assert.True(table.IsDirectory(22));
        Assert.Equal(3, table.DepthBelow(23, 20));
        Assert.Equal(-1, table.DepthBelow(23, 25));
    }

    [Fact]
    public void BuildsPathsRelativeToAnAncestorWithTheCallersSpelling()
    {
        var table = CreateSampleTable();
        var builder = new StringBuilder();

        Assert.True(table.TryBuildPathFrom(23, 21, @"c:\users\MAXIM", builder));
        Assert.Equal(@"c:\users\MAXIM\projects\report.docx", builder.ToString());
        Assert.False(table.TryBuildPathFrom(26, 22, @"C:\x", builder));
    }

    [Fact]
    public void RemoveIgnoresAReferenceWhoseSequenceWasReused()
    {
        var table = CreateSampleTable();

        Assert.False(table.Remove(Ref(23, sequence: 9)));
        Assert.True(table.Contains(Ref(23)));
        Assert.True(table.Remove(Ref(23)));
        Assert.False(table.Contains(Ref(23)));
        Assert.Null(table.TryGetPath(23, Root));
        Assert.False(table.Remove(Ref(5, 5)));
    }

    [Fact]
    public void RenamesAndMovesUpdateDescendantPathsAndCompactionKeepsNames()
    {
        var table = CreateSampleTable();

        table.Set(Ref(22), Ref(29), "archive", FileAttributes.Directory);
        Assert.Equal(@"C:\Users\maxim\notes\archive\report.docx", table.TryGetPath(23, Root));

        for (var i = 0; i < 50; i++)
            table.Set(Ref(24), Ref(22), $"renamed-{i}.pdf", FileAttributes.Archive);

        table.CompactIfFragmented(garbageRatio: 0.01);
        Assert.Equal(@"C:\Users\maxim\notes\archive\renamed-49.pdf", table.TryGetPath(24, Root));
        Assert.Equal(@"C:\Users\maxim\notes\projects-plan.txt", table.TryGetPath(30, Root));
    }

    [Fact]
    public void StoresAsciiNamesCompactlyAndKeepsUnicodeNamesThroughCompaction()
    {
        var table = CreateSampleTable();
        var asciiBytes = table.NameBytesInUse;
        table.Set(Ref(60), Ref(21), "Résumé.docx", FileAttributes.Archive);
        table.Set(Ref(61), Ref(21), "x", FileAttributes.Archive);
        table.Set(Ref(62), Ref(21), "日本語フォルダ", FileAttributes.Directory);
        table.Set(Ref(63), Ref(62), "a.txt", FileAttributes.Archive);

        Assert.Equal(VolumeEntryFlags.WideName, table.FlagsOf(60) & VolumeEntryFlags.WideName);
        Assert.Equal(VolumeEntryFlags.None, table.FlagsOf(61) & VolumeEntryFlags.WideName);
        Assert.True(table.NameBytesInUse >= asciiBytes + 1 + ("Résumé.docx".Length + "日本語フォルダ".Length) * 2);

        table.Set(Ref(61), Ref(21), "renamed", FileAttributes.Archive);
        table.Remove(Ref(23));
        table.CompactIfFragmented(garbageRatio: 0);
        table.TrimExcess();

        Assert.Equal(@"C:\Users\maxim\Résumé.docx", table.TryGetPath(60, Root));
        Assert.Equal(@"C:\Users\maxim\日本語フォルダ\a.txt", table.TryGetPath(63, Root));
        Assert.Equal("renamed", table.NameString(61));
        Assert.Equal(@"C:\Users\maxim\projects\Report Final.pdf", table.TryGetPath(24, Root));
    }

    [Fact]
    public void OrphansWithAnUnknownParentHaveNoPath()
    {
        var table = CreateSampleTable();
        table.Set(Ref(40), Ref(99), "orphan.txt", FileAttributes.Archive);

        Assert.True(table.Contains(Ref(40)));
        Assert.Null(table.TryGetPath(40, Root));
    }

    [Fact]
    public void RejectsNamesTheTableCannotStore()
    {
        var table = CreateSampleTable();

        Assert.False(table.Set(Ref(41), Ref(21), string.Empty, FileAttributes.Archive));
        Assert.False(table.Set(Ref(42), Ref(21), new string('x', 256), FileAttributes.Archive));
        Assert.False(table.Set(Ref(43), Ref(43), "self-parent", FileAttributes.Archive));
    }

    [Fact]
    public void SnapshotRoundTripPreservesEntriesHeaderAndUnpairedSurrogates()
    {
        var table = CreateSampleTable();
        var oddName = "bad\uD800name.txt";
        table.Set(Ref(50, 3), Ref(21), oddName, FileAttributes.Archive | FileAttributes.System);
        var header = new VolumeSnapshotHeader(
            @"\\?\VOLUME{1234}",
            "42",
            Root,
            JournalId: 0xABCDEF,
            CheckpointUsn: 123456,
            VolumeBuildMethod.MasterFileTable,
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc),
            table.RootRecord,
            5,
            table.Count);

        using var stream = new MemoryStream();
        VolumeSnapshotSerializer.Write(stream, header, table);
        stream.Position = 0;
        var read = VolumeSnapshotSerializer.TryRead(stream, readEntries: true, out var error);

        Assert.Null(error);
        Assert.NotNull(read);
        var (readHeader, readTable) = read.Value;
        Assert.Equal(header.VolumeKey, readHeader.VolumeKey);
        Assert.Equal(header.JournalId, readHeader.JournalId);
        Assert.Equal(header.CheckpointUsn, readHeader.CheckpointUsn);
        Assert.Equal(VolumeBuildMethod.MasterFileTable, readHeader.BuildMethod);
        Assert.Equal(header.BuiltUtc, readHeader.BuiltUtc);
        Assert.Equal(table.Count, readTable.Count);
        Assert.Equal(@"C:\Users\" + "maxim" + @"\" + oddName, readTable.TryGetPath(50, Root));
        Assert.Equal((ushort)3, readTable.SequenceAt(50));
        Assert.Equal(table.FlagsOf(25), readTable.FlagsOf(25));
        Assert.Equal(table.TryGetPath(28, Root), readTable.TryGetPath(28, Root));
    }

    [Fact]
    public void SnapshotRoundTripsATableWhoseRootWasNeverReported()
    {
        // Master file table enumeration never reports the root directory's
        // own record; a snapshot of such a table must still load.
        var table = new VolumeNameTable();
        table.Set(Ref(20), Ref(5, 5), "Users", FileAttributes.Directory);
        table.Set(Ref(21), Ref(20), "file.txt", FileAttributes.Archive);
        Assert.False(table.IsInUse(table.RootRecord));
        var header = new VolumeSnapshotHeader("KEY", null, Root, 1, 1, VolumeBuildMethod.MasterFileTable,
            DateTime.UtcNow, DateTime.UtcNow, table.RootRecord, 5, table.Count);

        using var stream = new MemoryStream();
        VolumeSnapshotSerializer.Write(stream, header, table);
        stream.Position = 0;
        var read = VolumeSnapshotSerializer.TryRead(stream, readEntries: true, out var error);

        Assert.Null(error);
        Assert.NotNull(read);
        Assert.True(read.Value.Table.IsInUse(read.Value.Table.RootRecord));
        Assert.Equal(@"C:\Users\file.txt", read.Value.Table.TryGetPath(21, Root));
        Assert.Equal(3, read.Value.Header.EntryCount);

        MasterFileTableScanner.EnsureRoot(table, @"\\?\Volume{00000000-0000-0000-0000-000000000000}");
        Assert.True(table.IsInUse(table.RootRecord));
        Assert.Equal((ushort)5, table.SequenceAt(table.RootRecord));
    }

    [Fact]
    public void CorruptOrForeignSnapshotsAreRejectedWithoutThrowing()
    {
        var table = CreateSampleTable();
        var header = new VolumeSnapshotHeader("KEY", null, Root, 1, 1, VolumeBuildMethod.DirectoryWalk,
            DateTime.UtcNow, DateTime.UtcNow, table.RootRecord, 5, table.Count);
        using var stream = new MemoryStream();
        VolumeSnapshotSerializer.Write(stream, header, table);
        var bytes = stream.ToArray();

        using var truncated = new MemoryStream(bytes, 0, bytes.Length / 2);
        Assert.Null(VolumeSnapshotSerializer.TryRead(truncated, readEntries: true, out var truncatedError));
        Assert.NotNull(truncatedError);

        var foreign = (byte[])bytes.Clone();
        foreign[0] = (byte)'X';
        using var foreignStream = new MemoryStream(foreign);
        Assert.Null(VolumeSnapshotSerializer.TryRead(foreignStream, readEntries: true, out var foreignError));
        Assert.Contains("Not a FileSearch", foreignError);

        using var headerOnly = new MemoryStream(bytes);
        var headerRead = VolumeSnapshotSerializer.TryRead(headerOnly, readEntries: false, out _);
        Assert.Equal(table.Count, headerRead?.Header.EntryCount);
    }

    [Fact]
    public void MasterFileTableRecordsParseIntoTheTable()
    {
        var buffer = new List<byte>(new byte[8]);
        AppendUsnRecordV2(buffer, Ref(5, 5), Ref(5, 5), FileAttributes.Directory, ".");
        AppendUsnRecordV2(buffer, Ref(3, 3), Ref(5, 5), FileAttributes.Hidden | FileAttributes.System, "$Volume");
        AppendUsnRecordV2(buffer, Ref(64), Ref(5, 5), FileAttributes.Directory, "Windows");
        AppendUsnRecordV2(buffer, Ref(65), Ref(64), FileAttributes.Archive, "notepad.exe");
        var table = new VolumeNameTable();

        var parsed = MasterFileTableScanner.ParseRecords(buffer.ToArray(), table);

        Assert.Equal(3, parsed);
        Assert.Equal(@"C:\Windows\notepad.exe", table.TryGetPath(65, Root));
        Assert.False(table.IsInUse(3));
    }

    [Fact]
    public void DirectoryEntriesParseFileIdsAndSkipDotEntries()
    {
        var buffer = new List<byte>();
        AppendDirectoryEntry(buffer, ".", 1, FileAttributes.Directory, last: false);
        AppendDirectoryEntry(buffer, "..", 2, FileAttributes.Directory, last: false);
        AppendDirectoryEntry(buffer, "child", Ref(77, 4), FileAttributes.Directory, last: false);
        AppendDirectoryEntry(buffer, "file.txt", Ref(78, 2), FileAttributes.Archive, last: true);
        var batch = new List<(ulong Reference, FileAttributes Attributes, string Name)>();

        DirectoryWalkScanner.ParseEntries(buffer.ToArray(), batch);

        Assert.Equal(2, batch.Count);
        Assert.Equal((Ref(77, 4), FileAttributes.Directory, "child"), batch[0]);
        Assert.Equal((Ref(78, 2), FileAttributes.Archive, "file.txt"), batch[1]);
    }

    [Fact]
    public void ParsesJournalReferenceStrings()
    {
        Assert.True(VolumeChangeApplier.TryParseReference("1407374883553285", out var decimalReference));
        Assert.Equal(1407374883553285UL, decimalReference);

        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, Ref(123, 7));
        Assert.True(VolumeChangeApplier.TryParseReference(Convert.ToHexString(bytes), out var hexReference));
        Assert.Equal(Ref(123, 7), hexReference);
        Assert.False(VolumeChangeApplier.TryParseReference("not-a-number", out _));
    }

    private static void AppendUsnRecordV2(List<byte> buffer, ulong reference, ulong parent, FileAttributes attributes, string name)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (60 + nameBytes.Length + 7) & ~7;
        var record = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(record, length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), reference);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), parent);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), (uint)attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
        nameBytes.CopyTo(record, 60);
        buffer.AddRange(record);
    }

    private static void AppendDirectoryEntry(List<byte> buffer, string name, ulong fileId, FileAttributes attributes, bool last)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (104 + nameBytes.Length + 7) & ~7;
        var entry = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(entry, last ? 0 : length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(56), (uint)attributes);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(60), nameBytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(96), fileId);
        nameBytes.CopyTo(entry, 104);
        buffer.AddRange(entry);
    }
}

public sealed class VolumeChangeApplierTests
{
    private static ulong Ref(int record, ushort sequence = 1) => VolumeNameTable.ComposeReference(record, sequence);

    private static UsnChangeRecord Record(ulong reference, ulong parent, uint reason, FileAttributes attributes = FileAttributes.Archive) =>
        new(reference.ToString(System.Globalization.CultureInfo.InvariantCulture),
            parent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Usn: 0,
            DateTime.UtcNow,
            reason,
            attributes,
            Name: string.Empty);

    private static int Apply(VolumeNameTable table, IEnumerable<UsnChangeRecord> records, FakeResolver resolver)
    {
        var changes = VolumeChangeApplier.FilterRelevant(table, VolumeChangeApplier.Coalesce(records));
        VolumeChangeApplier.ResolveNames(changes, resolver);
        return VolumeChangeApplier.Apply(table, changes);
    }

    [Fact]
    public void AppliesCreatesInJournalOrderSoNewFoldersPrecedeTheirFiles()
    {
        var table = VolumeNameTableTests.CreateSampleTable();
        var resolver = new FakeResolver
        {
            [Ref(60)] = new(VolumeFileIdLookupStatus.Found, "new-folder", FileAttributes.Directory),
            [Ref(61)] = new(VolumeFileIdLookupStatus.Found, "inside.txt", FileAttributes.Archive),
        };

        var applied = Apply(table, new[]
        {
            Record(Ref(60), Ref(22), VolumeChangeApplier.ReasonFileCreate, FileAttributes.Directory),
            Record(Ref(61), Ref(60), VolumeChangeApplier.ReasonFileCreate),
            Record(Ref(61), Ref(60), 0x00000002),
        }, resolver);

        Assert.Equal(2, applied);
        Assert.Equal(@"C:\Users\maxim\projects\new-folder\inside.txt", table.TryGetPath(61, @"C:\"));
    }

    [Fact]
    public void RenamesMovesAndDeletesFollowTheFinalState()
    {
        var table = VolumeNameTableTests.CreateSampleTable();
        var resolver = new FakeResolver
        {
            [Ref(23)] = new(VolumeFileIdLookupStatus.Found, "report-v2.docx", FileAttributes.Archive),
        };

        Apply(table, new[]
        {
            Record(Ref(23), Ref(22), VolumeChangeApplier.ReasonRenameOldName),
            Record(Ref(23), Ref(29), VolumeChangeApplier.ReasonRenameNewName),
            Record(Ref(30), Ref(29), VolumeChangeApplier.ReasonFileDelete | 0x80000000),
            Record(Ref(70), Ref(22), VolumeChangeApplier.ReasonFileCreate),
            Record(Ref(70), Ref(22), VolumeChangeApplier.ReasonFileDelete),
        }, resolver);

        Assert.Equal(@"C:\Users\maxim\notes\report-v2.docx", table.TryGetPath(23, @"C:\"));
        Assert.False(table.Contains(Ref(30)));
        Assert.False(table.IsInUse(70));
        Assert.DoesNotContain(Ref(70), resolver.Requested);
    }

    [Fact]
    public void DropsChangesUnderFoldersTheTableDoesNotKnow()
    {
        var table = VolumeNameTableTests.CreateSampleTable();
        var resolver = new FakeResolver();

        var applied = Apply(table, new[]
        {
            Record(Ref(80), Ref(999), VolumeChangeApplier.ReasonFileCreate),
            Record(Ref(81), Ref(998), VolumeChangeApplier.ReasonFileDelete),
        }, resolver);

        Assert.Equal(0, applied);
        Assert.Empty(resolver.Requested);
    }

    [Fact]
    public void UnresolvableNamesKeepTheOldNameButFollowTheMove()
    {
        var table = VolumeNameTableTests.CreateSampleTable();
        var resolver = new FakeResolver
        {
            [Ref(24)] = new(VolumeFileIdLookupStatus.AccessDenied, null, 0),
            [Ref(23)] = new(VolumeFileIdLookupStatus.Gone, null, 0),
        };

        Apply(table, new[]
        {
            Record(Ref(24), Ref(29), VolumeChangeApplier.ReasonRenameNewName),
            Record(Ref(23), Ref(22), VolumeChangeApplier.ReasonBasicInfoChange),
        }, resolver);

        Assert.Equal(@"C:\Users\maxim\notes\Report Final.pdf", table.TryGetPath(24, @"C:\"));
        Assert.False(table.Contains(Ref(23)));
    }

    [Fact]
    public void IgnoresDataOnlyChanges()
    {
        var changes = VolumeChangeApplier.Coalesce(new[]
        {
            Record(Ref(23), Ref(22), 0x00000001 | 0x00000002 | 0x80000000),
        });

        Assert.Empty(changes);
    }

    private sealed class FakeResolver : Dictionary<ulong, VolumeFileIdLookup>, IVolumeFileIdResolver
    {
        public List<ulong> Requested { get; } = new();

        public VolumeFileIdLookup Resolve(ulong fileReference)
        {
            Requested.Add(fileReference);
            return TryGetValue(fileReference, out var lookup)
                ? lookup
                : new VolumeFileIdLookup(VolumeFileIdLookupStatus.Gone, null, 0);
        }

        public void Dispose()
        {
        }
    }
}

public sealed class VolumeTermSearchTests
{
    private static List<VolumeTermMatch> Search(string text, VolumeTermSearchRequest? request = null, int[]? scope = null)
    {
        var table = VolumeNameTableTests.CreateSampleTable();
        var compiled = VolumeTermSearch.Compile(text);
        Assert.NotNull(compiled);
        var (matches, _) = VolumeTermSearch.Search(
            table,
            @"C:\",
            compiled,
            request ?? new VolumeTermSearchRequest(text),
            new HashSet<int>(scope ?? new[] { table.RootRecord }),
            CancellationToken.None);
        return matches;
    }

    [Fact]
    public void EveryTermMustMatchTheNameOrAFolderAndOneTheOwnName()
    {
        var paths = Search("projects report").Select(match => match.Path).ToList();

        Assert.Contains(@"C:\Users\maxim\projects\report.docx", paths);
        Assert.Contains(@"C:\Users\maxim\projects\Report Final.pdf", paths);
        Assert.DoesNotContain(@"C:\Users\maxim\notes\projects-plan.txt", paths);
        Assert.DoesNotContain(@"C:\Users\maxim\projects", paths);
    }

    [Fact]
    public void SingleFolderTermListsTheFolderNotEverythingBeneathIt()
    {
        var paths = Search("projects").Select(match => match.Path).ToList();

        Assert.Equal(
            new[] { @"C:\Users\maxim\projects", @"C:\Users\maxim\notes\projects-plan.txt" },
            paths);
    }

    [Fact]
    public void PrunesHiddenFoldersAndExcludedNamesButKeepsHiddenFiles()
    {
        var paths = Search("report").Select(match => match.Path).ToList();
        Assert.DoesNotContain(@"C:\Users\maxim\AppData\report-cache.tmp", paths);
        Assert.DoesNotContain(@"C:\Users\maxim\projects\node_modules\report.js", paths);
        Assert.Contains(@"C:\Users\maxim\.secret", Search("secret").Select(match => match.Path));

        var withHidden = Search("report", request: new VolumeTermSearchRequest(
            "report",
            IncludeHiddenFolders: true,
            ExcludeDirectoryNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        Assert.Contains(@"C:\Users\maxim\AppData\report-cache.tmp", withHidden.Select(match => match.Path));
        Assert.Contains(@"C:\Users\maxim\projects\node_modules\report.js", withHidden.Select(match => match.Path));
    }

    [Fact]
    public void RanksExactAndShorterNamesFirst()
    {
        var matches = Search("report");

        Assert.Equal(@"C:\Users\maxim\projects\report.docx", matches[0].Path);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public void PathTermsMustAppearInTheFullPath()
    {
        var paths = Search(@"maxim\projects\rep").Select(match => match.Path).ToList();

        Assert.Equal(2, paths.Count);
        Assert.All(paths, path => Assert.Contains(@"maxim\projects\", path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScopesRestrictResultsToTheirSubtrees()
    {
        var paths = Search("report", scope: new[] { 29 }).Select(match => match.Path).ToList();
        Assert.Empty(paths);

        var notes = Search("plan", scope: new[] { 29 }).Select(match => match.Path).ToList();
        Assert.Equal(new[] { @"C:\Users\maxim\notes\projects-plan.txt" }, notes);
    }

    [Fact]
    public void FileAndFolderSwitchesFilterResults()
    {
        var foldersOnly = Search("projects", request: new VolumeTermSearchRequest("projects", IncludeFiles: false));
        Assert.All(foldersOnly, match => Assert.True(match.IsDirectory));

        var limited = Search("e", request: new VolumeTermSearchRequest("e", MaxResults: 2));
        Assert.Equal(2, limited.Count);
    }
}
