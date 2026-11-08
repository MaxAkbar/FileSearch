using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using FileSearch.Core.Engine;
using FileSearch.Core.Queries;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.ViewModels;

public sealed partial class QuickSearchViewModel
{
    // The two metadata sources each contribute at most 80 names. Live discovery
    // can also show one provisional match before its bounded ranked scan finishes.
    private const int MaxCandidateFiles = MaxResultFiles * 3 + 1;
    private readonly HashSet<SearchHitIdentity> _contentHitsSeen = [];
    private readonly HashSet<string> _contentFilesSeen = new(StringComparer.OrdinalIgnoreCase);
    private QuickSearchNameRanking? _nameRanking;
    private bool _contentLimitReached;
    private bool _updatingRankedResults;
    private bool _selectingAutomatically;
    private bool _selectionBindingNeedsRestore;

    private int CompareCandidates(FileResultViewModel left, FileResultViewModel right)
    {
        int order = CandidateScore(right).CompareTo(CandidateScore(left));
        if (order != 0) return order;
        // Backend scores use different scales; they never override lexical name
        // relevance. They only order content-only results with no name relevance.
        if (CandidateScore(left) == 0)
        {
            order = FiniteScore(right.BestScore).CompareTo(FiniteScore(left.BestScore));
            if (order != 0) return order;
        }
        order = right.IsPinned.CompareTo(left.IsPinned);
        if (order != 0) return order;
        return StringComparer.OrdinalIgnoreCase.Compare(left.ResultKey, right.ResultKey);
    }

    private double CandidateScore(FileResultViewModel file) =>
        file.IsStoreMessage ? 0 : _nameRanking?.Score(file.FullPath, file.IsDirectory) ?? 0;

    private static double FiniteScore(double score) => double.IsFinite(score) ? score : 0;

    private bool AdmitCandidate(FileResultViewModel candidate)
    {
        if (_filesByPath.Count < MaxCandidateFiles) return true;
        var worst = _filesByPath.Values
            .Where(file => !_hasExplicitSelection || !ReferenceEquals(file, SelectedResult))
            .MaxBy(file => file, Comparer<FileResultViewModel>.Create(CompareCandidates));
        if (worst is null || CompareCandidates(candidate, worst) >= 0) return false;
        _filesByPath.Remove(worst.ResultKey);
        foreach (var hit in worst.Hits) _seenHits.Remove(SearchHitIdentity.Create(hit));
        _hitLimitReached = true;
        return true;
    }

    private void ProjectRankedResults()
    {
        var selected = SelectedResult;
        var ordered = _filesByPath.Values.Order(Comparer<FileResultViewModel>.Create(CompareCandidates)).Take(MaxResultFiles).ToList();
        if (_hasExplicitSelection && selected is not null && _filesByPath.ContainsKey(selected.ResultKey) && !ordered.Contains(selected))
        {
            if (ordered.Count == MaxResultFiles) ordered.RemoveAt(ordered.Count - 1);
            ordered.Add(selected);
        }
        if (_filesByPath.Count > MaxResultFiles) _hitLimitReached = true;

        _updatingRankedResults = true;
        try
        {
            for (int index = Results.Count - 1; index >= 0; index--)
                if (!ordered.Contains(Results[index])) Results.RemoveAt(index);
            for (int index = 0; index < ordered.Count; index++)
            {
                int current = Results.IndexOf(ordered[index]);
                if (current < 0) Results.Insert(index, ordered[index]);
                else if (current != index) Results.Move(current, index);
            }
        }
        finally { _updatingRankedResults = false; }

        if (!_hasExplicitSelection || selected is null)
            SelectAutomatically(ordered.FirstOrDefault());
        if (_selectionBindingNeedsRestore)
        {
            _selectionBindingNeedsRestore = false;
            OnPropertyChanged(nameof(SelectedResult));
        }
    }

    private void SelectAutomatically(FileResultViewModel? file)
    {
        _selectingAutomatically = true;
        try { SelectedResult = file; }
        finally { _selectingAutomatically = false; }
    }

    private sealed record MetadataCandidate(string Path, double Score);

    private static int CompareMetadataCandidates(MetadataCandidate left, MetadataCandidate right)
    {
        int order = right.Score.CompareTo(left.Score);
        return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    private static async Task<bool> EnqueueRankedFilesystemMetadataMatchesAsync(
        IReadOnlyList<string> roots, string text, ChannelWriter<QueuedHit> pendingHits,
        int scanBudgetMilliseconds, CancellationToken token)
    {
        var terms = SplitMetadataTerms(text);
        if (terms.Length == 0) return false;
        var ranking = QuickSearchNameRanking.Compile(text);
        var candidates = new List<MetadataCandidate>(MaxResultFiles + 1);
        var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var comparer = Comparer<MetadataCandidate>.Create(CompareMetadataCandidates);
        long matches = 0;
        bool expired = false;
        var started = Stopwatch.GetTimestamp();
        foreach (string root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (IsMetadataScanBudgetSpent(started, scanBudgetMilliseconds)) { expired = true; break; }
            if (!Directory.Exists(root)) continue;
            foreach (string path in EnumerateFilesSafe(root, token))
            {
                token.ThrowIfCancellationRequested();
                if (IsMetadataScanBudgetSpent(started, scanBudgetMilliseconds)) { expired = true; goto Complete; }
                if (!MetadataMatches(path, terms)) continue;
                if (candidatePaths.Contains(path)) continue;
                var candidate = new MetadataCandidate(path, ranking.Score(path));
                // Keep early feedback while collecting the best names across the
                // existing bounded scan, rather than stopping at the first 80.
                if (matches++ == 0) await pendingHits.WriteAsync(ToQueued(candidate), token).ConfigureAwait(false);
                int position = candidates.BinarySearch(candidate, comparer);
                if (position < 0) position = ~position;
                if (position >= MaxResultFiles) continue;
                candidates.Insert(position, candidate);
                candidatePaths.Add(path);
                if (candidates.Count > MaxResultFiles)
                {
                    candidatePaths.Remove(candidates[MaxResultFiles].Path);
                    candidates.RemoveAt(MaxResultFiles);
                }
            }
        }
    Complete:
        foreach (var candidate in candidates)
            await pendingHits.WriteAsync(ToQueued(candidate), token).ConfigureAwait(false);
        return expired || matches > MaxResultFiles;

        static QueuedHit ToQueued(MetadataCandidate candidate) => new(new Hit(candidate.Path, 0,
            "Filename/path match", [], HitKind.Metadata, Score: candidate.Score), false);
    }
}
