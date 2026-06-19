using System.IO;
using System.Linq;
using System.Windows;

namespace FileSearch.Gui.Services;

public sealed class FileOperationService : IFileOperationService
{
    public Task<FileOperationResult> RenameFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult(FileOperationResult.Failed("File does not exist."));

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            return Task.FromResult(FileOperationResult.Failed("File folder could not be resolved."));

        var window = new RenameFileWindow(Path.GetFileName(path));
        var owner = GetOwnerWindow();
        if (owner is not null)
            window.Owner = owner;

        if (window.ShowDialog() != true)
            return Task.FromResult(FileOperationResult.Cancelled());

        var newPath = Path.Combine(directory, window.FileName);
        if (string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(FileOperationResult.Cancelled("Name unchanged."));

        if (File.Exists(newPath) || Directory.Exists(newPath))
            return Task.FromResult(FileOperationResult.Failed("A file or folder with that name already exists."));

        try
        {
            File.Move(path, newPath, overwrite: false);
            return Task.FromResult(FileOperationResult.Renamed(newPath));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult.Failed($"Couldn't rename file: {ex.Message}"));
        }
    }

    public Task<FileOperationResult> MoveFileToRecycleBinAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult(FileOperationResult.Failed("File does not exist."));

        var owner = GetOwnerWindow();
        var result = owner is null
            ? System.Windows.MessageBox.Show(
                $"Move this file to the Recycle Bin?\n\n{path}",
                "Delete Result",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No)
            : System.Windows.MessageBox.Show(
                owner,
                $"Move this file to the Recycle Bin?\n\n{path}",
                "Delete Result",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return Task.FromResult(FileOperationResult.Cancelled());

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return Task.FromResult(FileOperationResult.Deleted());
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult.Failed($"Couldn't delete file: {ex.Message}"));
        }
    }

    private static Window? GetOwnerWindow()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return null;

        return app.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive)
               ?? app.MainWindow;
    }
}
