using System;
using System.Collections.Generic;
using System.Linq;

namespace FileSearch.Core.Engine;

public sealed record RankedSearchResult
{
    public RankedSearchResult(
        int rank,
        string path,
        double score,
        IReadOnlyList<SearchCandidate> candidates,
        IReadOnlyList<SearchResultExplanation>? explanations = null)
    {
        if (rank < 1)
            throw new ArgumentOutOfRangeException(nameof(rank), "Rank is one-based.");

        Rank = rank;
        Path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required.", nameof(path)) : path;
        Score = score;
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        Explanations = explanations ?? Array.Empty<SearchResultExplanation>();
    }

    public int Rank { get; init; }

    public string Path { get; init; }

    public double Score { get; init; }

    public IReadOnlyList<SearchCandidate> Candidates { get; init; }

    public IReadOnlyList<SearchResultExplanation> Explanations { get; init; }

    public SearchCandidate? BestCandidate =>
        Candidates.Count == 0
            ? null
            : Candidates.OrderByDescending(candidate => candidate.Score).First();
}

