> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Host: onboarding · Camunda 8 / Zeebe · native BPMN

Camunda 8 / Zeebe supports the native flow only. The flow is a BPMN graph
(`bpmn/membership-onboard.bpmn`) that you draw in a visual editor. The host deploys it to the broker at
startup. The broker owns the flow. This process is the .NET job worker. It is also a web control panel on
port 5006, and it runs until you stop it. The panel operates onboarding (A) and the erasure sweep (D).

- **A Onboarding**: a native BPMN flow.
  - Each `soex-onboard-step` service task runs one governed `Onboard` step. The kind and the sequence go
    in the task headers.
  - A correlated message resumes the `invite-accepted` message-catch.
  - At completion, a process end execution-listener job (`soex-terminal`) runs the crypto-shred
    termination.

In this example, the broker journals only the sealed seed (ciphertext) and the instance id, which contains
no PII. You can see these values in Operate. This result comes from the variables of this flow. The
adapter does not enforce it.

- **D Erasure**: the **Forget subject** button sends an erasure request for the subject. The host admits
  the request, then drains it at once.

The other hosts demonstrate subscription and offboarding. The Zeebe host runs the onboarding flow because
this flow uses the BPMN shape of service task, message-catch, and end-listener.

## Requirement: Camunda 8 Run

This host needs Camunda 8 Run (gateway `127.0.0.1:26500`, Operate `:8090`). To start it, run
`./c8run start -port 8090`, or run `examples/dev/piimaker.sh --runtime zeebe`. If the host cannot reach the
gateway, it prints a message and stops.

```bash
dotnet run --project examples/PiiMaker/Hosts/Zeebe/PiiMaker.Host.Zeebe.csproj
```

The host prints the address of the panel:

```
PiiMaker Zeebe (Camunda 8) control panel → http://localhost:5006  (gateway 127.0.0.1:26500, Operate http://localhost:8090)
```

The page opens with this host (`:5006`) selected.

The example connects with the plaintext `ZeebeWorkflowHost.Connect`, because the gateway is on loopback.
For a gateway on a network, use `ZeebeWorkflowHost.ConnectSecure` (TLS, optional access token).
