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

/// <summary>
/// File name → the type color at low alpha: the soft badge fill behind
/// <see cref="FileTypeToInkBrushConverter"/> text. Alpha (not a white mix)
/// keeps the tint sitting correctly on any card surface.
/// </summary>
public sealed class FileTypeToTintBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<Color, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = FileTypeCatalog.GetColor(value as string);
        return Cache.GetOrAdd(color, c =>
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// File name → the type color darkened to a readable ink for the soft badge
/// on light surfaces (dark themes switch to the neutral badge instead).
/// </summary>
public sealed class FileTypeToInkBrushConverter : IValueConverter
{
    private const double InkShade = 0.6;

    private static readonly ConcurrentDictionary<Color, SolidColorBrush> Cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = FileTypeCatalog.GetColor(value as string);
        return Cache.GetOrAdd(color, c =>
        {
            var brush = new SolidColorBrush(Color.FromRgb(
                (byte)(c.R * InkShade),
                (byte)(c.G * InkShade),
                (byte)(c.B * InkShade)));
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
