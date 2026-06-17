> [!IMPORTANT]
> This file was LLM generated and is pending editing by the project maintainer.

# Transport security

Crypto-shred is a data-at-rest guarantee: every payload SoEx journals is sealed, and destroying the
per-instance key makes it unrecoverable. It says nothing about data *in flight*. This page covers the
network hops the framework and its adapters use — what crosses each one, what the default is, and how to
secure it when a hop leaves the local host. It is the in-flight companion to the
[threat model](../explanation/crypto-shred-and-erasure.md#threat-model).

The short version: sealed payloads are ciphertext wherever they travel, so the data itself is safe in
flight even on a plaintext connection. The exposure is everything *around* the payload — bearer tokens,
connection credentials, the plaintext a server-side key store needs, and the PII-free-but-correlatable
instance id. Two of the connections also default to plain HTTP. So on anything other than a loopback or a
trusted private network, use TLS.

## Who builds the connection

It matters whether the framework opens a connection for you or you hand it a client you built, because that
decides where the TLS knob lives.

- **Framework-built (the adapter opens it):** the Zeebe gateway client, the OpenBao key store, the Restate
  sidecar and its .NET step host. These have an explicit seam described below.
- **Consumer-built (you pass a configured client/connection):** Temporal, Durable Task, RavenDB, and Elsa.
  The adapter takes your `IWorkerClient` / connection string / `IDocumentStore` / Elsa module, so TLS is
  configured the same way you would for any other use of those SDKs — SoEx neither adds nor removes it.

## Framework-built connections

### Restate (sidecar to .NET step host)

The Rust sidecar calls back to the .NET step host (`STEP_URL`) on every step and termination, and the host
binds whatever URL `RestateWorkflowHost.Build` is given. Payloads are sealed, but the shared bearer token
(`STEP_TOKEN`) crosses in the clear, so plain HTTP exposes it.

- Default: `http://127.0.0.1:9090`, loopback. Fine for co-located sidecar and host.
- To secure across a network: give the sidecar an `https://` `STEP_URL` (TLS verifies against the
  system/web-PKI roots; set `STEP_CA_CERT` to a PEM file to trust a private CA), and serve the .NET host
  over HTTPS — pass an `https://` `stepUrl` to `RestateWorkflowHost.Build` with the optional
  `serverCertificate`, or configure the certificate through standard Kestrel config
  (`Kestrel:Certificates:Default`). See the [Restate adapter README](../../src/SoEx.Workflow.Runtime.Restate/README.md).

The token is compared in constant time, and the host refuses to start without one.

### Zeebe (gRPC to the gateway)

`ZeebeWorkflowHost.Connect(gatewayAddress)` opens a **plaintext** gRPC client — correct for a loopback
gateway, but it ships the gateway credentials and the journaled variables in the clear over a network. For
a networked gateway use `ZeebeWorkflowHost.ConnectSecure(gatewayAddress, rootCertificatePath, accessToken)`,
which negotiates TLS (pin a private CA with `rootCertificatePath`, omit it for the OS trust store) and
attaches a bearer token if the gateway requires one. You can also build your own `IZeebeClient` and pass it
to `DeployAsync`/`OpenStepWorker`/`OpenTerminationListener`.

### OpenBao (key store)

`OpenBaoInstanceKeyStore` does its crypto server-side, so unlike the other key stores it sends the
*plaintext* (base64) to the server on every seal and receives it back on every unseal. A plain `http://`
address therefore ships both the protected data and the token in the clear. Use an `https://` address in
production (loopback `http` is fine for dev), or inject a TLS/mTLS-configured `IVaultClient` through the
`OpenBaoInstanceKeyStore(IVaultClient, mountPoint)` overload when you need a custom handler, client
certificate, or pinned CA. The RavenDB key store, by contrast, only ever puts wrapped (sealed) key material
on the wire.

## Consumer-built connections

These adapters take a client or connection you configure, so secure them as you would any other use of the
SDK:

- **Temporal:** build the `TemporalClient` with `TlsOptions` (and any API-key/mTLS auth) and pass the
  resulting client to `TemporalWorkflowHost.BuildWorker`.
- **Durable Task:** the connection string you give `DurableTaskWorkflowHost.Build` carries the transport
  and auth. The local DTS emulator is `Endpoint=http://…;Authentication=None`; a hosted Durable Task
  Scheduler uses TLS and an authentication mode — set them in the connection string.
- **RavenDB** (key store, subject index, idempotency, maintenance registries): configure the
  `IDocumentStore` you pass in with an `https` URL and a client certificate (`DocumentStore.Certificate`)
  for mTLS.
- **Elsa:** the adapter opens no connections of its own; whatever persistence you configure (e.g. EF Core
  over a network database) carries its own TLS settings.

## In-process

The in-process runtime opens no network connections — everything stays in memory in one process.
