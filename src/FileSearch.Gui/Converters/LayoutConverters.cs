using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;
using FileSearch.Gui.ViewModels;
using Binding = System.Windows.Data.Binding;

namespace FileSearch.Gui.Converters;

/// <summary>
/// Result-group header summary ("4 files · 5 matches"). Bound as a
/// MultiBinding over the group's <c>Items</c> and <c>ItemCount</c> so the
/// text refreshes as streamed results join the group.
/// </summary>
public sealed class GroupSummaryConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not IEnumerable items)
            return string.Empty;

        var files = items.OfType<FileResultViewModel>().ToList();
        var matches = files.Sum(file => file.HitCount);
        return string.Create(
            culture,
            $"{files.Count:n0} {(files.Count == 1 ? "file" : "files")} · {matches:n0} {(matches == 1 ? "match" : "matches")}");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Splits a folder path for two-part sidebar rows: <c>ConverterParameter=leaf</c>
/// gives the last segment ("src"), anything else the shortened parent ("…\viking").
/// </summary>
public sealed class FolderPathPartConverter : IValueConverter
{
    private const int ParentBudget = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        var wantsLeaf = string.Equals(parameter as string, "leaf", StringComparison.OrdinalIgnoreCase);
        if (cut <= 0)
            return wantsLeaf ? path : string.Empty;

        if (wantsLeaf)
            return trimmed[(cut + 1)..];

        var parent = trimmed[..cut];
        if (parent.EndsWith(':'))
            parent += Path.DirectorySeparatorChar;
        return ShortenParent(parent);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    private static string ShortenParent(string parent)
    {
        if (parent.Length <= ParentBudget)
            return parent;

        var parts = parent.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var shown = parts[^1];
        for (var i = parts.Length - 2; i >= 0 && shown.Length + parts[i].Length + 1 <= ParentBudget - 2; i--)
            shown = parts[i] + Path.DirectorySeparatorChar + shown;
        return "…" + Path.DirectorySeparatorChar + shown;
    }
}

/// <summary>True when two bound paths name the same folder (case-insensitive, trailing slash ignored).</summary>
public sealed class PathsEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 &&
        values[0] is string left &&
        values[1] is string right &&
        string.Equals(left.Trim().TrimEnd('\\', '/'), right.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><c>true</c> → Collapsed, <c>false</c> → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
