using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace FileSearch.Gui.Converters;

/// <summary>
/// File name (or extension) → a frozen <see cref="SolidColorBrush"/> for the
/// type badge. Brushes are cached per color so cards share instances.
/// </summary>
public sealed class FileTypeToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<Color, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = FileTypeCatalog.GetColor(value as string);
        return Cache.GetOrAdd(color, c =>
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>File name → short type label (e.g. "C#", "PDF", "TXT").</summary>
public sealed class FileTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        FileTypeCatalog.GetLabel(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
