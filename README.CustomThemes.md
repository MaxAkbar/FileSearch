# FileSearch Custom Themes

FileSearch uses a hybrid theme model:

- XAML keeps the reusable control templates, layout rules, spacing, and visual states.
- JSON custom themes override resource tokens such as colors and fonts.

Custom theme files live in:

```text
%AppData%\FileSearch\Themes
```

Open FileSearch settings, place a `*.json` file in that folder, then use **Refresh** under **Themes**.

## Theme File Shape

```json
{
  "name": "Nord Dark",
  "baseTheme": "Dark",
  "colors": {
    "Atlas.PaperBrush": "#FF1E2328",
    "Atlas.SurfaceBrush": "#FF252B31",
    "Atlas.Surface2Brush": "#FF2E353D",
    "Atlas.SidebarBrush": "#FF20262D",
    "Atlas.InkBrush": "#FFECEFF4",
    "Atlas.Ink2Brush": "#FFD8DEE9",
    "Atlas.Ink3Brush": "#FF8F9AA6",
    "Atlas.LineBrush": "#33414A",
    "Atlas.Line2Brush": "#FF3B4650",
    "Atlas.AccentBrush": "#FF88C0D0",
    "Atlas.Accent2Brush": "#FF8FBCBB",
    "Atlas.AccentTintBrush": "#3388C0D0",
    "Atlas.AccentSoftBrush": "#2288C0D0",
    "Atlas.OnAccentBrush": "#FF172026",
    "Atlas.HitBrush": "#99EBCB8B",
    "Atlas.HitInkBrush": "#FF172026",

    "ApplicationPageBackgroundThemeBrush": "#FF1E2328",
    "SystemControlBackgroundChromeMediumLowBrush": "#FF252B31",
    "SystemControlBackgroundChromeMediumBrush": "#FF252B31",
    "SystemControlBackgroundAltMediumLowBrush": "#FF1E2328",
    "SystemControlBackgroundAltMediumBrush": "#FF2E353D",
    "SystemControlBackgroundAccentBrush": "#FF88C0D0",
    "SystemControlHighlightAccentBrush": "#FF88C0D0",
    "SystemAccentColor": "#FF88C0D0",
    "SystemAccentColorBrush": "#FF88C0D0"
  },
  "fonts": {
    "Atlas.SansFont": "Segoe UI",
    "Atlas.SerifFont": "Cambria, Georgia, Times New Roman",
    "Atlas.MonoFont": "Cascadia Code, Consolas, Courier New"
  }
}
```

## Notes

- `baseTheme` can be `Light`, `Dark`, `VisualStudio`, or `System`.
- Color values use WPF color syntax, usually `#RRGGBB` or `#AARRGGBB`.
- Keys ending in `Color` become `Color` resources. Other color keys become `SolidColorBrush` resources.
- JSON themes are overlays. Missing keys fall back to the selected base theme.
