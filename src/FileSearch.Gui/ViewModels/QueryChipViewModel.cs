using CommunityToolkit.Mvvm.ComponentModel;
using FileSearch.Core.Queries;

namespace FileSearch.Gui.ViewModels;

public sealed partial class QueryChipViewModel : ObservableObject
{
    private readonly Action<QueryChipViewModel, string>? _replaceValue;

    public QueryChipViewModel(UnifiedQueryChip chip, Action<QueryChipViewModel, string>? replaceValue = null)
    {
        Field = chip.Field;
        _value = chip.Value;
        RawText = chip.RawText;
        Position = chip.Position;
        Length = chip.Length;
        IsEnabled = chip.IsEnabled;
        Explanation = chip.Explanation ?? string.Empty;
        _replaceValue = replaceValue;
    }

    public string Field { get; }
    public string RawText { get; }
    public int Position { get; }
    public int Length { get; }
    public bool IsEnabled { get; }
    public bool IsDisabled => !IsEnabled;
    public string Explanation { get; }
    public string DisplayText => $"{Field}: {Value}";
    public string ToolTip => IsEnabled
        ? $"Remove {DisplayText}"
        : Explanation;

    [ObservableProperty] private string _value = string.Empty;

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(ToolTip));
        _replaceValue?.Invoke(this, value);
    }
}
