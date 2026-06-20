namespace FileSearch.WindowsOcr;

public sealed class WindowsImageOcrOptions
{
    public Func<bool> IsEnabled { get; set; } = () => true;

    public long MaxImageBytes { get; set; } = 20 * 1024 * 1024;

    public long MaxImagePixels { get; set; } = 25_000_000;

    public long MaxPdfBytes { get; set; } = 100 * 1024 * 1024;

    public int MaxPdfPages { get; set; } = 50;

    public Func<int>? MaxPdfPagesProvider { get; set; }

    public string OcrLanguageTag { get; set; } = string.Empty;

    public Func<string?>? OcrLanguageTagProvider { get; set; }

    public int ResolveMaxPdfPages()
    {
        try
        {
            return Math.Max(0, MaxPdfPagesProvider?.Invoke() ?? MaxPdfPages);
        }
        catch
        {
            return Math.Max(0, MaxPdfPages);
        }
    }

    public string ResolveOcrLanguageTag()
    {
        try
        {
            return (OcrLanguageTagProvider?.Invoke() ?? OcrLanguageTag).Trim();
        }
        catch
        {
            return OcrLanguageTag.Trim();
        }
    }
}
