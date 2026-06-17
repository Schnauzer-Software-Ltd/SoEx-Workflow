namespace SoEx.Workflow.Runtime.InMemory;

/// <summary>
/// The in-memory, null-durability runtime — the Tier-1 baseline runtime. State, the
/// step sequence, driver-owned events, and a time-skipping clock all live in process
/// memory; nothing survives a restart (by design). The clock is advanced explicitly
/// (<see cref="Advance"/>) so durable timers fire instantly under test.
/// </summary>
public sealed class InMemoryWorkflowRuntime(string instanceId) : IWorkflowRuntime
{
    private readonly Dictionary<string, object?> _state = [];
    private long _sequence;

    // Events and timers are reached from outside the driver's own flow (a gateway raises events,
    // a test advances time, while the driver runs concurrently), so guard them with one lock.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<byte[]>> _waiters = [];
    private readonly Dictionary<string, byte[]> _delivered = [];

    // Raise ids already handled for this instance — the per-instance dedup state for idempotent raises.
    // A re-raise carrying a handled id is dropped; ids persist across generations (an id is a business
    // idempotency token, not a per-generation one), so a retried raise stays a no-op.
    private readonly HashSet<string> _handledRaiseIds = [];

    private readonly List<PendingTimer> _timers = [];
    private TimeSpan _clock;

    public string InstanceId { get; } = instanceId;

    public long NextSequence() => _sequence++;

    public Task DelayAsync(TimeSpan delay)
    {
        lock (_gate)
        {
            var timer = new PendingTimer(_clock + delay);
            _timers.Add(timer);
            return timer.Task;
        }
    }

    public Task<byte[]> WaitForEventAsync(string eventName)
    {
        lock (_gate)
        {
            if (_delivered.Remove(eventName, out byte[]? buffered))
            {
                return Task.FromResult(buffered);
            }

            if (!_waiters.TryGetValue(eventName, out TaskCompletionSource<byte[]>? waiter))
            {
                _waiters[eventName] = waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return waiter.Task;
        }
    }

    public Task RaiseEventAsync(string instanceId, string eventName, byte[] payload, string? raiseId = null)
    {
        if (instanceId == InstanceId)
        {
            TaskCompletionSource<byte[]>? waiter;
            lock (_gate)
            {
                // Idempotent raise: a re-raise carrying an already-handled id is dropped, so it cannot
                // satisfy a wait twice. A raise with no id (or a new id) falls through and delivers.
                if (raiseId is not null && !_handledRaiseIds.Add(raiseId))
                {
                    return Task.CompletedTask;
                }

                if (!_waiters.Remove(eventName, out waiter))
                {
                    _delivered[eventName] = payload;
                }
            }

            waiter?.SetResult(payload);
        }

        return Task.CompletedTask;
    }

    /// <summary>Advances the in-memory clock, firing any timers whose deadline has passed (each fires once).</summary>
    public void Advance(TimeSpan by)
    {
        List<PendingTimer> due;
        TimeSpan now;
        lock (_gate)
        {
            _clock += by;
            now = _clock;
            due = [.. _timers];
        }

        foreach (PendingTimer timer in due)
        {
            timer.FireIfDue(now);
        }
    }

    public T? GetState<T>(string key) => _state.TryGetValue(key, out object? value) ? (T?)value : default;

    public void SetState<T>(string key, T value) => _state[key] = value;

    public bool ShouldContinueAsNew { get; private set; }

    public byte[]? CarryState { get; private set; }

    public void ContinueAsNew(byte[] carryState)
    {
        ShouldContinueAsNew = true;
        CarryState = carryState;
    }

    private sealed class PendingTimer(TimeSpan fireAt)
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _fired;

        public Task Task => _tcs.Task;

        public void FireIfDue(TimeSpan now)
        {
            if (!_fired && fireAt <= now)
            {
                _fired = true;
                _tcs.TrySetResult();
            }
        }
    }
}
