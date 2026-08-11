using CyberCloud.ServiceDefaults.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace CyberCloud.ServiceDefaults;

/// <summary>
///     The cross-cutting host wiring every Cyber Cloud process shares — descended in shape from
///     Survival's <c>Survival.ServiceDefaults.Extensions</c> (docs/plan/03 § Foundation).
/// </summary>
public static class ServiceDefaultsExtensions {
    /// <summary>The activity/meter source names Cyber Cloud emits under.</summary>
    /// <remarks>
    ///     A prefix rather than a list, so a new assembly's <c>ActivitySource</c> is collected
    ///     without an edit here. Orleans' own sources are added separately below, by their exact
    ///     names, because they are not ours to rename.
    /// </remarks>
    public const string TelemetrySourcePrefix = "CyberCloud";

    const string HealthCheckPolicy = "HealthChecks";

    /// <summary>
    ///     OpenTelemetry, the default health checks and forwarded headers. Called by every host.
    /// </summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.Configure<ForwardedHeadersOptions>(options => {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // ⚠ Cleared on purpose. The default only trusts loopback, which in a cluster means the
                // headers the ingress sets are dropped and every request appears to come from the node.
                // The proxy in front of these pods is the platform's own ingress; anything reaching a
                // pod directly is not a request path we serve.
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
            }
        );

        return builder;
    }

    /// <summary>
    ///     Traces and metrics. docs/plan/02 § Data, transport, Kubernetes — the OTLP exporter,
    ///     Orleans' own sources, and everything under <see cref="TelemetrySourcePrefix" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is only half of what makes a trace survive a grain call. The other half is
    ///     <c>AddActivityPropagation()</c> on the Orleans builder — see
    ///     <see cref="OrleansApplication.CreateSilo" />. Collecting
    ///     <c>Microsoft.Orleans.Application</c> here without it produces a trace that stops at the
    ///     gateway (docs/plan/04 § Silo composition), which looks like working tracing until somebody needs it.
    /// </remarks>
    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging => {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            }
        );

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddAttributes(
                    new Dictionary<string, object>(StringComparer.Ordinal) {
                        ["service.instance.id"] = Environment.GetEnvironmentVariable("POD_NAME")
                            ?? Guid.NewGuid().ToString("N"),
                        ["service.version"] =
                            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"
                    }
                )
            )
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Microsoft.Orleans")
                .AddMeter($"{TelemetrySourcePrefix}.*")
            )
            .WithTracing(tracing => tracing
                .AddSource("Microsoft.Orleans.Runtime")
                .AddSource("Microsoft.Orleans.Application")
                .AddSource($"{TelemetrySourcePrefix}.*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
            );

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    ///     The one check that is safe to put behind a liveness probe.
    /// </summary>
    /// <remarks>
    ///     <c>self</c> answers "is this process able to run a callback", that is, is it wedged. It is
    ///     tagged <see cref="HealthCheckTags.Live" /> and nothing else ever should be — see the
    ///     remarks on <see cref="HealthCheckTags" />.
    /// </remarks>
    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddRequestTimeouts(timeouts =>
            timeouts.AddPolicy(HealthCheckPolicy, TimeSpan.FromSeconds(5))
        );

        builder.Services.AddOutputCache(caching =>
            caching.AddPolicy(HealthCheckPolicy, policy => policy.Expire(TimeSpan.FromSeconds(10)))
        );

        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [HealthCheckTags.Live]);

        return builder;
    }

    /// <summary>
    ///     The silo's health checks. docs/plan/04 § Silo composition.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exactly one check carries <see cref="HealthCheckTags.Ready" />.</b> Readiness is
    ///         a single question — <i>would a request routed here be served?</i> — and
    ///         <see cref="SiloReadinessHealthCheck" /> is the only thing that answers it. The other
    ///         two are reported on <c>/api/health</c> and scraped, but they cannot evict a pod:
    ///         <c>cluster</c> describes peers, and <c>silo-participants</c> cannot tell an idle silo
    ///         from a wedged one. Adding a tag to either is how a rolling upgrade turns into a
    ///         cascading eviction.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Silo-only.</b> <see cref="SiloReadinessHealthCheck" /> resolves
    ///         <see cref="ILocalSiloDetails" />, which is not registered in an Orleans client. The
    ///         gateway calls <see cref="AddOrleansClientHealthChecks" /> instead.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is NOT here, and Survival has:</b> a <c>GrainHealthCheck</c> that pings a
    ///         <c>[StatelessWorker(1)]</c> grain of its own, and a <c>StorageHealthCheck</c> that
    ///         calls a grain which touches storage. The first is subsumed — the readiness check's
    ///         <c>IManagementGrain</c> call already proves the grain runtime routes messages, using
    ///         Orleans' own system grain rather than one this assembly has to declare. The second has
    ///         an answer that is deliberately not in this method: <see cref="DurableShardHealthCheck" />
    ///         is registered by <c>AddCyberCloudGrainStorage</c>, because only a silo that has a
    ///         durable tier can meaningfully report on one — and it is <b>untagged</b>, so a shard
    ///         outage is visible on <c>/api/health</c> and cannot reach readiness. See the remarks on
    ///         <see cref="SiloReadinessHealthCheck" /> for why a storage dependency must not gate
    ///         readiness, and the remarks on <see cref="DurableShardHealthCheck" /> for why
    ///         <c>Degraded</c>-plus-<c>ready</c> was rejected as the alternative.
    ///     </para>
    /// </remarks>
    public static IHostApplicationBuilder AddOrleansHealthChecks(this IHostApplicationBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        // Singleton because SiloParticipantsHealthCheck carries the "since when" window across
        // probes; a transient instance would ask about a window it had just reset.
        builder.Services.AddSingleton<SiloParticipantsHealthCheck>();

        builder.Services
            .AddHealthChecks()
            .AddCheck<SiloReadinessHealthCheck>("silo-ready", tags: [HealthCheckTags.Ready])
            .AddCheck<SiloParticipantsHealthCheck>("silo-participants")
            .AddCheck<ClusterHealthCheck>("cluster");

        return builder;
    }

    /// <summary>
    ///     The Orleans <i>client</i>'s health checks — the gateway's half of
    ///     <see cref="AddOrleansHealthChecks" />.
    /// </summary>
    /// <remarks>
    ///     A gateway is ready when it can reach the cluster, and "can reach the cluster" is what
    ///     <see cref="ClusterHealthCheck" /> measures — but it is registered <b>untagged</b> here
    ///     too, for the same cascading-eviction reason: an Orleans client reconnects on its own, and
    ///     evicting every gateway pod during a silo rollout is precisely the failure that
    ///     docs/plan/00 § The quality bar, concretely forbids. A gateway's readiness is its HTTP
    ///     listener, which the <c>self</c> check covers.
    /// </remarks>
    public static IHostApplicationBuilder AddOrleansClientHealthChecks(
        this IHostApplicationBuilder builder
    ) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHealthChecks().AddCheck<ClusterHealthCheck>("cluster");

        return builder;
    }

    /// <summary>
    ///     Maps <c>/alive</c> (liveness), <c>/health</c> (readiness) and <c>/api/health</c> (the
    ///     human-readable detail).
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>/health</c> runs only the <see cref="HealthCheckTags.Ready" /> checks, not
    ///     everything. The ASP.NET default predicate is "run every registered check", which would
    ///     put <c>cluster</c> — a statement about <i>other</i> machines — behind this process's
    ///     readiness probe.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1062:Validate arguments of public methods",
        Justification = "app is dereferenced immediately; a null there is a null-reference at the "
            + "call site, which is the same diagnostic one line earlier."
    )]
    public static IEndpointRouteBuilder MapDefaultEndpoints(this IEndpointRouteBuilder app) {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(string.Empty);
        group.CacheOutput(HealthCheckPolicy).WithRequestTimeout(HealthCheckPolicy);

        group.MapHealthChecks(
            "/alive",
            new() { Predicate = static r => r.Tags.Contains(HealthCheckTags.Live) }
        );

        group.MapHealthChecks(
            "/health",
            new() { Predicate = static r => r.Tags.Contains(HealthCheckTags.Ready) }
        );

        group.MapHealthChecks(
            "/api/health",
            new() { Predicate = static _ => true, ResponseWriter = HealthReportBody.WriteAsync }
        );

        return app;
    }
}
