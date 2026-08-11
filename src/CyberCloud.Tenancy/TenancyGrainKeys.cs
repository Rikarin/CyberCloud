using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;

namespace CyberCloud.Tenancy;

/// <summary>
///     The one place a grain in this assembly decodes its own key.
/// </summary>
/// <remarks>
///     <para>
///         A grain's key is its identity, and every grain here begins by asking what its key says.
///         Doing that through <c>GrainKeys.Parse</c> rather than by string surgery is ADR-002's
///         whole point (docs/plan/02 § ADR-002 — "<c>GrainKeys</c> is the only type allowed to build
///         the within-tenant part"), and the parser's canonicity guard means a key that would
///         round-trip to a different string is rejected here rather than becoming a second
///         activation of the same entity.
///     </para>
///     <para>
///         ⚠ <b>A key this class rejects is a bug, not a domain error, and it throws.</b>
///         docs/plan/00 § Non-negotiables reserves <c>Result</c> for domain outcomes and exceptions
///         for bugs and infrastructure. Nothing outside this process can produce a malformed grain
///         key: keys are built by <c>GrainKeys</c> and the gateway never forwards a raw one. So an
///         unparseable key means our own code composed one, which must page someone rather than
///         return a tidy <c>400</c>.
///     </para>
/// </remarks>
static class TenancyGrainKeys {
    /// <summary>
    ///     The within-tenant key of a tenant-qualified grain, decoded and checked against the kind
    ///     the grain type expects.
    /// </summary>
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
                + $"which is a {key.Kind} key. A grain reached through the wrong key shape would "
                + "read another entity's state."
            );
        }

        return key;
    }

    /// <summary>
    ///     The tenant a tenant-qualified grain belongs to, as a GUID.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         This is exactly the <c>Guid.Parse(tenantId)</c> that docs/plan/05 § Storage provider
    ///         wiring gets wrong, and it is correct <i>here</i> for a reason that does not generalise.
    ///     </b>
    ///     The storage callback receives <c>"Null"</c> for platform grains and must not parse; a
    ///     <i>tenant-qualified</i> grain, by construction, was reached through
    ///     <c>IGrainFactory.ForTenant(guid)</c> and its tenant id is a GUID. A null here means the
    ///     grain was reached without tenant qualification at all, which is a wiring bug and is
    ///     reported as one.
    /// </remarks>
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

    /// <summary>
    ///     Asserts that a null-tenant platform grain really has no tenant qualification, and that its
    ///     key is the singleton it should be.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is tenant-qualified, or the key is wrong.</exception>
    public static void EnsurePlatformSingleton(IAddressable grain, string singleton) {
        var tenantId = grain.GetTenantId();

        if (tenantId is not null) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} is a null-tenant platform grain (docs/plan/04 § Grain "
                + $"taxonomy) but was activated for tenant '{tenantId}'. Tenant-qualifying it would "
                + "give the platform one activation per tenant of a thing there is exactly one of."
            );
        }

        var key = grain.GetKeyWithinTenant();
        var expected = GrainKeys.PlatformSingleton(singleton);

        if (!string.Equals(key, expected, StringComparison.Ordinal)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated with the key '{key}' and its only key is "
                + $"'{expected}'. A platform singleton reached through a second key string is a "
                + "second activation of a thing there is exactly one of."
            );
        }
    }

    /// <summary>The <c>Result</c> failure a grain returns when it has not been created yet.</summary>
    public static Error NotCreated(ErrorCode code, string what, string key) =>
        new(code, $"{what} '{key}' does not exist.");
}
