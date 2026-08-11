using CyberCloud.Authorization.Evaluation;
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
public static class AuthorizationSiloBuilderExtensions
{
    /// <summary>
    ///     Registers the built-in schema (docs/plan/07 § Azure RBAC, expressed in it), the
    ///     document's caps, the M1 no-op membership index and the no-op write interceptor.
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
    ///     Every registration is <c>TryAdd</c>, so a host that has already registered its own
    ///     limits, membership index or interceptor keeps them — the same convention
    ///     <c>AddCyberCloudTenancy</c> uses for <c>IClock</c>.
    /// </remarks>
    public static ISiloBuilder AddCyberCloudAuthorization(
        this ISiloBuilder silo, AuthorizationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(schema);

        return silo.ConfigureServices(services =>
        {
            services.TryAddSingleton(schema);
            services.TryAddSingleton(AuthorizationLimits.Default);
            services.TryAddSingleton<IMembershipIndex>(NoMembershipIndex.Instance);
            services.TryAddSingleton<IRelationWriteInterceptor>(NoRelationWriteInterceptor.Instance);
        });
    }
}
