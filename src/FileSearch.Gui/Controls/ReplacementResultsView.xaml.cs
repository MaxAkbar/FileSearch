using System.ComponentModel;
using System.Windows.Controls;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Controls;

public partial class ReplacementResultsView : System.Windows.Controls.UserControl
{
    public ReplacementResultsView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is ReplacementViewModel old) old.PropertyChanged -= OnReplacementChanged;
            if (args.NewValue is ReplacementViewModel current) current.PropertyChanged += OnReplacementChanged;
            UpdateColumns();
        };
    }

    private void OnReplacementChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ReplacementViewModel.IsNames)) UpdateColumns();
    }
    private void UpdateColumns() => DestinationColumn.Visibility = DataContext is ReplacementViewModel { IsNames: true } ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}
