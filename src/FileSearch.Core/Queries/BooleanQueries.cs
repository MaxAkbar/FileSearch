using System;
using System.Collections.Generic;

namespace FileSearch.Core.Queries;

public sealed class AndQuery : Query
{
    public IReadOnlyList<Query> Children { get; }

    public AndQuery(IReadOnlyList<Query> children)
    {
        if (children is null || children.Count == 0)
            throw new ArgumentException("AND requires at least one child.", nameof(children));
        Children = children;
    }

    public override bool IsMatch(string line)
    {
        for (int i = 0; i < Children.Count; i++)
            if (!Children[i].IsMatch(line)) return false;
        return true;
    }

    public override bool TryCollectHighlights(string line, List<MatchSpan> sink)
    {
        var startCount = sink.Count;
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i].TryCollectHighlights(line, sink))
                continue;

            sink.RemoveRange(startCount, sink.Count - startCount);
            return false;
        }

        return true;
    }

    public override void CollectHighlights(string line, List<MatchSpan> sink)
    {
        for (int i = 0; i < Children.Count; i++)
            Children[i].CollectHighlights(line, sink);
    }
}

public sealed class OrQuery : Query
{
    public IReadOnlyList<Query> Children { get; }

    public OrQuery(IReadOnlyList<Query> children)
    {
        if (children is null || children.Count == 0)
            throw new ArgumentException("OR requires at least one child.", nameof(children));
        Children = children;
    }

    public override bool IsMatch(string line)
    {
        for (int i = 0; i < Children.Count; i++)
            if (Children[i].IsMatch(line)) return true;
        return false;
    }

    public override bool TryCollectHighlights(string line, List<MatchSpan> sink)
    {
        var matched = false;
        for (int i = 0; i < Children.Count; i++)
            if (Children[i].TryCollectHighlights(line, sink))
                matched = true;

        return matched;
    }

    public override void CollectHighlights(string line, List<MatchSpan> sink)
    {
        for (int i = 0; i < Children.Count; i++)
            if (Children[i].IsMatch(line))
                Children[i].CollectHighlights(line, sink);
    }
}

public sealed class NotQuery : Query
{
    public Query Child { get; }

    public NotQuery(Query child) =>
        Child = child ?? throw new ArgumentNullException(nameof(child));

    public override bool IsMatch(string line) => !Child.IsMatch(line);

    public override bool TryCollectHighlights(string line, List<MatchSpan> sink) =>
        IsMatch(line);

    // NOT contributes no highlights — there's nothing to highlight when the
    // child *didn't* match.
}
