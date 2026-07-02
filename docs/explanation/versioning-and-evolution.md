> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Versioning and evolution

You have instances running. You change a flow and redeploy. What happens to the instances that were
already in flight when the new code went out?

SoEx.Workflow ships no versioning type and no migration engine, so the honest answer is "it depends on
two things you already chose": which consumption model the instance runs under, and which runtime it
runs on. This page explains both, and shows the one pattern that gives you hard isolation between an old
and a new version without any framework machinery. For the step-by-step recipe, see
[Evolve a running flow](../how-to/evolve-a-running-flow.md).

## The portable flow rolls forward

In the [portable flow](consumption-models.md) the orchestration is framework code and never changes
with your business logic. Your operation runs as a step off the replay path (a Temporal or Durable Task
activity, an HTTP call on Restate, a direct dispatch in-process). Because your code runs off the replay
path, changing it does not produce a non-determinism error. An in-flight instance that resumes after you
redeploy runs its remaining steps under the new code; steps already recorded are not re-executed.

So the default for the portable flow is roll-forward: a paused instance picks up your latest code from
the point it resumes. That is what you want for a bug fix or an additive change, and it matches how a
re-drive works everywhere else in the system, which is against whatever is currently deployed.

The case to watch is a change where the new step code cannot cope with state an earlier version already
produced. If version 2 of a step reads a field that version 1 never sealed into the seed, or retires a
step kind an in-flight instance is about to route to, nothing will flag it at replay time. The instance
simply fails, or behaves wrongly, when it reaches the changed step. Two ways to stay safe:

- Keep step inputs backward-compatible. Add fields rather than removing or repurposing them, and keep
  handling the old kinds while any instance might still route to them.
- Or segregate the versions so old instances never meet new code. See [pin-and-drain](#pin-and-drain-with-no-framework-machinery)
  below.

One caveat sits above your own code: upgrading the SoEx.Workflow package itself can change the framework
orchestration that the portable flow replays. Treat a framework upgrade the way you would treat any
change to workflow code on your runtime, and if the orchestration shape changed between versions, drain
or pin across the upgrade rather than redeploying under running instances.

## The native flow follows its runtime's rules

In a [native flow](../how-to/author-a-native-flow.md) you author the orchestration yourself, so the
flow's control structure is the thing being replayed or resumed. Each runtime has its own rules for
changing that structure under running instances, and this is where the runtime you picked matters most.

## What each runtime does on redeploy

| Runtime | In-flight instances on redeploy | Tool for a breaking change |
|---|---|---|
| **Durable Task (DTFx / DTS)** | Portable: safe, the step is an activity. Native: the orchestrator is replayed and there is no in-code patch API, so a changed orchestrator risks a corrupt replay. | Deploy the new version under a different orchestration name and route new starts to it; old instances drain on the old name. The gateway takes the orchestration name as a parameter. |
| **Temporal** | Portable: safe, the step is an activity. Native: the `[Workflow]` is replayed and a change to its control flow can throw a non-determinism error. | Use Temporal's own facilities: `Workflow.Patched` / `GetVersion` to gate the change, or Worker Build-ID versioning to keep running instances on the old worker while new starts take the new build. |
| **Elsa** | Definitions are versioned natively. Running instances stay pinned to the version they started on; a newly published version only affects new starts. | The gateway starts `VersionOptions.Latest`, so new starts pick up your latest published definition while in-flight instances finish on theirs. Pin a specific version in the definition handle to hold new starts back. |
| **Restate** | Deployments are versioned natively. In-flight invocations keep running against the deployment they started on; a new deployment serves new invocations. | The flow lives in the sidecar binary, so a new version is a new sidecar deployment; Restate's deployment model handles the cutover. |
| **Camunda 8 / Zeebe** (native only) | BPMN process definitions are versioned natively. Running instances stay on the version they were created under; a new deployment gets a new version number that only new starts use. | The gateway starts `.LatestVersion()`, so new starts use your newest diagram while in-flight instances drain. Camunda also supports explicit process-instance migration (mapping old activities to new), which you drive against the engine, outside the framework. |
| **InProc** | Keeps no state across a restart; it is for tests and demos. A restart loses in-flight instances, so there is no in-flight-versioning question to answer. | Not applicable. |

The pattern across the table: on the runtimes that pin definitions natively (Elsa, Restate, Zeebe) an
in-flight instance is already isolated from a new deployment, and you mostly decide when new starts move
to the new version. On the event-sourced runtimes (Temporal, Durable Task) the portable flow is safe
because your code is an activity, and the native flow is where you reach for the runtime's own
versioning tool.

## Pin-and-drain with no framework machinery

When you need old instances to never touch new code, whether because a change to step state is not
backward-compatible or because it is a native flow you cannot safely patch, you can segregate the two
versions today using the same id derivation you already use to trigger flows. `DeterministicInstanceId`
folds its prefix into the id, so a version token in the prefix gives each version its own id space:

```csharp
// v1 in production today
var id = DeterministicInstanceId.For("onboard", orgId, email);

// cut over: new starts land in a distinct id space that never collides with v1's ids
var id = DeterministicInstanceId.For("onboard.v2", orgId, email);
```

Point your start-side code at the new prefix when you cut over. Keep raising events at the in-flight v1
instances under the old prefix until they finish, which is the drain. New `onboard.v2` starts get fresh
ids that cannot collide with the `onboard` instances still running.

That id split does the whole job on the runtimes that pin definitions natively (Elsa, Restate, Zeebe),
and on Temporal or Durable Task portable flows the id space plus a redeploy is enough because the step
code runs off the replay path. For a native Temporal or Durable Task flow, combine the id-space split
with a distinct workflow type or orchestration name so the old journals never replay under new code.

There is a trade-off to design for. A caller that re-derives an id in order to raise an event has to
know which version's prefix the target instance is on. During a drain window that usually means trying
the current version and falling back to the previous one, or holding on to the id you were given at
start time instead of re-deriving it.

## Why there is no migration

The framework does not rewrite a running instance from one version's definition into another. That is
deliberate, and it is a different decision from [why there is no migration between consumption
models](consumption-models.md#why-theres-no-migration). When you fix code and redeploy, a re-drive runs
against whatever is currently deployed, which is the roll-forward above; when you need isolation, you
drain. True in-flight migration, meaning rewriting a running instance's recorded history to a new shape,
is a runtime-native operation such as Temporal patching or Camunda instance migration, and you drive it
directly against the engine.

## See also

- [Evolve a running flow](../how-to/evolve-a-running-flow.md) — the recipe that puts this to work.
- [Consumption models](consumption-models.md) — the portable and native models and the replay paths.
- [Runtime matrix](../reference/runtime-matrix.md) — the per-runtime evolution summary alongside the
  other divergences.
- [The triggering seam](the-triggering-seam.md) — how `DeterministicInstanceId` derives an id, which the
  pin-and-drain pattern builds on.
