using Orleans.Multitenant;

namespace CyberCloud.Billing;

/// <summary>
///     The one place a grain in this assembly decodes its own key — the same type, for the same
///     reasons, as <c>CyberCloud.Metering.MeteringGrainKeys</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>No new key shape.</b> The billing account is keyed like the tenant it bills
///     (<c>tenant/{tenantId:N}</c>), the cost query like the subscription it prices
///     (<c>sub/{subscriptionId:N}</c>), the budget like the resource it is (<c>res/{budgetId:N}</c>)
///     and the numbering grain is a platform singleton. Each borrows an existing shape for the entity
///     it is about, which costs what <c>CommunicationGrainKeys</c> says borrowing costs: the shape is
///     checked here, the meaning is not, and a repair tool routing by kind would send the key to the
///     tenancy grain of the same shape.
/// </remarks>
static class BillingGrainKeys {
    /// <summary>The within-tenant key, decoded and checked against the kind the grain type expects.</summary>
    /// <param name="grain">The activating grain.</param>
    /// <param name="expected">The key shape this grain type is addressed by.</param>
    /// <exception cref="InvalidOperationException">The key is malformed or the wrong shape.</exception>
    public static GrainKey Decode(IAddressable grain, GrainKeyKind expected) {
        var within = grain.GetKeyWithinTenant();
        var parsed = GrainKeys.Parse(within);

        if (parsed.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated with the key '{within}', which is not a grain key: {error.Message}"
            );
        }

        var key = parsed.GetValueOrThrow();
        if (key.Kind != expected) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} expects a {expected} key and was activated with '{within}', which is a "
                + $"{key.Kind} key. A billing grain reached through the wrong key shape would price or invoice "
                + "somebody else's usage."
            );
        }

        return key;
    }

    /// <summary>The tenant a tenant-qualified grain belongs to.</summary>
    /// <param name="grain">The activating grain.</param>
    /// <exception cref="InvalidOperationException">The grain is not tenant-qualified.</exception>
    public static Guid TenantOf(IAddressable grain) {
        var tenantId = grain.GetTenantId();

        _ = tenantId
            ?? throw new InvalidOperationException(
                $"{grain.GetType().Name} is a tenant-scoped grain but was activated with no tenant qualification. "
                + "Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<…>(…) — ADR-002."
            );

        return Guid.TryParse(tenantId, out var id)
            ? id
            : throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated for tenant '{tenantId}', which is not a GUID — the "
                + "null-tenant sentinel belongs to platform grains only."
            );
    }
}
