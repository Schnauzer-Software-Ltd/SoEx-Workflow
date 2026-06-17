namespace SoEx.Workflow.Runtime.InMemory;

/// <summary>
/// In-memory <see cref="ISubjectIndex"/>: additive edge sets held in process memory. Concurrency-safe — the
/// governed-step path indexes edges from concurrent steps (a busy host runs many instances; native fan-out
/// issues parallel governed steps), and reads (the erasure sweep) may run alongside those writes. A single
/// lock guards every mutation and read, and reads return snapshots so callers iterate safely.
/// </summary>
public sealed class InMemorySubjectIndex : ISubjectIndex
{
    private readonly Dictionary<string, HashSet<string>> _instancesBySubject = [];
    private readonly Dictionary<string, HashSet<string>> _subjectsByInstance = [];
    private readonly object _gate = new();

    public void AddEdge(string subjectId, string instanceId)
    {
        lock (_gate)
        {
            Edge(_instancesBySubject, subjectId).Add(instanceId);
            Edge(_subjectsByInstance, instanceId).Add(subjectId);
        }
    }

    public void RemoveInstance(string instanceId)
    {
        lock (_gate)
        {
            if (_subjectsByInstance.Remove(instanceId, out HashSet<string>? subjects))
            {
                foreach (string subject in subjects)
                {
                    if (_instancesBySubject.TryGetValue(subject, out HashSet<string>? instances))
                    {
                        instances.Remove(instanceId);
                        if (instances.Count == 0)
                        {
                            _instancesBySubject.Remove(subject);
                        }
                    }
                }
            }
        }
    }

    public IReadOnlyCollection<string> InstancesFor(string subjectId)
    {
        lock (_gate)
        {
            return _instancesBySubject.TryGetValue(subjectId, out HashSet<string>? instances) ? [.. instances] : [];
        }
    }

    public IReadOnlyCollection<string> SubjectsFor(string instanceId)
    {
        lock (_gate)
        {
            return _subjectsByInstance.TryGetValue(instanceId, out HashSet<string>? subjects) ? [.. subjects] : [];
        }
    }

    private static HashSet<string> Edge(Dictionary<string, HashSet<string>> map, string key) =>
        map.TryGetValue(key, out HashSet<string>? set) ? set : map[key] = [];
}
