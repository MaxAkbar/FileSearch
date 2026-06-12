using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileSearch.Core.Queries;
using FileSearch.Core.Walker;
using FileSearch.Core.Workflows;

namespace FileSearch.Gui.ViewModels;

/// <summary>
/// What a step editor needs from its owning workflow editor: creating sibling
/// steps with fresh unique ids, signalling edits/structure changes (for
/// validation, dirty tracking and the flattened step list), and the folder
/// picker. Kept as an interface so the step view models stay decoupled from
/// <see cref="WorkflowsViewModel"/>.
/// </summary>
public interface IWorkflowStepHost
{
    /// <summary>Creates a step view model of the given JSON kind with a generated unique id.</summary>
    WorkflowStepViewModel CreateStep(string kind);

    /// <summary>A step was added, removed or moved somewhere in the tree.</summary>
    void NotifyStructureChanged();

    /// <summary>A property of a step was edited.</summary>
    void NotifyStepEdited(WorkflowStepViewModel step, string? propertyName);

    /// <summary>Shows the folder picker; returns the chosen folder or null.</summary>
    string? PickFolder(string title, string? initialDirectory);
}

/// <summary>
/// Sentinel display strings for the "reference an earlier search step"
/// dropdowns, plus the mapping to and from the model's nullable step ids.
/// </summary>
public static class WorkflowEditorOptions
{
    /// <summary>Search-step scope: no scope, walk the root folders.</summary>
    public const string NoScope = "(search folders)";

    /// <summary>Source option meaning "the most recent search step".</summary>
    public const string LastSearch = "(last search)";

    /// <summary>Export source option meaning "all search steps so far".</summary>
    public const string AllSearches = "(all searches)";

    public static string? ToStepId(string option)
    {
        var trimmed = (option ?? "").Trim();
        return trimmed.Length == 0 || trimmed is NoScope or LastSearch or AllSearches ? null : trimmed;
    }

    public static string FromStepId(string? stepId, string noneOption) =>
        string.IsNullOrWhiteSpace(stepId) ? noneOption : stepId!;
}

/// <summary>Enum value lists for the editor's combo boxes (bound via x:Static).</summary>
public static class WorkflowEnumValues
{
    public static IReadOnlyList<QueryMode> QueryModes { get; } =
        new[] { QueryMode.PlainText, QueryMode.Regex, QueryMode.Boolean };

    public static IReadOnlyList<ExportFormat> ExportFormats { get; } =
        new[] { ExportFormat.Json, ExportFormat.Csv, ExportFormat.Markdown };

    public static IReadOnlyList<FileOperationKind> FileOperationKinds { get; } =
        new[] { FileOperationKind.Copy, FileOperationKind.Move };

    public static IReadOnlyList<FileCollisionPolicy> CollisionPolicies { get; } =
        new[] { FileCollisionPolicy.Rename, FileCollisionPolicy.Skip, FileCollisionPolicy.Overwrite };

    public static IReadOnlyList<ConditionMetric> ConditionMetrics { get; } =
        new[] { ConditionMetric.HitCount, ConditionMetric.FileCount };

    public static IReadOnlyList<ConditionOperator> ConditionOperators { get; } = new[]
    {
        ConditionOperator.Equals,
        ConditionOperator.NotEquals,
        ConditionOperator.GreaterThan,
        ConditionOperator.GreaterOrEqual,
        ConditionOperator.LessThan,
        ConditionOperator.LessOrEqual,
    };
}

/// <summary>Editable form of a <see cref="WorkflowCondition"/> (if-branches and retry exits).</summary>
public sealed partial class WorkflowConditionViewModel : ObservableObject
{
    [ObservableProperty] private string _source = WorkflowEditorOptions.LastSearch;
    [ObservableProperty] private ConditionMetric _metric = ConditionMetric.HitCount;
    [ObservableProperty] private ConditionOperator _operator = ConditionOperator.GreaterOrEqual;
    [ObservableProperty] private long _value;

    public string Summary => ToCondition().Describe();

    public WorkflowCondition ToCondition() => new()
    {
        Source = WorkflowEditorOptions.ToStepId(Source),
        Metric = Metric,
        Operator = Operator,
        Value = Value,
    };

    public void Load(WorkflowCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        Source = WorkflowEditorOptions.FromStepId(condition.Source, WorkflowEditorOptions.LastSearch);
        Metric = condition.Metric;
        Operator = condition.Operator;
        Value = condition.Value;
    }

    partial void OnSourceChanged(string value) => OnPropertyChanged(nameof(Summary));
    partial void OnMetricChanged(ConditionMetric value) => OnPropertyChanged(nameof(Summary));
    partial void OnOperatorChanged(ConditionOperator value) => OnPropertyChanged(nameof(Summary));
    partial void OnValueChanged(long value) => OnPropertyChanged(nameof(Summary));
}

/// <summary>
/// One retry parameter set, edited as <c>key=value</c> lines — kept as plain
/// text so the editor round-trips arbitrary variable names without a grid.
/// </summary>
public sealed partial class WorkflowParameterSetViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "";

    public IReadOnlyDictionary<string, string> ToParameterSet()
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in Text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var separator = line.IndexOf('=');
            var key = separator < 0 ? line : line[..separator].Trim();
            if (key.Length == 0)
                continue;
            set[key] = separator < 0 ? "" : line[(separator + 1)..].Trim();
        }

        return set;
    }

    public static WorkflowParameterSetViewModel FromParameterSet(IReadOnlyDictionary<string, string> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new WorkflowParameterSetViewModel
        {
            Text = string.Join(Environment.NewLine, set.Select(pair => $"{pair.Key}={pair.Value}")),
        };
    }
}

/// <summary>
/// Base of the editable step tree. Mirrors <see cref="WorkflowStep"/>: one
/// derived view model per JSON step kind, each able to rebuild its model
/// (<see cref="ToStep"/>). <see cref="Depth"/>, <see cref="BranchTag"/> and
/// <see cref="OwnerList"/> are assigned by the editor whenever it re-flattens
/// the tree into the displayed list.
/// </summary>
public abstract partial class WorkflowStepViewModel : ObservableObject
{
    private protected WorkflowStepViewModel(IWorkflowStepHost host, string id)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _id = id;
    }

    private protected IWorkflowStepHost Host { get; }

    /// <summary>Sibling collection this step currently lives in (set on flatten).</summary>
    internal ObservableCollection<WorkflowStepViewModel>? OwnerList { get; set; }

    [ObservableProperty] private string _id;
    [ObservableProperty] private string? _name;
    [ObservableProperty] private int _depth;
    [ObservableProperty] private string _branchTag = "";

    /// <summary>The JSON discriminator, e.g. "search" or "forEach".</summary>
    public abstract string Kind { get; }

    /// <summary>One-line description shown under the step name in the list.</summary>
    public abstract string Summary { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public bool HasBranchTag => BranchTag.Length > 0;

    /// <summary>Child step groups (label + steps) for tree flattening.</summary>
    public virtual IEnumerable<(string Label, ObservableCollection<WorkflowStepViewModel> Steps)> ChildGroups() =>
        Array.Empty<(string, ObservableCollection<WorkflowStepViewModel>)>();

    /// <summary>Builds the immutable model step (including nested children).</summary>
    public abstract WorkflowStep ToStep();

    /// <summary>
    /// Properties that only affect presentation or editor chrome; edits to
    /// them must not mark the workflow dirty or trigger re-validation.
    /// </summary>
    protected virtual bool IsTransientProperty(string? propertyName) =>
        propertyName is nameof(Depth) or nameof(BranchTag) or nameof(Indent)
            or nameof(HasBranchTag) or nameof(DisplayName) or nameof(Summary);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!IsTransientProperty(e?.PropertyName))
            Host.NotifyStepEdited(this, e?.PropertyName);
    }

    protected void RefreshSummary() => OnPropertyChanged(nameof(Summary));

    protected string? NameOrNull() => string.IsNullOrWhiteSpace(Name) ? null : Name!.Trim();

    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(Indent));
    partial void OnBranchTagChanged(string value) => OnPropertyChanged(nameof(HasBranchTag));
    partial void OnIdChanged(string value) => OnPropertyChanged(nameof(DisplayName));
    partial void OnNameChanged(string? value) => OnPropertyChanged(nameof(DisplayName));

    private static readonly char[] s_patternSeparators = { ';', ',' };

    private protected static string[] SplitPatterns(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(s_patternSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Editor for <see cref="SearchStep"/>.</summary>
public sealed partial class SearchStepViewModel : WorkflowStepViewModel
{
    public SearchStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
    }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private QueryMode _mode = QueryMode.PlainText;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private string _scopeStepId = WorkflowEditorOptions.NoScope;
    [ObservableProperty] private bool _useIndex;
    [ObservableProperty] private string _includeGlobs = "";
    [ObservableProperty] private string _excludeGlobs = "";
    [ObservableProperty] private bool _useDefaultExcludedFolders = true;
    [ObservableProperty] private string _excludeDirectories = "";
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _includeHidden;
    [ObservableProperty] private long _minFileSizeBytes;
    [ObservableProperty] private long _maxFileSizeBytes = WalkerOptions.DefaultMaxFileSizeBytes;
    [ObservableProperty] private bool _modifiedAfterEnabled;
    [ObservableProperty] private DateTime _modifiedAfter = DateTime.Today.AddDays(-7);
    [ObservableProperty] private bool _modifiedBeforeEnabled;
    [ObservableProperty] private DateTime _modifiedBefore = DateTime.Today;
    [ObservableProperty] private int _maxHits;

    // Editor-only state for the roots list.
    [ObservableProperty] private string? _selectedRoot;
    [ObservableProperty] private string _newRoot = "";

    public ObservableCollection<string> Roots { get; } = new();

    public override string Kind => "search";

    public override string Summary => string.IsNullOrWhiteSpace(Query) ? "(no query)" : Query.Trim();

    /// <summary>
    /// True when this step walks the filesystem (no scope step selected).
    /// The filters group and the index only apply in that mode; the editor
    /// disables them when the search is scoped to a previous step's results.
    /// </summary>
    public bool IsFileSystemScope => WorkflowEditorOptions.ToStepId(ScopeStepId) is null;

    /// <summary>The custom excluded-folders list only applies when defaults are off.</summary>
    public bool CustomExcludedFoldersEnabled => !UseDefaultExcludedFolders;

    [RelayCommand]
    private void AddRoot()
    {
        var root = NewRoot.Trim();
        if (root.Length == 0)
            return;

        Roots.Add(root);
        NewRoot = "";
        Host.NotifyStepEdited(this, nameof(Roots));
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var folder = Host.PickFolder("Add root folder", null);
        if (folder is null)
            return;

        Roots.Add(folder);
        Host.NotifyStepEdited(this, nameof(Roots));
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRoot))]
    private void RemoveRoot()
    {
        if (SelectedRoot is null)
            return;

        Roots.Remove(SelectedRoot);
        Host.NotifyStepEdited(this, nameof(Roots));
    }

    private bool CanRemoveRoot() => SelectedRoot is not null;

    public override WorkflowStep ToStep() => new SearchStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        Query = Query,
        Mode = Mode,
        CaseSensitive = CaseSensitive,
        Roots = Roots.ToArray(),
        ScopeStepId = WorkflowEditorOptions.ToStepId(ScopeStepId),
        UseIndex = UseIndex,
        Filters = new SearchFilters
        {
            IncludeGlobs = SplitPatterns(IncludeGlobs),
            ExcludeGlobs = SplitPatterns(ExcludeGlobs),
            // Null keeps the engine default (.git/.vs/node_modules); an
            // explicit empty list means walk everything.
            ExcludeDirectories = UseDefaultExcludedFolders ? null : SplitPatterns(ExcludeDirectories),
            Recursive = Recursive,
            IncludeHidden = IncludeHidden,
            MinFileSizeBytes = MinFileSizeBytes,
            MaxFileSizeBytes = MaxFileSizeBytes,
            ModifiedAfterUtc = ModifiedAfterEnabled ? ModifiedAfter.ToUniversalTime() : null,
            ModifiedBeforeUtc = ModifiedBeforeEnabled ? ModifiedBeforeToUtc(ModifiedBefore) : null,
        },
        MaxHits = MaxHits,
    };

    /// <summary>
    /// A date picked in the editor (local midnight) means "through the end of
    /// that day", matching the main search window's inclusive Before field; a
    /// precise timestamp from hand-edited JSON is preserved as-is.
    /// </summary>
    private static DateTime ModifiedBeforeToUtc(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero
            ? value.AddDays(1).AddSeconds(-1).ToUniversalTime() // inclusive end-of-day
            : value.ToUniversalTime();

    protected override bool IsTransientProperty(string? propertyName) =>
        base.IsTransientProperty(propertyName)
            || propertyName is nameof(SelectedRoot) or nameof(NewRoot) or nameof(IsFileSystemScope)
            or nameof(CustomExcludedFoldersEnabled);

    partial void OnQueryChanged(string value) => RefreshSummary();
    partial void OnScopeStepIdChanged(string value) => OnPropertyChanged(nameof(IsFileSystemScope));
    partial void OnUseDefaultExcludedFoldersChanged(bool value) => OnPropertyChanged(nameof(CustomExcludedFoldersEnabled));
    partial void OnSelectedRootChanged(string? value) => RemoveRootCommand.NotifyCanExecuteChanged();
}

/// <summary>Editor for <see cref="IfStep"/>.</summary>
public sealed partial class IfStepViewModel : WorkflowStepViewModel
{
    public IfStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
        Condition = new WorkflowConditionViewModel();
        Condition.PropertyChanged += OnConditionChanged;
    }

    public WorkflowConditionViewModel Condition { get; }

    public ObservableCollection<WorkflowStepViewModel> ThenSteps { get; } = new();

    public ObservableCollection<WorkflowStepViewModel> ElseSteps { get; } = new();

    public override string Kind => "if";

    public override string Summary => $"if {Condition.Summary}";

    [RelayCommand]
    private void AddThenStep(string kind)
    {
        ThenSteps.Add(Host.CreateStep(kind));
        Host.NotifyStructureChanged();
    }

    [RelayCommand]
    private void AddElseStep(string kind)
    {
        ElseSteps.Add(Host.CreateStep(kind));
        Host.NotifyStructureChanged();
    }

    public override IEnumerable<(string Label, ObservableCollection<WorkflowStepViewModel> Steps)> ChildGroups()
    {
        yield return ("then", ThenSteps);
        yield return ("else", ElseSteps);
    }

    public override WorkflowStep ToStep() => new IfStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        Condition = Condition.ToCondition(),
        Then = ThenSteps.Select(step => step.ToStep()).ToArray(),
        Else = ElseSteps.Select(step => step.ToStep()).ToArray(),
    };

    private void OnConditionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowConditionViewModel.Summary))
            return;

        RefreshSummary();
        Host.NotifyStepEdited(this, nameof(Condition));
    }
}

/// <summary>Editor for <see cref="RetryStep"/>.</summary>
public sealed partial class RetryStepViewModel : WorkflowStepViewModel
{
    public RetryStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
        Until = new WorkflowConditionViewModel();
        Until.PropertyChanged += OnUntilChanged;
    }

    public WorkflowConditionViewModel Until { get; }

    public ObservableCollection<WorkflowStepViewModel> BodySteps { get; } = new();

    public ObservableCollection<WorkflowParameterSetViewModel> ParameterSets { get; } = new();

    [ObservableProperty] private int _maxIterations = 5;

    public override string Kind => "retry";

    public override string Summary => $"until {Until.Summary}, max {MaxIterations}";

    [RelayCommand]
    private void AddBodyStep(string kind)
    {
        BodySteps.Add(Host.CreateStep(kind));
        Host.NotifyStructureChanged();
    }

    [RelayCommand]
    private void AddParameterSet()
    {
        var set = new WorkflowParameterSetViewModel();
        set.PropertyChanged += OnParameterSetChanged;
        ParameterSets.Add(set);
        Host.NotifyStepEdited(this, nameof(ParameterSets));
    }

    [RelayCommand]
    private void RemoveParameterSet(WorkflowParameterSetViewModel? set)
    {
        if (set is null)
            return;

        set.PropertyChanged -= OnParameterSetChanged;
        ParameterSets.Remove(set);
        Host.NotifyStepEdited(this, nameof(ParameterSets));
    }

    public void LoadParameterSets(IReadOnlyList<IReadOnlyDictionary<string, string>> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        foreach (var set in sets)
        {
            var viewModel = WorkflowParameterSetViewModel.FromParameterSet(set);
            viewModel.PropertyChanged += OnParameterSetChanged;
            ParameterSets.Add(viewModel);
        }
    }

    public override IEnumerable<(string Label, ObservableCollection<WorkflowStepViewModel> Steps)> ChildGroups()
    {
        yield return ("", BodySteps);
    }

    public override WorkflowStep ToStep() => new RetryStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        Body = BodySteps.Select(step => step.ToStep()).ToArray(),
        Until = Until.ToCondition(),
        MaxIterations = MaxIterations,
        ParameterSets = ParameterSets.Select(set => set.ToParameterSet()).ToArray(),
    };

    private void OnUntilChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowConditionViewModel.Summary))
            return;

        RefreshSummary();
        Host.NotifyStepEdited(this, nameof(Until));
    }

    private void OnParameterSetChanged(object? sender, PropertyChangedEventArgs e) =>
        Host.NotifyStepEdited(this, nameof(ParameterSets));

    partial void OnMaxIterationsChanged(int value) => RefreshSummary();
}

/// <summary>Editor for <see cref="ForEachStep"/>.</summary>
public sealed partial class ForEachStepViewModel : WorkflowStepViewModel
{
    public ForEachStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
    }

    [ObservableProperty] private string _sourceStepId = WorkflowEditorOptions.LastSearch;
    [ObservableProperty] private int _maxItems = 100;

    public ObservableCollection<WorkflowStepViewModel> BodySteps { get; } = new();

    public override string Kind => "forEach";

    public override string Summary => $"each file from {SourceStepId}";

    [RelayCommand]
    private void AddBodyStep(string kind)
    {
        BodySteps.Add(Host.CreateStep(kind));
        Host.NotifyStructureChanged();
    }

    public override IEnumerable<(string Label, ObservableCollection<WorkflowStepViewModel> Steps)> ChildGroups()
    {
        yield return ("", BodySteps);
    }

    public override WorkflowStep ToStep() => new ForEachStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        SourceStepId = WorkflowEditorOptions.ToStepId(SourceStepId),
        Body = BodySteps.Select(step => step.ToStep()).ToArray(),
        MaxItems = MaxItems,
    };

    partial void OnSourceStepIdChanged(string value) => RefreshSummary();
}

/// <summary>Editor for <see cref="ExportStep"/>.</summary>
public sealed partial class ExportStepViewModel : WorkflowStepViewModel
{
    public ExportStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
    }

    [ObservableProperty] private string _sourceStepId = WorkflowEditorOptions.AllSearches;
    [ObservableProperty] private ExportFormat _format = ExportFormat.Json;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _overwrite = true;

    public override string Kind => "export";

    public override string Summary => string.IsNullOrWhiteSpace(Path) ? "(no output path)" : $"{Format} → {Path}";

    public override WorkflowStep ToStep() => new ExportStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        SourceStepId = WorkflowEditorOptions.ToStepId(SourceStepId),
        Format = Format,
        Path = Path.Trim(),
        Overwrite = Overwrite,
    };

    partial void OnPathChanged(string value) => RefreshSummary();
    partial void OnFormatChanged(ExportFormat value) => RefreshSummary();
}

/// <summary>Editor for <see cref="FileOperationStep"/>.</summary>
public sealed partial class FileOperationStepViewModel : WorkflowStepViewModel
{
    public FileOperationStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
    }

    [ObservableProperty] private FileOperationKind _operation = FileOperationKind.Copy;
    [ObservableProperty] private string _sourceStepId = WorkflowEditorOptions.LastSearch;
    [ObservableProperty] private string _destinationDirectory = "";
    [ObservableProperty] private FileCollisionPolicy _collision = FileCollisionPolicy.Rename;

    public override string Kind => "fileOperation";

    public override string Summary =>
        string.IsNullOrWhiteSpace(DestinationDirectory)
            ? $"{Operation} (no destination)"
            : $"{Operation} → {DestinationDirectory}";

    [RelayCommand]
    private void BrowseDestination()
    {
        var folder = Host.PickFolder("Choose destination folder", DestinationDirectory);
        if (folder is not null)
            DestinationDirectory = folder;
    }

    public override WorkflowStep ToStep() => new FileOperationStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        Operation = Operation,
        SourceStepId = WorkflowEditorOptions.ToStepId(SourceStepId),
        DestinationDirectory = DestinationDirectory.Trim(),
        Collision = Collision,
    };

    partial void OnOperationChanged(FileOperationKind value) => RefreshSummary();
    partial void OnDestinationDirectoryChanged(string value) => RefreshSummary();
}

/// <summary>Editor for <see cref="RunProgramStep"/>.</summary>
public sealed partial class RunProgramStepViewModel : WorkflowStepViewModel
{
    public RunProgramStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
    }

    [ObservableProperty] private string _program = "";

    // Quoted by default so the per-file pattern stays safe for paths with spaces.
    [ObservableProperty] private string _arguments = "\"${file}\"";

    [ObservableProperty] private bool _perFile;
    [ObservableProperty] private string _sourceStepId = WorkflowEditorOptions.LastSearch;
    [ObservableProperty] private string _workingDirectory = "";
    [ObservableProperty] private bool _waitForExit = true;
    [ObservableProperty] private int _timeoutSeconds = 60;
    [ObservableProperty] private int _maxFiles = 100;

    public override string Kind => "runProgram";

    public override string Summary =>
        string.IsNullOrWhiteSpace(Program) ? "(no program)" : $"{Program} {Arguments}".Trim();

    public override WorkflowStep ToStep() => new RunProgramStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        Program = Program.Trim(),
        Arguments = Arguments,
        PerFile = PerFile,
        SourceStepId = WorkflowEditorOptions.ToStepId(SourceStepId),
        WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim(),
        WaitForExit = WaitForExit,
        TimeoutSeconds = TimeoutSeconds,
        MaxFiles = MaxFiles,
    };

    partial void OnProgramChanged(string value) => RefreshSummary();
    partial void OnArgumentsChanged(string value) => RefreshSummary();
}

/// <summary>Editor for <see cref="StopStep"/>.</summary>
public sealed partial class StopStepViewModel : WorkflowStepViewModel
{
    public StopStepViewModel(IWorkflowStepHost host, string id)
        : base(host, id)
    {
    }

    [ObservableProperty] private bool _succeeded = true;
    [ObservableProperty] private string _message = "";

    public override string Kind => "stop";

    public override string Summary =>
        (Succeeded ? "stop (success)" : "stop (failure)")
            + (string.IsNullOrWhiteSpace(Message) ? "" : $": {Message}");

    public override WorkflowStep ToStep() => new StopStep
    {
        Id = Id.Trim(),
        Name = NameOrNull(),
        Succeeded = Succeeded,
        Message = string.IsNullOrWhiteSpace(Message) ? null : Message,
    };

    partial void OnSucceededChanged(bool value) => RefreshSummary();
    partial void OnMessageChanged(string value) => RefreshSummary();
}
