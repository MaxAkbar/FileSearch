using System;
using System.Collections.Generic;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public sealed record SearchCandidate
{
    public SearchCandidate(
        CandidateProviderKind provider,
        string providerId,
        string path,
        string displayText,
        double score = 0,
        HitKind kind = HitKind.Content,
        int? lineNumber = null,
        IReadOnlyList<MatchSpan>? highlights = null,
        long? fileId = null,
        long? contentUnitId = null,
        long? sizeBytes = null,
        DateTime? modifiedUtc = null,
        HitRoute? route = null,
        SourceAnchor? anchor = null,
        SourceLocator? locator = null,
        SearchSnippet? snippet = null,
        IReadOnlyList<SearchResultExplanation>? explanations = null)
    {
        Provider = provider;
        ProviderId = string.IsNullOrWhiteSpace(providerId) ? provider.ToString() : providerId;
        Path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Path is required.", nameof(path)) : path;
        DisplayText = displayText ?? string.Empty;
        Score = score;
        Kind = kind;
        LineNumber = lineNumber;
        Highlights = highlights ?? Array.Empty<MatchSpan>();
        FileId = fileId;
        ContentUnitId = contentUnitId;
        SizeBytes = sizeBytes;
        ModifiedUtc = modifiedUtc;
        Route = route;
        Anchor = anchor;
        Locator = locator;
        Snippet = snippet;
        Explanations = explanations ?? Array.Empty<SearchResultExplanation>();
    }

    public CandidateProviderKind Provider { get; init; }

    public string ProviderId { get; init; }

    public string Path { get; init; }

    public string DisplayText { get; init; }

    public double Score { get; init; }

    public HitKind Kind { get; init; }

    public int? LineNumber { get; init; }

    public IReadOnlyList<MatchSpan> Highlights { get; init; }

    public long? FileId { get; init; }

    public long? ContentUnitId { get; init; }

    public long? SizeBytes { get; init; }

    public DateTime? ModifiedUtc { get; init; }

    public HitRoute? Route { get; init; }

    public SourceAnchor? Anchor { get; init; }

    public SourceLocator? Locator { get; init; }

    public SearchSnippet? Snippet { get; init; }

    public IReadOnlyList<SearchResultExplanation> Explanations { get; init; }

    public static SearchCandidate FromHit(
        Hit hit,
        CandidateProviderKind provider,
        string? providerId = null,
        IReadOnlyList<SearchResultExplanation>? explanations = null)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return new SearchCandidate(
            provider,
            providerId ?? provider.ToString(),
            hit.Path,
            hit.LineContent,
            hit.Score,
            hit.Kind,
            hit.LineNumber > 0 ? hit.LineNumber : null,
            hit.Highlights,
            contentUnitId: hit.ContentUnitId,
            sizeBytes: hit.SizeBytes,
            modifiedUtc: hit.ModifiedUtc,
            route: hit.Route,
            anchor: hit.Anchor,
            locator: hit.Locator,
            snippet: hit.Snippet,
            explanations: explanations);
    }
}
