using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FileSearch.Core.Workflows;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui;

/// <summary>
/// Workflow library, step editor and run host. Owned by the main window like
/// <see cref="IndexedLocationsWindow"/>: created on demand with the shared
/// <see cref="MainViewModel"/> as its DataContext and shown as a dialog.
/// While open it supplies the runner's confirmation prompts; closing it
/// cancels any run in progress.
/// </summary>
public partial class WorkflowsWindow : Window
{
    public WorkflowsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        ((INotifyCollectionChanged)RunLogList.Items).CollectionChanged += OnRunLogChanged;
    }

    private WorkflowsViewModel? Workflows => (DataContext as MainViewModel)?.Workflows;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Workflows is not { } workflows)
            return;

        workflows.Interaction = new WindowWorkflowInteraction(this);
        workflows.ConfirmDiscard = name => System.Windows.MessageBox.Show(
            this,
            $"Discard unsaved changes to '{name}'?",
            "Workflows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        workflows.OnOpened();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Workflows is not { } workflows)
            return;

        workflows.Interaction = null;
        workflows.ConfirmDiscard = null;
        workflows.CancelRunCommand.Execute(null);
    }

    private void OnRunLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Keep the newest log line visible while a run streams progress.
        if (e.Action == NotifyCollectionChangedAction.Add && RunLogList.Items.Count > 0)
            RunLogList.ScrollIntoView(RunLogList.Items[RunLogList.Items.Count - 1]);
    }

    private void OnAddMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        var helpPath = Path.Combine(AppContext.BaseDirectory, "Help", "workflows.html");
        if (!File.Exists(helpPath))
        {
            System.Windows.MessageBox.Show(
                this,
                $"The FileSearch help files could not be found.\n\nExpected location:\n{helpPath}",
                "FileSearch Help",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var helpWindow = new HelpWindow(helpPath)
            {
                Owner = this,
            };

            helpWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"The FileSearch help files could not be opened.\n\n{ex.Message}",
                "FileSearch Help",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Bridges the runner's confirmation requests (background threads) onto
    /// this window's dispatcher: shows the modal confirmation dialog and
    /// completes the returned task with the user's choice. Cancelling the run
    /// resolves the prompt as declined and closes the dialog if it is open.
    /// </summary>
    private sealed class WindowWorkflowInteraction : IWorkflowInteraction
    {
        private readonly WorkflowsWindow _owner;

        public WindowWorkflowInteraction(WorkflowsWindow owner) => _owner = owner;

        public async Task<bool> ConfirmAsync(WorkflowConfirmation confirmation, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkflowConfirmationWindow? dialog = null;

            _ = _owner.Dispatcher.BeginInvoke(() =>
            {
                if (cancellationToken.IsCancellationRequested || !_owner.IsVisible)
                {
                    completion.TrySetResult(false);
                    return;
                }

                dialog = new WorkflowConfirmationWindow(confirmation)
                {
                    Owner = _owner,
                };
                completion.TrySetResult(dialog.ShowDialog() == true);
            });

            using var registration = cancellationToken.Register(() =>
            {
                completion.TrySetResult(false);
                _ = _owner.Dispatcher.BeginInvoke(() => dialog?.Close());
            });

            return await completion.Task.ConfigureAwait(false);
        }
    }
}
