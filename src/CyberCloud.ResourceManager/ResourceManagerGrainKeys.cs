using Orleans.Multitenant;

namespace CyberCloud.ResourceManager;

/// <summary>
///     The one place a grain in this assembly decodes its own key.
/// </summary>
/// <remarks>
///     The same type, for the same reasons, as <c>CyberCloud.Tenancy/TenancyGrainKeys.cs</c>: a
///     grain's key is its identity, decoding it through <c>GrainKeys.Parse</c> rather than by string
///     surgery is ADR-002's point, and the parser's canonicity guard means a key that would
///     round-trip to a different string is rejected here rather than becoming a second activation of
///     the same entity.
///     <para>
///         ⚠ <b>A key this class rejects is a bug, not a domain error, and it throws.</b> Nothing
///         outside this process can produce a malformed grain key — keys are built by
///         <c>GrainKeys</c> and the gateway never forwards a raw one — so an unparseable key means our
///         own code composed one, which must page someone rather than return a tidy <c>400</c>
///         (docs/plan/00 § Coding standards).
///     </para>
///     <para>
///         It is written out here rather than shared with the tenancy assembly because
///         <c>TenancyGrainKeys</c> is <c>internal</c> to that assembly and making it public would
///         publish a helper whose whole value is that only grains use it.
///     </para>
/// </remarks>
static class ResourceManagerGrainKeys {
    /// <summary>The within-tenant key, decoded and checked against the kind the grain type expects.</summary>
    /// <exception cref="InvalidOperationException">The key is malformed or is the wrong shape.</exception>
    public static GrainKey Decode(IAddressable grain, GrainKeyKind expected) {
        var within = grain.GetKeyWithinTenant();
        var parsed = GrainKeys.Parse(within);

        if (parsed.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated with the key '{within}', which is not a grain "
                + $"key: {error.Message}"
            );
        }

        var key = parsed.GetValueOrThrow();
        if (key.Kind != expected) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} expects a {expected} key and was activated with '{within}', "
                + $"which is a {key.Kind} key. A grain reached through the wrong key shape would read "
                + "another entity's state."
            );
        }

        return key;
    }

    /// <summary>The tenant a tenant-qualified grain belongs to, as a GUID.</summary>
    /// <exception cref="InvalidOperationException">The grain is not tenant-qualified.</exception>
    public static Guid TenantOf(IAddressable grain) {
        var tenantId = grain.GetTenantId();

        _ = tenantId
            ?? throw new InvalidOperationException(
                $"{grain.GetType().Name} is a tenant-scoped grain but was activated with no tenant "
                + "qualification. Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<…>(…), not "
                + "with IGrainFactory.GetGrain<…>(…) — ADR-002."
            );

        if (!Guid.TryParse(tenantId, out var id)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated for tenant '{tenantId}', which is not a GUID. "
                + "Tenant-scoped grains are qualified with a tenant GUID; the non-GUID tenant id is "
                + "Orleans.Multitenant's null-tenant sentinel and belongs only to platform grains "
                + "(docs/plan/04 § Grain taxonomy, the Platform row)."
            );
        }

        return id;
    }
}
