using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core.Workflows;

/// <summary>
/// Executes a <see cref="WorkflowDefinition"/>: searches, structured control
/// flow, exports, file operations and program launches. Front-end-agnostic —
/// hosts observe progress via <see cref="IWorkflowObserver"/> and answer
/// confirmation prompts via <see cref="IWorkflowInteraction"/>.
/// </summary>
public interface IWorkflowRunner
{
    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition workflow,
        WorkflowRunOptions? options = null,
        IWorkflowObserver? observer = null,
        IWorkflowInteraction? interaction = null,
        CancellationToken cancellationToken = default);
}
