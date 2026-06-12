using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Canonical (de)serialization for the documented workflow JSON format:
/// camelCase members, string enums, indented output, <c>"type"</c> step
/// discriminators. All readers and writers go through here so the format
/// stays uniform across GUI, CLI and tests.
/// </summary>
public static class WorkflowJson
{
    /// <summary>
    /// Version stamped into new files. Readers reject files from a newer
    /// version instead of silently dropping fields they don't know.
    /// </summary>
    public const int CurrentVersion = 1;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Hand-edited files shouldn't break because "type" isn't first.
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return JsonSerializer.Serialize(workflow, Options);
    }

    /// <summary>
    /// Parses workflow JSON; returns null and an error message instead of
    /// throwing so callers can surface bad hand-edited files gracefully.
    /// </summary>
    public static WorkflowDefinition? TryDeserialize(string json, out string? error)
    {
        try
        {
            var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options);
            if (workflow is null)
            {
                error = "File contains no workflow.";
                return null;
            }

            if (workflow.Version > CurrentVersion)
            {
                error = $"Workflow format version {workflow.Version} is newer than this app supports ({CurrentVersion}).";
                return null;
            }

            if (ContainsNullStep(workflow.Steps))
            {
                error = "Workflow contains a null step entry (check for a stray comma in the steps array).";
                return null;
            }

            error = null;
            return workflow;
        }
        catch (JsonException ex)
        {
            error = $"Invalid workflow JSON: {ex.Message}";
            return null;
        }
        catch (NotSupportedException)
        {
            // System.Text.Json polymorphism throws this for a missing or
            // unknown "type" discriminator on a step.
            error = "A step is missing a valid \"type\" — every step needs one of: "
                + "search, if, retry, forEach, export, fileOperation, runProgram, stop.";
            return null;
        }
    }

    private static bool ContainsNullStep(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            IEnumerable<WorkflowStep>? children = step switch
            {
                null => null,
                IfStep i => i.Then.Concat(i.Else),
                RetryStep r => r.Body,
                ForEachStep f => f.Body,
                _ => Enumerable.Empty<WorkflowStep>(),
            };
            if (children is null || ContainsNullStep(children.ToList()))
                return true;
        }

        return false;
    }
}
