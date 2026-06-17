using System.Collections.Generic;

namespace FileSearch.Core.Indexing;

internal static class FileTypeCategory
{
    private static readonly HashSet<string> s_code = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx", ".py", ".java", ".cpp", ".c", ".h",
        ".hpp", ".go", ".rs", ".php", ".rb", ".swift", ".kt", ".kts", ".sql", ".ps1", ".sh",
        ".bat", ".cmd", ".json", ".xml", ".xaml", ".html", ".css", ".scss", ".yaml", ".yml",
    };

    private static readonly HashSet<string> s_documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".rtf", ".pdf", ".doc", ".docx", ".odt", ".xls", ".xlsx", ".ppt",
        ".pptx", ".csv", ".tsv", ".epub", ".eml", ".ics", ".vcf",
    };

    private static readonly HashSet<string> s_images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg", ".ico",
    };

    private static readonly HashSet<string> s_audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma",
    };

    private static readonly HashSet<string> s_video = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v",
    };

    private static readonly HashSet<string> s_archives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz",
    };

    public static string ForExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return "other";

        if (s_code.Contains(extension))
            return "code";
        if (s_documents.Contains(extension))
            return "document";
        if (s_images.Contains(extension))
            return "image";
        if (s_audio.Contains(extension))
            return "audio";
        if (s_video.Contains(extension))
            return "video";
        if (s_archives.Contains(extension))
            return "archive";
        if (extension is ".exe" or ".dll" or ".msi" or ".appx")
            return "application";

        return "other";
    }
}
