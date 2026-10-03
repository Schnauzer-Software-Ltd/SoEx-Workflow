> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# How to evolve a flow that has instances running

Use this guide to change a flow that has live instances. The live instances must continue to work after
the change. This guide gives the decision and the steps.
[Versioning and evolution](../explanation/versioning-and-evolution.md) explains the behavior of each
runtime.

## 1. Classify the change

Decide which type of change you make. The type sets if a redeploy is sufficient.

- **Backward-compatible.** A bug fix, or a change to a step input that only adds fields and continues to
  handle the old step kinds. An in-flight instance can resume into the new code. The new code can use the
  state that the instance produced before.
- **Breaking.** One of these changes:
  - a step that now requires state that an earlier version did not produce
  - a step kind that you retired or renamed, when an in-flight instance will route to it next
  - a change to the control flow of a native orchestration

## 2. Redeploy a backward-compatible change on the portable flow

The portable flow runs your step off the replay path. Thus a backward-compatible change needs no special
action.

1. Redeploy.

   In-flight instances resume into the new code from the point where they paused. The runtime does not
   run the recorded steps again. This behavior is roll-forward. It is the usual case.

2. Make an in-flight instance resume.

   Raise its next event, or let its timer fire.

3. Make sure that the in-flight instance completes.
4. Start a new instance.
5. Make sure that the new instance uses the new code.

## 3. Isolate the versions for a breaking change

A breaking change must not get to the instances that use the old version. Use one of the two options in
this section.

### Option A: pin-and-drain by id space (portable, and native on pinned runtimes)

In this option, the new version gets its own instance-id space. A version token in the id prefix makes
the new space. New starts go to the new space, and the old instances complete.

1. Add a version token to the prefix that your start-side code gives to `DeterministicInstanceId`:

   ```csharp
   // was: DeterministicInstanceId.For("onboard", orgId, email)
   var id = DeterministicInstanceId.For("onboard.v2", orgId, email);
   ```

2. Deploy the new code.
3. Move new starts to the new prefix.
4. Continue to serve the in-flight v1 instances under the old prefix until they drain.

   A caller that derives an id again to raise an event must use the prefix of the correct version.
   During the drain window, do one of these:

   - Try the current version first. If that fails, try the previous version.
   - Keep the id that you got at start time.

On Elsa, Restate, and Zeebe, the runtime keeps the two definition versions apart. There, the id split
mostly routes new starts. On Temporal and Durable Task, a portable flow needs only the id split and the
redeploy. The step code is off the replay path.

### Option B: the versioning tool of the runtime (native flows)

If you write the flow natively, use the versioning mechanism of the runtime. Then old journals never
replay under new code.

- **Durable Task (DTFx / DTS)**: deploy the new orchestration under a different name. Point new starts
  at the new name. `DurableTaskWorkflowGateway` takes the orchestration name as a constructor argument.
  Thus this is a one-line change on the start side. Old instances drain on the old name.
- **Temporal**: gate the change with `Workflow.Patched` or `GetVersion`. Alternatively, use Worker
  Build-ID versioning. It keeps the instances that run on the old worker. New starts use the new build.
- **Elsa**: publish the new definition version. Instances that run stay pinned to their version. To keep
  new starts on a specific version, pin the version in the definition handle. Otherwise new starts use the
  latest version.
- **Restate**: deploy the new sidecar as a new deployment. In-flight invocations continue on the
  deployment where they started.
- **Camunda 8 / Zeebe**: deploy the new BPMN version. New starts use it, and instances that run drain. To
  move an instance that runs onto the new diagram, use the process-instance migration of Camunda.

You can use Option A and Option B together. Split the id space. Also register a different workflow type or
orchestration name for the new version. Then the ids and the definitions are both isolated.

## 4. Verify the drain

An evolution is complete when the last old-version instance completes.

1. Monitor the old id space until it has zero live instances.

   For Option B, monitor the old orchestration name or the old definition version.

2. Keep the old code deployed until the drain is complete.

   The instances in the drain must be able to resume.

3. Retire the old code.

## Migration of a running instance

The framework does not migrate a running instance from one version to a different version. It does not
change the recorded history of a live instance into a new shape. Migration is an operation of the
runtime. If you must migrate, use Temporal patching or Camunda instance migration directly on the runtime.

## See also

- [Versioning and evolution](../explanation/versioning-and-evolution.md) explains the reasons for each
  step.
- [Trigger flows from outside](trigger-flows-from-outside.md) tells how to derive ids and raise events.
  Pin-and-drain uses these.
- [Author a native flow](author-a-native-flow.md) gives the code shape for each runtime.
