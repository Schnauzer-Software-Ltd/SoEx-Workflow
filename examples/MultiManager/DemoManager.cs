using System.Text;
using SoEx.Workflow;

// A stand-in business manager. Its only role in this demo is the erasure contract: it reads its
// instance's still-live payload in OnRetaining (the must-retain carve-out point) and records that it
// handled that instance — so we can show each manager only ever drives its own instances.
internal sealed class DemoManager(string name, IInstanceKeyStore keys, IReadOnlyDictionary<string, byte[]> sealedPayloads)
    : IErasureEvents
{
    public List<string> Retained { get; } = [];

    public Task OnRetaining(RetainingContext context)
    {
        // The key is still live here, so the payload is readable — extract what must be retained.
        byte[] plain = keys.Decrypt(context.InstanceId, sealedPayloads[context.InstanceId]);
        Retained.Add($"{context.InstanceId} (read \"{Encoding.UTF8.GetString(plain)}\")");
        return Task.CompletedTask;
    }

    public Task OnTerminated(TerminatedContext context) => Task.CompletedTask;

    public Task OnRetentionHeld(RetentionHeldContext context) => Task.CompletedTask;

    public override string ToString() => name;
}
