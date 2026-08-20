using CyberCloud.ServiceDefaults.Logging;
using CyberCloud.ServiceDefaults.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Serilog;
using System.Net;

namespace CyberCloud.ServiceDefaults;

/// <summary>
///     The two host builders every Cyber Cloud process starts from — docs/plan/04 § Silo composition, descended
///     in shape from Survival's <c>Survival.Host.OrleansApplication</c> (docs/plan/03 § Foundation).
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The silo and the gateway are separate processes</b> and this class is where that
///         shows. Survival co-hosts them; docs/plan/03 § Hosts says why Cyber Cloud does not — the
///         gateway is I/O bound and scales with request rate, the silo is memory bound and scales
///         with resident grains, so co-hosting means one of them is always the wrong size, and a
///         gateway deploy would move grains.
///     </para>
///     <para>
///         ⚠ <b>THE CALLER MUST REGISTER AN ABP MODULE BEFORE CALLING <c>Build()</c>.</b>
///     </para>
///     <code>
///     var builder = OrleansApplication.CreateSilo(args);
///     await builder.AddApplicationAsync&lt;SiloHostModule&gt;();   // ← required
///     var app = builder.Build();
///     await app.InitializeApplicationAsync();
///     </code>
///     <para>
///         docs/plan/04 § Silo composition shows <c>CreateSilo</c> returning a builder and stops there, which
///         reads as though <c>Build()</c> follows directly. It does not work:
///         <c>builder.Host.UseAutofac()</c> (docs/plan/04 § Silo composition) installs ABP's service-provider
///         factory, and that factory calls <c>services.GetSingletonInstance&lt;IModuleContainer&gt;()</c>
///         during <c>Build()</c>. With no module registered the host dies with
///     </para>
///     <code>
///     InvalidOperationException: Could not find singleton service:
///         Volo.Abp.Modularity.IModuleContainer, Volo.Abp.Core
///     </code>
///     <para>
///         — a message that names neither <c>UseAutofac</c> nor the missing call. Survival gets this
///         right (<c>Survival.Backend.Host/Program.cs</c>:12-15 puts
///         <c>AddApplicationAsync&lt;BackendHostModule&gt;</c> between the two) and docs/plan/04's
///         excerpt omits the line. <c>SiloHostTests</c> exercises the whole sequence so the
///         constraint is checked rather than remembered.
///     </para>
/// </remarks>
public static class OrleansApplication {
    /// <summary>
    ///     A silo host. docs/plan/04 § Silo composition.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configureCluster">
    ///     The storage, stream and reminder wiring — see the ⚠ block in the body. The host supplies
    ///     it; this assembly deliberately does not.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Three things in the body are decisions rather than boilerplate (docs/plan/04 § Silo composition):
    ///         <c>AddActivityPropagation()</c>, <c>UseKubernetesHosting()</c>, and — pending, see
    ///         below — <c>AddMultitenantCommunicationSeparation</c>.
    ///     </para>
    /// </remarks>
    /// <param name="configureStorage">
    ///     Replaces the default <c>AddCyberCloudGrainStorage(options)</c> call, for a host that
    ///     supplies its own <c>IShardMapCache</c>.
    ///     <para>
    ///         ⚠ It is a <i>replacement</i> and not an addition, because the shard map is
    ///         <b>captured</b> by the two tier configurators rather than resolved from the container
    ///         (see <c>TenantOptionsConfigurator</c>). Wiring storage twice would leave whichever
    ///         call ran first holding the map that actually routes writes, which is the kind of
    ///         defect that shows up as a tenant reading an empty database. <c>CyberCloud.Tenancy</c>'s
    ///         <c>AddCyberCloudTenancy</c> is the intended argument.
    ///     </para>
    /// </param>
    public static WebApplicationBuilder CreateSilo(
        string[] args,
        Action<ISiloBuilder>? configureCluster = null,
        Action<ISiloBuilder, CyberCloudStorageOptions>? configureStorage = null
    ) {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureHost(builder);
        builder.AddServiceDefaults();
        builder.AddOrleansHealthChecks();

        var cluster = BindClusterOptions(builder);

        builder.UseOrleans(silo => {
                silo.Configure<ClusterOptions>(options => {
                        options.ClusterId = cluster.ClusterId;
                        options.ServiceId = cluster.ServiceId;
                    }
                );

                // ⚠ NOT OPTIONAL — docs/plan/04 § Silo composition. Without this, an Activity does not cross a grain
                // call: distributed tracing stops at the gateway and every latency investigation
                // becomes archaeology. ConfigureOpenTelemetry() collecting
                // "Microsoft.Orleans.Application" is the other half and is useless without this one.
                silo.AddActivityPropagation();

                // ── The two grain-storage tiers ── docs/plan/04 § Silo composition, docs/plan/05.
                //
                // Wired only when CyberCloud:Storage is configured, and that condition is the design
                // rather than a convenience:
                //
                //  * calling it unconditionally would make a configured Redis and a configured Postgres
                //    a precondition of `CreateSilo` returning a usable host, so every host test in this
                //    repository would need two containers to assert anything about health checks;
                //  * a silo with no configured storage is a silo that CANNOT PERSIST A GRAIN, and that
                //    has to stay visible rather than degrade into an in-memory provider nobody chose.
                //    `SiloHostTests.TheSiloHasNoGrainStorageUnlessTheStorageSectionIsConfigured` is the
                //    assertion.
                //
                // What is STILL a seam here, and why:
                //
                //   .AddMultitenantStreams(StreamProviders.Events, NatsStreamProvider.Configure)
                //   .AddMultitenantCommunicationSeparation(_ => new PlatformCrossTenantAuthorizer())
                //
                // ⚠ AddMultitenantCommunicationSeparation is the one that matters and it is NOT here.
                // docs/plan/04 § Silo composition calls it "not optional"; its argument is an
                // ICrossTenantAuthorizer and authorization is CyberCloud.Authorization's (ADR-007), so
                // referencing it from ServiceDefaults would invert the assembly graph. It plugs in
                // through `configureCluster`, immediately after this call. Until it is wired, a grain
                // still cannot be *stored* in the wrong tenant's shard — the key selects the store —
                // but one grain can still *call* another tenant's grain.
                var storage = builder.Configuration.GetSection(CyberCloudStorageOptions.SectionName);
                if (storage.Exists()) {
                    var storageOptions = new CyberCloudStorageOptions();
                    storage.Bind(storageOptions);

                    // ── Reminders are the host's, and `configureStorage` is how it gets to choose ──
                    //
                    // ⚠ NOT WIRED HERE, AND THIS COMMENT IS WHERE THE ATTEMPT WENT. docs/plan/04
                    // § Reminders puts a Redis reminder service "sharded with the hot tier", which
                    // reads like it belongs beside the tiers below — and wiring it here made
                    // `UnreachableShardReadinessFixture` fail at start-up, because that fixture
                    // configures a hot-tier connection string on purpose and points it at nothing.
                    // "The hot tier is configured" and "there is a Redis behind it" are different
                    // facts, and only a host knows which it has. CyberCloud.Silo.Host's
                    // configureStorage lambda is what calls UseRedisReminderService, from these same
                    // options.
                    if (configureStorage is null) {
                        silo.AddCyberCloudGrainStorage(storageOptions);
                    } else {
                        configureStorage(silo, storageOptions);
                    }
                }

                configureCluster?.Invoke(silo);

                if (builder.Environment.IsDevelopment()) {
                    // ADR-004 (docs/plan/02 § ADR-004): Kubernetes membership in production, and only in
                    // development is there no Kubernetes API to write membership CRs into.
                    //
                    // ⚠ The third argument is what makes a SECOND local silo join the FIRST one's
                    // cluster instead of starting its own — see CyberCloudClusterOptions
                    // .LocalhostPrimarySiloPort, where the silent-two-clusters failure is written out.
                    silo.UseLocalhostClustering(
                        cluster.LocalhostSiloPort,
                        cluster.LocalhostGatewayPort,
                        cluster.LocalhostPrimarySiloPort == 0
                            ? null
                            : new IPEndPoint(IPAddress.Loopback, cluster.LocalhostPrimarySiloPort)
                    );
                } else {
                    // Membership as custom resources in the silo's own namespace. No clustering
                    // database — ADR-004.
                    silo.UseKubeMembership();

                    // Pod identity becomes silo identity, so a SIGTERM from a rolling update is a
                    // graceful StopAsync with grain migration rather than a 60-second gap
                    // (docs/plan/04 § Silo composition). This one is on Services, not on the silo builder — that is
                    // the package's shape, not a mistake.
                    builder.Services.UseKubernetesHosting();
                }
            }
        );

        return builder;
    }

    /// <summary>
    ///     A gateway host — an Orleans <i>client</i>. docs/plan/03 § Hosts.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configureClient">Extra client wiring, if the host needs any.</param>
    /// <remarks>
    ///     ⚠ <b>No <c>AddOrleansHealthChecks</c> here.</b> That set resolves
    ///     <c>ILocalSiloDetails</c>, which a client does not have.
    ///     <see cref="ServiceDefaultsExtensions.AddOrleansClientHealthChecks" /> is the client's
    ///     half, and it is deliberately not wired into readiness — see its remarks.
    /// </remarks>
    public static WebApplicationBuilder CreateClient(
        string[] args,
        Action<IClientBuilder>? configureClient = null
    ) {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureHost(builder);
        builder.AddServiceDefaults();
        builder.AddOrleansClientHealthChecks();

        var cluster = BindClusterOptions(builder);

        builder.UseOrleansClient(client => {
                client.Configure<ClusterOptions>(options => {
                        options.ClusterId = cluster.ClusterId;
                        options.ServiceId = cluster.ServiceId;
                    }
                );

                // The gateway end of docs/plan/04 § Silo composition. A trace that reaches the gateway and stops there
                // is the exact symptom this prevents, and the gateway is where it would be missed.
                client.AddActivityPropagation();

                configureClient?.Invoke(client);

                if (builder.Environment.IsDevelopment()) {
                    client.UseLocalhostClustering(cluster.LocalhostGatewayPort);
                } else {
                    // The read side of UseKubeMembership: the gateway list comes from the same silo
                    // custom resources, so a client needs no clustering database either (ADR-004).
                    client.UseKubeGatewayListProvider();
                }
            }
        );

        return builder;
    }

    /// <summary>
    ///     The host wiring both builders share — docs/plan/04 § Silo composition.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>AddAppSettingsSecretsJson</c> and <c>UseAutofac</c> are ABP's, and they are the
    ///         only ABP surface this assembly touches (ADR-006 — "a module system, not a framework
    ///         we live inside"). Autofac is here rather than in each host because every host is an
    ///         ABP module graph and a host that forgets it fails at
    ///         <c>AddApplicationAsync&lt;TModule&gt;()</c>, late.
    ///     </para>
    ///     <para>
    ///         ⚠ Serilog is wired through <c>builder.Services.AddSerilog</c>, not
    ///         <c>builder.Host.UseSerilog()</c> as docs/plan/04 § Silo composition and Survival both write it.
    ///         <c>UseSerilog</c> is <c>[Obsolete]</c> in Serilog.AspNetCore 10, and
    ///         <c>TreatWarningsAsErrors</c> (docs/plan/00 § Non-negotiables) makes an obsolete call
    ///         a build failure. The two are equivalent; only the spelling changed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Configuration, not <c>Log.Logger</c>.</b> Survival's <c>LoggerDefault.Configure</c>
    ///         builds a static logger from <c>appsettings.json</c> before the host exists. Reading
    ///         from <c>builder.Configuration</c> instead is what lets a pod's environment variables
    ///         and mounted secrets reach the logger, which is how it is configured in a cluster.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE SINKS MOVED OUT OF <c>appsettings.json</c> AND INTO THIS METHOD, AND THAT IS
    ///         THE LOG CANARY RATHER THAN A TIDY-UP.</b> docs/plan/18 § Platform security, row
    ///         Secrets asks for a scanner on the log pipeline;
    ///         <see cref="Logging.SecretScrubbingSink" /> is it, and it is only a control over the
    ///         sinks it wraps. <c>ReadFrom.Configuration</c> adds sinks beside the wrapper rather
    ///         than inside it, so a <c>Serilog:WriteTo</c> entry would be an unscrubbed egress that
    ///         nothing failed on. Declaring the sinks here and refusing configured ones
    ///         (<see cref="Logging.LogEgress.RefuseConfiguredSinks" />) is what makes "every line is
    ///         scanned" a property of the host instead of a property of the current
    ///         <c>appsettings.json</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>ClearProviders</c>, and what it does <i>not</i> close — because the first
    ///         version of this note claimed the opposite and was wrong.</b> The obvious reading is
    ///         that the console, debug and event-source providers
    ///         <c>WebApplication.CreateBuilder</c> installs are unscrubbed egress paths, since a
    ///         <c>Microsoft.Extensions.Logging</c> provider is fed directly and not through Serilog.
    ///         They are not. <c>AddSerilog</c> with the default <c>writeToProviders: false</c>
    ///         <b>replaces <c>ILoggerFactory</c></b> with <c>SerilogLoggerFactory</c>, so MEL's own
    ///         factory is never constructed and no registered provider is ever consumed —
    ///         <c>LogEgressTests.EveryLoggerInTheProcessIsASerilogLogger</c> is the assertion, and it
    ///         is the one that makes the egress singular. This call removes registrations that were
    ///         already inert, so that the service collection stops describing egress paths that do
    ///         not exist.
    ///     </para>
    /// </remarks>
    static void ConfigureHost(WebApplicationBuilder builder) {
        builder.Host.AddAppSettingsSecretsJson().UseAutofac();

        LogEgress.RefuseConfiguredSinks(builder.Configuration);

        builder.Logging.ClearProviders();

        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.ScrubbingSecrets(sinks => {
                    // stdout, which in a cluster is the node's log collector. Formerly
                    // `Serilog:WriteTo: [{ Name: Console }]` in every host's appsettings.json.
                    sinks.Console();

                    // The OTLP sink, so logs land in the same pipeline as traces (docs/plan/16). It
                    // is a no-op without OTEL_EXPORTER_OTLP_ENDPOINT, which is what makes a local
                    // run quiet.
                    sinks.OpenTelemetry();
                }
            )
        );
    }

    static CyberCloudClusterOptions BindClusterOptions(WebApplicationBuilder builder) {
        var section = builder.Configuration.GetSection(CyberCloudClusterOptions.SectionName);
        builder.Services.Configure<CyberCloudClusterOptions>(section);

        var options = new CyberCloudClusterOptions();
        section.Bind(options);
        return options;
    }
}
