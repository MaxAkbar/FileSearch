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

    // NOT contributes no highlights — there's nothing to highlight when the
    // child *didn't* match.
}
