# Third-party licenses

Attribution for the third-party dependencies of **SoEx.Workflow** (the public product repo). Lists the full *transitive* dependency closure of the shipped artifacts — the .NET adapter packages and the Rust Restate sidecar. Every dependency can be taken under a permissive license (MIT, BSD-2/3-Clause, Apache-2.0, Zlib, Unicode-3.0); none imposes a copyleft obligation. A few crates are multi-licensed `… OR LGPL` — the permissive option is always available, so no copyleft applies.

Generated 2026-06-09. .NET licenses are read from each package's `.nuspec` `<license>` expression in the local NuGet cache; Rust licenses from each crate's `Cargo.toml` `license` field. See [How this was generated](#how-this-was-generated) to reproduce. Test-only dependencies (in the private tests repo) are out of scope — they are not distributed.

## Summary

| License | .NET | Rust | Total |
|---|---:|---:|---:|
| MIT | 146 | 38 | 184 |
| MIT OR Apache-2.0 |  | 137 | 137 |
| Unicode-3.0 |  | 18 | 18 |
| Apache-2.0 | 20 | 7 | 27 |
| Apache-2.0 WITH LLVM-exception |  | 10 | 10 |
| BSD-2-Clause | 9 |  | 9 |
| BSD-3-Clause | 4 | 3 | 7 |
| MIT OR Apache-2.0 OR Apache-2.0 WITH LLVM-exception |  | 3 | 3 |
| Zlib |  | 2 | 2 |
| BSD-2-Clause OR Apache-2.0 OR MIT |  | 2 | 2 |
| Zlib OR Apache-2.0 OR MIT |  | 1 | 1 |
| MIT OR Apache-2.0 OR BSD-1-Clause |  | 1 | 1 |
| MIT OR Apache-2.0 OR LGPL-2.1-or-later |  | 1 | 1 |
| MIT OR Apache-2.0 OR Zlib |  | 1 | 1 |
| (MIT OR Apache-2.0) AND Unicode-3.0 |  | 1 | 1 |
| Unlicense OR MIT |  | 1 | 1 |
| **Total** | **179** | **226** | **405** |

## .NET dependencies (NuGet)

Transitive closure of the 15 product projects (179 packages).

### MIT (146)

- Autofac 9.1.0
- Azure.Core 1.53.0
- Azure.Identity 1.21.0
- BouncyCastle.Cryptography 2.6.2
- Cronos 0.11.1
- CShells.Abstractions 0.0.14
- CShells.AspNetCore.Abstractions 0.0.14
- CShells.FastEndpoints.Abstractions 0.0.14
- DistributedLock.Core 1.0.8
- DistributedLock.FileSystem 1.0.3
- Elsa 3.7.0
- Elsa.Api.Common 3.7.0
- Elsa.Caching 3.7.0
- Elsa.Common 3.7.0
- Elsa.Expressions 3.7.0
- Elsa.Features 3.7.0
- Elsa.KeyValues 3.7.0
- Elsa.Mediator 3.7.0
- Elsa.Tenants 3.7.0
- Elsa.Workflows.Core 3.7.0
- Elsa.Workflows.Management 3.7.0
- Elsa.Workflows.Runtime 3.7.0
- FastEndpoints 7.2.0
- FastEndpoints.Attributes 7.2.0
- FastEndpoints.Core 7.2.0
- FastEndpoints.JobQueues 7.2.0
- FastEndpoints.Messaging 7.2.0
- FastEndpoints.Messaging.Core 7.2.0
- FastEndpoints.Security 7.2.0
- FastEndpoints.Swagger 7.2.0
- Humanizer.Core 3.0.1
- JetBrains.Annotations 2025.2.4
- Lambda2Js.Signed 3.1.4 †
- LinqKit.Core 1.2.9
- Microsoft.AspNetCore.Authentication.JwtBearer 10.0.1
- Microsoft.AspNetCore.JsonPatch 8.0.18
- Microsoft.Bcl.AsyncInterfaces 10.0.3
- Microsoft.DurableTask.Abstractions 1.25.0-preview.1
- Microsoft.DurableTask.Client 1.25.0-preview.1
- Microsoft.DurableTask.Client.AzureManaged 1.25.0-preview.1
- Microsoft.DurableTask.Client.Grpc 1.25.0-preview.1
- Microsoft.DurableTask.Grpc 1.25.0-preview.1
- Microsoft.DurableTask.Worker 1.25.0-preview.1
- Microsoft.DurableTask.Worker.AzureManaged 1.25.0-preview.1
- Microsoft.DurableTask.Worker.Grpc 1.25.0-preview.1
- Microsoft.EntityFrameworkCore 10.0.3
- Microsoft.EntityFrameworkCore.Abstractions 10.0.3
- Microsoft.EntityFrameworkCore.Analyzers 10.0.3
- Microsoft.Extensions.Caching.Abstractions 10.0.3
- Microsoft.Extensions.Caching.Abstractions 8.0.0
- Microsoft.Extensions.Caching.Memory 10.0.3
- Microsoft.Extensions.Caching.Memory 8.0.1
- Microsoft.Extensions.Configuration 10.0.3
- Microsoft.Extensions.Configuration 10.0.5
- Microsoft.Extensions.Configuration.Abstractions 10.0.3
- Microsoft.Extensions.Configuration.Abstractions 10.0.5
- Microsoft.Extensions.Configuration.Binder 10.0.3
- Microsoft.Extensions.Configuration.Binder 10.0.5
- Microsoft.Extensions.Configuration.CommandLine 10.0.5
- Microsoft.Extensions.Configuration.EnvironmentVariables 10.0.5
- Microsoft.Extensions.Configuration.FileExtensions 10.0.3
- Microsoft.Extensions.Configuration.FileExtensions 10.0.5
- Microsoft.Extensions.Configuration.Json 10.0.3
- Microsoft.Extensions.Configuration.Json 10.0.5
- Microsoft.Extensions.Configuration.UserSecrets 10.0.5
- Microsoft.Extensions.DependencyInjection 10.0.3
- Microsoft.Extensions.DependencyInjection 10.0.5
- Microsoft.Extensions.DependencyInjection.Abstractions 10.0.5
- Microsoft.Extensions.DependencyModel 10.0.0
- Microsoft.Extensions.Diagnostics 10.0.5
- Microsoft.Extensions.Diagnostics.Abstractions 10.0.3
- Microsoft.Extensions.Diagnostics.Abstractions 10.0.5
- Microsoft.Extensions.FileProviders.Abstractions 10.0.3
- Microsoft.Extensions.FileProviders.Abstractions 10.0.5
- Microsoft.Extensions.FileProviders.Physical 10.0.3
- Microsoft.Extensions.FileProviders.Physical 10.0.5
- Microsoft.Extensions.FileSystemGlobbing 10.0.3
- Microsoft.Extensions.FileSystemGlobbing 10.0.5
- Microsoft.Extensions.Hosting 10.0.5
- Microsoft.Extensions.Hosting.Abstractions 10.0.3
- Microsoft.Extensions.Hosting.Abstractions 10.0.5
- Microsoft.Extensions.Logging 10.0.3
- Microsoft.Extensions.Logging 10.0.5
- Microsoft.Extensions.Logging.Abstractions 10.0.5
- Microsoft.Extensions.Logging.Configuration 10.0.5
- Microsoft.Extensions.Logging.Console 10.0.5
- Microsoft.Extensions.Logging.Debug 10.0.5
- Microsoft.Extensions.Logging.EventLog 10.0.5
- Microsoft.Extensions.Logging.EventSource 10.0.5
- Microsoft.Extensions.Options 10.0.3
- Microsoft.Extensions.Options 10.0.5
- Microsoft.Extensions.Options.ConfigurationExtensions 10.0.3
- Microsoft.Extensions.Options.ConfigurationExtensions 10.0.5
- Microsoft.Extensions.Options.DataAnnotations 10.0.2
- Microsoft.Extensions.Primitives 10.0.3
- Microsoft.Extensions.Primitives 10.0.5
- Microsoft.Identity.Client 4.83.1
- Microsoft.Identity.Client.Extensions.Msal 4.83.1
- Microsoft.IdentityModel.Abstractions 8.0.1
- Microsoft.IdentityModel.Abstractions 8.14.0
- Microsoft.IdentityModel.JsonWebTokens 8.0.1
- Microsoft.IdentityModel.Logging 8.0.1
- Microsoft.IdentityModel.Protocols 8.0.1
- Microsoft.IdentityModel.Protocols.OpenIdConnect 8.0.1
- Microsoft.IdentityModel.Tokens 8.0.1
- Microsoft.IO.RecyclableMemoryStream 3.0.1
- Namotion.Reflection 3.4.3
- Newtonsoft.Json 13.0.1
- Newtonsoft.Json 13.0.3
- Newtonsoft.Json 13.0.4
- NexusRpc 0.3.0
- Nito.AsyncEx.Coordination 5.1.2
- Nito.AsyncEx.Tasks 5.1.2
- Nito.Collections.Deque 1.1.1
- Nito.Disposables 2.2.1
- NJsonSchema 11.5.2
- NJsonSchema.Annotations 11.5.2
- NJsonSchema.NewtonsoftJson 11.5.2
- NJsonSchema.Yaml 11.5.2
- NSwag.Annotations 14.6.3
- NSwag.AspNetCore 14.6.3
- NSwag.Core 14.6.3
- NSwag.Core.Yaml 14.6.3
- NSwag.Generation 14.6.3
- NSwag.Generation.AspNetCore 14.6.3
- Open.Linq.AsyncExtensions 1.2.0
- RavenDB.Client 7.1.2
- Scrutor 7.0.0
- ShortGuid 2.0.1 †
- System.ClientModel 1.10.0
- System.CodeDom 7.0.0
- System.Diagnostics.EventLog 10.0.5
- System.IdentityModel.Tokens.Jwt 8.0.1
- System.Management 7.0.2
- System.Memory.Data 10.0.3
- System.Reactive 4.4.1
- System.Reactive.Compatibility 4.4.1 †
- System.Reactive.Core 4.4.1
- System.Reactive.Interfaces 4.4.1
- System.Reactive.Linq 4.4.1
- System.Reactive.PlatformServices 4.4.1
- System.Reactive.Providers 4.4.1
- System.Security.Cryptography.ProtectedData 4.5.0
- Temporalio 1.15.0
- YamlDotNet 16.3.0
- ZstdSharp.Port 0.8.6

### Apache-2.0 (20)

- Castle.Core 5.2.1
- FluentValidation 12.1.1
- Google.Apis 1.69.0
- Google.Apis.Auth 1.69.0
- Google.Apis.Core 1.69.0
- Grpc.Auth 2.71.0
- Grpc.Core.Api 2.71.0
- Grpc.Core.Api 2.76.0
- Grpc.Net.Client 2.71.0
- Grpc.Net.Client 2.76.0
- Grpc.Net.Common 2.71.0
- Grpc.Net.Common 2.76.0
- IronCompress 1.7.0
- Microsoft.AspNetCore.Http.Abstractions 2.3.9
- Microsoft.AspNetCore.Http.Features 2.3.0
- Microsoft.Azure.DurableTask.Core 3.8.0
- System.Linq.Dynamic.Core 1.7.1
- ThrottleDebounce 2.0.1
- VaultSharp 1.17.5.1
- zb-client 2.10.0

### BSD-2-Clause (9)

- SoEx.Abstractions 0.0.0-alpha-3.0
- SoEx.Container 0.0.0-alpha-3.0
- SoEx.Context 0.0.0-alpha-3.0
- SoEx.Context.Abstractions 0.0.0-alpha-3.0
- SoEx.Diagnostics 0.0.0-alpha-3.0
- SoEx.Endpoint 0.0.0-alpha-3.0
- SoEx.Exceptions 0.0.0-alpha-3.0
- SoEx.Proxy 0.0.0-alpha-3.0
- SoEx.Topology 0.0.0-alpha-3.0

### BSD-3-Clause (4)

- Google.Protobuf 3.26.1
- Google.Protobuf 3.33.0
- Google.Protobuf 3.33.5
- Snappier 1.3.1

## Rust dependencies (Cargo) — Restate sidecar

Transitive closure of `restate-sidecar-rs` (226 crates).

### MIT OR Apache-2.0 (137)

- allocator-api2 0.2.21
- anyhow 1.0.102
- atomic-waker 1.1.2
- autocfg 1.5.1
- base16ct 0.2.0
- base64 0.22.1
- base64ct 1.8.3
- bitflags 2.13.0
- block-buffer 0.10.4
- block-buffer 0.12.0
- bs58 0.5.1
- bumpalo 3.20.3 ‡
- bytes-utils 0.1.4
- cfg-if 1.0.4
- chacha20 0.10.0
- const-oid 0.10.2
- const-oid 0.9.6
- cpufeatures 0.2.17
- cpufeatures 0.3.0
- crypto-bigint 0.5.5
- crypto-common 0.1.6
- crypto-common 0.2.2
- curve25519-dalek-derive 0.1.1
- der 0.7.10
- digest 0.10.7
- digest 0.11.3
- displaydoc 0.2.6
- dyn-clone 1.0.20
- ecdsa 0.16.9
- ed25519 2.2.3
- either 1.16.0
- elliptic-curve 0.13.8
- equivalent 1.0.2
- errno 0.3.14
- ff 0.13.1
- fnv 1.0.7
- form_urlencoded 1.2.2
- futures 0.3.32
- futures-channel 0.3.32
- futures-core 0.3.32
- futures-executor 0.3.32
- futures-io 0.3.32
- futures-macro 0.3.32
- futures-sink 0.3.32
- futures-task 0.3.32
- futures-util 0.3.32
- getrandom 0.2.17
- getrandom 0.4.2
- group 0.13.0
- hashbrown 0.15.5 ‡
- hashbrown 0.16.1
- hashbrown 0.17.1
- heck 0.5.0
- hkdf 0.12.4
- hmac 0.12.1
- http 1.4.2
- httparse 1.10.1
- httpdate 1.0.3
- hybrid-array 0.4.12
- id-arena 2.3.0 ‡
- idna 1.1.0
- idna_adapter 1.2.2
- indexmap 2.14.0
- ipnet 2.12.0
- itertools 0.14.0
- itoa 1.0.18
- js-sys 0.3.99 ‡
- jsonptr 0.7.1
- lazy_static 1.5.0
- leb128fmt 0.1.0 ‡
- libc 0.2.186
- log 0.4.32
- num-bigint-dig 0.8.6
- num-integer 0.1.46
- num-iter 0.1.45
- num-traits 0.2.19
- once_cell 1.21.4
- p256 0.13.2
- p384 0.13.1
- pastey 0.2.3
- pem-rfc7468 0.7.0
- percent-encoding 2.3.2
- pin-project-lite 0.2.17
- pkcs1 0.7.5
- pkcs8 0.10.2
- ppv-lite86 0.2.21
- prettyplease 0.2.37
- primeorder 0.13.6
- proc-macro2 1.0.106
- quote 1.0.45
- rand 0.10.1
- rand 0.8.6
- rand_chacha 0.3.1
- rand_core 0.10.1
- rand_core 0.6.4
- regress 0.10.5
- regress 0.11.1
- reqwest 0.13.4
- rfc6979 0.4.0
- rsa 0.9.10
- rustc_version 0.4.1
- rustversion 1.0.22 ‡
- sec1 0.7.3
- semver 1.0.28
- serde 1.0.228
- serde_core 1.0.228
- serde_derive 1.0.228
- serde_derive_internals 0.29.1
- serde_json 1.0.150
- sha2 0.10.9
- sha2 0.11.0
- signal-hook-registry 1.4.8
- signature 2.2.0
- smallvec 1.15.1
- socket2 0.6.4
- spki 0.7.3
- stable_deref_trait 1.2.1
- syn 2.0.117
- thiserror 2.0.18
- thiserror-impl 2.0.18
- thread_local 1.1.9
- typenum 1.20.1
- unicode-xid 0.2.6 ‡
- url 2.5.8
- utf8_iter 1.0.4
- uuid 1.23.2
- version_check 0.9.5
- wasm-bindgen 0.2.122 ‡
- wasm-bindgen-futures 0.4.72 ‡
- wasm-bindgen-macro 0.2.122 ‡
- wasm-bindgen-macro-support 0.2.122 ‡
- wasm-bindgen-shared 0.2.122 ‡
- web-sys 0.3.99 ‡
- windows-link 0.2.1 ‡
- windows-sys 0.61.2 ‡
- zeroize 1.8.2
- zeroize_derive 1.4.3

### MIT (38)

- bytes 1.11.1
- generic-array 0.14.9
- h2 0.4.14
- http-body 1.0.1
- http-body-util 0.1.3
- hyper 1.10.1
- hyper-util 0.1.20
- jsonwebtoken 10.4.0
- libm 0.2.16
- mio 1.2.1
- nu-ansi-term 0.50.3
- restate-sdk 0.10.0
- restate-sdk-macros 0.10.0
- restate-sdk-shared-core 0.10.0
- schemars 0.8.22
- schemars_derive 0.8.22
- sharded-slab 0.1.7
- slab 0.4.12
- spin 0.9.8
- strum 0.27.2
- strum_macros 0.27.2
- synstructure 0.13.2
- tokio 1.52.3
- tokio-macros 2.7.0
- tokio-util 0.7.18
- tower 0.5.3
- tower-http 0.6.11
- tower-layer 0.3.3
- tower-service 0.3.3
- tracing 0.1.44
- tracing-attributes 0.1.31
- tracing-core 0.1.36
- tracing-log 0.2.0
- tracing-subscriber 0.3.23
- try-lock 0.2.5
- valuable 0.1.1 ‡
- want 0.3.1
- zmij 1.0.21

### Unicode-3.0 (18)

- icu_collections 2.2.0
- icu_locale_core 2.2.0
- icu_normalizer 2.2.0
- icu_normalizer_data 2.2.0
- icu_properties 2.2.0
- icu_properties_data 2.2.0
- icu_provider 2.2.0
- litemap 0.8.2
- potential_utf 0.1.5
- tinystr 0.8.3
- writeable 0.6.3
- yoke 0.8.3
- yoke-derive 0.8.2
- zerofrom 0.1.8
- zerofrom-derive 0.1.7
- zerotrie 0.2.4
- zerovec 0.11.6
- zerovec-derive 0.11.3

### Apache-2.0 WITH LLVM-exception (10)

- wasm-encoder 0.244.0 ‡
- wasm-metadata 0.244.0 ‡
- wasmparser 0.244.0 ‡
- wit-bindgen 0.51.0 ‡
- wit-bindgen 0.57.1 ‡
- wit-bindgen-core 0.51.0 ‡
- wit-bindgen-rust 0.51.0 ‡
- wit-bindgen-rust-macro 0.51.0 ‡
- wit-component 0.244.0 ‡
- wit-parser 0.244.0 ‡

### Apache-2.0 (7)

- prost 0.14.4
- prost-derive 0.14.4
- serde_tokenstream 0.2.3
- sync_wrapper 1.0.2
- typify 0.6.2
- typify-impl 0.6.2
- typify-macro 0.6.2

### BSD-3-Clause (3)

- curve25519-dalek 4.1.3
- ed25519-dalek 2.2.0
- subtle 2.6.1

### MIT OR Apache-2.0 OR Apache-2.0 WITH LLVM-exception (3)

- wasi 0.11.1+wasi-snapshot-preview1 ‡
- wasip2 1.0.3+wasi-0.2.9 ‡
- wasip3 0.4.0+wasi-0.3.0-rc-2026-01-06 ‡

### BSD-2-Clause OR Apache-2.0 OR MIT (2)

- zerocopy 0.8.50
- zerocopy-derive 0.8.50 ‡

### Zlib (2)

- foldhash 0.1.5 ‡
- foldhash 0.2.0

### (MIT OR Apache-2.0) AND Unicode-3.0 (1)

- unicode-ident 1.0.24

### MIT OR Apache-2.0 OR BSD-1-Clause (1)

- fiat-crypto 0.2.9 ‡

### MIT OR Apache-2.0 OR LGPL-2.1-or-later (1)

- r-efi 6.0.0 ‡

### MIT OR Apache-2.0 OR Zlib (1)

- tinyvec_macros 0.1.1

### Unlicense OR MIT (1)

- memchr 2.8.1

### Zlib OR Apache-2.0 OR MIT (1)

- tinyvec 1.11.0

## Notes

**†** — the NuGet package's metadata carries a legacy `licenseUrl` (or omits a license expression); the SPDX shown is the upstream project's actual license. `System.Reactive.Compatibility` is a façade over Rx.NET (MIT); `ShortGuid`'s package omits license metadata and its upstream repo is MIT; `Lambda2Js.Signed` carries the deprecated `aka.ms/deprecateLicenseUrl` placeholder and its upstream repo is MIT.

**‡** — crate not present in the local Cargo cache because it is platform-gated (wasm32 / windows targets) or a build-time code-generation dependency of `restate-sdk` (`wit-*`, `wasm-*`), so it is **not compiled into the Linux binary**. Its SPDX is taken from crates.io and listed for completeness. The bytecodealliance `wit-*`/`wasm-*` crates carry the `Apache-2.0 WITH LLVM-exception` identifier.

**Not a consumed library:** the Restate *server* (`ghcr.io/restatedev/restate`) is run as an external service for Tier-2 testing and is licensed under the **Business Source License 1.1** (BSL). It is not linked or redistributed here — only the MIT-licensed `restate-sdk` crate is. The Azure Durable Task Scheduler emulator is likewise an external dev service, not a consumed library.

## How this was generated

```
# .NET — full transitive graph from restored assets, license per package nuspec
dotnet restore SoEx.Workflow.sln
dotnet list SoEx.Workflow.sln package --include-transitive
#   then map each id@version to ~/.nuget/packages/<id>/<ver>/*.nuspec <license>

# Rust — full transitive graph from Cargo.lock, license per crate Cargo.toml
cargo metadata --format-version 1   # carries a `license` field per package
```

