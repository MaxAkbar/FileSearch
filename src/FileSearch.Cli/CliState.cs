using System.Globalization;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;

namespace FileSearch.Cli;

internal sealed class CliState
{
    public const int DefaultResultLimit = 50;
    private const long DefaultMaxFileSizeBytes = 50L * 1024 * 1024;

    public string Root { get; set; } = Directory.GetCurrentDirectory();

    public QueryMode Mode { get; set; } = QueryMode.PlainText;

    public bool CaseSensitive { get; set; }

    public bool Recursive { get; set; } = true;

    public bool IncludeHidden { get; set; }

    public bool UseIndex { get; set; }

    public int ResultLimit { get; set; } = DefaultResultLimit;

    public List<string> IncludeGlobs { get; } = [];

    public List<string> ExcludeGlobs { get; } = [];

    public HashSet<string> IncludeExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ExcludeExtensions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long MinFileSizeBytes { get; set; }

    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    public DateTime? ModifiedAfterUtc { get; set; }

    public DateTime? ModifiedBeforeUtc { get; set; }

    public WalkerOptions ToWalkerOptions() =>
        new()
        {
            IncludeGlobs = IncludeGlobs.ToArray(),
            ExcludeGlobs = ExcludeGlobs.ToArray(),
            IncludeExtensions = new HashSet<string>(IncludeExtensions, StringComparer.OrdinalIgnoreCase),
            ExcludeExtensions = new HashSet<string>(ExcludeExtensions, StringComparer.OrdinalIgnoreCase),
            Recursive = Recursive,
            IncludeHidden = IncludeHidden,
            MinFileSizeBytes = MinFileSizeBytes,
            MaxFileSizeBytes = MaxFileSizeBytes,
            ModifiedAfterUtc = ModifiedAfterUtc,
            ModifiedBeforeUtc = ModifiedBeforeUtc,
        };

    public void ClearFilters()
    {
        IncludeGlobs.Clear();
        ExcludeGlobs.Clear();
        IncludeExtensions.Clear();
        ExcludeExtensions.Clear();
        MinFileSizeBytes = 0;
        MaxFileSizeBytes = DefaultMaxFileSizeBytes;
        ModifiedAfterUtc = null;
        ModifiedBeforeUtc = null;
    }

    public static IReadOnlyList<string> SplitList(string input) =>
        input.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static string NormalizeExtension(string extension)
    {
        var value = extension.Trim();
        if (value.Length == 0)
            return value;

        return value[0] == '.' ? value : $".{value}";
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:n0} {units[unit]}"
            : $"{value:n1} {units[unit]}";
    }

    public static bool TryParseSize(string input, out long bytes)
    {
        bytes = 0;
        var value = input.Trim().ToLowerInvariant();
        if (value.Length == 0)
            return false;

        var multiplier = 1L;
        foreach (var (suffix, scale) in new[]
        {
            ("kb", 1024L),
            ("k", 1024L),
            ("mb", 1024L * 1024),
            ("m", 1024L * 1024),
            ("gb", 1024L * 1024 * 1024),
            ("g", 1024L * 1024 * 1024),
        })
        {
            if (!value.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            multiplier = scale;
            value = value[..^suffix.Length].Trim();
            break;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            number < 0)
        {
            return false;
        }

        try
        {
            bytes = checked((long)(number * multiplier));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
