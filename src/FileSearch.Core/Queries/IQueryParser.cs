namespace FileSearch.Core.Queries;

public interface IQueryParser
{
    Query Parse(string input);
}
