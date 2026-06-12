using System.Windows;
using FileSearch.Core.Workflows;

namespace FileSearch.Gui;

/// <summary>
/// Modal confirmation for side-effecting workflow steps (file operations,
/// program launches): title, one-line description and a scrollable sample of
/// the affected items. OK returns true, Cancel/close returns false.
/// </summary>
public partial class WorkflowConfirmationWindow : Window
{
    public WorkflowConfirmationWindow(WorkflowConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        InitializeComponent();

        TitleText.Text = confirmation.Title;
        DescriptionText.Text = confirmation.Description;
        DetailsList.ItemsSource = confirmation.Details;
    }

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
