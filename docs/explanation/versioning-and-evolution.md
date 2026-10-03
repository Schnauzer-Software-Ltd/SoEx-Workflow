> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Versioning and evolution

This page describes what occurs to in-flight instances when you change a flow and redeploy it. Two
choices that you already made control the result: the consumption model of the instance and its
runtime. SoEx.Workflow has no versioning type and no migration engine. This page also gives one pattern
that fully isolates an old version from a new version with no framework machinery. For the procedure,
see [Evolve a running flow](../how-to/evolve-a-running-flow.md).

## The portable flow rolls forward

In the [portable flow](consumption-models.md), the orchestration is framework code. It stays the same
when your business logic changes. Your operation runs as a step off the replay path. On Temporal or
Durable Task the step is an activity, on Restate it is an HTTP call, and on InProc it is a direct
dispatch. Thus a change to your code causes no non-determinism error. When an in-flight instance
resumes after a redeploy, its remaining steps run the new code. The runtime does not run again the steps
that it already recorded.

The default for the portable flow is thus roll-forward. A paused instance runs your latest code from the
point where it resumes. This is correct for a bug fix or an additive change. A re-drive in all other
parts of the system works the same way: it runs against the code that is deployed now.

Incompatible state is the risk. New step code can fail on state that an earlier version made. Two
examples:

- Version 2 of a step reads a field that version 1 did not seal into the seed.
- Version 2 retires a step kind, and an in-flight instance will route to that kind.

No check finds this at replay time. The instance fails, or behaves incorrectly, when it gets to the
changed step. Use one of these two methods to prevent this:

- Keep step inputs backward-compatible. Add fields. Do not remove or repurpose fields. Continue to handle
  the old kinds while an instance can still route to them.
- Segregate the versions, so that old instances never meet new code. See
  [pin-and-drain](#pin-and-drain-with-no-framework-machinery) below.

An upgrade of the SoEx.Workflow package can change the framework orchestration that the portable flow
replays. Treat a framework upgrade as a change to workflow code on your runtime. If the orchestration
shape changed between versions, drain or pin across the upgrade. Do not redeploy under running
instances.

## The native flow follows its runtime's rules

In a [native flow](../how-to/author-a-native-flow.md), you write the orchestration. The runtime replays
or resumes the control structure of your flow. Each runtime has its own rules for a change to that
structure under running instances. For a native flow, your choice of runtime thus has the largest
effect.

## What each runtime does on redeploy

| Runtime | In-flight instances on redeploy | Tool for a breaking change |
|---|---|---|
| **Durable Task (DTFx / DTS)** | Portable: safe, because the step is an activity. Native: the runtime replays the orchestrator, and it has no in-code patch API. A changed orchestrator can corrupt the replay. | Deploy the new version under a different orchestration name and route new starts to it. Old instances drain on the old name. The gateway takes the orchestration name as a parameter. |
| **Temporal** | Portable: safe, because the step is an activity. Native: the runtime replays the `[Workflow]`. A change to its control flow can throw a non-determinism error. | Use the facilities of Temporal: `Workflow.Patched` / `GetVersion` to gate the change, or Worker Build-ID versioning. Build-ID versioning keeps running instances on the old worker, and new starts use the new build. |
| **Elsa** | Elsa versions definitions natively. Each running instance stays on the version on which it started. A newly published version applies only to new starts. | The gateway starts `VersionOptions.Latest`. New starts thus use your latest published definition, and in-flight instances finish on their own definitions. To hold back new starts, pin a specific version in the definition handle. |
| **Restate** | Restate versions deployments natively. In-flight invocations continue to run against the deployment on which they started. A new deployment serves new invocations. | The flow is in the sidecar binary, so a new version is a new sidecar deployment. The deployment model of Restate does the cutover. |
| **Camunda 8 / Zeebe** (native only) | Camunda versions BPMN process definitions natively. Each running instance stays on the version under which it was created. A new deployment gets a new version number, and only new starts use it. | The gateway starts `.LatestVersion()`. New starts thus use your newest diagram, and in-flight instances drain. Camunda also supports explicit process-instance migration, which maps old activities to new activities. You drive it against the runtime, outside the framework. |
| **InProc** | Keeps no state across a restart. Use it for tests and demos. A restart loses in-flight instances, so in-flight versioning does not apply. | Not applicable. |

The table shows two groups of runtimes:

- Elsa, Restate, and Zeebe pin definitions natively. An in-flight instance is already isolated from a
  new deployment. Your main decision is when new starts move to the new version.
- Temporal and Durable Task are event-sourced. The portable flow is safe, because your code is an
  activity. For the native flow, use the versioning tool of the runtime.

## Pin-and-drain with no framework machinery

Pin-and-drain keeps old instances away from new code. Use it when a change to step state is not
backward-compatible, or when you cannot safely patch a native flow. It uses the same id derivation that
you use to trigger flows. `DeterministicInstanceId` folds its prefix into the id. A version token in the
prefix thus gives each version its own id space:

```csharp
// v1 in production today
var id = DeterministicInstanceId.For("onboard", orgId, email);

// cut over: new starts land in a distinct id space that never collides with v1's ids
var id = DeterministicInstanceId.For("onboard.v2", orgId, email);
```

1. At the cutover, point your start-side code at the new prefix.
2. Continue to raise events at the in-flight v1 instances under the old prefix until they finish. This
   is the drain.

New `onboard.v2` starts get new ids. These ids cannot collide with the `onboard` instances that still
run.

The id split is sufficient by itself on the runtimes that pin definitions natively (Elsa, Restate,
Zeebe). On Temporal or Durable Task portable flows, the id space and a redeploy are sufficient, because
the step code runs off the replay path. For a native Temporal or Durable Task flow, add a distinct
workflow type or orchestration name to the id-space split. The old journals then never replay under new
code.

This pattern has a trade-off to design for. A caller that derives an id again to raise an event must
know the version prefix of the target instance. During a drain window, the caller usually tries the
current version and then the previous version. Alternatively, the caller keeps the id that it received
at start time and does not derive it again.

## No version migration

A running instance stays on the definition of its version. The framework gives two operations for a new
version:

- **Roll-forward.** When you fix code and redeploy, a re-drive runs against the code that is deployed
  now. This is the roll-forward above.
- **Drain.** When you need isolation, drain the old version.

The framework does not rewrite a running instance from the definition of one version into another. This
decision is deliberate. It is a different decision from [why there is no migration between consumption
models](consumption-models.md#why-theres-no-migration). In-flight migration rewrites the recorded history
of a running instance to a new shape. It is a runtime-native operation, for example Temporal patching or
Camunda instance migration. You drive it directly against the runtime.

## See also

- [Evolve a running flow](../how-to/evolve-a-running-flow.md): the recipe that applies this page.
- [Consumption models](consumption-models.md): the portable and native models and the replay paths.
- [Runtime matrix](../reference/runtime-matrix.md): the per-runtime evolution summary, with the
  other divergences.
- [The triggering seam](the-triggering-seam.md): how `DeterministicInstanceId` derives an id. The
  pin-and-drain pattern uses this derivation.
