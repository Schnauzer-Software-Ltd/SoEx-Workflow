namespace SoEx.Workflow;

/// <summary>
/// Dispatches one opaque step envelope to the hosted entrypoint and returns the opaque
/// response envelope (a serialized <c>InvocationResponse</c> whose payload is the
/// <see cref="WorkflowAction"/>). A bound <c>WorkflowEndpoint</c> is the implementation;
/// it runs the SoEx endpoint pipeline.
/// </summary>
public interface IWorkflowDispatch
{
    Task<byte[]> DispatchAsync(byte[] stepEnvelope);
}
