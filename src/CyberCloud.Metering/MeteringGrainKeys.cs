using Orleans.Multitenant;

namespace CyberCloud.Metering;

/// <summary>
///     The one place a grain in this assembly decodes its own key. The same type, for the same
///     reasons, as <c>CyberCloud.Tenancy.TenancyGrainKeys</c> and
///     <c>CyberCloud.ResourceManager.ResourceManagerGrainKeys</c>.
/// </summary>
/// <remarks>
///     <para>
///         A grain's key is its identity, and every grain here begins by asking what its key says.
///         Doing that through <c>GrainKeys.Parse</c> rather than by string surgery is ADR-002's whole
///         point (docs/plan/02 § ADR-002 — "<c>GrainKeys</c> is the only type allowed to build the
///         within-tenant part"), and the parser's canonicity guard means a key that would round-trip
///         to a different string is rejected here rather than becoming a second activation of the
///         same entity.
///     </para>
///     <para>
///         ⚠ <b>A key this class rejects is a bug, not a domain error, and it throws.</b>
///         docs/plan/00 § Non-negotiables reserves <c>Result</c> for domain outcomes and exceptions
///         for bugs and infrastructure. Nothing outside this process can produce a malformed grain
///         key, so an unparseable one means our own code composed it.
///     </para>
///     <para>
///         ⚠ <b>Every grain here is keyed <c>sub/{subscriptionId:N}</c> and none adds a key
///         shape.</b> <c>GrainKeys</c> accepts a closed set of shapes and adding one would be a
///         change to <c>CyberCloud.Core</c> reviewed like a schema change. Nothing here needs one:
///         the sampler, the rollup and the ledger are each per-subscription, which is a key that
///         already exists. A rollup keyed per (subscription, hour) was considered and rejected for
///         exactly that reason — it would have bought an activation boundary and cost a new key
///         shape, a new parse case and a new way to spell a subscription.
///     </para>
/// </remarks>
static class MeteringGrainKeys {
    /// <summary>
    ///     The within-tenant key of a tenant-qualified grain, decoded and checked against the kind
    ///     the grain type expects.
    /// </summary>
    /// <param name="grain">The activating grain.</param>
    /// <param name="expected">The key shape this grain type is addressed by.</param>
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
                + $"which is a {key.Kind} key. A metering grain reached through the wrong key shape "
                + "would bill another subscription."
            );
        }

        return key;
    }

    /// <summary>The tenant a tenant-qualified grain belongs to, as a GUID.</summary>
    /// <param name="grain">The activating grain.</param>
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
