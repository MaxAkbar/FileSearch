using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Engine;
using FileSearch.Core.Workflows;
using FileSearch.Gui.Services;

namespace FileSearch.Gui.ViewModels;

/// <summary>One row in the workflow library list (wraps a store summary).</summary>
public sealed class WorkflowLibraryItemViewModel
{
    public WorkflowLibraryItemViewModel(WorkflowSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public WorkflowSummary Summary { get; }

    public string FileName => Summary.FileName;

    public string Name => string.IsNullOrWhiteSpace(Summary.Name) ? Summary.FileName : Summary.Name;

    public string Description => Summary.Description;

    public string? Error => Summary.Error;

    public bool HasError => Summary.Error is not null;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Summary.Description);
}

/// <summary>One streamed search hit shown in the run results list.</summary>
public sealed record WorkflowHitViewModel(string Path, int LineNumber, string LineContent);

/// <summary>
/// Workflow library, step editor and run host for the Workflows window:
/// lists workflows from <see cref="IWorkflowStore"/>, edits the step tree
/// (flattened into an indented list), validates via
/// <see cref="WorkflowValidator"/>, and runs workflows through
/// <see cref="IWorkflowRunner"/> streaming progress into the log and hit
/// lists. Runner callbacks arrive on background threads; they are queued and
/// drained on the UI thread in timed batches (same idiom as
/// <see cref="SearchViewModel"/>).
/// </summary>
public sealed partial class WorkflowsViewModel : ObservableObject, IWorkflowStepHost, IDisposable
{
    /// <summary>Hits kept in the visible results list; counting continues past the cap.</summary>
    public const int MaxDisplayedHits = 5000;

    private const int MaxLogLines = 4000;
    private const int MaxEventsPerDrain = 1000;

    private readonly IWorkflowStore _store;
    private readonly IWorkflowRunner _runner;
    private readonly IFileLauncher _fileLauncher;
    private readonly IFolderPicker _folderPicker;

    private CancellationTokenSource? _runCts;
    private bool _isLoadingEditor;
    private bool _suppressSelectionLoad;
    private bool _logTruncationNoted;
    private string? _currentFileName;

    public WorkflowsViewModel(
        IWorkflowStore store,
        IWorkflowRunner runner,
        IFileLauncher fileLauncher,
        IFolderPicker folderPicker)
    {
        _store = store;
        _runner = runner;
        _fileLauncher = fileLauncher;
        _folderPicker = folderPicker;
    }

    // ----- library -----

    public ObservableCollection<WorkflowLibraryItemViewModel> Library { get; } = new();

    [ObservableProperty] private WorkflowLibraryItemViewModel? _selectedLibraryItem;

    /// <summary>
    /// Called by the window when it opens: refreshes the library and, on the
    /// first open, loads the first workflow (or starts a new one).
    /// </summary>
    public void OnOpened()
    {
        RefreshLibrary(_currentFileName);
        if (StepRows.Count > 0 || IsDirty)
            return;

        if (Library.Count > 0)
            SelectedLibraryItem = Library[0];
        else
            NewWorkflow();
    }

    public void RefreshLibrary(string? selectFileName = null)
    {
        _suppressSelectionLoad = true;
        try
        {
            Library.Clear();
            foreach (var summary in _store.List())
                Library.Add(new WorkflowLibraryItemViewModel(summary));

            SelectedLibraryItem = selectFileName is null
                ? null
                : Library.FirstOrDefault(item =>
                    string.Equals(item.FileName, selectFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not read the workflow library: {ex.Message}";
        }
        finally
        {
            _suppressSelectionLoad = false;
        }
    }

    [RelayCommand]
    private void NewWorkflow()
    {
        if (!ConfirmDiscardEdits())
            return;

        _suppressSelectionLoad = true;
        try
        {
            SelectedLibraryItem = null;
        }
        finally
        {
            _suppressSelectionLoad = false;
        }

        LoadDefinition(new WorkflowDefinition
        {
            Name = "New workflow",
            Steps = new WorkflowStep[] { new SearchStep { Id = "search-1" } },
        }, fileName: null);
        IsDirty = true;
        StatusText = "New workflow — name it, edit the steps, then Save.";
    }

    [RelayCommand(CanExecute = nameof(HasLibrarySelection))]
    private void DuplicateWorkflow()
    {
        if (SelectedLibraryItem is not { } item)
            return;

        var workflow = _store.TryLoad(item.FileName, out var error);
        if (workflow is null)
        {
            StatusText = error ?? $"Could not load {item.FileName}.";
            return;
        }

        var baseName = string.IsNullOrWhiteSpace(workflow.Name)
            ? Path.GetFileNameWithoutExtension(item.FileName)
            : workflow.Name;
        var copyName = $"{baseName} (copy)";
        for (var n = 2; File.Exists(_store.GetFullPath(JsonWorkflowStore.ToFileName(copyName))); n++)
            copyName = $"{baseName} (copy {n})";

        try
        {
            var copy = workflow with { Name = copyName };
            var fileName = _store.Save(copy);
            RefreshLibrary(fileName);
            LoadDefinition(copy, fileName);
            StatusText = $"Duplicated to {fileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not duplicate: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasLibrarySelection))]
    private void DeleteWorkflow()
    {
        if (SelectedLibraryItem is not { } item)
            return;

        try
        {
            _store.Delete(item.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not delete: {ex.Message}";
            return;
        }

        if (string.Equals(_currentFileName, item.FileName, StringComparison.OrdinalIgnoreCase))
        {
            // The editor keeps the steps as unsaved content.
            _currentFileName = null;
            IsDirty = true;
        }

        RefreshLibrary(_currentFileName);
        StatusText = $"Deleted {item.FileName}.";
    }

    [RelayCommand]
    private void OpenWorkflowsFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            _fileLauncher.Open(_store.DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open the workflows folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveWorkflow()
    {
        var workflow = BuildDefinition();
        Revalidate();
        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            StatusText = "Give the workflow a name before saving.";
            return;
        }

        try
        {
            // A new workflow must not silently overwrite an existing file its
            // name happens to slug to; pick a free variant instead. Renames of
            // an already-saved workflow keep using the original file name.
            _currentFileName = _store.Save(workflow, _currentFileName ?? ToAvailableFileName(workflow.Name));
            IsDirty = false;
            RefreshLibrary(_currentFileName);
            StatusText = $"Saved {_currentFileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText = $"Could not save: {ex.Message}";
        }
    }

    private string ToAvailableFileName(string workflowName)
    {
        var fileName = JsonWorkflowStore.ToFileName(workflowName);
        if (!File.Exists(_store.GetFullPath(fileName)))
            return fileName;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName}-{n}.json";
            if (!File.Exists(_store.GetFullPath(candidate)))
                return candidate;
        }
    }

    private bool HasLibrarySelection() => SelectedLibraryItem is not null;

    /// <summary>
    /// Asks the user to confirm discarding unsaved edits (argument: the
    /// workflow's display name); supplied by the Workflows window while it is
    /// open. Null (headless) discards without prompting.
    /// </summary>
    public Func<string, bool>? ConfirmDiscard { get; set; }

    private bool ConfirmDiscardEdits()
    {
        if (!IsDirty)
            return true;

        var name = string.IsNullOrWhiteSpace(WorkflowName) ? "(untitled)" : WorkflowName.Trim();
        return ConfirmDiscard?.Invoke(name) ?? true;
    }

    partial void OnSelectedLibraryItemChanged(
        WorkflowLibraryItemViewModel? oldValue, WorkflowLibraryItemViewModel? newValue)
    {
        DuplicateWorkflowCommand.NotifyCanExecuteChanged();
        DeleteWorkflowCommand.NotifyCanExecuteChanged();
        if (_suppressSelectionLoad || newValue is null)
            return;

        if (!ConfirmDiscardEdits())
        {
            RestoreSelection(oldValue);
            return;
        }

        var workflow = _store.TryLoad(newValue.FileName, out var error);
        if (workflow is null)
        {
            StatusText = error ?? $"Could not load {newValue.FileName}.";
            return;
        }

        LoadDefinition(workflow, newValue.FileName);
        StatusText = $"Loaded {newValue.FileName}.";
    }

    /// <summary>
    /// Puts the previous library selection back after a declined discard.
    /// The revert is posted when a synchronization context is available:
    /// the list box is still delivering the selection being undone, and WPF
    /// ignores a source change made re-entrantly inside that update.
    /// </summary>
    private void RestoreSelection(WorkflowLibraryItemViewModel? previous)
    {
        void Restore()
        {
            _suppressSelectionLoad = true;
            try
            {
                SelectedLibraryItem = previous;
            }
            finally
            {
                _suppressSelectionLoad = false;
            }
        }

        if (SynchronizationContext.Current is { } context)
            context.Post(_ => Restore(), null);
        else
            Restore();
    }

    // ----- editor -----

    [ObservableProperty] private string _workflowName = "";
    [ObservableProperty] private string _workflowDescription = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private WorkflowStepViewModel? _selectedStep;

    /// <summary>Nested step tree (the single source of truth for the editor).</summary>
    public ObservableCollection<WorkflowStepViewModel> RootSteps { get; } = new();

    /// <summary>The tree flattened into an indented list for display.</summary>
    public ObservableCollection<WorkflowStepViewModel> StepRows { get; } = new();

    public ObservableCollection<string> ValidationErrors { get; } = new();

    public bool HasValidationErrors => ValidationErrors.Count > 0;

    public bool HasSelectedStep => SelectedStep is not null;

    /// <summary>Options for a search step's scope dropdown.</summary>
    public ObservableCollection<string> ScopeStepOptions { get; } = new() { WorkflowEditorOptions.NoScope };

    /// <summary>Options for "source search step" dropdowns (forEach, fileOperation, runProgram, conditions).</summary>
    public ObservableCollection<string> SourceStepOptions { get; } = new() { WorkflowEditorOptions.LastSearch };

    /// <summary>Options for an export step's source dropdown.</summary>
    public ObservableCollection<string> ExportSourceOptions { get; } = new() { WorkflowEditorOptions.AllSearches };

    [RelayCommand]
    private void AddStep(string kind)
    {
        var step = CreateStep(kind);
        if (SelectedStep is { OwnerList: { } owner } selected)
            owner.Insert(owner.IndexOf(selected) + 1, step);
        else
            RootSteps.Add(step);

        NotifyStructureChanged();
        SelectedStep = step;
    }

    [RelayCommand(CanExecute = nameof(HasStepSelection))]
    private void RemoveStep()
    {
        if (SelectedStep is not { OwnerList: { } owner } step)
            return;

        owner.Remove(step);
        SelectedStep = null;
        NotifyStructureChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMoveStepUp))]
    private void MoveStepUp()
    {
        if (SelectedStep is not { OwnerList: { } owner } step)
            return;

        var index = owner.IndexOf(step);
        if (index <= 0)
            return;

        owner.Move(index, index - 1);
        NotifyStructureChanged();
        SelectedStep = step;
    }

    [RelayCommand(CanExecute = nameof(CanMoveStepDown))]
    private void MoveStepDown()
    {
        if (SelectedStep is not { OwnerList: { } owner } step)
            return;

        var index = owner.IndexOf(step);
        if (index < 0 || index >= owner.Count - 1)
            return;

        owner.Move(index, index + 1);
        NotifyStructureChanged();
        SelectedStep = step;
    }

    private bool HasStepSelection() => SelectedStep is not null;

    private bool CanMoveStepUp() =>
        SelectedStep is { OwnerList: { } owner } step && owner.IndexOf(step) > 0;

    private bool CanMoveStepDown() =>
        SelectedStep is { OwnerList: { } owner } step && owner.IndexOf(step) < owner.Count - 1;

    partial void OnSelectedStepChanged(WorkflowStepViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedStep));
        NotifyStepCommands();
    }

    partial void OnWorkflowNameChanged(string value)
    {
        if (_isLoadingEditor)
            return;
        IsDirty = true;
        Revalidate();
    }

    partial void OnWorkflowDescriptionChanged(string value)
    {
        if (_isLoadingEditor)
            return;
        IsDirty = true;
    }

    // ----- IWorkflowStepHost -----

    public WorkflowStepViewModel CreateStep(string kind) => kind switch
    {
        "search" => new SearchStepViewModel(this, GenerateId("search")),
        "if" => new IfStepViewModel(this, GenerateId("if")),
        "retry" => new RetryStepViewModel(this, GenerateId("retry")),
        "forEach" => new ForEachStepViewModel(this, GenerateId("forEach")),
        "export" => new ExportStepViewModel(this, GenerateId("export")),
        "fileOperation" => new FileOperationStepViewModel(this, GenerateId("fileOperation")),
        "runProgram" => new RunProgramStepViewModel(this, GenerateId("runProgram")),
        "stop" => new StopStepViewModel(this, GenerateId("stop")),
        _ => throw new ArgumentException($"Unknown step kind '{kind}'.", nameof(kind)),
    };

    public void NotifyStructureChanged()
    {
        if (_isLoadingEditor)
            return;
        RefreshStructure();
        IsDirty = true;
    }

    public void NotifyStepEdited(WorkflowStepViewModel step, string? propertyName)
    {
        if (_isLoadingEditor)
            return;

        // Ids feed the step-reference dropdowns and the displayed rows.
        if (propertyName == nameof(WorkflowStepViewModel.Id))
            RefreshStructure();
        else
            Revalidate();
        IsDirty = true;
    }

    public string? PickFolder(string title, string? initialDirectory) =>
        _folderPicker.PickFolder(title, initialDirectory);

    // ----- model <-> editor -----

    internal WorkflowDefinition BuildDefinition() => new()
    {
        Name = WorkflowName.Trim(),
        Description = WorkflowDescription.Trim(),
        Steps = RootSteps.Select(step => step.ToStep()).ToArray(),
    };

    internal void LoadDefinition(WorkflowDefinition workflow, string? fileName)
    {
        _isLoadingEditor = true;
        try
        {
            _currentFileName = fileName;
            WorkflowName = workflow.Name;
            WorkflowDescription = workflow.Description;
            SelectedStep = null;
            RootSteps.Clear();
            foreach (var step in workflow.Steps)
                RootSteps.Add(FromModel(step));
        }
        finally
        {
            _isLoadingEditor = false;
        }

        RefreshStructure();
        IsDirty = false;
    }

    private WorkflowStepViewModel FromModel(WorkflowStep step)
    {
        switch (step)
        {
            case SearchStep search:
                {
                    var viewModel = new SearchStepViewModel(this, search.Id)
                    {
                        Name = search.Name,
                        Query = search.Query,
                        Mode = search.Mode,
                        CaseSensitive = search.CaseSensitive,
                        ScopeStepId = WorkflowEditorOptions.FromStepId(search.ScopeStepId, WorkflowEditorOptions.NoScope),
                        UseIndex = search.UseIndex,
                        IncludeGlobs = string.Join("; ", search.Filters.IncludeGlobs),
                        ExcludeGlobs = string.Join("; ", search.Filters.ExcludeGlobs),
                        UseDefaultExcludedFolders = search.Filters.ExcludeDirectories is null,
                        ExcludeDirectories = search.Filters.ExcludeDirectories is null
                            ? ""
                            : string.Join("; ", search.Filters.ExcludeDirectories),
                        Recursive = search.Filters.Recursive,
                        IncludeHidden = search.Filters.IncludeHidden,
                        MinFileSizeBytes = search.Filters.MinFileSizeBytes,
                        MaxFileSizeBytes = search.Filters.MaxFileSizeBytes,
                        MaxHits = search.MaxHits,
                    };
                    if (search.Filters.ModifiedAfterUtc is { } after)
                    {
                        viewModel.ModifiedAfterEnabled = true;
                        viewModel.ModifiedAfter = after.ToLocalTime();
                    }
                    if (search.Filters.ModifiedBeforeUtc is { } before)
                    {
                        viewModel.ModifiedBeforeEnabled = true;
                        viewModel.ModifiedBefore = before.ToLocalTime();
                    }
                    foreach (var root in search.Roots)
                        viewModel.Roots.Add(root);
                    return viewModel;
                }

            case IfStep ifStep:
                {
                    var viewModel = new IfStepViewModel(this, ifStep.Id) { Name = ifStep.Name };
                    viewModel.Condition.Load(ifStep.Condition);
                    foreach (var child in ifStep.Then)
                        viewModel.ThenSteps.Add(FromModel(child));
                    foreach (var child in ifStep.Else)
                        viewModel.ElseSteps.Add(FromModel(child));
                    return viewModel;
                }

            case RetryStep retry:
                {
                    var viewModel = new RetryStepViewModel(this, retry.Id)
                    {
                        Name = retry.Name,
                        MaxIterations = retry.MaxIterations,
                    };
                    viewModel.Until.Load(retry.Until);
                    viewModel.LoadParameterSets(retry.ParameterSets);
                    foreach (var child in retry.Body)
                        viewModel.BodySteps.Add(FromModel(child));
                    return viewModel;
                }

            case ForEachStep forEach:
                {
                    var viewModel = new ForEachStepViewModel(this, forEach.Id)
                    {
                        Name = forEach.Name,
                        SourceStepId = WorkflowEditorOptions.FromStepId(forEach.SourceStepId, WorkflowEditorOptions.LastSearch),
                        MaxItems = forEach.MaxItems,
                    };
                    foreach (var child in forEach.Body)
                        viewModel.BodySteps.Add(FromModel(child));
                    return viewModel;
                }

            case ExportStep export:
                return new ExportStepViewModel(this, export.Id)
                {
                    Name = export.Name,
                    SourceStepId = WorkflowEditorOptions.FromStepId(export.SourceStepId, WorkflowEditorOptions.AllSearches),
                    Format = export.Format,
                    Path = export.Path,
                    Overwrite = export.Overwrite,
                };

            case FileOperationStep operation:
                return new FileOperationStepViewModel(this, operation.Id)
                {
                    Name = operation.Name,
                    Operation = operation.Operation,
                    SourceStepId = WorkflowEditorOptions.FromStepId(operation.SourceStepId, WorkflowEditorOptions.LastSearch),
                    DestinationDirectory = operation.DestinationDirectory,
                    Collision = operation.Collision,
                };

            case RunProgramStep program:
                return new RunProgramStepViewModel(this, program.Id)
                {
                    Name = program.Name,
                    Program = program.Program,
                    Arguments = program.Arguments,
                    PerFile = program.PerFile,
                    SourceStepId = WorkflowEditorOptions.FromStepId(program.SourceStepId, WorkflowEditorOptions.LastSearch),
                    WorkingDirectory = program.WorkingDirectory ?? "",
                    WaitForExit = program.WaitForExit,
                    TimeoutSeconds = program.TimeoutSeconds,
                    MaxFiles = program.MaxFiles,
                };

            case StopStep stop:
                return new StopStepViewModel(this, stop.Id)
                {
                    Name = stop.Name,
                    Succeeded = stop.Succeeded,
                    Message = stop.Message ?? "",
                };

            default:
                throw new InvalidOperationException($"Unknown step kind '{step.Kind}'.");
        }
    }

    /// <summary>Re-flattens the tree, refreshes id dropdowns and re-validates.</summary>
    private void RefreshStructure()
    {
        var selected = SelectedStep;
        StepRows.Clear();
        AppendRows(RootSteps, depth: 0, branchTag: "");
        if (selected is not null && StepRows.Contains(selected))
            SelectedStep = selected;

        // Rebuilding the option lists momentarily empties them, and the
        // editable ComboBoxes bound to them push that emptiness straight back
        // into the step view models through their two-way Text bindings.
        // Snapshot every stored reference first and restore whatever the
        // rebuild clobbered.
        var restoreReferences = CaptureReferenceRestores();

        var searchIds = StepRows.OfType<SearchStepViewModel>().Select(s => s.Id).ToArray();
        ReplaceOptions(ScopeStepOptions, WorkflowEditorOptions.NoScope, searchIds);
        ReplaceOptions(SourceStepOptions, WorkflowEditorOptions.LastSearch, searchIds);
        ReplaceOptions(ExportSourceOptions, WorkflowEditorOptions.AllSearches, searchIds);

        foreach (var restore in restoreReferences)
            restore();

        Revalidate();
        NotifyStepCommands();
    }

    /// <summary>
    /// One restore action per step-reference value bound to an editable
    /// dropdown (scope, source, condition source). Invoking it puts the value
    /// captured before the option rebuild back; setters no-op when nothing
    /// was clobbered.
    /// </summary>
    private List<Action> CaptureReferenceRestores()
    {
        var restores = new List<Action>();
        foreach (var step in EnumerateSteps(RootSteps))
        {
            switch (step)
            {
                case SearchStepViewModel search:
                    var scope = search.ScopeStepId;
                    restores.Add(() => search.ScopeStepId = scope);
                    break;

                case IfStepViewModel ifStep:
                    var conditionSource = ifStep.Condition.Source;
                    restores.Add(() => ifStep.Condition.Source = conditionSource);
                    break;

                case RetryStepViewModel retry:
                    var untilSource = retry.Until.Source;
                    restores.Add(() => retry.Until.Source = untilSource);
                    break;

                case ForEachStepViewModel forEach:
                    var forEachSource = forEach.SourceStepId;
                    restores.Add(() => forEach.SourceStepId = forEachSource);
                    break;

                case ExportStepViewModel export:
                    var exportSource = export.SourceStepId;
                    restores.Add(() => export.SourceStepId = exportSource);
                    break;

                case FileOperationStepViewModel operation:
                    var operationSource = operation.SourceStepId;
                    restores.Add(() => operation.SourceStepId = operationSource);
                    break;

                case RunProgramStepViewModel program:
                    var programSource = program.SourceStepId;
                    restores.Add(() => program.SourceStepId = programSource);
                    break;
            }
        }

        return restores;
    }

    private void AppendRows(ObservableCollection<WorkflowStepViewModel> steps, int depth, string branchTag)
    {
        foreach (var step in steps)
        {
            step.OwnerList = steps;
            step.Depth = depth;
            step.BranchTag = branchTag;
            StepRows.Add(step);
            foreach (var (label, children) in step.ChildGroups())
                AppendRows(children, depth + 1, label);
        }
    }

    private static void ReplaceOptions(
        ObservableCollection<string> options, string noneOption, IReadOnlyList<string> searchIds)
    {
        options.Clear();
        options.Add(noneOption);
        foreach (var id in searchIds)
            options.Add(id);
    }

    private void Revalidate()
    {
        var errors = WorkflowValidator.Validate(BuildDefinition());
        ValidationErrors.Clear();
        foreach (var error in errors)
            ValidationErrors.Add(error);
        OnPropertyChanged(nameof(HasValidationErrors));
    }

    private void NotifyStepCommands()
    {
        RemoveStepCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
    }

    private string GenerateId(string prefix)
    {
        var ids = new HashSet<string>(EnumerateSteps(RootSteps).Select(s => s.Id), StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = $"{prefix}-{n}";
            if (!ids.Contains(candidate))
                return candidate;
        }
    }

    private static IEnumerable<WorkflowStepViewModel> EnumerateSteps(IEnumerable<WorkflowStepViewModel> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var (_, children) in step.ChildGroups())
            {
                foreach (var child in EnumerateSteps(children))
                    yield return child;
            }
        }
    }

    // ----- running -----

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hitDisplayCapped;
    [ObservableProperty] private long _totalRunHits;
    [ObservableProperty] private string _runStatusText = "";

    public ObservableCollection<string> RunLog { get; } = new();

    public ObservableCollection<WorkflowHitViewModel> RunHits { get; } = new();

    public string RunHitsSummary =>
        TotalRunHits == 0
            ? ""
            : HitDisplayCapped
                ? $"{TotalRunHits:n0} hits — display capped at first {MaxDisplayedHits:n0}"
                : $"{TotalRunHits:n0} hits";

    /// <summary>
    /// Confirmation host for file operations and program launches; supplied by
    /// the Workflows window while it is open (null means headless consent).
    /// </summary>
    public IWorkflowInteraction? Interaction { get; set; }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunWorkflowAsync() => RunCoreAsync(dryRun: false);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task DryRunWorkflowAsync() => RunCoreAsync(dryRun: true);

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun() => _runCts?.Cancel();

    private bool CanRun() => !IsRunning;

    private bool CanCancelRun() => IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        RunWorkflowCommand.NotifyCanExecuteChanged();
        DryRunWorkflowCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    partial void OnTotalRunHitsChanged(long value) => OnPropertyChanged(nameof(RunHitsSummary));

    partial void OnHitDisplayCappedChanged(bool value) => OnPropertyChanged(nameof(RunHitsSummary));

    private async Task RunCoreAsync(bool dryRun)
    {
        var workflow = BuildDefinition();
        Revalidate();
        if (HasValidationErrors)
        {
            RunStatusText = "Fix the problems listed in the editor, then run again.";
            return;
        }

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        RunLog.Clear();
        RunHits.Clear();
        _logTruncationNoted = false;
        HitDisplayCapped = false;
        TotalRunHits = 0;
        IsRunning = true;
        RunStatusText = dryRun ? "Dry run..." : "Running...";
        AppendLog($"=== {(dryRun ? "Dry run" : "Run")}: {workflow.Name}");

        var events = new ConcurrentQueue<RunEvent>();
        var observer = new QueueObserver(events);
        var interaction = Interaction;

        try
        {
            // The runner reports from background threads; the observer queues
            // events and this loop drains them on the UI thread in timed
            // batches so a heavy hit stream can't flood the dispatcher.
            var runTask = Task.Run(
                () => _runner.RunAsync(workflow, new WorkflowRunOptions { DryRun = dryRun }, observer, interaction, token),
                token);

            while (true)
            {
                if (token.IsCancellationRequested || runTask.IsCanceled)
                {
                    // Cancelled: the queued backlog is stale progress — drop
                    // it and end the run promptly instead of replaying it
                    // into the log (the catch below logs "Cancelled." once).
                    events.Clear();
                    break;
                }

                DrainRunEvents(events);
                if (runTask.IsCompleted && events.IsEmpty)
                    break;
                await Task.Delay(75).ConfigureAwait(true);
            }

            var result = await runTask.ConfigureAwait(true);
            var summary = result.Status switch
            {
                WorkflowRunStatus.Completed => "Completed",
                WorkflowRunStatus.Stopped => result.Succeeded ? "Stopped (success)" : "Stopped (failure)",
                WorkflowRunStatus.Failed => "Failed",
                WorkflowRunStatus.Cancelled => "Cancelled",
                _ => $"{result.Status}",
            };
            RunStatusText = string.IsNullOrWhiteSpace(result.Message)
                ? $"{summary} — {TotalRunHits:n0} hit(s)."
                : $"{summary} — {result.Message}";
            AppendLog($"=== {RunStatusText}");
        }
        catch (OperationCanceledException)
        {
            RunStatusText = "Cancelled.";
            AppendLog("=== Cancelled.");
        }
        catch (Exception ex)
        {
            RunStatusText = $"Run failed: {ex.Message}";
            AppendLog($"=== {RunStatusText}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void DrainRunEvents(ConcurrentQueue<RunEvent> events)
    {
        var drained = 0;
        var hits = TotalRunHits;

        while (drained < MaxEventsPerDrain && events.TryDequeue(out var runEvent))
        {
            drained++;
            switch (runEvent)
            {
                case StepStartedEvent started:
                    AppendLog($"{IndentFor(started.Depth)}▶ {started.Kind} '{started.Name}'");
                    break;

                case StepCompletedEvent completed:
                    var outcome = completed.Outcome;
                    var marker = outcome.Succeeded ? "✓" : "✗";
                    var detail = string.IsNullOrEmpty(outcome.Detail) ? "" : $" — {outcome.Detail}";
                    AppendLog($"{IndentFor(completed.Depth)}{marker} {outcome.StepKind} '{outcome.DisplayName}'{detail}");
                    break;

                case HitEvent hitEvent:
                    hits++;
                    if (RunHits.Count < MaxDisplayedHits)
                        RunHits.Add(new WorkflowHitViewModel(
                            hitEvent.Hit.Path, hitEvent.Hit.LineNumber, hitEvent.Hit.LineContent.Trim()));
                    else
                        HitDisplayCapped = true;
                    break;

                case LogEvent log:
                    AppendLog(log.Message);
                    break;
            }
        }

        if (hits != TotalRunHits)
            TotalRunHits = hits;
    }

    private void AppendLog(string line)
    {
        if (RunLog.Count < MaxLogLines)
        {
            RunLog.Add(line);
        }
        else if (!_logTruncationNoted)
        {
            _logTruncationNoted = true;
            RunLog.Add("… further log lines omitted.");
        }
    }

    private static string IndentFor(int depth) => new(' ', depth * 2);

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
    }

    // ----- run events (queued by the observer, drained on the UI thread) -----

    private abstract record RunEvent;

    private sealed record StepStartedEvent(string Kind, string Name, int Depth) : RunEvent;

    private sealed record StepCompletedEvent(WorkflowStepOutcome Outcome, int Depth) : RunEvent;

    private sealed record HitEvent(Hit Hit) : RunEvent;

    private sealed record LogEvent(string Message) : RunEvent;

    /// <summary>
    /// Thread-safe observer: callbacks arrive on background threads and are
    /// queued for the UI drain loop, preserving their order.
    /// </summary>
    private sealed class QueueObserver : IWorkflowObserver
    {
        private readonly ConcurrentQueue<RunEvent> _events;
        private readonly ConcurrentDictionary<string, int> _depths = new(StringComparer.Ordinal);

        public QueueObserver(ConcurrentQueue<RunEvent> events) => _events = events;

        public void OnStepStarted(WorkflowStep step, int depth)
        {
            _depths[step.Id] = depth;
            _events.Enqueue(new StepStartedEvent(step.Kind, step.DisplayName, depth));
        }

        public void OnStepCompleted(WorkflowStepOutcome outcome) =>
            _events.Enqueue(new StepCompletedEvent(
                outcome, _depths.TryGetValue(outcome.StepId, out var depth) ? depth : 0));

        public void OnHit(SearchStep step, Hit hit) => _events.Enqueue(new HitEvent(hit));

        public void OnLog(string message) => _events.Enqueue(new LogEvent(message));
    }
}
