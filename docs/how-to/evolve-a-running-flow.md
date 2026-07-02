> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to evolve a flow that has instances running

You need to change a flow that already has live instances, and you want the change to go out without
breaking the instances mid-flight. This guide gives you the decision and the steps. For why each runtime
behaves the way it does, see [Versioning and evolution](../explanation/versioning-and-evolution.md).

## Step 1 — classify the change

Decide which kind of change you are making, because it determines whether you can just redeploy.

- **Backward-compatible.** A bug fix, or a step-input change that only adds fields and keeps handling the
  old step kinds. An in-flight instance can resume into this new code and still make sense of the state
  it already produced.
- **Breaking.** A step that now requires state an earlier version never produced, a retired or renamed
  step kind that an in-flight instance is about to route to, or a change to the control flow of a native
  orchestration.

## Step 2 — for a backward-compatible change on the portable flow, redeploy

The portable flow runs your step off the replay path, so a backward-compatible change needs nothing
special. Redeploy. In-flight instances resume into the new code from the point they were paused, and
steps already recorded are not re-run. This is the roll-forward behaviour, and it is the common case.

Confirm it the way you would confirm any deploy: let an in-flight instance resume (raise its next event
or let its timer fire) and check it completes, and start a fresh instance and check it exercises the new
code.

## Step 3 — for a breaking change, isolate the versions

A breaking change must not reach the instances that are still on the old version. Pick one of the two
approaches below.

### Option A — pin-and-drain by id space (portable, and native on pinned runtimes)

Give the new version its own instance-id space by putting a version token in the id prefix, then move new
starts onto it and let the old ones finish.

1. Add a version token to the prefix your start-side code passes to `DeterministicInstanceId`:

   ```csharp
   // was: DeterministicInstanceId.For("onboard", orgId, email)
   var id = DeterministicInstanceId.For("onboard.v2", orgId, email);
   ```

2. Deploy the new code and cut new starts over to the new prefix.
3. Keep serving the in-flight v1 instances under the old prefix until they drain. A caller re-deriving an
   id to raise an event has to target the right version's prefix, so during the drain window either try
   the current version and fall back to the previous one, or hold on to the id you got at start time.

On Elsa, Restate, and Zeebe the runtime already keeps the two definition versions apart, so the id split
is mostly about routing new starts. On Temporal and Durable Task portable flows the id split plus the
redeploy is enough, because the step code is off the replay path.

### Option B — the runtime's own versioning tool (native flows)

If you author the flow natively, reach for the runtime's own mechanism so old journals never replay under
new code:

- **Durable Task (DTFx / DTS)** — deploy the new orchestration under a different name and point new starts
  at it. `DurableTaskWorkflowGateway` takes the orchestration name as a constructor argument, so this is a
  one-line change on the start side. Old instances drain on the old name.
- **Temporal** — gate the change with `Workflow.Patched` / `GetVersion`, or use Worker Build-ID versioning
  to keep running instances on the old worker while new starts take the new build.
- **Elsa** — publish the new definition version; running instances stay pinned to theirs. Pin the version
  in the definition handle if you want to hold new starts on a specific version rather than the latest.
- **Restate** — deploy the new sidecar as a new deployment; in-flight invocations keep running against the
  one they started on.
- **Camunda 8 / Zeebe** — deploy the new BPMN version; new starts use it and running instances drain. Use
  Camunda's process-instance migration if you must move a running instance onto the new diagram.

You can combine Option A and Option B: split the id space and register a distinct workflow type or
orchestration name for the new version, so both the ids and the definitions are isolated.

## Step 4 — verify the drain

An evolution is finished when the last old-version instance has completed. Watch the old id space (or the
old orchestration name / definition version) drain to zero live instances before you retire the old code.
Until then, keep the old code deployed so the draining instances can still resume.

## What the framework will not do

It will not migrate a running instance from one version onto another, meaning it will not rewrite a live
instance's recorded history into a new shape. That is a runtime-native operation. Drive Temporal
patching or Camunda instance migration directly against the engine when you genuinely need it.

## See also

- [Versioning and evolution](../explanation/versioning-and-evolution.md) — the reasoning behind each step.
- [Trigger flows from outside](trigger-flows-from-outside.md) — how ids are derived and raised, which
  pin-and-drain builds on.
- [Author a native flow](author-a-native-flow.md) — the per-runtime authoring shapes referenced above.
