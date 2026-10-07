using System.Resources;

namespace FileSearch.Core.Engine;

internal static class EmbeddingLicenseResources
{
    private const string LegacyResourcePrefix = "FileSearch.Core.Licenses.";
    private static readonly ResourceManager s_resources = new(
        "FileSearch.Core.Engine.EmbeddingLicenses", typeof(EmbeddingLicenseResources).Assembly);

    internal static string GetText(string resourceName)
    {
        var name = resourceName.StartsWith(LegacyResourcePrefix, StringComparison.Ordinal)
            ? resourceName[LegacyResourcePrefix.Length..]
            : resourceName;
        return s_resources.GetString(name, System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException($"Bundled model notice is missing: {name}.");
    }
}
