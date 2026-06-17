namespace FileSearch.Core.Extractors;

public sealed class ExtractorHostException : Exception
{
    public ExtractorHostException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
