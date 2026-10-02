# AI Provenance

This repository contains code and documentation produced with AI assistance
(Anthropic Claude).

## Boundary

The SoEx core libraries are a separate, human-authored project. Code from this
repository must not be copied, merged, or otherwise contributed into the SoEx
core repository. This project integrates with SoEx through its published package
API only.

This marker exists so the origin of code in this repository is unambiguous and
the boundary with SoEx core is auditable.

## Note on the SoEx core serializer

SoEx.Workflow relies on the host-supplied `IMessageSerializer`. The default SoEx
serializer is System.Text.Json bound to declared types plus an explicit known-types
list. SoEx also ships a polymorphic Newtonsoft serializer (`TypeNameHandling.All`)
that a host can select instead; SoEx.Workflow keeps that gadget surface unreachable
by authenticated-decrypting every payload under the per-instance key before
deserializing it (see
[Crypto-shred and erasure](docs/explanation/crypto-shred-and-erasure.md#deserialization-safety-rests-on-the-seal)).
This is recorded so the dependency is auditable, not actionable in this repository.
