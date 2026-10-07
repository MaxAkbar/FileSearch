using CommunityToolkit.Mvvm.ComponentModel;
using FileSearch.Core.Replacement;
using FileSearch.Core.Workflows;

namespace FileSearch.Gui.ViewModels;

/// <summary>Uses the existing folder/filter editor state, with replacement-specific matching options.</summary>
public sealed partial class ReplacementStepViewModel : SearchStepViewModel
{
    public ReplacementStepViewModel(IWorkflowStepHost host, string id) : base(host, id) { }
    [ObservableProperty] private string _find = "";
    [ObservableProperty] private string _replaceWith = "";
    [ObservableProperty] private ReplacementTarget _target;
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private ReplacementNameTarget _nameTarget = ReplacementNameTarget.Both;
    [ObservableProperty] private bool _includeExtensions;
    [ObservableProperty] private bool _includeFormulas;
    [ObservableProperty] private string _additionalTextExtensions = "";
    public override string Kind => "replace";
    public override string Summary => $"{Target}: {Find} → {ReplaceWith}";
    public override WorkflowStep ToStep()
    {
        var scope = (SearchStep)base.ToStep();
        return new ReplacementStep { Id = scope.Id, Name = scope.Name, Roots = scope.Roots, ScopeStepId = scope.ScopeStepId,
            Filters = scope.Filters, Find = Find, ReplaceWith = ReplaceWith, Target = Target, UseRegex = UseRegex,
            MatchCase = CaseSensitive, NameTarget = NameTarget, IncludeExtensions = IncludeExtensions, IncludeFormulas = IncludeFormulas,
            AdditionalTextExtensions = SplitPatterns(AdditionalTextExtensions) };
    }
    public void Load(ReplacementStep step)
    {
        Name = step.Name; Find = step.Find; ReplaceWith = step.ReplaceWith; Target = step.Target; UseRegex = step.UseRegex;
        CaseSensitive = step.MatchCase; NameTarget = step.NameTarget; IncludeExtensions = step.IncludeExtensions; IncludeFormulas = step.IncludeFormulas;
        AdditionalTextExtensions = string.Join("; ", step.AdditionalTextExtensions);
        ScopeStepId = WorkflowEditorOptions.FromStepId(step.ScopeStepId, WorkflowEditorOptions.NoScope);
        foreach (var root in step.Roots) Roots.Add(root);
        IncludeGlobs = string.Join("; ", step.Filters.IncludeGlobs); ExcludeGlobs = string.Join("; ", step.Filters.ExcludeGlobs);
        UseDefaultExcludedFolders = step.Filters.ExcludeDirectories is null;
        ExcludeDirectories = string.Join("; ", step.Filters.ExcludeDirectories ?? []);
        Recursive = step.Filters.Recursive; IncludeHidden = step.Filters.IncludeHidden;
        MinFileSizeBytes = step.Filters.MinFileSizeBytes; MaxFileSizeBytes = step.Filters.MaxFileSizeBytes;
        if (step.Filters.ModifiedAfterUtc is { } after) { ModifiedAfterEnabled = true; ModifiedAfter = after.ToLocalTime(); }
        if (step.Filters.ModifiedBeforeUtc is { } before) { ModifiedBeforeEnabled = true; ModifiedBefore = before.ToLocalTime(); }
    }
    partial void OnFindChanged(string value) => RefreshSummary();
    partial void OnReplaceWithChanged(string value) => RefreshSummary();
    partial void OnTargetChanged(ReplacementTarget value) => RefreshSummary();
}
