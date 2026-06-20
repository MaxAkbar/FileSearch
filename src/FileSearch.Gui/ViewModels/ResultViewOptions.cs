using FileSearch.Core.Extractors;

namespace FileSearch.Gui.ViewModels;

public enum ResultSortMode
{
    Relevance,
    Recency,
    Filename,
    HitCount,
}

public enum ResultGroupMode
{
    File,
    Folder,
    FileType,
    ModifiedDate,
}

public enum SearchResultExportFormat
{
    Json,
    Csv,
    JsonLines,
    Markdown,
}

public sealed class ResultSortOption
{
    public ResultSortOption(ResultSortMode value, string label)
    {
        Value = value;
        Label = label;
    }

    public ResultSortMode Value { get; }

    public string Label { get; }
}

public sealed class ResultGroupOption
{
    public ResultGroupOption(ResultGroupMode value, string label)
    {
        Value = value;
        Label = label;
    }

    public ResultGroupMode Value { get; }

    public string Label { get; }
}

public sealed class ResultFacetOption
{
    public const string AllValue = "__all";

    public ResultFacetOption(string value, string label, int count)
    {
        Value = value;
        Label = label;
        Count = count;
    }

    public string Value { get; }

    public string Label { get; }

    public int Count { get; }

    public string DisplayText => Count >= 0 ? $"{Label} ({Count:n0})" : Label;
}

public sealed record SearchResultsExportDocument(
    string Query,
    string SearchPath,
    DateTime ExportedUtc,
    int FileCount,
    int HitCount,
    IReadOnlyList<ExportFile> Files);

public sealed record ExportFile(
    string Path,
    string FileName,
    string Folder,
    string Extension,
    long? SizeBytes,
    DateTime? ModifiedUtc,
    string Source,
    int HitCount,
    IReadOnlyList<ExportHit> Hits);

public sealed record ExportHit(
    string Path,
    string FileName,
    string Folder,
    string Extension,
    long? SizeBytes,
    DateTime? ModifiedUtc,
    string Source,
    int FileHitCount,
    int LineNumber,
    string Kind,
    string Line,
    SourceAnchor? Anchor);
