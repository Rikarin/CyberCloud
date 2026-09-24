using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Authorization;

/// <summary>
///     The silo-side registration for the authorization engine.
/// </summary>
/// <remarks>
///     ⚠ <b>Grain classes need no registration; the four services below do.</b> Orleans discovers
///     grain implementations from the assembly. What it cannot discover is which schema the check
///     grain evaluates against, which is deliberately a decision the host makes: a test silo runs a
///     purpose-built schema and production runs <see cref="CyberCloudSchema" />.
/// </remarks>
public static class AuthorizationSiloBuilderExtensions {
    /// <summary>
    ///     Registers the built-in schema (docs/plan/07 § Azure RBAC, expressed in it), the
    ///     document's caps, the no-op write interceptor, and the system clock.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static ISiloBuilder AddCyberCloudAuthorization(this ISiloBuilder silo) =>
        silo.AddCyberCloudAuthorization(CyberCloudSchema.Instance);

    /// <summary>Registers a specific schema, and the rest of the defaults.</summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="schema">The schema every check on this silo evaluates against.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         Every registration is <c>TryAdd</c>, so a host that has already registered its own
    ///         limits or interceptor keeps them — the same convention <c>AddCyberCloudTenancy</c>
    ///         uses for <c>IClock</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The membership index is not a service any more.</b> Until issue #37 this
    ///         registered <c>NoMembershipIndex</c> and a host could swap it; now <c>CheckGrain</c>
    ///         and <c>ListObjectsGrain</c> build a <c>MembershipIndexReader</c> per request over
    ///         the tenant's <c>IMembershipIndexGrain</c>s, the way they build their tuple readers,
    ///         because an index reader holds a per-request cache and a tenant, neither of which a
    ///         singleton can. A host that wants the walk without the index has
    ///         <c>ConsistencyMode.FullyConsistent</c>, which is the one mode that never reads it.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudAuthorization(
        this ISiloBuilder silo,
        AuthorizationSchema schema
    ) {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(schema);

        return silo.ConfigureServices(services => {
                services.TryAddSingleton(schema);
                services.TryAddSingleton(AuthorizationLimits.Default);
                services.TryAddSingleton<IRelationWriteInterceptor>(NoRelationWriteInterceptor.Instance);

                // The one "now" a tuple's expiry is compared with — by the object grain that hides
                // an expired tuple, the check cache that stops serving an allow it proved, and the
                // store's sweep. A test registers its own clock first and moves it.
                services.TryAddSingleton<IClock, SystemClock>();
            }
        );
    }
}
