namespace FileSearch.Core.Extractors;

public sealed class WindowsIFilterExtractionOptions
{
    public bool Enabled { get; set; } = true;

    public HashSet<string> AllowedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> BlockedExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk",
        ".url",
        ".search-ms",
        ".searchconnector-ms",
    };

    public bool AllowsPath(string path)
    {
        var extension = NormalizeExtension(Path.GetExtension(path));
        if (extension.Length == 0)
            return AllowedExtensions.Count == 0;

        if (BlockedExtensions.Contains(extension))
            return false;

        return AllowedExtensions.Count == 0 || AllowedExtensions.Contains(extension);
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return string.Empty;

        extension = extension.Trim();
        return extension.StartsWith('.')
            ? extension
            : "." + extension;
    }
}
