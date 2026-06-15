namespace SoEx.Workflow;

/// <summary>
/// Per-step framework governance driven from <see cref="StepMetadata"/>: the instance
/// key is minted unconditionally (start-anchored — every instance keys its own bytes),
/// and subjects are indexed only for workflow-managed flows. Externally-managed flows
/// still key their bytes but defer subject indexing/routing to the consumer's system.
/// </summary>
public sealed class InstanceGovernor(IInstanceKeyStore keys, ISubjectIndex index)
{
    public void OnStep(StepMetadata meta)
    {
        keys.Mint(meta.InstanceId);

        if (meta.WorkflowManaged)
        {
            foreach (string subjectId in meta.SubjectIds)
            {
                index.AddEdge(subjectId, meta.InstanceId);
            }
        }
    }
}
