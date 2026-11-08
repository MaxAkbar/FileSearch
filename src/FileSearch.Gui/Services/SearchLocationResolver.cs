using System.IO;
using FileSearch.Gui.Settings;

namespace FileSearch.Gui.Services;

public sealed record SearchLocationResolution(IReadOnlyList<string> Roots, string Notice);

internal static class SearchLocationResolver
{
    private static readonly SemaphoreSlim AvailabilityReaders = new(4, 4);

    internal static async Task<SearchLocationResolution> ResolveAsync(LocationScopeKind scope, string folder,
        IEnumerable<string> selectedRoots, IEnumerable<string> registeredRoots, CancellationToken token)
    {
        // Folder searches do not probe availability here and must not wait for
        // obsolete native checks of disconnected indexed locations.
        if (scope == LocationScopeKind.SelectedFolder)
            return Resolve(scope, folder, [], [], token);
        var selected = selectedRoots.ToArray();
        var registered = registeredRoots.ToArray();
        await AvailabilityReaders.WaitAsync(token).ConfigureAwait(false);
        var read = Task.Run(() => Resolve(scope, folder, selected, registered, token), CancellationToken.None);
        try { return await read.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            // Native availability checks may finish after cancellation. Bound their
            // concurrency without keeping the UI waiting for an obsolete check.
            if (read.IsCompleted) { _ = read.Exception; AvailabilityReaders.Release(); }
            else _ = read.ContinueWith(task => { _ = task.Exception; AvailabilityReaders.Release(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static string? Normalize(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim())); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static string[] NormalizeRoots(IEnumerable<string> roots) => roots.Select(Normalize).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static SearchLocationResolution Resolve(LocationScopeKind scope, string folder,
        IEnumerable<string> selectedRoots, IEnumerable<string> registeredRoots, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (scope == LocationScopeKind.SelectedFolder)
        {
            var root = Normalize(folder);
            return root is null ? new([], "Choose a folder to search.") : new([folder], "");
        }
        var registered = NormalizeRoots(registeredRoots);
        var selected = NormalizeRoots(selectedRoots);
        if (scope == LocationScopeKind.SelectedIndexedLocations && selected.Length == 0)
            return new([], "No indexed locations selected. Choose locations or select All indexed locations.");
        var requested = scope == LocationScopeKind.AllIndexedLocations ? registered : selected;
        var available = new List<string>();
        var notices = new List<string>();
        foreach (string root in requested)
        {
            token.ThrowIfCancellationRequested();
            if (!registered.Contains(root, StringComparer.OrdinalIgnoreCase))
            { notices.Add($"No longer indexed: {root}"); continue; }
            if (!Directory.Exists(root))
            { notices.Add($"Offline or inaccessible: {root}"); continue; }
            available.Add(root);
        }
        if (requested.Length == 0) notices.Add("No indexed locations are registered.");
        if (available.Count == 0 && notices.Count > 0) notices.Insert(0, "No available locations in this scope.");
        // Keep overlapping roots: index recursion and exclusions can differ per root.
        // The result identity layer removes repeated hits without losing coverage.
        string notice = string.Join(" · ", notices.Take(8));
        if (notices.Count > 8) notice += $" · {notices.Count - 8:n0} additional unavailable locations.";
        return new(available.AsReadOnly(), notice);
    }
}
