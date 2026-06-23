using System.Windows.Media;
using FileSearch.Gui.Services;
using Color = System.Windows.Media.Color;

namespace FileSearch.Gui.Tests;

public sealed class CustomThemeJsonTests
{
    [Fact]
    public void LoadInfoReadsThemeMetadata()
    {
        var path = WriteThemeJson(
            """
            {
              "name": "Nord Dark",
              "baseTheme": "Dark",
              "colors": {
                "Atlas.PaperBrush": "#FF1E2328"
              }
            }
            """);

        var info = CustomThemeJson.LoadInfo(path);

        Assert.Equal("Nord Dark", info.Name);
        Assert.Equal(Path.GetFileName(path), info.FileName);
        Assert.Equal(AppTheme.Dark, info.BaseTheme);
    }

    [Fact]
    public void CreateResourceDictionaryMapsColorsAndFontsToWpfResources()
    {
        var path = WriteThemeJson(
            """
            {
              "name": "Test",
              "colors": {
                "Atlas.PaperBrush": "#FF1E2328",
                "SystemAccentColor": "#FF88C0D0"
              },
              "fonts": {
                "Atlas.MonoFont": "Cascadia Code"
              }
            }
            """);
        var definition = CustomThemeJson.LoadDefinition(path);

        var resources = CustomThemeJson.CreateResourceDictionary(definition);

        var paper = Assert.IsType<SolidColorBrush>(resources["Atlas.PaperBrush"]);
        Assert.Equal(Color.FromArgb(0xFF, 0x1E, 0x23, 0x28), paper.Color);
        Assert.Equal(Color.FromArgb(0xFF, 0x1E, 0x23, 0x28), Assert.IsType<Color>(resources["Atlas.PaperColor"]));
        Assert.Equal(Color.FromArgb(0xFF, 0x88, 0xC0, 0xD0), Assert.IsType<Color>(resources["SystemAccentColor"]));
        Assert.Equal("Cascadia Code", Assert.IsType<FontFamily>(resources["Atlas.MonoFont"]).Source);
    }

    private static string WriteThemeJson(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "filesearch-theme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "theme.json");
        File.WriteAllText(path, json);
        return path;
    }
}
