using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FileSearch.Core.Replacement;

namespace FileSearch.Gui.ViewModels;

public sealed partial class WorkflowReplacementReviewViewModel : ObservableObject
{
    public WorkflowReplacementReviewViewModel(ReplacementPlan plan)
    {
        foreach (var item in plan.Items)
        {
            var row = new ReplacementItemViewModel(item);
            row.PropertyChanged += OnItemChanged;
            Items.Add(row);
        }
        SelectedItem = Items.FirstOrDefault(item => item.CanApply) ?? Items.FirstOrDefault();
        UpdateSelection();
    }
    public ObservableCollection<ReplacementItemViewModel> Items { get; } = [];
    [ObservableProperty] private ReplacementItemViewModel? _selectedItem;
    public long CheckedChanges => Items.Where(item => item.IsChecked && item.CanApply).Sum(item => (long)item.ChangeCount);
    public bool CanApply => CheckedChanges > 0;
    public string Summary => $"{Items.Count(item => item.IsChecked && item.CanApply)} items checked · {CheckedChanges:N0} changes · {Items.Count(item => !item.CanApply)} skipped";
    public string SelectedPreview => SelectedItem is { } item ? item.Path + (item.Destination is null ? "" : "\nProposed path: " + item.Destination) + "\n\n" + item.Preview : "Select an item to review.";
    public IReadOnlySet<string> CheckedIds => Items.Where(item => item.IsChecked && item.CanApply).Select(item => item.Item.Id).ToHashSet();
    partial void OnSelectedItemChanged(ReplacementItemViewModel? value) => OnPropertyChanged(nameof(SelectedPreview));
    private void OnItemChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(ReplacementItemViewModel.IsChecked)) UpdateSelection(); }
    private void UpdateSelection()
    {
        foreach (var item in Items)
        {
            var path = item.Item.NewPath;
            if (path is null) continue;
            foreach (var parent in Items.Where(parent => parent != item && parent.IsChecked && parent.CanApply && parent.Item.IsDirectory).OrderByDescending(parent => parent.Path.Length))
                path = ReplacementViewModel.Remap(path, parent.Path, parent.Item.NewPath!, true);
            item.Destination = path;
        }
        OnPropertyChanged(nameof(CheckedChanges)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(SelectedPreview));
    }
}
