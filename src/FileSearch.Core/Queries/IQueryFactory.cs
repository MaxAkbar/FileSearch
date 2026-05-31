namespace FileSearch.Core.Queries;

/// <summary>
/// Builds a <see cref="Query"/> from raw user input, dispatching to either
/// the Boolean <see cref="IQueryParser"/> or directly constructing a
/// <see cref="TermQuery"/> / <see cref="RegexQuery"/>.
/// </summary>
public interface IQueryFactory
{
    Query Build(string input, QueryMode mode, bool caseSensitive);
}
