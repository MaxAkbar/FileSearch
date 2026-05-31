using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Gui.Services;

/// <summary>
/// Builds the preview text for the selected file: a numbered listing of
/// every hit line with surrounding context, with disjoint regions
/// separated by a "---" marker.
/// </summary>
public interface IFilePreviewService
{
    Task<string> LoadHitsPreviewAsync(
        string path,
        IReadOnlyList<int> hitLineNumbers,
        int contextLines,
        CancellationToken cancellationToken);
}
