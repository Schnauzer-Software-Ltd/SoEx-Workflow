namespace SoEx.Workflow;

/// <summary>
/// The fallback subject→instance index for workflow-managed flows. Additive and
/// multi-subject (an instance may gain further subjects as it runs). Self-governing
/// and one layer deep: edges are pruned at termination (<c>OnTerminated</c>), so its
/// lifetime equals the instance's.
/// </summary>
public interface ISubjectIndex
{
    /// <summary>Adds an <c>(subject, instance)</c> edge. Idempotent.</summary>
    void AddEdge(string subjectId, string instanceId);

    /// <summary>Removes every edge for an instance (termination pruning).</summary>
    void RemoveInstance(string instanceId);

    IReadOnlyCollection<string> InstancesFor(string subjectId);

    IReadOnlyCollection<string> SubjectsFor(string instanceId);
}
