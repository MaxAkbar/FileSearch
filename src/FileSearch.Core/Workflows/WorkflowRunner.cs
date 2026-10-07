using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FileSearch.Core.Engine;
using FileSearch.Core.Extractors;
using FileSearch.Core.Queries;
using FileSearch.Core.Replacement;
using FileSearch.Core.Walker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Executes workflows step by step. Search steps stream through the regular
/// engine (<see cref="ISearcher"/>); sub-searches run a private
/// <see cref="Searcher"/> over a <see cref="FixedListFileWalker"/> of the
/// source step's matched files. Control flow is structured — if/else, retry
/// loops, for-each loops, stop — with a step-execution budget so an
/// authoring mistake cannot loop forever.
/// </summary>
public sealed partial class WorkflowRunner : IWorkflowRunner
{
    private readonly ISearcher _searcher;
    private readonly IQueryFactory _queryFactory;
    private readonly IExtractorRegistry _extractors;
    private readonly SearchOptions? _searchOptions;
    private readonly ILogger _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IReplacementService? _replacement;

    public WorkflowRunner(
        ISearcher searcher,
        IQueryFactory queryFactory,
        IExtractorRegistry extractors,
        SearchOptions? searchOptions = null,
        ILogger<WorkflowRunner>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IReplacementService? replacement = null)
    {
        _searcher = searcher ?? throw new ArgumentNullException(nameof(searcher));
        _queryFactory = queryFactory ?? throw new ArgumentNullException(nameof(queryFactory));
        _extractors = extractors ?? throw new ArgumentNullException(nameof(extractors));
        _searchOptions = searchOptions;
        _logger = logger ?? NullLogger<WorkflowRunner>.Instance;
        _loggerFactory = loggerFactory;
        _replacement = replacement;
    }

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition workflow,
        WorkflowRunOptions? options = null,
        IWorkflowObserver? observer = null,
        IWorkflowInteraction? interaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var validationErrors = WorkflowValidator.Validate(workflow);
        if (validationErrors.Count > 0)
        {
            return new WorkflowRunResult
            {
                Status = WorkflowRunStatus.Failed,
                Succeeded = false,
                Message = $"Workflow is invalid: {validationErrors[0]}",
                ValidationErrors = validationErrors,
            };
        }

        var run = new RunState(workflow, options ?? new WorkflowRunOptions(), observer ?? NullObserver.Instance, interaction);
        _logger.LogInformation("Workflow '{Name}' started.", workflow.Name);

        try
        {
            var flow = await ExecuteStepsAsync(workflow.Steps, run, depth: 0, cancellationToken).ConfigureAwait(false);
            var result = flow switch
            {
                Flow.Stop => new WorkflowRunResult
                {
                    Status = WorkflowRunStatus.Stopped,
                    Succeeded = run.StopSucceeded,
                    Message = run.StopMessage,
                    StepOutcomes = run.Outcomes,
                },
                Flow.Fail => new WorkflowRunResult
                {
                    Status = WorkflowRunStatus.Failed,
                    Succeeded = false,
                    Message = run.FailMessage,
                    StepOutcomes = run.Outcomes,
                },
                _ => new WorkflowRunResult
                {
                    Status = WorkflowRunStatus.Completed,
                    Succeeded = true,
                    StepOutcomes = run.Outcomes,
                },
            };
            _logger.LogInformation("Workflow '{Name}' finished: {Status}.", workflow.Name, result.Status);
            return result with { RecoveryGroupId = run.ReplacementBatches.Count > 0 ? run.RecoveryGroupId : null, ReplacementBatches = run.ReplacementBatches };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Workflow '{Name}' cancelled.", workflow.Name);
            return new WorkflowRunResult
            {
                Status = WorkflowRunStatus.Cancelled,
                Succeeded = false,
                Message = "Cancelled.",
                StepOutcomes = run.Outcomes,
                RecoveryGroupId = run.ReplacementBatches.Count > 0 ? run.RecoveryGroupId : null,
                ReplacementBatches = run.ReplacementBatches,
            };
        }
    }

    private async Task<Flow> ExecuteStepsAsync(
        IReadOnlyList<WorkflowStep> steps, RunState run, int depth, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();

            if (++run.StepExecutions > run.Options.MaxStepExecutions)
                return Fail(run, step, $"Exceeded the limit of {run.Options.MaxStepExecutions} step executions (possible runaway loop).");

            run.Observer.OnStepStarted(step, depth);

            Flow flow;
            try
            {
                flow = step switch
                {
                    SearchStep s => await ExecuteSearchAsync(s, run, ct).ConfigureAwait(false),
                    ReplacementStep s => await ExecuteReplacementAsync(s, run, ct).ConfigureAwait(false),
                    IfStep s => await ExecuteIfAsync(s, run, depth, ct).ConfigureAwait(false),
                    RetryStep s => await ExecuteRetryAsync(s, run, depth, ct).ConfigureAwait(false),
                    ForEachStep s => await ExecuteForEachAsync(s, run, depth, ct).ConfigureAwait(false),
                    ExportStep s => await ExecuteExportAsync(s, run, ct).ConfigureAwait(false),
                    FileOperationStep s => await ExecuteFileOperationAsync(s, run, ct).ConfigureAwait(false),
                    RunProgramStep s => await ExecuteRunProgramAsync(s, run, ct).ConfigureAwait(false),
                    StopStep s => ExecuteStop(s, run),
                    _ => Fail(run, step, $"Unknown step kind '{step.Kind}'."),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A step blowing up (I/O, permissions, bad regex deep in a
                // loop) fails the run predictably instead of half-continuing.
                _logger.LogWarning(ex, "Workflow step {Id} failed.", step.Id);
                return Fail(run, step, ex.Message);
            }

            if (flow != Flow.Continue)
                return flow;
        }

        return Flow.Continue;
    }

    private async Task<Flow> ExecuteSearchAsync(SearchStep step, RunState run, CancellationToken ct)
    {
        var queryText = run.Substitute(step.Query);

        Query query;
        try
        {
            query = _queryFactory.Build(queryText, step.Mode, step.CaseSensitive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(run, step, $"Invalid query '{queryText}': {ex.Message}");
        }

        ISearcher searcher;
        SearchRequest request;
        string scopeNote = "";
        SearchProgress? progress = null;
        IReadOnlyList<string> scopeRoots;
        var incompleteScope = false;

        if (step.ScopeStepId is not null)
        {
            if (!run.TryGetResults(step.ScopeStepId, out var source, out var error))
                return Fail(run, step, error);

            scopeNote = $" (within {source.Files.Count} file(s) from '{step.ScopeStepId}')";
            scopeRoots = source.Roots;
            incompleteScope = source.IncompleteScope;
            if (source.Files.Count == 0)
            {
                run.RecordSearchResults(step.Id, new StepResults());
                Record(run, step, succeeded: true, $"0 hits — scope step '{step.ScopeStepId}' matched no files");
                return Flow.Continue;
            }

            searcher = new Searcher(new FixedListFileWalker(source.Files.ToArray()), _extractors, _searchOptions,
                _loggerFactory?.CreateLogger<Searcher>());
            request = new SearchRequest(query, Array.Empty<string>(), new WalkerOptions(),
                Progress: p => progress = p, Status: run.Observer.OnLog, RawQuery: queryText, Mode: step.Mode);
        }
        else
        {
            var roots = step.Roots.Select(run.Substitute).Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
            scopeRoots = roots;
            if (roots.Length == 0)
                return Fail(run, step, "No root folders to search after variable substitution.");

            var walkerOptions = step.Filters.ToWalkerOptions(
                step.Filters.IncludeGlobs.Select(run.Substitute).ToArray(),
                step.Filters.ExcludeGlobs.Select(run.Substitute).ToArray());

            searcher = _searcher;
            request = new SearchRequest(query, roots, walkerOptions,
                Progress: p => progress = p, UseIndex: step.UseIndex, Status: run.Observer.OnLog, RawQuery: queryText, Mode: step.Mode);
        }

        var results = new StepResults { Roots = scopeRoots, IncompleteScope = incompleteScope };
        var bufferCap = run.Options.MaxBufferedHitsPerStep;
        var cappedByMaxHits = false;

        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await foreach (var hit in searcher.SearchAsync(request, stepCts.Token).ConfigureAwait(false))
            {
                results.HitCount++;
                results.AddFile(hit.Path);
                if (bufferCap <= 0 || results.Hits.Count < bufferCap)
                    results.Hits.Add(hit);
                else
                    results.Truncated = true;

                run.Observer.OnHit(step, hit);

                if (step.MaxHits > 0 && results.HitCount >= step.MaxHits)
                {
                    cappedByMaxHits = true;
                    stepCts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Reached the step's maxHits cap; the partial results are the results.
        }

        run.RecordSearchResults(step.Id, results);
        results.IncompleteScope |= cappedByMaxHits;
        var detail = $"{results.HitCount} hit(s) in {results.Files.Count} file(s){scopeNote}"
            + (cappedByMaxHits ? $", stopped at maxHits {step.MaxHits}" : "");
        // Unreadable files don't produce hits; surface them so conditions and
        // file operations downstream aren't trusted blindly. The indexed path
        // reports no per-file progress, so this only covers live scans.
        if (progress is { } finalProgress)
        {
            if (finalProgress.FilesFailed > 0)
            {
                results.IncompleteScope = true;
                detail += $", {finalProgress.FilesFailed} file(s) failed to read";
            }
            if (finalProgress.FilesSkipped > 0)
                detail += $", {finalProgress.FilesSkipped} file(s) skipped";
        }

        Record(run, step, succeeded: true, detail, results.HitCount, results.Files.Count);
        return Flow.Continue;
    }

    private async Task<Flow> ExecuteReplacementAsync(ReplacementStep step, RunState run, CancellationToken ct)
    {
        if (_replacement is null) return Fail(run, step, "Replacement service is unavailable.");
        IReadOnlyList<string> roots = step.Roots.Select(run.Substitute).ToArray();
        IReadOnlyList<string>? sourcePaths = null;
        if (step.ScopeStepId is not null)
        {
            if (!run.TryGetResults(step.ScopeStepId, out var source, out var error)) return Fail(run, step, error);
            if (source.IncompleteScope) return Fail(run, step, "Source search was stopped by a limit or failed to read files. Use a fresh folder scope or a complete search.");
            if (source.Files.Count == 0) { Record(run, step, true, "No source items to replace."); return Flow.Continue; }
            roots = source.Roots; sourcePaths = source.Files.ToArray();
        }
        var walker = step.Filters.ToWalkerOptions(step.Filters.IncludeGlobs.Select(run.Substitute).ToArray(), step.Filters.ExcludeGlobs.Select(run.Substitute).ToArray());
        var request = new ReplacementRequest(roots, walker, run.Substitute(step.Find), run.Substitute(step.ReplaceWith), step.Target,
            step.UseRegex, step.MatchCase, step.NameTarget, step.IncludeExtensions, step.IncludeFormulas,
            run.Options.ProtectedReplacementRoots, step.AdditionalTextExtensions.Select(extension => extension.StartsWith('.') ? extension : "." + extension).ToHashSet(StringComparer.OrdinalIgnoreCase),
            sourcePaths, run.RecoveryGroupId, run.Workflow.Name);
        var plan = await _replacement.PreviewAsync(request, ct).ConfigureAwait(false);
        var ready = plan.Items.Where(item => item.CanApply).ToArray();
        var changes = ready.Sum(item => (long)item.ChangeCount);
        var skipped = plan.Items.Count - ready.Length;
        run.Observer.OnLog($"Replacement preview: {ready.Length} items, {changes} changes, {skipped} skipped.");
        foreach (var item in plan.Items.Take(20))
        {
            run.Observer.OnLog(item.Path + (item.SkipReason is { } reason ? ": skipped — " + reason : $": {item.ChangeCount} changes"));
            foreach (var change in item.Changes.Take(3))
                run.Observer.OnLog($"  {change.Location}: {PreviewText(change.Before)} → {PreviewText(change.After)}");
        }
        if (run.Options.DryRun || ready.Length == 0)
        {
            Record(run, step, true, $"{(run.Options.DryRun ? "[dry run] " : "")}{changes} changes in {ready.Length} items; {skipped} skipped", fileCount: ready.Length, changeCount: changes);
            return Flow.Continue;
        }
        IReadOnlySet<string>? selected;
        if (run.Interaction is IWorkflowReplacementInteraction review)
            selected = await review.ReviewReplacementAsync(plan, ct).ConfigureAwait(false);
        else
        {
            if (!run.Options.AllowReplacementApply) return Fail(run, step, "Replacement requires interactive item review or explicit authorization (--apply-replacements in the CLI).");
            var confirmed = await ConfirmAsync(run, new WorkflowConfirmation($"Replace {changes} matches in {ready.Length} items", "Originals will be backed up. Review the preview before applying.",
                ready.Take(20).Select(item => item.Path + (item.NewPath is null ? "" : " → " + item.NewPath)).ToArray()), ct).ConfigureAwait(false);
            selected = confirmed ? ready.Select(item => item.Id).ToHashSet() : null;
        }
        ct.ThrowIfCancellationRequested();
        if (selected is null || selected.Count == 0) { Record(run, step, true, "Replacement skipped — no items approved."); return Flow.Continue; }
        var result = await _replacement.ApplyAsync(plan, selected, ct).ConfigureAwait(false);
        if (result.BatchId is not null) run.ReplacementBatches.Add(result);
        foreach (var outcome in result.Outcomes)
        {
            run.Observer.OnLog($"{outcome.Path}: {outcome.Message}");
            if (outcome.Succeeded && outcome.NewPath is not null) run.RemapPaths(outcome.Path, outcome.NewPath, outcome.IsDirectory);
        }
        var appliedChanges = ready.Where(item => result.Outcomes.Any(outcome => outcome.Succeeded && outcome.Path == item.Path)).Sum(item => (long)item.ChangeCount);
        var failures = result.Outcomes.Count(outcome => !outcome.Succeeded);
        var detail = $"{appliedChanges} changes in {result.SucceededCount} items; {skipped} preview skips; {failures} apply failures; batch {result.BatchId ?? "none"}";
        Record(run, step, failures == 0, detail, fileCount: result.SucceededCount, changeCount: appliedChanges, replacementBatch: result);
        ct.ThrowIfCancellationRequested();
        if (failures > 0) { run.FailMessage = $"Replacement step '{step.DisplayName}' partially failed; completed items remain undoable."; return Flow.Fail; }
        return Flow.Continue;
    }

    private static string PreviewText(string text) => (text.Length > 200 ? text[..200] + "…" : text).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private async Task<Flow> ExecuteIfAsync(IfStep step, RunState run, int depth, CancellationToken ct)
    {
        if (!run.TryResolveCondition(step.Condition, out var actual, out var error))
            return Fail(run, step, error);

        var satisfied = step.Condition.IsSatisfiedBy(actual);
        Record(run, step, succeeded: true, $"{step.Condition.Describe()} → {(satisfied ? "true" : "false")} (actual: {actual})");

        var branch = satisfied ? step.Then : step.Else;
        return await ExecuteStepsAsync(branch, run, depth + 1, ct).ConfigureAwait(false);
    }

    private async Task<Flow> ExecuteRetryAsync(RetryStep step, RunState run, int depth, CancellationToken ct)
    {
        var iterations = step.ParameterSets.Count > 0
            ? Math.Min(step.ParameterSets.Count, step.MaxIterations)
            : step.MaxIterations;

        var conditionMet = false;
        var iterationsRun = 0;

        for (var i = 1; i <= iterations && !conditionMet; i++)
        {
            ct.ThrowIfCancellationRequested();
            iterationsRun = i;
            run.Observer.OnLog($"Retry '{step.DisplayName}': iteration {i} of {iterations}.");

            var scope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["iteration"] = i.ToString(CultureInfo.InvariantCulture),
            };
            if (step.ParameterSets.Count > 0)
            {
                foreach (var (key, value) in step.ParameterSets[i - 1])
                    scope[key] = value;
            }

            var flow = await run.WithScopeAsync(scope,
                () => ExecuteStepsAsync(step.Body, run, depth + 1, ct)).ConfigureAwait(false);
            if (flow != Flow.Continue)
                return flow;

            if (!run.TryResolveCondition(step.Until, out var actual, out var error))
                return Fail(run, step, error);
            conditionMet = step.Until.IsSatisfiedBy(actual);
        }

        Record(run, step, succeeded: true, conditionMet
            ? $"{step.Until.Describe()} met after {iterationsRun} iteration(s)"
            : $"{step.Until.Describe()} not met after {iterationsRun} iteration(s)");
        return Flow.Continue;
    }

    private async Task<Flow> ExecuteForEachAsync(ForEachStep step, RunState run, int depth, CancellationToken ct)
    {
        if (!run.TryGetResults(step.SourceStepId, out var source, out var error))
            return Fail(run, step, error);

        var files = source.Files;
        var count = step.MaxItems > 0 ? Math.Min(step.MaxItems, files.Count) : files.Count;

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var scope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["file"] = file,
                ["fileName"] = Path.GetFileName(file),
                ["directory"] = Path.GetDirectoryName(file) ?? "",
            };

            var flow = await run.WithScopeAsync(scope,
                () => ExecuteStepsAsync(step.Body, run, depth + 1, ct)).ConfigureAwait(false);
            if (flow != Flow.Continue)
                return flow;
        }

        Record(run, step, succeeded: true,
            $"iterated {count} file(s)" + (count < files.Count ? $" of {files.Count} (maxItems)" : ""));
        return Flow.Continue;
    }

    private async Task<Flow> ExecuteExportAsync(ExportStep step, RunState run, CancellationToken ct)
    {
        long hitCount;
        List<Hit> hits;
        List<string> files;
        bool truncated;

        if (step.SourceStepId is not null)
        {
            if (!run.TryGetResults(step.SourceStepId, out var source, out var error))
                return Fail(run, step, error);
            hitCount = source.HitCount;
            hits = source.Hits;
            files = source.Files;
            truncated = source.Truncated;
        }
        else
        {
            hits = new List<Hit>();
            files = new List<string>();
            hitCount = 0;
            truncated = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in run.ExecutedSearchIds)
            {
                var source = run.Results[id];
                hitCount += source.HitCount;
                hits.AddRange(source.Hits);
                truncated |= source.Truncated;
                foreach (var file in source.Files)
                {
                    if (seen.Add(file))
                        files.Add(file);
                }
            }
        }

        var path = Path.GetFullPath(run.Substitute(step.Path));
        var targetExists = File.Exists(path);
        if (targetExists && !step.Overwrite)
            return Fail(run, step, $"'{path}' already exists and overwrite is off.");

        if (run.Options.DryRun)
        {
            run.Observer.OnLog($"[dry run] Would write {hits.Count} hit(s) to '{path}'.");
            Record(run, step, succeeded: true,
                $"[dry run] would write {hits.Count} hit(s) to '{path}'", hitCount, files.Count);
            return Flow.Continue;
        }

        if (targetExists)
        {
            var confirmed = await ConfirmAsync(run, new WorkflowConfirmation(
                "Overwrite export file",
                $"'{path}' already exists and will be replaced.",
                new[] { path }), ct).ConfigureAwait(false);
            if (!confirmed)
            {
                Record(run, step, succeeded: true, "skipped — declined by user");
                return Flow.Continue;
            }
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var content = step.Format switch
        {
            ExportFormat.Csv => RenderCsv(hits),
            ExportFormat.Markdown => RenderMarkdown(run.Workflow.Name, hits, files, hitCount, truncated),
            _ => RenderJson(run.Workflow.Name, step.SourceStepId, hits, files, hitCount, truncated),
        };

        // Write-then-move (like the stores) so cancellation or an I/O error
        // mid-write can't leave a truncated export or destroy a previous one.
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }

        Record(run, step, succeeded: true,
            $"wrote {hits.Count} hit(s) from {files.Count} file(s) to '{path}'"
            + (truncated || hits.Count < hitCount ? $" ({hitCount} total hits; buffer capped)" : ""),
            hitCount, files.Count);
        return Flow.Continue;
    }

    private async Task<Flow> ExecuteFileOperationAsync(FileOperationStep step, RunState run, CancellationToken ct)
    {
        if (!run.TryGetResults(step.SourceStepId, out var source, out var error))
            return Fail(run, step, error);

        var verb = step.Operation == FileOperationKind.Move ? "Move" : "Copy";
        var destination = Path.GetFullPath(run.Substitute(step.DestinationDirectory));
        var files = source.Files;

        if (files.Count == 0)
        {
            Record(run, step, succeeded: true, $"no files to {verb.ToLowerInvariant()}");
            return Flow.Continue;
        }

        if (run.Options.DryRun)
        {
            run.Observer.OnLog($"[dry run] Would {verb.ToLowerInvariant()} {files.Count} file(s) to '{destination}'.");
            Record(run, step, succeeded: true, $"[dry run] would {verb.ToLowerInvariant()} {files.Count} file(s) to '{destination}'");
            return Flow.Continue;
        }

        var confirmed = await ConfirmAsync(run, new WorkflowConfirmation(
            $"{verb} {files.Count} file(s)",
            $"Destination: {destination}",
            Sample(files)), ct).ConfigureAwait(false);
        if (!confirmed)
        {
            Record(run, step, succeeded: true, "skipped — declined by user");
            return Flow.Continue;
        }

        Directory.CreateDirectory(destination);
        var pastTense = step.Operation == FileOperationKind.Move ? "moved" : "copied";
        int done = 0, skipped = 0, failed = 0;

        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var target = Path.Combine(destination, Path.GetFileName(file));
                    if (string.Equals(Path.GetFullPath(file), target, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    var overwrite = false;
                    if (File.Exists(target))
                    {
                        switch (step.Collision)
                        {
                            case FileCollisionPolicy.Skip:
                                skipped++;
                                continue;
                            case FileCollisionPolicy.Overwrite:
                                overwrite = true;
                                break;
                            default:
                                target = NextAvailableName(target);
                                break;
                        }
                    }

                    if (step.Operation == FileOperationKind.Move)
                        File.Move(file, target, overwrite);
                    else
                        File.Copy(file, target, overwrite);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    run.Observer.OnLog($"{verb} failed for '{file}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Files already moved/copied must not vanish from the record.
            Record(run, step, succeeded: false,
                $"cancelled — {pastTense} {done} of {files.Count} file(s) to '{destination}' before stopping",
                fileCount: done);
            throw;
        }

        var detail = $"{pastTense} {done} of {files.Count} file(s) to '{destination}'"
            + (skipped > 0 ? $", {skipped} skipped" : "")
            + (failed > 0 ? $", {failed} failed" : "");
        Record(run, step, succeeded: failed == 0, detail, fileCount: done);
        return Flow.Continue;
    }

    private async Task<Flow> ExecuteRunProgramAsync(RunProgramStep step, RunState run, CancellationToken ct)
    {
        var program = run.Substitute(step.Program);

        List<string> commandArguments;
        if (step.PerFile)
        {
            if (!run.TryGetResults(step.SourceStepId, out var source, out var error))
                return Fail(run, step, error);

            var files = source.Files;
            var count = step.MaxFiles > 0 ? Math.Min(step.MaxFiles, files.Count) : files.Count;
            commandArguments = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var file = files[i];
                // Quotes are invalid in Windows paths, so stripping them never
                // changes a real path — it only defuses a crafted value that
                // could otherwise escape the author's quoting in Arguments.
                var scope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["file"] = StripQuotes(file),
                    ["fileName"] = StripQuotes(Path.GetFileName(file)),
                    ["directory"] = StripQuotes(Path.GetDirectoryName(file) ?? ""),
                };
                commandArguments.Add(run.SubstituteWith(step.Arguments, scope));
            }
            if (count < files.Count)
                run.Observer.OnLog($"Run-program step '{step.DisplayName}': limited to {count} of {files.Count} file(s) (maxFiles).");
        }
        else
        {
            commandArguments = new List<string> { run.Substitute(step.Arguments) };
        }

        if (commandArguments.Count == 0)
        {
            Record(run, step, succeeded: true, "no files to run the program for");
            return Flow.Continue;
        }

        if (run.Options.DryRun)
        {
            foreach (var args in Sample(commandArguments))
                run.Observer.OnLog($"[dry run] Would run: {program} {args}");
            Record(run, step, succeeded: true, $"[dry run] would launch {commandArguments.Count} process(es)");
            return Flow.Continue;
        }

        var confirmed = await ConfirmAsync(run, new WorkflowConfirmation(
            commandArguments.Count == 1 ? "Run program" : $"Run program for {commandArguments.Count} file(s)",
            program,
            Sample(commandArguments.Select(a => $"{program} {a}").ToList())), ct).ConfigureAwait(false);
        if (!confirmed)
        {
            Record(run, step, succeeded: true, "skipped — declined by user");
            return Flow.Continue;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(step.WorkingDirectory)
            ? ""
            : run.Substitute(step.WorkingDirectory!);

        int launched = 0, failures = 0, nonZeroExits = 0, timeouts = 0;
        try
        {
            foreach (var args in commandArguments)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = program,
                        Arguments = args,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (process is null)
                    {
                        failures++;
                        run.Observer.OnLog($"Could not start '{program}'.");
                        continue;
                    }

                    launched++;
                    if (step.WaitForExit)
                    {
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        if (step.TimeoutSeconds > 0)
                            waitCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                        try
                        {
                            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                            if (process.ExitCode != 0)
                            {
                                nonZeroExits++;
                                run.Observer.OnLog($"'{program} {args}' exited with code {process.ExitCode}.");
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            timeouts++;
                            run.Observer.OnLog($"'{program} {args}' still running after {step.TimeoutSeconds}s; not waiting further.");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    run.Observer.OnLog($"Could not start '{program} {args}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Record(run, step, succeeded: false,
                $"cancelled — launched {launched} of {commandArguments.Count} process(es) before stopping");
            throw;
        }

        var detail = $"launched {launched} process(es)"
            + (nonZeroExits > 0 ? $", {nonZeroExits} non-zero exit code(s)" : "")
            + (timeouts > 0 ? $", {timeouts} timed out" : "")
            + (failures > 0 ? $", {failures} failed to start" : "");
        Record(run, step, succeeded: failures == 0 && nonZeroExits == 0 && timeouts == 0, detail);
        return Flow.Continue;
    }

    private static Flow ExecuteStop(StopStep step, RunState run)
    {
        run.StopSucceeded = step.Succeeded;
        run.StopMessage = step.Message is null ? null : run.Substitute(step.Message);
        Record(run, step, succeeded: true,
            (step.Succeeded ? "stop (success)" : "stop (failure)")
            + (run.StopMessage is null ? "" : $": {run.StopMessage}"));
        return Flow.Stop;
    }

    private static async Task<bool> ConfirmAsync(RunState run, WorkflowConfirmation confirmation, CancellationToken ct)
    {
        // No interaction host means headless consent: the user authored the
        // step, so unattended runs proceed.
        if (run.Interaction is null)
            return true;

        var confirmed = await run.Interaction.ConfirmAsync(confirmation, ct).ConfigureAwait(false);

        // A "decline" produced by cancelling the run must surface as
        // cancellation, not be recorded as a user choice.
        ct.ThrowIfCancellationRequested();
        return confirmed;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string StripQuotes(string value) =>
        value.Replace("\"", "", StringComparison.Ordinal);

    private static Flow Fail(RunState run, WorkflowStep step, string message)
    {
        Record(run, step, succeeded: false, message);
        run.FailMessage = $"Step '{step.DisplayName}' ({step.Kind}): {message}";
        return Flow.Fail;
    }

    private static void Record(
        RunState run, WorkflowStep step, bool succeeded, string? detail, long hitCount = 0, int fileCount = 0,
        long changeCount = 0, ReplacementBatchResult? replacementBatch = null)
    {
        var outcome = new WorkflowStepOutcome(step.Id, step.Kind, step.DisplayName, succeeded, detail, hitCount, fileCount, changeCount, replacementBatch);
        run.Outcomes.Add(outcome);
        run.Observer.OnStepCompleted(outcome);
    }

    private static IReadOnlyList<string> Sample(List<string> items)
    {
        const int max = 20;
        if (items.Count <= max)
            return items;
        return items.Take(max).Append($"… and {items.Count - max} more").ToArray();
    }

    private static string NextAvailableName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static string RenderJson(
        string workflowName, string? sourceStep, List<Hit> hits, List<string> files, long hitCount, bool truncated)
    {
        var document = new ExportDocument(
            workflowName,
            sourceStep,
            DateTime.UtcNow,
            hitCount,
            files.Count,
            truncated || hits.Count < hitCount,
            hits.Select(h => new ExportHit(h.Path, h.LineNumber, h.LineContent)).ToArray());
        return JsonSerializer.Serialize(document, WorkflowJson.Options);
    }

    private static string RenderCsv(List<Hit> hits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("path,lineNumber,line");
        foreach (var hit in hits)
        {
            sb.Append(CsvField(hit.Path)).Append(',');
            sb.Append(hit.LineNumber.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.AppendLine(CsvField(hit.LineContent));
        }
        return sb.ToString();
    }

    private static readonly SearchValues<char> s_csvSpecialChars = SearchValues.Create(",\"\n\r");

    private static string CsvField(string value)
    {
        if (!value.AsSpan().ContainsAny(s_csvSpecialChars))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string RenderMarkdown(
        string workflowName, List<Hit> hits, List<string> files, long hitCount, bool truncated)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {workflowName} — search results");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.Append(CultureInfo.InvariantCulture, $"- Hits: {hitCount} in {files.Count} file(s)");
        if (truncated || hits.Count < hitCount)
            sb.Append(CultureInfo.InvariantCulture, $" ({hits.Count} listed; buffer capped)");
        sb.AppendLine();

        foreach (var group in hits.GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}");
            sb.AppendLine();
            foreach (var hit in group)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- L{hit.LineNumber}: {hit.LineContent.Trim()}");
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\$\{([A-Za-z0-9_][A-Za-z0-9_.\-]*)\}")]
    private static partial Regex VariablePattern();

    private enum Flow
    {
        Continue,
        Stop,
        Fail,
    }

    /// <summary>Mutable per-run state: results by step id, variables, outcomes.</summary>
    private sealed class RunState
    {
        public RunState(
            WorkflowDefinition workflow,
            WorkflowRunOptions options,
            IWorkflowObserver observer,
            IWorkflowInteraction? interaction)
        {
            Workflow = workflow;
            Options = options;
            Observer = observer;
            Interaction = interaction;
        }

        public WorkflowDefinition Workflow { get; }
        public WorkflowRunOptions Options { get; }
        public IWorkflowObserver Observer { get; }
        public IWorkflowInteraction? Interaction { get; }

        public Dictionary<string, StepResults> Results { get; } = new(StringComparer.Ordinal);
        public List<string> ExecutedSearchIds { get; } = new();
        public List<WorkflowStepOutcome> Outcomes { get; } = new();
        public string RecoveryGroupId { get; } = Guid.NewGuid().ToString("N");
        public List<ReplacementBatchResult> ReplacementBatches { get; } = [];
        public void RemapPaths(string oldPath, string newPath, bool directory)
        {
            string Map(string path) => path.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ? newPath :
                directory && path.StartsWith(oldPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? newPath.TrimEnd('\\', '/') + path[oldPath.TrimEnd('\\', '/').Length..] : path;
            foreach (var result in Results.Values)
            {
                for (var i = 0; i < result.Files.Count; i++) result.Files[i] = Map(result.Files[i]);
                for (var i = 0; i < result.Hits.Count; i++) result.Hits[i] = result.Hits[i] with { Path = Map(result.Hits[i].Path) };
            }
            // Update suspended scopes too: a rename inside a retry body must still
            // be reflected when its enclosing for-each body resumes.
            foreach (var variables in _savedVariableScopes.Append(_variables))
            {
                if (variables.TryGetValue("file", out var file))
                {
                    var mapped = Map(file);
                    variables["file"] = mapped;
                    if (variables.TryGetValue("fileName", out var name) && name == Path.GetFileName(file))
                        variables["fileName"] = Path.GetFileName(mapped);
                }
                if (variables.TryGetValue("directory", out var folder)) variables["directory"] = Map(folder);
            }
        }
        public int StepExecutions;
        public string? LastSearchStepId;
        public bool StopSucceeded = true;
        public string? StopMessage;
        public string? FailMessage;

        private Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Dictionary<string, string>> _savedVariableScopes = [];

        public void RecordSearchResults(string stepId, StepResults results)
        {
            if (!Results.ContainsKey(stepId))
                ExecutedSearchIds.Add(stepId);
            Results[stepId] = results;
            LastSearchStepId = stepId;
        }

        /// <summary>Resolves a step reference (null = most recent search) to its results.</summary>
        public bool TryGetResults(string? stepId, out StepResults results, out string error)
        {
            var id = stepId ?? LastSearchStepId;
            if (id is null)
            {
                results = new StepResults();
                error = "No search step has run yet.";
                return false;
            }

            if (!Results.TryGetValue(id, out var found))
            {
                results = new StepResults();
                error = $"Search step '{id}' has not run yet.";
                return false;
            }

            results = found;
            error = "";
            return true;
        }

        public bool TryResolveCondition(WorkflowCondition condition, out long actual, out string error)
        {
            if (!TryGetResults(condition.Source, out var results, out error))
            {
                actual = 0;
                return false;
            }

            actual = condition.Metric == ConditionMetric.FileCount ? results.Files.Count : results.HitCount;
            return true;
        }

        /// <summary>Runs <paramref name="body"/> with extra variables layered over the current scope.</summary>
        public async Task<Flow> WithScopeAsync(IReadOnlyDictionary<string, string> scope, Func<Task<Flow>> body)
        {
            var saved = _variables;
            var layered = new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in scope)
                layered[key] = value;
            _savedVariableScopes.Add(saved);
            _variables = layered;
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                _savedVariableScopes.RemoveAt(_savedVariableScopes.Count - 1);
                _variables = saved;
            }
        }

        /// <summary>Replaces <c>${name}</c> with the variable's value; unknown names stay literal.</summary>
        public string Substitute(string input) => SubstituteWith(input, null);

        public string SubstituteWith(string input, Dictionary<string, string>? extra)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            return VariablePattern().Replace(input, match =>
            {
                var name = match.Groups[1].Value;
                if (extra is not null && extra.TryGetValue(name, out var extraValue))
                    return extraValue;
                return _variables.TryGetValue(name, out var value) ? value : match.Value;
            });
        }
    }

    /// <summary>Buffered output of one search step execution.</summary>
    private sealed class StepResults
    {
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Total hits counted, including ones past the buffer cap.</summary>
        public long HitCount;

        /// <summary>True once hits stopped being buffered.</summary>
        public bool Truncated;
        public bool IncompleteScope;
        public IReadOnlyList<string> Roots { get; init; } = [];

        public List<Hit> Hits { get; } = new();

        /// <summary>Distinct matched files in first-hit order.</summary>
        public List<string> Files { get; } = new();

        public void AddFile(string path)
        {
            if (_seen.Add(path))
                Files.Add(path);
        }
    }

    private sealed record ExportDocument(
        string Workflow,
        string? SourceStep,
        DateTime ExportedUtc,
        long TotalHits,
        int FileCount,
        bool Truncated,
        IReadOnlyList<ExportHit> Hits);

    private sealed record ExportHit(string Path, int LineNumber, string Line);

    private sealed class NullObserver : IWorkflowObserver
    {
        public static NullObserver Instance { get; } = new();

        public void OnStepStarted(WorkflowStep step, int depth) { }
        public void OnStepCompleted(WorkflowStepOutcome outcome) { }
        public void OnHit(SearchStep step, Hit hit) { }
        public void OnLog(string message) { }
    }
}
