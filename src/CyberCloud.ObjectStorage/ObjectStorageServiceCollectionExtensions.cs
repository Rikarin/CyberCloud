using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.ObjectStorage;

/// <summary>Registers the S3 object store into a host that opted in.</summary>
public static class ObjectStorageServiceCollectionExtensions {
    /// <summary>
    ///     Replaces <c>UnavailableObjectStore</c> with <see cref="S3ObjectStore" /> over the
    ///     configured endpoint.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The <c>CyberCloud:ObjectStorage</c> section, bound.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentException">The section is incomplete, or names a plain-HTTP endpoint without opting in.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Call it only when <see cref="ObjectStorageOptions.IsConfigured" /> is true</b>,
    ///         the way the gateway calls <c>AddOpenBaoSecretResolver</c>: an unconfigured section
    ///         leaves the refusing default, whose message names this method; a configured one that
    ///         is wrong throws here, at composition, so the pod does not start. Misconfigured is not
    ///         the same as unconfigured.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>AddSingleton</c>, not <c>TryAdd</c>, and the order with
    ///             <c>AddCyberCloudResourceManager</c> does not matter.
    ///         </b> The manager registers its
    ///         refusing default with <c>TryAdd</c>; this registration is the host's deliberate
    ///         choice and has to win whichever line comes first. <c>ObjectStorageWiringTests</c>
    ///         asserts both orders.
    ///     </para>
    ///     <para>
    ///         One long-lived <see cref="HttpClient" /> per store, following <c>CyberCloud.Vault</c>:
    ///         nothing in the tree uses <c>IHttpClientFactory</c>, and the pooling the factory would
    ///         give is <c>SocketsHttpHandler</c>'s own.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddS3ObjectStore(this IServiceCollection services, ObjectStorageOptions options) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Validated now rather than at the first request, so a wrong section is a start-up failure.
        _ = S3ObjectStore.ValidatedEndpoint(options);

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton(options);
        services.AddSingleton<IObjectStore>(provider => new S3ObjectStore(
                new HttpClient { Timeout = options.RequestTimeout },
                options,
                provider.GetRequiredService<IClock>()
            )
        );

        return services;
    }
}
