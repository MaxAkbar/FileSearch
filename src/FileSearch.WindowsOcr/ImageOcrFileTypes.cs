namespace FileSearch.WindowsOcr;

public static class ImageOcrFileTypes
{
    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];
}
