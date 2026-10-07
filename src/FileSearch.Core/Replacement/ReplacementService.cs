using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text.Json;
using FileSearch.Core.Indexing;
using FileSearch.Core.Walker;

namespace FileSearch.Core.Replacement;

public sealed class ReplacementService : IReplacementService, IDisposable
{
    private readonly ReplacementOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WindowsIndexVolumeResolver _identityResolver = new();
    private string LatestPath => Path.Combine(_options.BackupDirectory, "latest.json");

    public ReplacementService(ReplacementOptions? options = null) => _options = options ?? new();

    public void Dispose() => _gate.Dispose();

    public Task<ReplacementPlan> PreviewAsync(ReplacementRequest request, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var matcher = new ReplacementMatcher(request);
        if (request.RecoveryGroupId is not null) ValidateRecoveryId(request.RecoveryGroupId);
        if (request.Roots.Count == 0 || request.Roots.Any(root => !Directory.Exists(root)))
            throw new ArgumentException("Choose an existing folder.");
        var items = new List<ReplacementItem>();
        foreach (var (path, directory) in Enumerate(request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureSafePath(path, request.Roots);
                var attributes = File.GetAttributes(path);
                if (!directory && (attributes & (FileAttributes.ReadOnly | FileAttributes.Encrypted)) != 0)
                    throw new NotSupportedException("Read-only or encrypted file.");
                if (request.Target == ReplacementTarget.Names)
                {
                    if (directory && request.NameTarget == ReplacementNameTarget.Files || !directory && request.NameTarget == ReplacementNameTarget.Folders) continue;
                    var name = Path.GetFileName(path);
                    var extension = PreservedExtension(name, directory, request.IncludeExtensions);
                    var stem = extension.Length > 0 ? name[..^extension.Length] : name;
                    var replaced = matcher.Replace(stem, out var count) + extension;
                    if (count == 0) continue;
                    var newPath = Path.Combine(Path.GetDirectoryName(path)!, replaced);
                    var reason = InvalidName(replaced);
                    if (directory && request.Roots.Concat(request.ProtectedRoots ?? []).Append(_options.BackupDirectory).Any(root => IsWithin(root, path)))
                        reason = "Scope roots, indexed roots, recovery folders, and their ancestors are protected.";
                    if (reason is null && DestinationExists(path, newPath)) reason = "Destination already exists.";
                    items.Add(new(Guid.NewGuid().ToString("N"), path, directory, Fingerprint(path, directory), Identity(path), count,
                        [new("Name", name, replaced)], newPath, reason));
                }
                else
                {
                    var bytes = ReadOriginal(path);
                    var replacement = ReplaceContent(path, bytes, request, matcher);
                    if (replacement.Count == 0 && replacement.Changes.Count == 0) continue;
                    items.Add(new(Guid.NewGuid().ToString("N"), path, false, Hash(bytes), Identity(path), replacement.Count,
                        replacement.Changes, SkipReason: replacement.Count == 0 ? "Only unsupported formula matches were found." : null));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (IsItemFailure(exception))
            {
                items.Add(new(Guid.NewGuid().ToString("N"), path, directory, "", "", 0, [], SkipReason: exception.Message));
            }
        }
        if (request.Target == ReplacementTarget.Names)
        {
            var collisions = items.Where(item => item.CanApply).GroupBy(item => item.NewPath!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).SelectMany(group => group.Select(item => item.Id)).ToHashSet();
            for (var i = 0; i < items.Count; i++)
                if (collisions.Contains(items[i].Id)) items[i] = items[i] with { SkipReason = "Multiple items would have the same destination name." };
        }
        return new ReplacementPlan(request, items);
    }, cancellationToken);

    public async Task<ReplacementBatchResult> ApplyAsync(ReplacementPlan plan, IReadOnlySet<string> checkedItemIds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Apply(plan, checkedItemIds, cancellationToken), CancellationToken.None).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private ReplacementBatchResult Apply(ReplacementPlan plan, IReadOnlySet<string> checkedIds, CancellationToken token)
    {
        var selected = plan.Items.Where(item => checkedIds.Contains(item.Id) && item.CanApply)
            .OrderByDescending(item => Depth(item.Path)).ThenBy(item => item.IsDirectory).ToArray();
        if (selected.Length == 0) return new(null, []);
        var matcher = new ReplacementMatcher(plan.Request);
        using var journalLock = LockJournal();
        var previousLatest = File.Exists(LatestPath) ? File.ReadAllBytes(LatestPath) : null;
        var groupPointer = Path.Combine(_options.BackupDirectory, "last-group.json");
        var previousGroup = File.Exists(groupPointer) ? File.ReadAllBytes(groupPointer) : null;
        var journal = new BatchJournal { Id = Guid.NewGuid().ToString("N"), Target = plan.Request.Target,
            GroupId = plan.Request.RecoveryGroupId, GroupName = plan.Request.RecoveryGroupName,
            PreviewSkips = plan.Items.Where(item => item.SkipReason is not null).Select(item => item.Path + ": " + item.SkipReason).ToList() };
        var batchDirectory = Path.Combine(_options.BackupDirectory, journal.Id);
        Directory.CreateDirectory(batchDirectory);
        // Prepared entries are persisted before each mutation. A stale/empty batch must not replace the previous Undo target.
        var outcomes = new List<ReplacementOutcome>();
        foreach (var item in selected)
        {
            if (token.IsCancellationRequested) break;
            JournalEntry? entry = null;
            try
            {
                EnsureSafePath(item.Path, plan.Request.Roots);
                if (Identity(item.Path) != item.Identity || Fingerprint(item.Path, item.IsDirectory) != item.Fingerprint)
                    throw new IOException("Changed since preview; preview again.");
                entry = new JournalEntry
                {
                    OldPath = item.Path, NewPath = item.NewPath ?? item.Path, IsDirectory = item.IsDirectory,
                    BeforeHash = item.Fingerprint, Identity = item.Identity,
                    ChangeCount = item.ChangeCount,
                };
                if (plan.Request.Target == ReplacementTarget.Contents)
                {
                    var original = ReadOriginal(item.Path);
                    if (Hash(original) != item.Fingerprint) throw new IOException("Changed since preview; preview again.");
                    var replacement = ReplaceContent(item.Path, original, plan.Request, matcher);
                    if (replacement.Count != item.ChangeCount) throw new IOException("Preview no longer matches the proposed changes.");
                    entry.BackupPath = Path.Combine(batchDirectory, item.Id + ".original");
                    WriteDurable(entry.BackupPath, original);
                    entry.AfterHash = Hash(replacement.Bytes);
                    entry.TemporaryPath = item.Path + ".filesearch-" + Guid.NewGuid().ToString("N") + ".tmp";
                    journal.Entries.Add(entry);
                    Save(journal, updateGroup: true);
                    WriteDurable(entry.TemporaryPath, replacement.Bytes);
                    if (Fingerprint(item.Path, false) != item.Fingerprint) throw new IOException("Changed while applying; preview again.");
                    File.Replace(entry.TemporaryPath, item.Path, null);
                }
                else
                {
                    var name = Path.GetFileName(item.Path);
                    var extension = PreservedExtension(name, item.IsDirectory, plan.Request.IncludeExtensions);
                    var stem = extension.Length > 0 ? name[..^extension.Length] : name;
                    var expectedName = matcher.Replace(stem, out _) + extension;
                    if (InvalidName(expectedName) is { } invalid) throw new IOException(invalid);
                    var expectedDestination = Path.Combine(Path.GetDirectoryName(item.Path)!, expectedName);
                    if (!string.Equals(expectedDestination, item.NewPath, StringComparison.Ordinal)) throw new IOException("Rename differs from preview.");
                    if (item.IsDirectory && plan.Request.Roots.Concat(plan.Request.ProtectedRoots ?? []).Append(_options.BackupDirectory).Any(root => IsWithin(root, item.Path)))
                        throw new IOException("Protected folder.");
                    if (DestinationExists(item.Path, entry.NewPath)) throw new IOException("Destination already exists.");
                    entry.AfterHash = item.Fingerprint;
                    if (string.Equals(item.Path, entry.NewPath, StringComparison.OrdinalIgnoreCase))
                        entry.TemporaryPath = Path.Combine(Path.GetDirectoryName(item.Path)!, ".filesearch-" + Guid.NewGuid().ToString("N"));
                    journal.Entries.Add(entry);
                    Save(journal, updateGroup: true);
                    if (entry.TemporaryPath is not null)
                    {
                        Move(item.Path, entry.TemporaryPath, item.IsDirectory);
                        Move(entry.TemporaryPath, entry.NewPath, item.IsDirectory);
                    }
                    else Move(item.Path, entry.NewPath, item.IsDirectory);
                }
                entry.State = "Applied";
                Save(journal, updateGroup: true);
                outcomes.Add(new(item.Path, true, "Applied", item.NewPath, item.IsDirectory));
            }
            catch (Exception exception) when (IsItemFailure(exception))
            {
                var committed = false;
                if (entry is not null && journal.Entries.Contains(entry))
                {
                    try
                    {
                        committed = plan.Request.Target == ReplacementTarget.Contents
                            ? Fingerprint(entry.OldPath, false) == entry.AfterHash
                            : Exists(entry.NewPath) && Identity(entry.NewPath) == entry.Identity &&
                              (entry.TemporaryPath is null || Directory.GetFileSystemEntries(Path.GetDirectoryName(entry.NewPath)!).Contains(entry.NewPath, StringComparer.Ordinal));
                        var strandedRename = plan.Request.Target == ReplacementTarget.Names && entry.TemporaryPath is not null && Exists(entry.TemporaryPath);
                        if (!committed && !strandedRename) { entry.State = "Failed"; entry.Error = exception.Message; CleanupTemporary(entry); }
                        Save(journal, updateGroup: true);
                    }
                    catch (Exception recoveryException) when (IsItemFailure(recoveryException)) { /* The prepared journal remains the recovery source. */ }
                }
                else
                {
                    journal.ApplySkips.Add(item.Path + ": " + exception.Message);
                    // Retain failures without replacing the last successful Undo target.
                    try { Save(journal, publishLatest: false, updateGroup: journal.Entries.Any(entry => entry.State != "Failed")); }
                    catch (Exception recoveryException) when (IsItemFailure(recoveryException)) { }
                }
                outcomes.Add(new(item.Path, committed, committed ? "Applied; recovery journal requires reconciliation." : exception.Message,
                    committed ? item.NewPath : null, item.IsDirectory));
            }
        }
        if (journal.Entries.Count > 0 && journal.Entries.All(entry => entry.State == "Failed"))
        {
            if (previousLatest is not null) AtomicWrite(LatestPath, previousLatest);
            else if (File.Exists(LatestPath)) File.Delete(LatestPath);
            if (journal.GroupId is not null)
            {
                if (previousGroup is not null) AtomicWrite(groupPointer, previousGroup);
                else if (File.Exists(groupPointer)) File.Delete(groupPointer);
            }
        }
        return new(journal.Id, outcomes, token.IsCancellationRequested);
    }

    public async Task<ReplacementBatchResult> UndoAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Undo(cancellationToken), CancellationToken.None).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private ReplacementBatchResult Undo(CancellationToken token, string? batchId = null, bool lockJournal = true,
        Dictionary<string, string>? restoredFiles = null)
    {
        using var journalLock = lockJournal ? LockJournal() : null;
        var journal = Load(batchId);
        if (journal is null) return new(null, []);
        var publishLatest = batchId is null || Load()?.Id == batchId;
        var outcomes = new List<ReplacementOutcome>();
        var blockedDirectories = new List<string>();
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            if (token.IsCancellationRequested) break;
            if (entry.State == "Undone") { TrackRestoredItem(journal.Target, entry, restoredFiles); continue; }
            if (entry.State == "Failed") continue;
            try
            {
                if (blockedDirectories.Any(directory => IsWithin(entry.NewPath, directory) || IsWithin(entry.OldPath, directory)))
                    throw new IOException("An ancestor rename could not be undone.");
                if (journal.Target == ReplacementTarget.Contents)
                {
                    EnsureNoReparsePoints(entry.OldPath);
                    var current = Fingerprint(entry.OldPath, false);
                    if (entry.State is "Prepared" or "Undoing" && current == entry.BeforeHash)
                    {
                        var recoveredUndo = entry.State == "Undoing";
                        entry.State = "Undone"; entry.RestoredIdentity = Identity(entry.OldPath);
                        Save(journal, publishLatest); TrackRestoredItem(journal.Target, entry, restoredFiles); CleanupTemporary(entry);
                        if (recoveredUndo) outcomes.Add(new(entry.NewPath, true, "Recovered completed Undo", IsDirectory: entry.IsDirectory));
                        continue;
                    }
                    if (current != entry.AfterHash) throw new IOException("File changed after replacement; original kept in backups.");
                    var backup = ReadOriginal(entry.BackupPath!);
                    if (Hash(backup) != entry.BeforeHash) throw new IOException("Backup is missing or changed.");
                    var temporary = entry.OldPath + ".filesearch-undo-" + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        WriteDurable(temporary, backup);
                        if (Fingerprint(entry.OldPath, false) != entry.AfterHash) throw new IOException("File changed while undoing.");
                        entry.State = "Undoing"; Save(journal, publishLatest);
                        File.Replace(temporary, entry.OldPath, null);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                else
                {
                    if (entry.State == "Undoing" && Exists(entry.OldPath) && Identity(entry.OldPath) == entry.Identity &&
                        Directory.GetFileSystemEntries(Path.GetDirectoryName(entry.OldPath)!).Contains(entry.OldPath, StringComparer.Ordinal))
                    { entry.State = "Undone"; Save(journal, publishLatest); TrackRestoredItem(journal.Target, entry, restoredFiles);
                        outcomes.Add(new(entry.NewPath, true, "Recovered completed Undo", entry.OldPath, entry.IsDirectory)); continue; }
                    // A crash during a case-only rename can leave the item at its journaled temporary name.
                    var source = entry.TemporaryPath is not null && Exists(entry.TemporaryPath) ? entry.TemporaryPath : entry.NewPath;
                    if (entry.State == "Prepared" && !Exists(source) && Exists(entry.OldPath) && Identity(entry.OldPath) == entry.Identity)
                    { entry.State = "Undone"; Save(journal, publishLatest); TrackRestoredItem(journal.Target, entry, restoredFiles); continue; }
                    EnsureNoReparsePoints(source);
                    // Atomic restoration creates a new file identity. Only verified restorations in
                    // this recovery group may update a preceding rename's expected identity.
                    if (!entry.IsDirectory && restoredFiles?.TryGetValue(source, out var restoredIdentity) == true &&
                        Identity(source) == restoredIdentity && Fingerprint(source, false) == entry.BeforeHash)
                        entry.Identity = restoredIdentity;
                    if (Identity(source) != entry.Identity) throw new IOException("Renamed item is missing or has been replaced.");
                    if (DestinationExists(source, entry.OldPath)) throw new IOException("Original name is occupied.");
                    entry.State = "Undoing"; Save(journal, publishLatest);
                    if (string.Equals(source, entry.OldPath, StringComparison.OrdinalIgnoreCase) && !string.Equals(source, entry.OldPath, StringComparison.Ordinal))
                    {
                        var temporary = Path.Combine(Path.GetDirectoryName(source)!, ".filesearch-undo-" + Guid.NewGuid().ToString("N"));
                        entry.TemporaryPath = temporary; Save(journal, publishLatest);
                        Move(source, temporary, entry.IsDirectory);
                        Move(temporary, entry.OldPath, entry.IsDirectory);
                    }
                    else if (!string.Equals(source, entry.OldPath, StringComparison.Ordinal)) Move(source, entry.OldPath, entry.IsDirectory);
                }
                entry.State = "Undone";
                if (journal.Target == ReplacementTarget.Contents) entry.RestoredIdentity = Identity(entry.OldPath);
                Save(journal, publishLatest);
                TrackRestoredItem(journal.Target, entry, restoredFiles);
                CleanupTemporary(entry);
                outcomes.Add(new(entry.NewPath, true, "Undone", journal.Target == ReplacementTarget.Names ? entry.OldPath : null, entry.IsDirectory));
            }
            catch (Exception exception) when (IsItemFailure(exception))
            {
                if (entry.IsDirectory) { blockedDirectories.Add(entry.NewPath); blockedDirectories.Add(entry.OldPath); }
                outcomes.Add(new(entry.NewPath, false, exception.Message, IsDirectory: entry.IsDirectory));
            }
        }
        return new(journal.Id, outcomes, token.IsCancellationRequested);
    }

    public Task<bool> CanUndoAsync(CancellationToken cancellationToken) => Task.Run(() => Load()?.Entries.Any(entry => entry.State is not ("Undone" or "Failed")) == true, cancellationToken);

    public Task<ReplacementRecoveryGroup?> GetLastRecoveryGroupAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var pointer = Path.Combine(_options.BackupDirectory, "last-group.json");
        var group = ReadJson<ReplacementRecoveryGroup>(pointer);
        return group is not null && group.BatchIds.Any(id => Load(id)?.Entries.Any(entry => entry.State is not ("Undone" or "Failed")) == true) ? group : null;
    }, cancellationToken);

    public async Task<ReplacementBatchResult> UndoGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        ValidateRecoveryId(groupId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var journalLock = LockJournal();
                var group = ReadJson<ReplacementRecoveryGroup>(GroupPath(groupId)) ?? throw new IOException("Workflow recovery group is missing.");
                var outcomes = new List<ReplacementOutcome>();
                var restoredFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in group.BatchIds.Reverse())
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        var batch = Load(id) ?? throw new IOException("A workflow batch journal is missing; earlier batches were retained.");
                        if (batch.GroupId != groupId) throw new IOException("Workflow recovery group does not match its batch journal.");
                        var result = Undo(cancellationToken, id, lockJournal: false, restoredFiles);
                        outcomes.AddRange(result.Outcomes);
                        // Earlier replacements can depend on this version or path. Retain them on a conflict.
                        if (result.Outcomes.Any(outcome => !outcome.Succeeded)) break;
                    }
                    catch (Exception exception) when (IsItemFailure(exception)) { outcomes.Add(new("Recovery batch " + id, false, exception.Message)); break; }
                }
                return new ReplacementBatchResult(groupId, outcomes, cancellationToken.IsCancellationRequested);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private ContentReplacement ReplaceContent(string path, byte[] bytes, ReplacementRequest request, ReplacementMatcher matcher) =>
        OfficeContentReplacer.Supports(path) ? OfficeContentReplacer.Replace(path, bytes, matcher, request.IncludeFormulas) :
        TextContentReplacer.Supports(path, request) ? TextContentReplacer.Replace(bytes, matcher) :
        throw new NotSupportedException("Content replacement is supported for text, DOCX, XLSX, and PPTX only.");

    private IEnumerable<(string Path, bool Directory)> Enumerate(ReplacementRequest request, CancellationToken token)
    {
        if (request.SourcePaths is not null)
        {
            foreach (var path in request.SourcePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                yield return (Path.GetFullPath(path), Directory.Exists(path));
            }
            yield break;
        }
        var options = request.WalkerOptions;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawRoot in request.Roots)
        {
            var root = Path.GetFullPath(rawRoot);
            EnsureNoReparsePoints(root);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var current))
            {
                token.ThrowIfCancellationRequested();
                string[] children;
                try { children = Directory.GetFileSystemEntries(current); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var path in children)
                {
                    token.ThrowIfCancellationRequested();
                    if (!seen.Add(path) || IsWithin(path, _options.BackupDirectory)) continue;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); } catch (IOException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || !options.IncludeHidden && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var directory = (attributes & FileAttributes.Directory) != 0;
                    if (directory)
                    {
                        if (options.ExcludeDirectories.Contains(Path.GetFileName(path))) continue;
                        if (options.Recursive) pending.Push(path);
                        if (request.Target == ReplacementTarget.Names && DateMatches(Directory.GetLastWriteTimeUtc(path), options)) yield return (path, true);
                    }
                    else
                    {
                        var info = new FileInfo(path);
                        var extension = Path.GetExtension(path);
                        if (info.Length < options.MinFileSizeBytes || options.MaxFileSizeBytes > 0 && info.Length > options.MaxFileSizeBytes || !DateMatches(info.LastWriteTimeUtc, options)) continue;
                        if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(extension) || options.ExcludeExtensions.Contains(extension)) continue;
                        if (options.IncludeDirectories.Count > 0 && !FileWalker.HasDirectorySegment(path, root, options.IncludeDirectories)) continue;
                        if (options.IncludeGlobs.Count > 0 && !options.IncludeGlobs.Any(glob => FileSystemName.MatchesSimpleExpression(glob, info.Name, true))) continue;
                        if (options.ExcludeGlobs.Any(glob => FileSystemName.MatchesSimpleExpression(glob, info.Name, true))) continue;
                        yield return (path, false);
                    }
                }
            }
        }
    }

    internal static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DateMatches(DateTime value, WalkerOptions options) =>
        (options.ModifiedAfterUtc is null || value >= options.ModifiedAfterUtc) && (options.ModifiedBeforeUtc is null || value <= options.ModifiedBeforeUtc);
    private static int Depth(string path) => path.Count(character => character is '/' or '\\');
    private static string PreservedExtension(string name, bool directory, bool includeExtensions) =>
        !directory && !includeExtensions && name.LastIndexOf('.') > 0 ? Path.GetExtension(name) : "";
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static bool DestinationExists(string source, string destination) => !source.Equals(destination, StringComparison.OrdinalIgnoreCase) && Exists(destination);
    private static string? InvalidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') || name.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character))) return "Invalid Windows name.";
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]) ? "Reserved Windows name." : null;
    }

    private string Identity(string path) => _identityResolver.TryGetFileIdentity(path, out var identity)
        ? Path.GetPathRoot(Path.GetFullPath(path)) + ":" + identity.FileReferenceNumber
        : throw new IOException("Stable file identity is unavailable; replacement skipped.");
    private static string Fingerprint(string path, bool directory) => directory ? Directory.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : Hash(ReadOriginal(path));
    private static byte[] ReadOriginal(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Move(string source, string destination, bool directory) { if (directory) Directory.Move(source, destination); else File.Move(source, destination, false); }
    private static bool IsItemFailure(Exception exception) => exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);
    private void EnsureSafePath(string path, IReadOnlyList<string> roots)
    {
        if (IsWithin(path, _options.BackupDirectory)) throw new IOException("Replacement recovery data is protected.");
        if (!roots.Any(root => IsWithin(path, root))) throw new IOException("Item is outside the preview scope.");
        EnsureNoReparsePoints(path);
    }
    private static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked files and folders are not replacement targets.");
    }

    private FileStream LockJournal()
    {
        Directory.CreateDirectory(_options.BackupDirectory);
        return new FileStream(Path.Combine(_options.BackupDirectory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void Save(BatchJournal journal, bool publishLatest = true, bool updateGroup = false)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(journal);
        var batchPath = Path.Combine(_options.BackupDirectory, journal.Id, "journal.json");
        AtomicWrite(batchPath, data);
        if (publishLatest) AtomicWrite(LatestPath, data);
        if (updateGroup && journal.GroupId is { } groupId)
        {
            var previous = ReadJson<ReplacementRecoveryGroup>(GroupPath(groupId));
            var ids = previous?.BatchIds.ToList() ?? [];
            if (!ids.Contains(journal.Id, StringComparer.Ordinal)) ids.Add(journal.Id);
            var batches = ids.Select(Load).Where(batch => batch is not null).ToArray();
            var group = new ReplacementRecoveryGroup(groupId, journal.GroupName ?? "Workflow", ids)
            {
                ChangeCount = batches.Sum(batch => batch!.Entries.Where(entry => entry.State != "Failed").Sum(entry => (long)entry.ChangeCount)),
                SkippedItemCount = batches.Sum(batch => batch!.PreviewSkips.Count + batch.ApplySkips.Count + batch.Entries.Count(entry => entry.State == "Failed")),
            };
            var groupData = JsonSerializer.SerializeToUtf8Bytes(group);
            AtomicWrite(GroupPath(groupId), groupData);
            AtomicWrite(Path.Combine(_options.BackupDirectory, "last-group.json"), groupData);
        }
    }
    private BatchJournal? Load(string? batchId = null)
    {
        if (batchId is not null) ValidateRecoveryId(batchId);
        return ReadJson<BatchJournal>(batchId is null ? LatestPath : Path.Combine(_options.BackupDirectory, batchId, "journal.json"));
    }
    private string GroupPath(string id) { ValidateRecoveryId(id); return Path.Combine(_options.BackupDirectory, "group-" + id + ".json"); }
    private static void ValidateRecoveryId(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid recovery id."); }
    private static T? ReadJson<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream);
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteDurable(temporary, bytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void CleanupTemporary(JournalEntry entry)
    {
        if (entry.TemporaryPath is not null && File.Exists(entry.TemporaryPath)) File.Delete(entry.TemporaryPath);
    }

    private static void TrackRestoredItem(ReplacementTarget target, JournalEntry entry, Dictionary<string, string>? restoredFiles)
    {
        if (restoredFiles is null) return;
        if (target == ReplacementTarget.Contents)
        {
            if (entry.RestoredIdentity is not null) restoredFiles[entry.OldPath] = entry.RestoredIdentity;
            return;
        }
        foreach (var path in restoredFiles.Keys.ToArray())
        {
            var renamed = path.Equals(entry.NewPath, StringComparison.OrdinalIgnoreCase) ? entry.OldPath :
                entry.IsDirectory && IsWithin(path, entry.NewPath) ? entry.OldPath.TrimEnd('\\', '/') + path[entry.NewPath.TrimEnd('\\', '/').Length..] : path;
            if (renamed == path) continue;
            var identity = restoredFiles[path]; restoredFiles.Remove(path); restoredFiles[renamed] = identity;
        }
    }

    private sealed class BatchJournal
    {
        public BatchJournal() { }
        public string Id { get; set; } = "";
        public ReplacementTarget Target { get; set; }
        public string? GroupId { get; set; }
        public string? GroupName { get; set; }
        public List<string> PreviewSkips { get; set; } = [];
        public List<string> ApplySkips { get; set; } = [];
        public List<JournalEntry> Entries { get; set; } = [];
    }
    private sealed class JournalEntry
    {
        public JournalEntry() { }
        public string OldPath { get; set; } = "";
        public string NewPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public string BeforeHash { get; set; } = "";
        public string AfterHash { get; set; } = "";
        public string Identity { get; set; } = "";
        public string? RestoredIdentity { get; set; }
        public int ChangeCount { get; set; }
        public string? Error { get; set; }
        public string State { get; set; } = "Prepared";
        public string? BackupPath { get; set; }
        public string? TemporaryPath { get; set; }
    }
}
