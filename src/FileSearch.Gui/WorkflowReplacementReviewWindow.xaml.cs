using System.Windows;
using System.Windows.Input;
using FileSearch.Core.Replacement;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui;

public partial class WorkflowReplacementReviewWindow : Window
{
    public WorkflowReplacementReviewWindow(ReplacementPlan plan)
    {
        InitializeComponent(); DataContext = new WorkflowReplacementReviewViewModel(plan);
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Return) args.Handled = true; };
    }
    public IReadOnlySet<string> CheckedIds => ((WorkflowReplacementReviewViewModel)DataContext).CheckedIds;
    private void OnApplyClick(object sender, RoutedEventArgs args) { if (CheckedIds.Count > 0) DialogResult = true; }
}
