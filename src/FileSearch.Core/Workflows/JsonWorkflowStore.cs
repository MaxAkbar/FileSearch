using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Stores each workflow as <c>%AppData%\FileSearch\Workflows\&lt;name&gt;.json</c>
/// (write-then-move, like the other JSON stores). Unlike the settings stores,
/// load/save errors are surfaced to the caller — a workflow the user authored
/// failing to save is something they must see, not a swallowed warning.
/// </summary>
public sealed class JsonWorkflowStore : IWorkflowStore
{
    public JsonWorkflowStore(string? directoryPath = null)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(directoryPath) ? GetDefaultDirectory() : directoryPath!;
    }

    public string DirectoryPath { get; }

    public IReadOnlyList<WorkflowSummary> List()
    {
        if (!Directory.Exists(DirectoryPath))
            return Array.Empty<WorkflowSummary>();

        var summaries = new List<WorkflowSummary>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(path);
            var workflow = TryLoad(fileName, out var error);
            summaries.Add(workflow is null
                ? new WorkflowSummary(fileName, Path.GetFileNameWithoutExtension(fileName), "", error)
                : new WorkflowSummary(fileName, workflow.Name, workflow.Description));
        }

        return summaries;
    }

    public WorkflowDefinition? TryLoad(string fileName, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var path = GetFullPath(fileName);
        if (!File.Exists(path))
        {
            error = $"Workflow file not found: {path}";
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not read '{fileName}': {ex.Message}";
            return null;
        }

        return WorkflowJson.TryDeserialize(json, out error);
    }

    public string Save(WorkflowDefinition workflow, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        fileName ??= ToFileName(workflow.Name);
        Directory.CreateDirectory(DirectoryPath);

        // Write-then-move so a crash mid-write can't truncate the file.
        var path = GetFullPath(fileName);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, WorkflowJson.Serialize(workflow));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        return fileName;
    }

    public void Delete(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var path = GetFullPath(fileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public string GetFullPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // Workflow files live flat in the library folder; reject anything that
        // would escape it (path separators, "..", drive prefixes).
        if (fileName != Path.GetFileName(fileName) || fileName is "." or "..")
            throw new ArgumentException($"'{fileName}' is not a plain file name.", nameof(fileName));

        return Path.Combine(DirectoryPath, fileName);
    }

    /// <summary>Derives a safe file name from a workflow name, e.g. "Find TODOs" → "find-todos.json".</summary>
    public static string ToFileName(string workflowName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((workflowName ?? "").Trim()
            .Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : char.ToLowerInvariant(c))
            .ToArray())
            .Trim('-', '.');
        return (cleaned.Length == 0 ? "workflow" : cleaned) + ".json";
    }

    private static string GetDefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileSearch",
        "Workflows");
}
