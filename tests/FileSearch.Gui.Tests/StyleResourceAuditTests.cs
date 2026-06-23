using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace FileSearch.Gui.Tests;

public sealed class StyleResourceAuditTests
{
    private static readonly Regex s_dynamicTokenReference = new(
        @"\{DynamicResource\s+((?:AppStyle|AppDensity)\.[^}\s]+)",
        RegexOptions.Compiled);

    private static readonly Regex s_tokenKey = new(
        @"x:Key=""((?:AppStyle|AppDensity)\.[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex s_themePaletteOverrideKey = new(
        @"x:Key=""((?:Atlas\.[^""]+(?:Brush|Color))|(?:ApplicationPageBackgroundThemeBrush)|(?:SystemAccentColor(?:Brush)?)|(?:SystemControl[^""]+Brush))""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryStyleTokenReferenceHasBaseFallback()
    {
        var root = FindRepositoryRoot();
        var guiRoot = Path.Combine(root, "src", "FileSearch.Gui");
        var baseKeys = ReadTokenKeys(Path.Combine(guiRoot, "Styles", "AppStyles.xaml")).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var file in Directory.GetFiles(guiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var key in ReadDynamicTokenReferences(text))
            {
                if (!baseKeys.Contains(key))
                    missing.Add($"{Path.GetRelativePath(root, file)}: {key}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "Dynamic AppStyle/AppDensity resources without base fallbacks:\n" +
            string.Join('\n', missing));
    }

    [Fact]
    public void ThemeAndStyleOverlaysOnlyOverrideKnownStyleTokens()
    {
        var root = FindRepositoryRoot();
        var guiRoot = Path.Combine(root, "src", "FileSearch.Gui");
        var basePath = Path.Combine(guiRoot, "Styles", "AppStyles.xaml");
        var baseKeys = ReadTokenKeys(basePath).ToHashSet(StringComparer.Ordinal);
        var unknown = new List<string>();

        foreach (var folder in new[] { "Styles", "Themes" })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(guiRoot, folder), "*.xaml"))
            {
                if (string.Equals(file, basePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var key in ReadTokenKeys(file))
                {
                    if (!baseKeys.Contains(key))
                        unknown.Add($"{Path.GetRelativePath(root, file)}: {key}");
                }
            }
        }

        Assert.True(
            unknown.Count == 0,
            "Theme/style overlays define AppStyle/AppDensity tokens missing from AppStyles.xaml:\n" +
            string.Join('\n', unknown));
    }

    [Fact]
    public void StyleOverlaysDoNotOverrideThemePaletteBrushes()
    {
        var root = FindRepositoryRoot();
        var stylesRoot = Path.Combine(root, "src", "FileSearch.Gui", "Styles");
        var basePath = Path.Combine(stylesRoot, "AppStyles.xaml");
        var overrides = new List<string>();

        foreach (var file in Directory.GetFiles(stylesRoot, "*.xaml"))
        {
            if (string.Equals(file, basePath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(Path.GetFileName(file), "Vela.xaml", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match match in s_themePaletteOverrideKey.Matches(text))
                overrides.Add($"{Path.GetRelativePath(root, file)}: {match.Groups[1].Value}");
        }

        Assert.True(
            overrides.Count == 0,
            "Style overlays should not override theme palette brushes; put palette changes in Themes/*.xaml:\n" +
            string.Join('\n', overrides));
    }

    [Fact]
    public void VelaOverridesEveryThemePaletteKey()
    {
        var root = FindRepositoryRoot();
        var guiRoot = Path.Combine(root, "src", "FileSearch.Gui");
        var themeKeys = Directory
            .GetFiles(Path.Combine(guiRoot, "Themes"), "*.xaml")
            .SelectMany(ReadThemePaletteKeys)
            .ToHashSet(StringComparer.Ordinal);
        var velaKeys = ReadThemePaletteKeys(Path.Combine(guiRoot, "Styles", "Vela.xaml"))
            .ToHashSet(StringComparer.Ordinal);
        var missing = themeKeys
            .Except(velaKeys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Vela owns its palette and must override every theme palette key so light themes cannot leak foreground/background brushes:\n" +
            string.Join('\n', missing));
    }

    [Fact]
    public void VelaBrushStyleTokensResolveToBrushes()
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.ResourceAssembly ??= typeof(global::FileSearch.Gui.App).Assembly;
                var resources = new ResourceDictionary();
                resources.MergedDictionaries.Add(LoadDictionary("Styles/AppStyles.xaml"));
                resources.MergedDictionaries.Add(LoadDictionary("Themes/AtlasDark.xaml"));
                resources.MergedDictionaries.Add(LoadDictionary("Styles/Vela.xaml"));

                foreach (var key in VelaBrushStyleTokenKeys)
                    Assert.IsAssignableFrom<Brush>(resources[key]);
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private static readonly string[] VelaBrushStyleTokenKeys =
    [
        "AppStyle.TitleStatusPillBackground",
        "AppStyle.TitleStatusPillBorderBrush",
        "AppStyle.StatusPillBackground",
        "AppStyle.StatusPillBorderBrush",
        "AppStyle.NavItemBackground",
        "AppStyle.NavItemBorderBrush",
        "AppStyle.NavSectionBackground",
        "AppStyle.NavSectionHeaderBackground",
        "AppStyle.NavSectionHeaderBorderBrush",
        "AppStyle.SearchSurfaceBackground",
        "AppStyle.SearchSurfaceBorderBrush",
        "AppStyle.SearchStatusPillBackground",
        "AppStyle.SearchStatusPillBorderBrush",
        "AppStyle.OmniBarBackground",
        "AppStyle.OmniPathChipBackground",
        "AppStyle.OmniPathChipBorderBrush",
        "AppStyle.FilterChipBackground",
        "AppStyle.ThemeMiniButtonSelectedBackground",
        "AppStyle.ThemeMiniButtonSelectedBorderBrush",
        "AppStyle.ThemeMiniButtonSelectedForeground",
        "AppStyle.QueryChipBackground",
        "AppStyle.PreviewMetadataBackground",
        "AppStyle.PreviewImageBackground",
        "AppStyle.SecondaryButtonBackground",
        "AppStyle.SecondaryButtonBorderBrush",
        "AppStyle.SecondaryButtonHoverBackground",
        "AppStyle.SecondaryButtonHoverBorderBrush",
        "AppStyle.SecondaryButtonHoverForeground",
        "AppStyle.IconButtonHoverBackground",
        "AppStyle.IconButtonHoverForeground",
        "AppStyle.PreviewCodeBackground",
        "AppStyle.ResultHitsPanelBackground",
    ];

    private static IEnumerable<string> ReadDynamicTokenReferences(string text) =>
        s_dynamicTokenReference.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> ReadTokenKeys(string file) =>
        s_tokenKey.Matches(File.ReadAllText(file))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> ReadThemePaletteKeys(string file) =>
        s_themePaletteOverrideKey.Matches(File.ReadAllText(file))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);

    private static ResourceDictionary LoadDictionary(string componentPath) =>
        new() { Source = new Uri($"pack://application:,,,/FileSearch.Gui;component/{componentPath}", UriKind.Absolute) };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FileSearch.slnx")))
                return directory.FullName;
            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Repository root (FileSearch.slnx) not found above test directory.");
    }
}
