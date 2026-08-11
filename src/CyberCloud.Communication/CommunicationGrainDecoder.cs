using Orleans.Multitenant;

namespace CyberCloud.Communication;

/// <summary>
///     The one place a grain in this assembly decodes its own key. The same type, for the same
///     reasons, as <c>CyberCloud.Metering.MeteringGrainKeys</c>.
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
///         for bugs and infrastructure. Nothing outside this process composes a grain key, so an
///         unparseable one means our own code did.
///     </para>
///     <para>
///         ⚠ <b>Every grain here is a <see cref="GrainKeyKind.Resource" /> key, including the two
///         that are not resources.</b> <see cref="CommunicationGrainKeys" /> says why and what it
///         costs. The consequence for this type is that <see cref="ResourceOf" /> cannot tell a message
///         grain's key from a service grain's — so it checks the shape and nothing more, and each
///         grain learns what it is from its own state rather than from its key. The one thing the
///         key does still catch is a grain reached with a <c>sub/</c> or <c>user/</c> key, which is
///         a wiring mistake and is the mistake that actually happens.
///     </para>
/// </remarks>
static class CommunicationGrainDecoder {
    /// <summary>The GUID in a <c>res/{guid:N}</c> key.</summary>
    /// <param name="grain">The activating grain.</param>
    /// <exception cref="InvalidOperationException">The key is malformed or is the wrong shape.</exception>
    public static Guid ResourceOf(IAddressable grain) {
        var within = grain.GetKeyWithinTenant();
        var parsed = GrainKeys.Parse(within);

        if (parsed.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated with the key '{within}', which is not a grain "
                + $"key: {error.Message}"
            );
        }

        var key = parsed.GetValueOrThrow();
        if (key.Kind != GrainKeyKind.Resource) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} expects a Resource key and was activated with '{within}', "
                + $"which is a {key.Kind} key. Address it through CommunicationGrainKeys — a "
                + "communication grain reached through the wrong key shape would send another "
                + "tenant's message or check another service's suppression list."
            );
        }

        return key.Id;
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
