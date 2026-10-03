> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Transport security

Crypto-shred protects data at rest. SoEx seals each payload that it journals. When the framework
destroys the per-instance key, the sealed payloads become unrecoverable. Transport security protects
data in flight. This page describes each network connection that the framework and its adapters use. For
each connection, it gives the data that crosses it, the default, and the procedure to secure it off the
local host. The [threat model](../explanation/crypto-shred-and-erasure.md#threat-model) describes the
protection at rest.

## Summary

Sealed payloads are ciphertext on each connection. Thus, the payload data stays protected on a
plaintext connection. These values travel with the payload and need protection:

- bearer tokens
- connection credentials
- the plaintext that a server-side key store needs
- the instance id, which is PII-free but can be correlated

Two of the connections use plain HTTP by default. Use TLS on all networks other than a loopback or a
trusted private network.

## Who builds the connection

Some connections are built by the framework. You build the others and give them to the framework. This
sets where you configure TLS.

- **Built by the framework (the adapter opens it):** the Zeebe gateway client, the OpenBao key store, and
  the Restate sidecar and its .NET step host. Each of these has a seam for TLS. The sections below
  describe them.
- **Built by the consumer (you give a configured client or connection):** Temporal, Durable Task,
  RavenDB, and Elsa. The adapter takes your `IWorkerClient`, connection string, `IDocumentStore`, or Elsa
  module. Configure TLS as for all other uses of those SDKs. SoEx does not add or remove TLS.

## Connections built by the framework

### Restate (sidecar to .NET step host)

The Rust sidecar calls the .NET step host (`STEP_URL`) on each step and termination. The host binds the
URL that you give to `RestateWorkflowHost.Build`. The payloads are sealed. The shared bearer token
(`STEP_TOKEN`) crosses in clear text. Thus, plain HTTP exposes the token.

- Default: `http://127.0.0.1:9090`, loopback. This is satisfactory when the sidecar and the host are on
  the same machine.
- To secure the connection across a network:
  1. Give the sidecar an `https://` `STEP_URL`. TLS verifies against the system or web-PKI roots. To
     trust a private CA, set `STEP_CA_CERT` to a PEM file.
  2. Serve the .NET host over HTTPS. Give an `https://` `stepUrl` and the optional `serverCertificate` to
     `RestateWorkflowHost.Build`. Alternatively, configure the certificate through the standard Kestrel
     configuration (`Kestrel:Certificates:Default`).

  See the [Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md).

The host compares the token in constant time. The host does not start without a token.

### Zeebe (gRPC to the gateway)

`ZeebeWorkflowHost.Connect(gatewayAddress)` opens a plaintext gRPC client. Use it for a loopback gateway
only. On a network, it sends the gateway credentials and the journaled variables in clear text.

For a gateway on a network, use `ZeebeWorkflowHost.ConnectSecure(gatewayAddress, rootCertificatePath, accessToken)`.
This method uses TLS. To pin a private CA, give `rootCertificatePath`. To use the OS trust store, omit it.
If the gateway requires a bearer token, the method attaches it. You can also build your own
`IZeebeClient` and give it to `DeployAsync`, `OpenStepWorker`, and `OpenTerminationListener`.

### OpenBao (key store)

`OpenBaoInstanceKeyStore` does its cryptography on the server. On each seal, it sends the plaintext
(base64) to the server. On each unseal, it receives the plaintext from the server. The other key stores
do not send plaintext. Thus, a plain `http://` address sends the protected data and the token in clear
text.

- In production, use an `https://` address. For development, loopback `http` is satisfactory.
- If you need a custom handler, a client certificate, or a pinned CA, inject an `IVaultClient` with TLS or
  mTLS. Use the `OpenBaoInstanceKeyStore(IVaultClient, mountPoint)` overload.

The RavenDB key store sends only wrapped (sealed) key material over the network.

## Connections built by the consumer

These adapters take a client or a connection that you configure. Secure them as for all other uses of
the SDK:

- **Temporal:** Build the `TemporalClient` with `TlsOptions` and the API-key or mTLS authentication that
  you need. Give the client to `TemporalWorkflowHost.BuildWorker`.
- **Durable Task:** The connection string that you give to `DurableTaskWorkflowHost.Build` sets the
  transport and the authentication. The local DTS emulator uses `Endpoint=http://…;Authentication=None`.
  A hosted Durable Task Scheduler uses TLS and an authentication mode. Set them in the connection string.
- **RavenDB** (key store, subject index, idempotency, maintenance registries): Configure the
  `IDocumentStore` that you give with an `https` URL and a client certificate (`DocumentStore.Certificate`)
  for mTLS.
- **Elsa:** The adapter opens no connections. The persistence that you configure has its own TLS
  settings, for example EF Core over a network database.

## In-process

The in-process runtime opens no network connections. All data stays in memory in one process.
