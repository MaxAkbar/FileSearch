namespace FileSearch.Core.Queries;

/// <summary>
/// How to interpret the user's raw query string when building the
/// <see cref="Query"/> AST.
/// </summary>
public enum QueryMode
{
    /// <summary>Whole input is treated as a single literal substring.</summary>
    PlainText,

    /// <summary>Whole input is treated as a single regular expression.</summary>
    Regex,

    /// <summary>Input is parsed with Boolean operators (AND/OR/NOT, parens).</summary>
    Boolean,

    /// <summary>Input is parsed as a structured query with field filters.</summary>
    Unified,
}
