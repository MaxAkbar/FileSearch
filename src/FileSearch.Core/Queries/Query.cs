using System.Collections.Generic;

namespace FileSearch.Core.Queries;

/// <summary>
/// Root of the query AST. Each subtype knows how to match itself against a
/// line of text and contribute highlight spans for any matches it produces.
/// New query operators can be added by extending this class — no other code in
/// the engine needs to change (OCP).
/// </summary>
public abstract class Query
{
    public abstract bool IsMatch(string line);

    /// <summary>
    /// For matched lines, append highlight spans to <paramref name="sink"/>.
    /// Negative queries (NOT) contribute nothing.
    /// </summary>
    public virtual void CollectHighlights(string line, List<MatchSpan> sink) { }
}
