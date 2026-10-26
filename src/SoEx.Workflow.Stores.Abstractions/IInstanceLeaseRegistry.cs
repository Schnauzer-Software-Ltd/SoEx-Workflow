namespace SoEx.Workflow;

/// <summary>
/// How long an instance may stay idle before the abandoned-instance sweep treats it as abandoned.
/// <see cref="ExpiresAt"/> is the moment the lease runs out; <see cref="RenewedAt"/> is when the last completed
/// step renewed it (the instance's last sign of life).
/// </summary>
public readonly record struct InstanceLease(string InstanceId, DateTimeOffset RenewedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// The per-instance leases that tell the abandoned-instance sweep how long each live instance may stay idle.
/// Each completed governed step renews its instance's lease with the idle period that step declared, so a flow
/// that enters a long wait declares a long lease and a short flow declares a short one. The sweep shreds an
/// instance whose lease has run out, and falls back to its key's age only for an instance that has no lease.
/// <para>
/// One entry per instance, keyed on the instance id. A renewal replaces the entry (it can shorten the lease as
/// well as extend it), and termination forgets it. In-process by default (<c>InMemoryInstanceLeaseRegistry</c>);
/// the step workers and the sweep host must share a durable implementation in a multi-process deployment.
/// </para>
/// </summary>
public interface IInstanceLeaseRegistry
{
    /// <summary>Records (or replaces) the lease for <see cref="InstanceLease.InstanceId"/>. Last write wins.</summary>
    void Renew(InstanceLease lease);

    /// <summary>Removes the lease for an instance. Idempotent.</summary>
    void Forget(string instanceId);

    /// <summary>A snapshot of every recorded lease.</summary>
    IReadOnlyCollection<InstanceLease> Leases();
}
