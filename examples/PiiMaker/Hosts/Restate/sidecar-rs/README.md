# PiiMaker example — Restate sidecar (Rust)

The example's own Restate sidecar, separate from the library/test sidecar
(`src/SoEx.Workflow.Runtime.Restate/restate-sidecar-rs`). Restate ships no .NET SDK, so on this runtime the
durable flow runs out-of-process in the sidecar's language and calls back to a thin .NET governed-step
host over HTTP.

This sidecar exists so the example can author the *consumer's* native flows — including offboarding's
fan-out — without modifying the shared sidecar the Tier-2 tests depend on. The `PiiMaker.Host.Restate`
host builds (`cargo build --release`), spawns, registers and kills it automatically; you don't run it
by hand.

## Services

| Service | Mode | Callback | Flow |
|---|---|---|---|
| `MembershipPortable` | portable (generic) | `/step` + `/terminate` | the durable step loop; drives any portable operation (onboarding A, renewal B) |
| `MembershipOnboard` | native | `/gov-step` + `/gov-terminate` | consumer-authored onboarding: lookup → create → reserve → invite → durable-promise wait → assign |
| `MembershipOffboard` | native | (calls `MembershipRevoke`) | consumer-authored offboarding: fans out a governed revocation per system in parallel, then shreds |
| `MembershipRevoke` | service | `/gov-step` | one governed revocation — the unit `MembershipOffboard` calls concurrently |

A note on parallelism: Restate parallelism is durable calls, not concurrent side-effects (the SDK
requires `ctx.run` to be awaited immediately). So the fan-out is modelled as parallel calls to
`MembershipRevoke`, joined with `DurableFuturesUnordered`, which is the idiomatic Restate pattern.

## Environment

- `STEP_URL` — where the sidecar calls back into .NET (default `http://127.0.0.1:9091`).
- `STEP_TOKEN` — required shared secret presented as `Authorization: Bearer …` on every callback.
- `BIND` — where restate-server discovers the sidecar (default `127.0.0.1:9081`).

Ports default to `:9091`/`:9081` so this sidecar can coexist with the library/test sidecar
(`:9090`/`:9080`).
