namespace FileSearch.Gui.Services;

public interface IFileOperationService
{
    Task<FileOperationResult> RenameFileAsync(string path, CancellationToken cancellationToken);

    Task<FileOperationResult> MoveFileToRecycleBinAsync(string path, CancellationToken cancellationToken);
}

public sealed record FileOperationResult(bool Succeeded, string? NewPath, string Message)
{
    public static FileOperationResult Cancelled(string message = "Operation canceled.") => new(false, null, message);

    public static FileOperationResult Failed(string message) => new(false, null, message);

    public static FileOperationResult Renamed(string newPath) => new(true, newPath, "Renamed file.");

    public static FileOperationResult Deleted() => new(true, null, "Moved file to Recycle Bin.");
}
