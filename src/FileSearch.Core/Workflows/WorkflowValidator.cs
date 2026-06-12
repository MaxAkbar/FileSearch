using System;
using System.Collections.Generic;
using System.Linq;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Structural validation of a workflow before it runs or is saved: ids are
/// present and unique, references point at search steps that actually run
/// before the referencing step (document order, with a retry's exit condition
/// checked after its body since that is when it evaluates), loops have bodies
/// and sane bounds. Returns human-readable problems rather than throwing, so
/// editors can show them inline.
/// </summary>
public static class WorkflowValidator
{
    public static IReadOnlyList<string> Validate(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(workflow.Name))
            errors.Add("Workflow needs a name.");

        if (workflow.Steps.Count == 0)
            errors.Add("Workflow has no steps.");

        ValidateSteps(workflow.Steps, new ValidationState(), errors);
        return errors;
    }

    /// <summary>All steps in document order, including nested bodies and branches.</summary>
    public static IEnumerable<WorkflowStep> Flatten(IReadOnlyList<WorkflowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        foreach (var step in steps)
        {
            if (step is null)
                continue;

            yield return step;
            var children = step switch
            {
                IfStep i => i.Then.Concat(i.Else),
                RetryStep r => r.Body,
                ForEachStep f => f.Body,
                _ => Enumerable.Empty<WorkflowStep>(),
            };
            foreach (var child in Flatten(children.ToList()))
                yield return child;
        }
    }

    private sealed class ValidationState
    {
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);

        /// <summary>Search-step ids encountered so far in the walk.</summary>
        public HashSet<string> SeenSearchIds { get; } = new(StringComparer.Ordinal);
    }

    private static void ValidateSteps(IReadOnlyList<WorkflowStep> steps, ValidationState state, List<string> errors)
    {
        foreach (var step in steps)
        {
            if (step is null)
            {
                errors.Add("Workflow contains a null step entry (check for a stray comma in the steps array).");
                continue;
            }

            var label = step.DisplayName;
            if (string.IsNullOrWhiteSpace(step.Id))
                errors.Add($"Step '{label}' ({step.Kind}) has no id.");
            else if (!state.Ids.Add(step.Id))
                errors.Add($"Duplicate step id '{step.Id}'.");

            switch (step)
            {
                case SearchStep s:
                    if (string.IsNullOrWhiteSpace(s.Query))
                        errors.Add($"Search step '{label}' has no query.");
                    if (s.ScopeStepId is null && s.Roots.Count == 0)
                        errors.Add($"Search step '{label}' needs at least one root folder (or a scope step).");
                    if (s.ScopeStepId is not null)
                        RequireEarlierSearch(errors, state, s.ScopeStepId, label, "scope");
                    if (!string.IsNullOrWhiteSpace(s.Id))
                        state.SeenSearchIds.Add(s.Id);
                    break;

                case IfStep i:
                    // The condition evaluates before either branch runs.
                    RequireConditionRef(errors, state, i.Condition, label);
                    if (i.Then.Count == 0 && i.Else.Count == 0)
                        errors.Add($"If step '{label}' has neither a then- nor an else-branch.");
                    ValidateSteps(i.Then, state, errors);
                    ValidateSteps(i.Else, state, errors);
                    break;

                case RetryStep r:
                    if (r.Body.Count == 0)
                        errors.Add($"Retry step '{label}' has an empty body.");
                    if (r.MaxIterations < 1)
                        errors.Add($"Retry step '{label}' needs maxIterations of at least 1.");
                    if (r.ParameterSets.Count > r.MaxIterations)
                        errors.Add($"Retry step '{label}' has {r.ParameterSets.Count} parameter sets but maxIterations is {r.MaxIterations} — the extra sets would never run.");
                    ValidateSteps(r.Body, state, errors);
                    // The exit condition evaluates after each iteration, so
                    // searches inside the body are legitimate sources.
                    RequireConditionRef(errors, state, r.Until, label);
                    break;

                case ForEachStep f:
                    if (f.Body.Count == 0)
                        errors.Add($"For-each step '{label}' has an empty body.");
                    if (f.SourceStepId is not null)
                        RequireEarlierSearch(errors, state, f.SourceStepId, label, "source");
                    ValidateSteps(f.Body, state, errors);
                    break;

                case ExportStep e:
                    if (string.IsNullOrWhiteSpace(e.Path))
                        errors.Add($"Export step '{label}' has no output path.");
                    if (e.SourceStepId is not null)
                        RequireEarlierSearch(errors, state, e.SourceStepId, label, "source");
                    break;

                case FileOperationStep o:
                    if (string.IsNullOrWhiteSpace(o.DestinationDirectory))
                        errors.Add($"File-operation step '{label}' has no destination folder.");
                    if (o.SourceStepId is not null)
                        RequireEarlierSearch(errors, state, o.SourceStepId, label, "source");
                    break;

                case RunProgramStep p:
                    if (string.IsNullOrWhiteSpace(p.Program))
                        errors.Add($"Run-program step '{label}' has no program.");
                    if (p.SourceStepId is not null)
                        RequireEarlierSearch(errors, state, p.SourceStepId, label, "source");
                    break;
            }
        }
    }

    private static void RequireEarlierSearch(
        List<string> errors, ValidationState state, string reference, string label, string role)
    {
        if (!state.SeenSearchIds.Contains(reference))
            errors.Add($"Step '{label}' references search step '{reference}' as its {role}, but no search step with that id runs before it.");
    }

    private static void RequireConditionRef(
        List<string> errors, ValidationState state, WorkflowCondition condition, string label)
    {
        if (condition.Source is not null && !state.SeenSearchIds.Contains(condition.Source))
            errors.Add($"Condition on step '{label}' references search step '{condition.Source}', but no search step with that id runs before it.");
    }
}
