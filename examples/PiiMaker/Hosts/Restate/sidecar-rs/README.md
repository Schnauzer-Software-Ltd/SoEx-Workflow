> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# PiiMaker example: Restate sidecar (Rust)

This is the Restate sidecar of the example. It is separate from the library/test sidecar
(`src/SoEx.Workflow.Runtime.Restate/restate-sidecar-rs`). Restate has no .NET SDK. Thus on this runtime,
the durable flow runs out-of-process in the language of the sidecar. The sidecar calls back over HTTP to a
small .NET host for the governed step.

With this sidecar, the example can contain the native flows of the *consumer*, offboarding's fan-out
included. The shared sidecar, which the Tier-2 tests use, stays unchanged. The `PiiMaker.Host.Restate`
host does these steps automatically:

1. If the sidecar binary is missing, it builds the sidecar (`cargo build --release`).
2. It starts the sidecar and sets its environment variables.
3. It registers the sidecar with restate-server.
4. When the host stops, it kills the sidecar process.

You do not start the sidecar by hand.

## Services

| Service | Mode | Callback | Flow |
|---|---|---|---|
| `MembershipPortable` | portable (generic) | `/step` + `/terminate` | the durable step loop. It can drive any portable operation. The example host uses it for renewal (B) only. |
| `MembershipOnboard` | native | `/gov-step` + `/gov-terminate` | consumer-authored onboarding: lookup → create → reserve → invite → durable-promise wait → assign |
| `MembershipOffboard` | native | `/gov-terminate` (and calls `MembershipRevoke`) | consumer-authored offboarding: fans out a governed revocation per system in parallel, then shreds |
| `MembershipRevoke` | service | `/gov-step` | one governed revocation: the unit that `MembershipOffboard` calls concurrently |

In Restate, parallelism is a set of durable calls. The SDK requires that you await `ctx.run` immediately,
so side effects cannot run concurrently. Thus the fan-out is a set of parallel calls to
`MembershipRevoke`, joined with `DurableFuturesUnordered`. This is the usual Restate pattern.

## Environment

The sidecar reads four environment variables:

- `STEP_URL`: the address of the .NET host for the native services (`MembershipOnboard`,
  `MembershipOffboard`, `MembershipRevoke`). These services call `/gov-step` and `/gov-terminate` at this
  address. The default is `http://127.0.0.1:9091`.
- `PORTABLE_STEP_URL`: the address of the .NET host for `MembershipPortable`. This service calls `/step`
  and `/terminate` at this address. If you do not set it, the sidecar uses the value of `STEP_URL`.
- `STEP_TOKEN`: a shared secret. It is mandatory. If it is not set, the sidecar stops at startup. The
  sidecar sends it as `Authorization: Bearer …` on each callback.
- `BIND`: the address where restate-server finds the sidecar. The default is `127.0.0.1:9081`.

The `PiiMaker.Host.Restate` host starts the sidecar with these values:

| Variable | Value |
|---|---|
| `STEP_URL` | `http://127.0.0.1:9091` |
| `PORTABLE_STEP_URL` | `http://127.0.0.1:9092` |
| `STEP_TOKEN` | the value of `PIIMAKER_STEP_TOKEN`, or `pii-example-token` if it is not set |
| `BIND` | `127.0.0.1:9081` |

The two callback hosts use separate ports, so the native flows and the portable flow can run at the same
time. The default ports are `:9091`/`:9081`. Thus this sidecar can run together with the library/test
sidecar (`:9090`/`:9080`).
