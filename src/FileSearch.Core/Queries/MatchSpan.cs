namespace FileSearch.Core.Queries;

/// <summary>
/// Position of a single highlight within a line. <see cref="Start"/> is the
/// zero-based character index; <see cref="Length"/> is the number of characters.
/// </summary>
public readonly record struct MatchSpan(int Start, int Length);
