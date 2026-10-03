using System.Collections.Generic;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;

namespace FileSearch.Core.Engine;

public enum HitKind
{
    Content,
    Metadata,
}

public enum HitRoute
{
    Live,
    Indexed,
}

/// <summary>
/// A single match found by the search engine.
/// </summary>
public sealed record Hit(
    string Path,
    int LineNumber,
    string LineContent,
    IReadOnlyList<MatchSpan> Highlights,
    HitKind Kind = HitKind.Content,
    double Score = 0,
    long? SizeBytes = null,
    DateTime? ModifiedUtc = null,
    HitRoute Route = HitRoute.Live,
    SourceAnchor? Anchor = null,
    long? ContentUnitId = null,
    SourceLocator? Locator = null,
    SearchSnippet? Snippet = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public MailMessageMetadata? MailMessage => Anchor?.MailMessage ?? Locator?.MailMessage ?? Snippet?.Locator?.MailMessage;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ResultKey => MailMessage is { StoreFingerprint: not null } message ? Path + "\0" + message.Id : Path;

    internal static string GetResultKey(string path, SourceAnchor? anchor) =>
        anchor?.MailMessage is { StoreFingerprint: not null } message
            ? path + "\0" + message.Id
            : path;
}
