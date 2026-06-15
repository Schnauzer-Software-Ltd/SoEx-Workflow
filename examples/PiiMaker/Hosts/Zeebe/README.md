# Host: onboarding · Camunda 8 / Zeebe · native BPMN

Camunda 8 / Zeebe is native-only: the flow is a BPMN graph authored in a visual editor
(`bpmn/membership-onboard.bpmn`) and deployed to the broker at startup. The broker owns the flow; this
process is the .NET job-worker side, driven from the browser as a control panel.

- **A Onboarding** — native BPMN flow:
  - each `soex-onboard-step` service task runs one governed `OnboardStep` (kind + sequence ride as task
    headers);
  - the `invite-accepted` message-catch is resumed by a correlated message;
  - a process end execution-listener job (`soex-terminal`) runs the crypto-shred termination at
    completion.

In this example the broker journals only the sealed seed (ciphertext) plus the PII-free instance id —
visible in Operate. That is a property of this flow's variables, not an enforced adapter guarantee.

Subscription, offboarding, and erasure are demonstrated on the other hosts; Zeebe carries the onboarding
flow because it is the one that exercises the BPMN service-task + message-catch + end-listener shape.

## Requires Camunda 8 Run

This host needs Camunda 8 Run (gateway `:26500`, Operate `:8090`). If it is not reachable the host prints
a message and exits.

```bash
dotnet run --project examples/PiiMaker/Hosts/Zeebe/PiiMaker.Host.Zeebe.csproj
```

The example connects with the plaintext `ZeebeWorkflowHost.Connect` because the gateway is on loopback.
A networked gateway should use `ZeebeWorkflowHost.ConnectSecure` (TLS, optional access token) instead.
