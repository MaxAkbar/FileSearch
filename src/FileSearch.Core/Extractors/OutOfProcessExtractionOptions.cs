namespace FileSearch.Core.Extractors;

public sealed class OutOfProcessExtractionOptions
{
    public bool Enabled { get; set; } = true;

    public bool UseReusableHostPool { get; set; } = true;

    public int HostPoolSize { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public string? HostPath { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public HashSet<string> ExtractorIds { get; } = new(StringComparer.Ordinal)
    {
        "filesearch.pdf-pdfpig",
        "filesearch.word-openxml",
        "filesearch.excel-closedxml",
        "filesearch.powerpoint-openxml",
        "filesearch.opendocument",
        "filesearch.epub",
        "filesearch.zip",
        "filesearch.ifilter",
    };
}
