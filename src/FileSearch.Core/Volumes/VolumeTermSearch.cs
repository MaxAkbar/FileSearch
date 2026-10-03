using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Volumes;

/// <summary>
/// A Quick Search style filename query over drive name indexes. The text is
/// split on whitespace; every term must appear in the entry's name or in
/// the name of one of its folders, and at least one term must appear in the
/// entry's own name (so typing a folder name lists that folder, not every
/// file beneath it). Terms that contain a path separator must appear in the
/// full path.
/// </summary>
public sealed record VolumeTermSearchRequest(
    string Text,
    int MaxResults = 100,
    IReadOnlyList<string>? ScopeRoots = null,
    bool IncludeFiles = true,
    bool IncludeFolders = true,
    bool IncludeHiddenFolders = false,
    IReadOnlySet<string>? ExcludeDirectoryNames = null);

public sealed record VolumeTermMatch(string Path, bool IsDirectory, double Score);

public sealed record VolumeTermSearchResult(
    IReadOnlyList<VolumeTermMatch> Matches,
    long TotalMatches,
    IReadOnlyList<string> SearchedVolumes,
    IReadOnlyList<string> UncoveredRoots,
    TimeSpan Elapsed)
{
    public static VolumeTermSearchResult Empty(IReadOnlyList<string> uncovered) =>
        new(Array.Empty<VolumeTermMatch>(), 0, Array.Empty<string>(), uncovered, TimeSpan.Zero);
}

/// <summary>Which folders a scan may descend into, mirroring the live walkers.</summary>
internal sealed class VolumeVisibilityRules
{
    public required bool PruneHiddenAndSystem { get; init; }
    public required bool PruneReparsePoints { get; init; }
    public required string[] ExcludedNames { get; init; }

    public static VolumeVisibilityRules From(IReadOnlySet<string>? excludedNames, bool pruneHidden, bool pruneReparsePoints) =>
        new()
        {
            PruneHiddenAndSystem = pruneHidden,
            PruneReparsePoints = pruneReparsePoints,
            ExcludedNames = (excludedNames ?? WalkerOptions.DefaultExcludeDirectories).ToArray(),
        };

    public bool IsPrunedDirectory(VolumeNameTable table, int record)
    {
        var flags = table.FlagsOf(record);
        if (PruneHiddenAndSystem && (flags & (VolumeEntryFlags.Hidden | VolumeEntryFlags.System)) != 0)
            return true;
        if (PruneReparsePoints && (flags & VolumeEntryFlags.ReparsePoint) != 0)
            return true;
        if (ExcludedNames.Length == 0)
            return false;

        Span<char> buffer = stackalloc char[VolumeNameTable.MaxNameLength];
        return IsExcludedName(table.NameOf(record, buffer));
    }

    public bool IsExcludedName(ReadOnlySpan<char> name)
    {
        foreach (var excluded in ExcludedNames)
        {
            if (name.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Per-search memo answering "may a walker starting at one of the scope
/// roots reach the children of this folder?" Each folder is decided once,
/// so a scan stays O(entries) instead of O(entries x depth). Concurrent
/// writers race benignly: every thread computes the same byte.
/// </summary>
internal sealed class VolumeVisibilityMemo : IDisposable
{
    private const byte Unknown = 0;
    private const byte Visible = 1;
    private const byte Hidden = 2;
    private readonly VolumeNameTable _table;
    private readonly VolumeVisibilityRules _rules;
    private readonly HashSet<int> _scopeRecords;
    private readonly byte[] _memo;

    // Records created after the memo was sized are treated as unreachable;
    // a chunked scan may outlive one read lock while the table grows.
    private readonly int _length;

    public VolumeVisibilityMemo(VolumeNameTable table, VolumeVisibilityRules rules, HashSet<int> scopeRecords)
    {
        _table = table;
        _rules = rules;
        _scopeRecords = scopeRecords;
        _length = Math.Max(table.HighWater, 1);
        _memo = ArrayPool<byte>.Shared.Rent(_length);
        Array.Clear(_memo, 0, _length);
    }

    /// <summary>True when the children of <paramref name="directory"/> are reachable.</summary>
    public bool ChildrenVisible(int directory)
    {
        Span<int> chain = stackalloc int[512];
        var length = 0;
        var current = directory;
        byte result;
        while (true)
        {
            if ((uint)current >= (uint)_length || (uint)current >= (uint)_table.HighWater)
            {
                result = Hidden;
                break;
            }

            var known = Volatile.Read(ref _memo[current]);
            if (known != Unknown)
            {
                result = known;
                break;
            }

            if (_scopeRecords.Contains(current))
            {
                result = Visible;
                chain[length++] = current;
                break;
            }

            if (current == _table.RootRecord ||
                !_table.IsInUse(current) ||
                length == chain.Length ||
                _rules.IsPrunedDirectory(_table, current))
            {
                result = Hidden;
                if (length < chain.Length)
                    chain[length++] = current;
                break;
            }

            chain[length++] = current;
            current = _table.ParentOf(current);
        }

        for (var i = 0; i < length; i++)
            Volatile.Write(ref _memo[chain[i]], result);

        return result == Visible;
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_memo);
}

internal static class VolumeTermSearch
{
    private const int PartitionSize = 1 << 16;

    internal sealed record CompiledTerms(string[] NameTerms, string[] PathTerms)
    {
        public int AllNameBits => NameTerms.Length >= 31 ? int.MaxValue : (1 << NameTerms.Length) - 1;
    }

    public static CompiledTerms? Compile(string text)
    {
        var raw = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nameTerms = new List<string>();
        var pathTerms = new List<string>();
        foreach (var term in raw.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = term.Replace('/', '\\');
            if (normalized.Contains('\\'))
            {
                pathTerms.Add(normalized);
                var lastSegment = normalized.TrimEnd('\\');
                lastSegment = lastSegment[(lastSegment.LastIndexOf('\\') + 1)..];
                if (lastSegment.Length > 0)
                    nameTerms.Add(lastSegment);
            }
            else
            {
                nameTerms.Add(normalized);
            }
        }

        if (nameTerms.Count == 0)
            return null;

        // Bit masks cap the number of name terms; extra terms are ignored
        // rather than failing a query nobody would type.
        return new CompiledTerms(
            nameTerms.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray(),
            pathTerms.ToArray());
    }

    /// <summary>
    /// Ranks matches in one table. Caller holds the table's read lock for
    /// the whole call (paths are built before it returns).
    /// </summary>
    public static (List<VolumeTermMatch> Matches, long Total) Search(
        VolumeNameTable table,
        string rootPath,
        CompiledTerms terms,
        VolumeTermSearchRequest request,
        HashSet<int> scopeRecords,
        CancellationToken cancellationToken)
    {
        var rules = VolumeVisibilityRules.From(
            request.ExcludeDirectoryNames,
            pruneHidden: !request.IncludeHiddenFolders,
            pruneReparsePoints: true);
        using var visibility = new VolumeVisibilityMemo(table, rules, scopeRecords);
        var ancestorMasks = terms.NameTerms.Length > 1
            ? new AncestorMaskMemo(table, terms.NameTerms)
            : null;
        var maxResults = Math.Max(1, request.MaxResults);

        // Name scores decide the candidates; depth then reorders them, which
        // keeps a shallow user file ahead of an equally named one buried in
        // a package cache without walking the parent chain of every match.
        var candidateCount = Math.Min(maxResults * RerankFactor, MaxRerankCandidates);
        var heaps = new ConcurrentBag<PriorityQueue<int, double>>();
        long total = 0;

        try
        {
            Parallel.ForEach(
                Partitioner.Create(0, table.HighWater, PartitionSize),
                new ParallelOptions { CancellationToken = cancellationToken },
                () => (Heap: new PriorityQueue<int, double>(candidateCount + 1), Count: 0L, Path: new StringBuilder(260)),
                (range, loopState, local) =>
                {
                    Span<char> nameBuffer = stackalloc char[VolumeNameTable.MaxNameLength];
                    for (var record = range.Item1; record < range.Item2; record++)
                    {
                        if (!table.IsInUse(record) || record == table.RootRecord)
                            continue;

                        var isDirectory = table.IsDirectory(record);
                        if (isDirectory ? !request.IncludeFolders : !request.IncludeFiles)
                            continue;

                        var name = table.NameOf(record, nameBuffer);
                        var nameMask = NameMask(name, terms.NameTerms);
                        if (nameMask == 0)
                            continue;

                        if (nameMask != terms.AllNameBits &&
                            (ancestorMasks is null ||
                             (nameMask | ancestorMasks.MaskOf(table.ParentOf(record))) != terms.AllNameBits))
                        {
                            continue;
                        }

                        if ((isDirectory && rules.IsPrunedDirectory(table, record)) ||
                            !visibility.ChildrenVisible(table.ParentOf(record)))
                        {
                            continue;
                        }

                        if (terms.PathTerms.Length > 0 &&
                            !MatchesPathTerms(table, record, rootPath, terms.PathTerms, local.Path))
                        {
                            continue;
                        }

                        local.Count++;
                        var score = Score(name, nameMask, terms.NameTerms);
                        if (local.Heap.Count < candidateCount)
                        {
                            local.Heap.Enqueue(record, score);
                        }
                        else if (local.Heap.TryPeek(out _, out var lowest) && score > lowest)
                        {
                            local.Heap.DequeueEnqueue(record, score);
                        }
                    }

                    return local;
                },
                local =>
                {
                    heaps.Add(local.Heap);
                    Interlocked.Add(ref total, local.Count);
                });
        }
        finally
        {
            ancestorMasks?.Dispose();
        }

        var ranked = new List<(int Record, double Score)>();
        foreach (var heap in heaps)
        {
            while (heap.TryDequeue(out var record, out var score))
                ranked.Add((record, score));
        }

        var builder = new StringBuilder(260);
        var matches = new List<VolumeTermMatch>(Math.Min(ranked.Count, maxResults));
        foreach (var (record, score) in ranked
                     .Select(item => (item.Record, Score: item.Score - DepthPenalty * Math.Max(table.DepthBelow(item.Record, table.RootRecord), 0)))
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.Record)
                     .Take(maxResults))
        {
            if (table.TryBuildPath(record, rootPath, builder))
                matches.Add(new VolumeTermMatch(builder.ToString(), table.IsDirectory(record), score));
        }

        return (matches, total);
    }

    private const int RerankFactor = 4;
    private const int MaxRerankCandidates = 4_000;
    private const double DepthPenalty = 8;

    internal static int NameMask(ReadOnlySpan<char> name, string[] terms)
    {
        var mask = 0;
        for (var i = 0; i < terms.Length; i++)
        {
            if (name.Contains(terms[i], StringComparison.OrdinalIgnoreCase))
                mask |= 1 << i;
        }

        return mask;
    }

    /// <summary>
    /// Higher is better: more terms in the entry's own name, exact and
    /// prefix matches, then shorter names.
    /// </summary>
    internal static double Score(ReadOnlySpan<char> name, int nameMask, string[] terms)
    {
        var matched = System.Numerics.BitOperations.PopCount((uint)nameMask);
        var score = 1000.0 * matched / terms.Length;
        var stem = name;
        var dot = name.LastIndexOf('.');
        if (dot > 0)
            stem = name[..dot];

        foreach (var term in terms)
        {
            if (name.Equals(term, StringComparison.OrdinalIgnoreCase) ||
                stem.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 400;
                break;
            }
        }

        if (name.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase))
            score += 150;

        return score - Math.Min(name.Length, 255);
    }

    private static bool MatchesPathTerms(
        VolumeNameTable table,
        int record,
        string rootPath,
        string[] pathTerms,
        StringBuilder builder)
    {
        if (!table.TryBuildPath(record, rootPath, builder))
            return false;

        var path = builder.ToString();
        foreach (var term in pathTerms)
        {
            if (!path.Contains(term, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Union of term bits over every folder from the volume root down to a
    /// directory, memoized per search.
    /// </summary>
    private sealed class AncestorMaskMemo : IDisposable
    {
        private const int Unknown = -1;
        private readonly VolumeNameTable _table;
        private readonly string[] _terms;
        private readonly int[] _memo;

        public AncestorMaskMemo(VolumeNameTable table, string[] terms)
        {
            _table = table;
            _terms = terms;
            _memo = ArrayPool<int>.Shared.Rent(Math.Max(table.HighWater, 1));
            Array.Fill(_memo, Unknown, 0, Math.Max(table.HighWater, 1));
        }

        public int MaskOf(int directory)
        {
            Span<int> chain = stackalloc int[512];
            var length = 0;
            var current = directory;
            var inherited = 0;
            while ((uint)current < (uint)_table.HighWater && current != _table.RootRecord && _table.IsInUse(current))
            {
                var known = Volatile.Read(ref _memo[current]);
                if (known != Unknown)
                {
                    inherited = known;
                    break;
                }

                if (length == chain.Length)
                    break;

                chain[length++] = current;
                current = _table.ParentOf(current);
            }

            Span<char> buffer = stackalloc char[VolumeNameTable.MaxNameLength];
            for (var i = length - 1; i >= 0; i--)
            {
                inherited |= NameMask(_table.NameOf(chain[i], buffer), _terms);
                Volatile.Write(ref _memo[chain[i]], inherited);
            }

            return inherited;
        }

        public void Dispose() => ArrayPool<int>.Shared.Return(_memo);
    }
}
