using Orleans.Multitenant;

namespace CyberCloud.Identity;

/// <summary>
///     The one place a grain in this assembly decodes its own key.
/// </summary>
/// <remarks>
///     The same type, for the same reasons, as <c>CyberCloud.Tenancy/TenancyGrainKeys.cs</c> and
///     <c>CyberCloud.ResourceManager/ResourceManagerGrainKeys.cs</c>: a grain's key is its identity,
///     decoding it through <c>GrainKeys.Parse</c> rather than by string surgery is ADR-002's point,
///     and the parser's canonicity guard means a key that would round-trip to a different string is
///     rejected here rather than becoming a second activation of the same entity.
///     <para>
///         ⚠ <b>A key this class rejects is a bug, not a domain error, and it throws.</b> Nothing
///         outside this process can produce a malformed grain key, so an unparseable key means our
///         own code composed one, which must page someone rather than return a tidy <c>400</c>
///         (docs/plan/00 § Coding standards).
///     </para>
/// </remarks>
static class IdentityGrainKeys {
    /// <summary>The within-tenant key, decoded and checked against the kind the grain type expects.</summary>
    /// <param name="grain">The activating grain.</param>
    /// <param name="expected">The shape this grain type is addressed by.</param>
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
                + "Every identity grain is tenant-scoped — docs/plan/11 § Sign-up and tenant creation "
                + "makes a user belong to exactly one tenant — so the null-tenant sentinel is never "
                + "correct here."
            );
        }

        return id;
    }
}
