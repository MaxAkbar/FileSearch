using System.Text.Json.Serialization;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Base of all workflow steps. Steps are serialized polymorphically with a
/// <c>"type"</c> discriminator so the on-disk JSON stays readable and
/// hand-editable. New step kinds are added by deriving a record and listing
/// it here — the runner dispatches on the concrete type (OCP, mirroring
/// <see cref="Queries.Query"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SearchStep), "search")]
[JsonDerivedType(typeof(IfStep), "if")]
[JsonDerivedType(typeof(RetryStep), "retry")]
[JsonDerivedType(typeof(ForEachStep), "forEach")]
[JsonDerivedType(typeof(ExportStep), "export")]
[JsonDerivedType(typeof(FileOperationStep), "fileOperation")]
[JsonDerivedType(typeof(RunProgramStep), "runProgram")]
[JsonDerivedType(typeof(StopStep), "stop")]
public abstract record WorkflowStep
{
    /// <summary>
    /// Unique id within the workflow (including nested steps). Conditions and
    /// scoped steps reference results by this id, so it must be stable under
    /// reordering — the GUI editor generates ids and keeps them unique.
    /// </summary>
    public string Id { get; init; } = "";

    /// <summary>Optional display name; falls back to the id in UI and logs.</summary>
    public string? Name { get; init; }

    /// <summary>Display name for logs and observers.</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name!;

    /// <summary>The JSON discriminator for this step, for logs and outcomes.</summary>
    [JsonIgnore]
    public string Kind => this switch
    {
        SearchStep => "search",
        IfStep => "if",
        RetryStep => "retry",
        ForEachStep => "forEach",
        ExportStep => "export",
        FileOperationStep => "fileOperation",
        RunProgramStep => "runProgram",
        StopStep => "stop",
        _ => GetType().Name,
    };
}
