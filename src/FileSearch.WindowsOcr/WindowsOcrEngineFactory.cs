using Windows.Globalization;
using Windows.Media.Ocr;

namespace FileSearch.WindowsOcr;

internal static class WindowsOcrEngineFactory
{
    public static OcrEngine? TryCreate(WindowsImageOcrOptions options)
    {
        var languageTag = options.ResolveOcrLanguageTag();
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            try
            {
                var language = new Language(languageTag);
                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine is not null)
                    return engine;
            }
            catch
            {
            }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages();
    }
}
