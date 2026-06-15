using System.Text;
using SoEx.Method.Workflow;
using SoEx.Workflow;
using SoEx.Workflow.InMemory;

// =============================================================================================
// Example: TWO managers, ONE workflow utility, ONE runtime — multi-manager right-to-erasure.
//
// A single SoEx.Workflow utility hosts two distinct business managers (Onboarding and Billing) that
// share one set of durable stores and one runtime. When a "forget subject S" request arrives, the
// utility must drive each in-flight instance to crypto-shred through ITS OWNING manager's erasure
// contract — not one global contract for all. This program shows that end to end:
//   * the async, durable front door  — RequestErase admits and returns at once; Drain shreds later;
//   * the synchronous shred itself    — routed per instance by the namespaced instance-id prefix;
//   * crypto-shred                    — each payload is readable before the shred, unrecoverable after.
//
// Run:  dotnet run --project examples/MultiManager
// =============================================================================================

// One utility's shared, durable stores. In-memory here; a real deployment swaps in OpenBao/RavenDB
// for the key store and a durable subject index / pending-request store (the wiring is identical).
var keys = new InMemoryInstanceKeyStore();
var index = new InMemorySubjectIndex();
var pending = new InMemoryPendingErasureRequests();

// The sealed first-step payload each "started" instance persists, kept here so each manager can read
// its own back inside OnRetaining (a real flow stores this in the runtime's journal).
var sealedPayloads = new Dictionary<string, byte[]>();

// Two managers. Each owns its erasure contract: in OnRetaining (while the key is still live) it reads
// its instance's payload — proving the data was readable — records a must-retain carve-out, then the
// utility shreds the key.
var onboarding = new DemoManager("Onboarding", keys, sealedPayloads);
var billing = new DemoManager("Billing", keys, sealedPayloads);

// The routing: the utility resolves the owning manager per instance from the instance-id prefix. With
// one manager this would be the single framework proxy; with several, this map names the owner.
var routing = ErasureRouting.ByPrefix(new Dictionary<string, IErasureEvents>
{
    ["onboarding"] = onboarding,
    ["billing"] = billing,
});

var utility = new WorkflowUtility(new WorkflowSeam(), keys, index, resolveErasureFor: routing, pending: pending);

// "Start" one instance under each manager for the same person. A real start runs a GovernedStep, which
// mints the per-instance key, seals the first step under it, and indexes the subject; the helper does
// those three directly to keep the demo on the erasure-routing concern.
const string subject = "alice@example.com";
string onboardingInstance = StartInstance("onboarding", subject, "onboarding state for alice");
string billingInstance = StartInstance("billing", subject, "billing state for alice");

Console.WriteLine($"Two live instances for {subject}, sharing one utility + one runtime:");
Console.WriteLine($"  onboarding -> {onboardingInstance}");
Console.WriteLine($"  billing    -> {billingInstance}");
Console.WriteLine($"  payloads readable now?  onboarding={CanRead(onboardingInstance)}  billing={CanRead(billingInstance)}");
Console.WriteLine();

// The async front door: admit the erasure request (returns immediately, nothing shredded), then drain
// it on a later pass — exactly where a host would schedule the drain.
string requestId = await utility.RequestEraseAsync(subject);
Console.WriteLine($"Admitted erasure request '{requestId}'. Pending: {pending.Pending().Count}. Nothing shredded yet.");

int drained = await utility.DrainEraseRequestsAsync();
Console.WriteLine($"Drained {drained} request(s) — the synchronous, per-manager shred ran.");
Console.WriteLine();

// Each manager retained ONLY its own instance, and both keys are now shredded.
Console.WriteLine($"Onboarding manager retained: {string.Join("; ", onboarding.Retained)}");
Console.WriteLine($"Billing manager retained:    {string.Join("; ", billing.Retained)}");
Console.WriteLine($"  payloads readable after shred?  onboarding={CanRead(onboardingInstance)}  billing={CanRead(billingInstance)}");
Console.WriteLine();
Console.WriteLine("Each instance was erased through its OWNING manager's contract, on one shared utility:");
Console.WriteLine("multi-manager routing, a synchronous shred core, and an async durable intake.");

string StartInstance(string prefix, string subj, string state)
{
    string id = DeterministicInstanceId.For(prefix, subj);
    keys.Mint(id);
    sealedPayloads[id] = keys.Encrypt(id, Encoding.UTF8.GetBytes(state));
    index.AddEdge(subj, id);
    return id;
}

bool CanRead(string instanceId)
{
    try
    {
        keys.Decrypt(instanceId, sealedPayloads[instanceId]);
        return true;
    }
    catch
    {
        return false;
    }
}

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
