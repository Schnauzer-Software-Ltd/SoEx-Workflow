namespace SoEx.Workflow;

/// <summary>The distinct, reportable states of an erasure.</summary>
public enum ErasureState
{
    Requested,
    InProgress,
    Held,       // quarantined (OnRetentionHeld) — non-final, key retained
    Complete,
}

/// <summary>How precisely an erasure can be reported — degrades without subject context.</summary>
public enum ReportingFidelity
{
    /// <summary>Subject context supplied: status reportable per subject.</summary>
    SubjectLevel,

    /// <summary>No subject context: status reportable only as "these instances exist / terminated / are held".</summary>
    InstanceLevel,
}

/// <summary>The erasure status of one instance.</summary>
public sealed record InstanceErasureStatus(string InstanceId, ErasureState State);

/// <summary>
/// An erasure-request report. <see cref="Fidelity"/> is the floor: with no subject
/// context the framework can speak only at instance level, and <see cref="Subjects"/> is
/// empty. <see cref="UsingDefaultPolicy"/> surfaces the conservative-default flag.
/// </summary>
public sealed record ErasureReport(
    string RequestId,
    ReportingFidelity Fidelity,
    bool UsingDefaultPolicy,
    IReadOnlyList<InstanceErasureStatus> Instances,
    IReadOnlyList<string> Subjects)
{
    /// <summary>True only when subject context was supplied; otherwise reporting is instance-level.</summary>
    public bool CanReportPerSubject => Fidelity == ReportingFidelity.SubjectLevel;
}

/// <summary>
/// Builds an <see cref="ErasureReport"/> for a request, choosing the reporting-fidelity
/// floor: subject-level only when the request carried subject context, instance-level
/// otherwise. A consumer with retention obligations must supply subject context to get
/// subject-accurate erasure reporting.
/// </summary>
public sealed class ErasureReporter
{
    public ErasureReport Report(
        ErasureRequest request,
        DeadlineStatus deadline,
        IReadOnlyDictionary<string, ErasureState> instanceStates)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(deadline);
        ArgumentNullException.ThrowIfNull(instanceStates);

        bool haveSubjects = request.Subjects.Count > 0;
        var instances = instanceStates
            .Select(kv => new InstanceErasureStatus(kv.Key, kv.Value))
            .ToList();

        return new ErasureReport(
            request.RequestId,
            haveSubjects ? ReportingFidelity.SubjectLevel : ReportingFidelity.InstanceLevel,
            deadline.UsingDefaultPolicy,
            instances,
            haveSubjects ? request.Subjects : []);
    }
}
