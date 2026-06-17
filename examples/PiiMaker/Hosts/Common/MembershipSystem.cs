using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiiMaker.Access.Billing.Interface;
using PiiMaker.Access.Billing.Service;
using PiiMaker.Access.Identity.Interface;
using PiiMaker.Access.Identity.Service;
using PiiMaker.Access.Provisioning.Interface;
using PiiMaker.Access.Provisioning.Service;
using PiiMaker.Access.Retention.Interface;
using PiiMaker.Access.Retention.Service;
using PiiMaker.Engine.Subscription.Interface;
using PiiMaker.Engine.Subscription.Service;
using PiiMaker.Manager.Membership.Interface;
using PiiMaker.Manager.Membership.Service;
using Native = PiiMaker.Manager.Membership.Interface.Native;
using Portable = PiiMaker.Manager.Membership.Interface.Portable;
using SoEx.Method.Workflow;
using WfExternal = SoEx.Method.Workflow.External;
using WfSubSystem = SoEx.Method.Workflow.SubSystem;
using SoEx.Abstractions;
using SoEx.Context;
using SoEx.Hosting;
using SoEx.Hosting.Default;
using SoEx.Transport.InProc;
using SoEx.Transport.Workflow;
using SoEx.Workflow;
using SoEx.Workflow.Runtime.InMemory;
using SoEx.Workflow.Keys.OpenBao;
using SoEx.Workflow.Keys.RavenDB;
using SoEx.Workflow.Idempotency.RavenDB;
using SoEx.Workflow.SubjectIndex.RavenDB;
using SoEx.Workflow.SubjectIndex.EfCore;
using SoEx.Workflow.Maintenance.RavenDB;
using SoEx.Workflow.Maintenance.EfCore;
using Microsoft.EntityFrameworkCore;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Topology = SoEx.Topology;

namespace PiiMaker.Hosting;

/// <summary>
/// Stands up the "membership" SoEx <see cref="Topology.System"/> and starts it: the Membership
/// manager is the subsystem entry point (hosted on a <see cref="WorkflowBinding{I}"/> for the governed step,
/// plus an <c>IErasureEvents</c> endpoint for the termination), proxying to the Engine and Access
/// COMPONENTS through the in-proc transport. Each component's state is a singleton on its own host
/// ServiceCollection. Returns the workflow dispatch + serializer the governed step needs, a system-resolved
/// erasure-events proxy for the termination, and the retained-records store for observation.
/// <para>Every example host reuses this; only how it drives the governed step/termination onto a runtime differs.</para>
/// </summary>
public static class MembershipSystem
{
    public sealed record Composition(
        IWorkflowDispatch NativeEndpoint, IWorkflowDispatch PortableEndpoint, IMessageSerializer Serializer, IErasureEvents Erasure, RetainedStore Retained,
        IMembershipManager Manager, WorkflowSeam Seam, BillingStore Billing, ILifetimeScope Scope,
        IInstanceKeyStore Keys, ISubjectIndex Index, IIdempotencyStore Idempotency, WfExternal.IWorkflowUtility Workflow,
        IHeldInstanceRegistry HeldLog);

    public static Composition Compose(string subSystem, MembershipPolicy policy)
    {
        var listeners = new WorkflowListeners();
        var retainedStore = new RetainedStore();
        var billingStore = new BillingStore();

        // The durable/persistent workflow stores are OWNED by the Workflow utility component (registered on its
        // ServiceCollection below); they are created here in the composition root and returned so the host can
        // wire the SAME instances into its GovernedStep/GovernedTermination (the per-step hot path can't proxy).
        IInstanceKeyStore keys = CreateKeyStore();
        IIdempotencyStore idempotency = CreateIdempotencyStore();
        // The subject index and the erasure-maintenance logs are always needed together, so when both select the
        // SAME durable backend the bundled default backs them from ONE store (one SQLite file / one Raven
        // database). A mixed or in-memory selection keeps the independent per-interface stores.
        ISubjectIndex index;
        IHeldInstanceRegistry heldRegistry;
        IErasureRequestRegistry requestRegistry;
        IPendingErasureRequests pendingRequests;
        if (TryCreateCollapsedErasureStores(keys, out var collapsed))
        {
            (index, heldRegistry, requestRegistry, pendingRequests) = collapsed;
        }
        else
        {
            index = CreateSubjectIndex(keys);
            (heldRegistry, requestRegistry, pendingRequests) = CreateMaintenanceLogs();
        }
        // The workflow seam the host connects AFTER composing (its gateways need the composed system); the
        // Workflow utility reads it to start/continue flows. Lives on the utility's ServiceCollection now.
        var seam = new WorkflowSeam();

        static IServiceCollection Svc() => new ServiceCollection().AddSingleton<IContextFlowPolicy, SubjectContextFlowPolicy>();

        // The deployment secret the manager keys its PII-free instance ids under (DeterministicInstanceId.Keyed):
        // the start side and the continue side both run through the manager, so it must be one stable value —
        // stable across restarts so a parked flow's id re-derives, and never journaled. A real deployment loads it
        // from a secret store (here PIIMAKER_INSTANCEID_SECRET, base64); the demo falls back to a fixed value so it
        // runs with no setup.
        byte[] idSecret = Environment.GetEnvironmentVariable("PIIMAKER_INSTANCEID_SECRET") is { Length: > 0 } b64
            ? Convert.FromBase64String(b64)
            : "piimaker-demo-instance-id-secret"u8.ToArray();

        IServiceCollection managerSvc = Svc().AddSingleton(listeners).AddSingleton(policy).AddSingleton(new InstanceIdSecret(idSecret));
        IServiceCollection workflowSvc = Svc().AddSingleton(seam).AddSingleton(keys).AddSingleton(index).AddSingleton(idempotency)
            .AddSingleton(heldRegistry).AddSingleton(requestRegistry).AddSingleton(pendingRequests);
        IServiceCollection identitySvc = Svc().AddSingleton(new IdentityStore());
        IServiceCollection subscriptionSvc = Svc().AddSingleton(new SubscriptionStore());
        IServiceCollection billingSvc = Svc().AddSingleton(billingStore);
        IServiceCollection provisioningSvc = Svc().AddSingleton(new ProvisioningStore());
        IServiceCollection retentionSvc = Svc().AddSingleton(retainedStore);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        // One shared in-proc listener registry across the root (for the system-level erasure proxy) and
        // every component, so proxies and endpoints route to each other through the in-proc transport.
        InProcExtensions.InProcClientWithSpan(builder.Services, managerSvc, workflowSvc, identitySvc, subscriptionSvc, billingSvc, provisioningSvc, retentionSvc);

        static Topology.Client Proxy<T>(string subSystem) where T : class =>
            new Topology.Client<T> { SubSystem = subSystem, Service = new InProcBinding<T>(subSystem) };

        static Topology.Host Component<TContract, TImpl>(string subSystem, IServiceCollection svc) => new()
        {
            Implementation = typeof(TImpl),
            Endpoints = [new InProcBinding<TContract>(subSystem)],
            Proxies = [],
            ServiceCollection = svc,
        };

        // The Workflow utility is its OWN subsystem alongside membership — a separate deployable boundary, not a
        // component of the membership subsystem. The manager reaches it cross-subsystem through a proxy.
        string workflowSub = $"{subSystem}-workflow";

        var system = new Topology.System
        {
            // The base DefaultPipeline, set as the system Defaults so it applies to the entry point and every
            // component host below. As of SoEx alpha-3.0 the framework routes a failed step's exception message
            // (at the error interceptor, the endpoint pipeline, and the dispatcher span) through the pipeline's
            // telemetry-confidentiality component, and the default FallbackConfidentiality redacts it. So the stock
            // pipeline already keeps the subject out of telemetry on the error path; no custom interceptor is needed.
            Defaults = new DefaultPipeline(),
            SubSystems =
            [
                new Topology.SubSystem
                {
                    Name = subSystem,
                    EntryPoint = new Topology.Host
                    {
                        Implementation = typeof(MembershipManager),
                        Endpoints =
                        [
                            // Two durable governed-step seams on the one entry component: the native single-step
                            // contract and the portable flow contract. A host binds its GovernedStep/endpoint to
                            // whichever contract owns the operation it drives (dispatch is by typeof(I)).
                            new WorkflowBinding<Native.IMembershipManager>(subSystem),
                            new WorkflowBinding<Portable.IMembershipManager>(subSystem),
                            // One erasure termination endpoint. Both the host (governed termination) and the utility
                            // (sweep) reach it through the single IErasureEvents client below — no second
                            // registration on the type, so no aliasing and no per-caller contract split.
                            new InProcBinding<IErasureEvents>(subSystem),
                            new InProcBinding<IMembershipManager>(subSystem),
                        ],
                        Proxies =
                        [
                            // The Workflow utility lives in its own subsystem — this proxy targets it cross-subsystem.
                            Proxy<WfSubSystem.IWorkflowUtility>(workflowSub),
                            Proxy<IIdentityAccess>(subSystem), Proxy<ISubscriptionEngine>(subSystem), Proxy<IBillingAccess>(subSystem),
                            Proxy<IProvisioningAccess>(subSystem), Proxy<IRetainedRecordAccess>(subSystem),
                        ],
                        ServiceCollection = managerSvc,
                    },
                    Components =
                    [
                        Component<IIdentityAccess, IdentityAccess>(subSystem, identitySvc),
                        Component<ISubscriptionEngine, SubscriptionEngine>(subSystem, subscriptionSvc),
                        Component<IBillingAccess, BillingAccess>(subSystem, billingSvc),
                        Component<IProvisioningAccess, ProvisioningAccess>(subSystem, provisioningSvc),
                        Component<IRetainedRecordAccess, RetainedRecordAccess>(subSystem, retentionSvc),
                    ],
                },
                // The Workflow utility subsystem: its entry point owns the durable workflow plumbing (stores +
                // seam + erasure) and hosts two faces — External (host calls: erase/sweep) and SubSystem (the
                // manager proxies in: start/raise-event). It reaches the membership manager's erasure termination through
                // the shared system IErasureEvents client (visible in its container), so it declares no proxy.
                new Topology.SubSystem
                {
                    Name = workflowSub,
                    EntryPoint = new Topology.Host
                    {
                        Implementation = typeof(WorkflowUtility),
                        Endpoints =
                        [
                            new InProcBinding<WfExternal.IWorkflowUtility>(workflowSub),
                            new InProcBinding<WfSubSystem.IWorkflowUtility>(workflowSub),
                        ],
                        Proxies = [],
                        ServiceCollection = workflowSvc,
                    },
                    Components = [],
                },
            ],
            Clients =
            [
                new Topology.Client<IErasureEvents> { SubSystem = subSystem, Service = new InProcBinding<IErasureEvents>(subSystem) },
                new Topology.Client<WfExternal.IWorkflowUtility> { SubSystem = workflowSub, Service = new InProcBinding<WfExternal.IWorkflowUtility>(workflowSub) },
                new Topology.Client<IMembershipManager> { SubSystem = subSystem, Service = new InProcBinding<IMembershipManager>(subSystem) },
            ],
        };

        builder.SoEx(system);
        IHost host = builder.Build();
        host.Start();

        IWorkflowDispatch nativeEndpoint = listeners.ForAddress(new WorkflowBinding<Native.IMembershipManager>(subSystem).Transport.Address);
        IWorkflowDispatch portableEndpoint = listeners.ForAddress(new WorkflowBinding<Portable.IMembershipManager>(subSystem).Transport.Address);
        var serializer = host.Services.GetRequiredService<IMessageSerializer>();
        var erasure = host.Services.GetRequiredService<IErasureEvents>();
        var entry = host.Services.GetRequiredService<IMembershipManager>();
        WfExternal.IWorkflowUtility workflow = host.Services.GetRequiredService<WfExternal.IWorkflowUtility>();
        // The Autofac root scope where the IMembershipEntry client proxy is registered. The web host sets
        // this async-local per request (UseSoContext), so the generated controller's Proxy.ForService<I>()
        // -> Container.Resolve<I>() resolves the entry proxy and dispatches through the pipeline.
        ILifetimeScope scope = host.Services.GetAutofacRoot();
        return new Composition(nativeEndpoint, portableEndpoint, serializer, erasure, retainedStore, entry, seam, billingStore, scope,
            keys, index, idempotency, workflow, heldRegistry);
    }

    // Picks the per-instance key store from PIIMAKER_KEYSTORE (inmemory|openbao|ravendb; default inmemory).
    // The key store is the crypto-shred root — a live key means a flow is in flight, Destroy means shredded —
    // so making THIS durable is what lets a shred survive a host restart. (The subject index and idempotency
    // store stay in-memory; only the key store needs to be durable to demonstrate the property.)
    private static IInstanceKeyStore CreateKeyStore()
    {
        string choice = (Environment.GetEnvironmentVariable("PIIMAKER_KEYSTORE") ?? "inmemory").Trim().ToLowerInvariant();
        return choice switch
        {
            "openbao" => CreateOpenBao(),
            "ravendb" => CreateRavenDb(),
            "inmemory" or "" => new InMemoryInstanceKeyStore(),
            _ => throw new ArgumentException($"unknown PIIMAKER_KEYSTORE '{choice}' (expected inmemory|openbao|ravendb)"),
        };
    }

    private static IInstanceKeyStore CreateOpenBao()
    {
        string address = Environment.GetEnvironmentVariable("PIIMAKER_OPENBAO_ADDR") ?? "http://127.0.0.1:8200";
        string token = Environment.GetEnvironmentVariable("PIIMAKER_OPENBAO_TOKEN") ?? "root";
        string mount = Environment.GetEnvironmentVariable("PIIMAKER_OPENBAO_MOUNT") ?? "transit";
        return new OpenBaoInstanceKeyStore(address, token, mount);
    }

    private static IInstanceKeyStore CreateRavenDb()
    {
        string database = Environment.GetEnvironmentVariable("PIIMAKER_RAVENDB_DATABASE") ?? "PiiMakerKeys";
        return new RavenDbInstanceKeyStore(BuildRavenStore(database), ResolveDemoKek());
    }

    // Picks the idempotency store from PIIMAKER_IDEMPOTENCY (inmemory|ravendb; default inmemory). The durable
    // RavenDB store makes step idempotency AND the Elsa gateway's idempotent re-raise survive a host restart.
    private static IIdempotencyStore CreateIdempotencyStore()
    {
        string choice = (Environment.GetEnvironmentVariable("PIIMAKER_IDEMPOTENCY") ?? "inmemory").Trim().ToLowerInvariant();
        return choice switch
        {
            "ravendb" => new RavenDbIdempotencyStore(BuildRavenStore(
                Environment.GetEnvironmentVariable("PIIMAKER_IDEMPOTENCY_DATABASE") ?? "PiiMakerIdempotency")),
            "inmemory" or "" => new InMemoryIdempotencyStore(),
            _ => throw new ArgumentException($"unknown PIIMAKER_IDEMPOTENCY '{choice}' (expected inmemory|ravendb)"),
        };
    }

    // The bundled default: when PIIMAKER_SUBJECTINDEX and PIIMAKER_MAINTENANCE_STORE pick the SAME durable
    // backend, the subject index and the three maintenance faces are backed by ONE physical store (one SQLite
    // file via ErasureStores, or one Raven database via RavenErasureStores) — the deployment wires one connection
    // instead of two for the stores erasure always needs together. Returns false (so the caller builds the
    // independent stores) for a mixed selection or an in-memory choice, where the collapse doesn't apply.
    // The interfaces stay separate either way; this only changes how many physical stores back them.
    private static bool TryCreateCollapsedErasureStores(IInstanceKeyStore keys,
        out (ISubjectIndex Index, IHeldInstanceRegistry Held, IErasureRequestRegistry Requests, IPendingErasureRequests Pending) stores)
    {
        string subjectChoice = (Environment.GetEnvironmentVariable("PIIMAKER_SUBJECTINDEX") ?? "inmemory").Trim().ToLowerInvariant();
        string maintChoice = (Environment.GetEnvironmentVariable("PIIMAKER_MAINTENANCE_STORE") ?? "inmemory").Trim().ToLowerInvariant();
        if (subjectChoice != maintChoice)
        {
            stores = default;
            return false; // a mixed selection keeps the independent per-interface stores.
        }

        switch (subjectChoice)
        {
            case "efcore":
                string db = Environment.GetEnvironmentVariable("PIIMAKER_ERASURE_SQLITE") ?? "/tmp/piimaker-erasure.db";
                DbContextOptions<ErasureDbContext> options = new DbContextOptionsBuilder<ErasureDbContext>()
                    .UseSqlite($"Data Source={db}").Options;
                var ef = new ErasureStores(options, SubjectProtector(), keys);
                stores = (ef.SubjectIndex, ef.HeldInstances, ef.ErasureRequests, ef.ErasureRequests);
                return true;
            case "ravendb":
                IDocumentStore store = BuildRavenStore(
                    Environment.GetEnvironmentVariable("PIIMAKER_ERASURE_DATABASE") ?? "PiiMakerErasure");
                var raven = new RavenErasureStores(store, SubjectProtector(), keys);
                stores = (raven.SubjectIndex, raven.HeldInstances, raven.ErasureRequests, raven.ErasureRequests);
                return true;
            default:
                stores = default; // inmemory (or any non-collapsible choice) — keep the independent stores.
                return false;
        }
    }

    // Picks the subject index from PIIMAKER_SUBJECTINDEX (inmemory|ravendb|efcore; default inmemory). Durable
    // variants make erasure routing (subject -> instances) survive a restart and be visible cross-process.
    // Used for the standalone (non-collapsed) path; see TryCreateCollapsedErasureStores for the bundled default.
    private static ISubjectIndex CreateSubjectIndex(IInstanceKeyStore keys)
    {
        string choice = (Environment.GetEnvironmentVariable("PIIMAKER_SUBJECTINDEX") ?? "inmemory").Trim().ToLowerInvariant();
        switch (choice)
        {
            case "ravendb":
                return new RavenDbSubjectIndex(BuildRavenStore(
                    Environment.GetEnvironmentVariable("PIIMAKER_SUBJECTINDEX_DATABASE") ?? "PiiMakerSubjects"),
                    SubjectProtector(), keys);
            case "efcore":
                string db = Environment.GetEnvironmentVariable("PIIMAKER_SUBJECTINDEX_SQLITE") ?? "/tmp/piimaker-subjects.db";
                DbContextOptions<SubjectIndexDbContext> options = new DbContextOptionsBuilder<SubjectIndexDbContext>()
                    .UseSqlite($"Data Source={db}").Options;
                return new EfCoreSubjectIndex(options, SubjectProtector(), keys);
            case "inmemory" or "":
                return new InMemorySubjectIndex();
            default:
                throw new ArgumentException($"unknown PIIMAKER_SUBJECTINDEX '{choice}' (expected inmemory|ravendb|efcore)");
        }
    }

    // Picks the erasure-maintenance logs (held instances + open requests) from PIIMAKER_MAINTENANCE_STORE
    // (inmemory|ravendb|efcore; default inmemory). Durable variants let the maintenance backstop survive a
    // restart and be driven by a separately-hosted scheduler across the fleet.
    // The durable erasure-request registry and the pending-intake store are the same concrete store (one
    // connection serves both faces), so the ravendb/efcore branches build it once and return it twice.
    private static (IHeldInstanceRegistry Held, IErasureRequestRegistry Requests, IPendingErasureRequests Pending) CreateMaintenanceLogs()
    {
        string choice = (Environment.GetEnvironmentVariable("PIIMAKER_MAINTENANCE_STORE") ?? "inmemory").Trim().ToLowerInvariant();
        switch (choice)
        {
            case "ravendb":
                IDocumentStore store = BuildRavenStore(
                    Environment.GetEnvironmentVariable("PIIMAKER_MAINTENANCE_DATABASE") ?? "PiiMakerMaintenance");
                var ravenRequests = new RavenDbErasureRequestRegistry(store, SubjectProtector());
                return (new RavenDbHeldInstanceRegistry(store), ravenRequests, ravenRequests);
            case "efcore":
                string db = Environment.GetEnvironmentVariable("PIIMAKER_MAINTENANCE_SQLITE") ?? "/tmp/piimaker-maintenance.db";
                DbContextOptions<MaintenanceDbContext> options = new DbContextOptionsBuilder<MaintenanceDbContext>()
                    .UseSqlite($"Data Source={db}").Options;
                var efRequests = new EfCoreErasureRequestRegistry(options, SubjectProtector());
                return (new EfCoreHeldInstanceRegistry(options), efRequests, efRequests);
            case "inmemory" or "":
                return (new InMemoryHeldInstanceRegistry(), new InMemoryErasureRequestRegistry(), new InMemoryPendingErasureRequests());
            default:
                throw new ArgumentException($"unknown PIIMAKER_MAINTENANCE_STORE '{choice}' (expected inmemory|ravendb|efcore)");
        }
    }

    private static IDocumentStore BuildRavenStore(string database)
    {
        string url = Environment.GetEnvironmentVariable("PIIMAKER_RAVENDB_URL") ?? "http://127.0.0.1:8085";
        var store = new DocumentStore { Urls = [url], Database = database };
        store.Initialize();
        EnsureDatabaseExists(store, database);
        return store;
    }

    // PIIMAKER_RAVENDB_KEK (base64, 32 bytes) overrides; otherwise a CLEARLY-MARKED DEMO-ONLY constant.
    // NEVER ship a hardcoded KEK: it is the master key that wraps every per-instance data key.
    private static byte[] ResolveDemoKek()
    {
        string? b64 = Environment.GetEnvironmentVariable("PIIMAKER_RAVENDB_KEK");
        if (!string.IsNullOrWhiteSpace(b64))
        {
            byte[] supplied = Convert.FromBase64String(b64);
            if (supplied.Length != 32)
            {
                throw new ArgumentException("PIIMAKER_RAVENDB_KEK must decode to exactly 32 bytes (AES-256).");
            }

            return supplied;
        }

        byte[] demo = new byte[32]; // DEMO-ONLY master KEK (0x00..0x1F) — deterministic, overridable, never for production.
        for (byte i = 0; i < 32; i++)
        {
            demo[i] = i;
        }

        return demo;
    }

    // The deployment secret that derives the durable subject index's (and erasure-request registry's) PII-free
    // at-rest lookup tokens (the subject plaintext itself is sealed under each instance's crypto-shred key, not
    // this secret). It is kept SEPARATE from the master KEK on purpose: sharing one secret across two purposes is
    // exactly what we don't want a copyist to learn — a KEK leak shouldn't also let an attacker confirm subjects
    // against index tokens. PIIMAKER_SUBJECT_SECRET (base64, >=16 bytes) overrides; in production give the index
    // its own independent stable secret from your secrets manager.
    private static ISubjectProtector SubjectProtector() => new HmacSubjectProtector(ResolveDemoSubjectSecret());

    private static byte[] ResolveDemoSubjectSecret()
    {
        string? b64 = Environment.GetEnvironmentVariable("PIIMAKER_SUBJECT_SECRET");
        if (!string.IsNullOrWhiteSpace(b64))
        {
            byte[] supplied = Convert.FromBase64String(b64);
            if (supplied.Length < 16)
            {
                throw new ArgumentException("PIIMAKER_SUBJECT_SECRET must decode to at least 16 bytes.");
            }

            return supplied;
        }

        byte[] demo = new byte[32]; // DEMO-ONLY subject-token secret (0x20..0x3F) — DISTINCT from the demo KEK, overridable, never for production.
        for (byte i = 0; i < 32; i++)
        {
            demo[i] = (byte)(0x20 + i);
        }

        return demo;
    }

    private static void EnsureDatabaseExists(IDocumentStore store, string database)
    {
        try
        {
            store.Maintenance.ForDatabase(database).Send(new GetStatisticsOperation());
        }
        catch (DatabaseDoesNotExistException)
        {
            try
            {
                store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(database)));
            }
            catch (RavenException)
            {
                // already created (e.g. a racing host between our check and create) — fine.
            }
        }
    }
}
