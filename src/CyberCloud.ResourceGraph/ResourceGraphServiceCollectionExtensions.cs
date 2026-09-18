using CyberCloud.Core.Time;
using CyberCloud.ResourceGraph.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CyberCloud.ResourceGraph;

/// <summary>Registers the stream's publisher, and on a silo its projector, into a host that opted in.</summary>
/// <remarks>
///     ⚠ <b>Two methods, because the two hosts are two processes with two jobs.</b> The gateway
///     publishes — <c>ResourceManagerService</c> runs there — and consumes nothing; a silo publishes
///     too (<c>OperationGrain</c> emits the terminal transitions) and runs the projector. A gateway
///     that ran the projector would read a tenant's check grain over the client connection on every
///     event, for a row the silo next to it was about to write anyway.
/// </remarks>
public static class ResourceGraphServiceCollectionExtensions {
    /// <summary>
    ///     Replaces the resource manager's logging sink with <see cref="NatsResourceChangedSink" />.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The bound section, with <see cref="ResourceGraphOptions.NatsUrl" /> set.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentException">The section has no NATS URL.</exception>
    /// <remarks>
    ///     ⚠ <b>Call it only when <see cref="ResourceGraphOptions.IsPublisherConfigured" /> is
    ///     true</b>, the way the hosts call <c>AddS3ObjectStore</c>: an unconfigured section leaves
    ///     <c>LoggingResourceChangedSink</c> in place, and this method refuses an empty URL rather
    ///     than registering a sink that would fail every publish with a connection error naming an
    ///     address nobody set. <c>AddSingleton</c>, not <c>TryAdd</c>, so the order against
    ///     <c>AddCyberCloudResourceManager</c>'s <c>TryAdd</c> does not matter.
    /// </remarks>
    public static IServiceCollection AddResourceChangedPublisher(this IServiceCollection services, ResourceGraphOptions options) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsPublisherConfigured) {
            throw new ArgumentException(
                $"{ResourceGraphOptions.SectionName}:NatsUrl is empty (and ConnectionStrings:{ResourceGraphOptions.NatsConnectionStringName} "
                + "is not set), so there is no stream to publish resource-changed on. Leave the section "
                + "unconfigured to keep the logging sink, or set the URL.",
                nameof(options)
            );
        }

        services.AddSingleton(options);
        services.AddSingleton<IResourceChangedSink>(provider => new NatsResourceChangedSink(
                options,
                provider.GetRequiredService<ILogger<NatsResourceChangedSink>>()
            )
        );

        return services;
    }

    /// <summary>
    ///     The publisher, plus the projector that consumes the stream into ClickHouse. A silo's call.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The bound section, with both halves set.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentException">The section is incomplete, or names a plain-HTTP ClickHouse without opting in.</exception>
    /// <remarks>
    ///     <para>
    ///         Validated now rather than at the first event, so a wrong section is a start-up
    ///         failure: <see cref="ClickHouseClient.ValidatedEndpoint" /> runs here, and a silo with a
    ///         plain-http endpoint and no <c>AllowInsecureTransport</c> does not start.
    ///     </para>
    ///     <para>
    ///         <see cref="IResourceAccessResolver" /> is <c>TryAdd</c>, so a host that registered a
    ///         substitute first keeps it — the test fixture's way in. The projector itself is the
    ///         host's deliberate choice and is <c>AddSingleton</c>; the ClickHouse client and the store
    ///         are <c>TryAdd</c>, because <see cref="AddResourceGraphQuery" /> wants the same two and a
    ///         host may call both.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddResourceGraphProjector(this IServiceCollection services, ResourceGraphOptions options) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddResourceChangedPublisher(options);

        if (!options.IsProjectorConfigured) {
            throw new ArgumentException(
                $"{ResourceGraphOptions.SectionName}:ClickHouseEndpoint is empty, so there is nowhere for the "
                + "resource-graph projection to land. A silo that publishes and does not project leaves "
                + "this method uncalled and calls AddResourceChangedPublisher instead.",
                nameof(options)
            );
        }

        _ = ClickHouseClient.ValidatedEndpoint(options);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IResourceAccessResolver, ReBacResourceAccessResolver>();
        AddClickHouse(services, options);
        services.AddSingleton<ResourceGraphProjector>();
        services.AddHostedService(provider => provider.GetRequiredService<ResourceGraphProjector>());

        return services;
    }

    /// <summary>
    ///     The resource graph's query API over ClickHouse — <see cref="IResourceGraphQuery" /> in
    ///     place of the resource manager's refusing default. The gateway's call.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The bound section, with the ClickHouse half set.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentException">The section has no ClickHouse endpoint, or names a plain-HTTP one without opting in.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Needs no NATS URL, and runs no projector.</b> A query reads the table; who fills
    ///         it is the silos' business. The gateway calls this when
    ///         <see cref="ResourceGraphOptions.IsQueryConfigured" /> is true and
    ///         <see cref="AddResourceChangedPublisher" /> when the publisher half is, and the two are
    ///         independent — a gateway with a ClickHouse endpoint and no stream answers queries over
    ///         whatever the silos projected.
    ///     </para>
    ///     <para>
    ///         <c>Replace</c> rather than <c>Add</c> for the seam, so the order against
    ///         <c>AddCyberCloudResourceManager</c>'s <c>TryAdd</c> of
    ///         <c>UnavailableResourceGraphQuery</c> does not matter and one registration remains.
    ///         <see cref="ICallerAccessResolver" /> is <c>TryAdd</c>, so a host — or a test — that
    ///         registered a substitute first keeps it, the way <see cref="IResourceAccessResolver" />
    ///         is for the projector.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddResourceGraphQuery(this IServiceCollection services, ResourceGraphOptions options) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsQueryConfigured) {
            throw new ArgumentException(
                $"{ResourceGraphOptions.SectionName}:ClickHouseEndpoint is empty, so there is no projection to query. "
                + "Leave the section unconfigured to keep the refusing default, or set the endpoint.",
                nameof(options)
            );
        }

        _ = ClickHouseClient.ValidatedEndpoint(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<ICallerAccessResolver, MembershipIndexCallerAccessResolver>();
        AddClickHouse(services, options);
        services.Replace(ServiceDescriptor.Singleton<IResourceGraphQuery>(provider => new ResourceGraphQueryService(
                    provider.GetRequiredService<ClickHouseClient>(),
                    provider.GetRequiredService<ClickHouseResourceGraphStore>(),
                    provider.GetRequiredService<ICallerAccessResolver>(),
                    options
                )
            )
        );

        return services;
    }

    /// <summary>
    ///     One <see cref="ClickHouseClient" /> and one store per process, whichever of the two calls
    ///     above runs first or both. One long-lived <see cref="HttpClient" /> per client, following
    ///     <c>CyberCloud.ObjectStorage</c>.
    /// </summary>
    static void AddClickHouse(IServiceCollection services, ResourceGraphOptions options) {
        services.TryAddSingleton(_ => new ClickHouseClient(new HttpClient { Timeout = options.RequestTimeout }, options));
        services.TryAddSingleton<ClickHouseResourceGraphStore>();
    }
}
