using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using Orleans.Multitenant;

namespace CyberCloud.Authorization;

/// <summary>
///     The one place a grain in this assembly decodes its own key. The same contract, and the same
///     "a key this class rejects is a bug and it throws", as <c>CyberCloud.Tenancy</c>'s
///     <c>TenancyGrainKeys</c>.
/// </summary>
static class AuthorizationGrainKeys {
    /// <summary>Decodes a <c>rel/…/{type}/{id}</c> key into the object it addresses.</summary>
    /// <exception cref="InvalidOperationException">The key is malformed or is the wrong shape.</exception>
    public static ObjectRef DecodeObject(IAddressable grain, GrainKeyKind expected) {
        var key = Decode(grain, expected);
        var reference = ObjectRef.Create(key.ObjectType, key.ObjectId);

        return reference.TryGetError(out var error)
            ? throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated with a key whose object is not a valid "
                + $"reference: {error.Message}"
            )
            : reference.GetValueOrThrow();
    }

    /// <summary>Decodes a key and checks it is the shape the grain type expects.</summary>
    /// <exception cref="InvalidOperationException">The key is malformed or is the wrong shape.</exception>
    public static GrainKey Decode(IAddressable grain, GrainKeyKind expected) {
        var within = grain.GetKeyWithinTenant();
        var parsed = GrainKeys.Parse(within);

        if (parsed.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated with the key '{within}', which is not a "
                + $"grain key: {error.Message}"
            );
        }

        var key = parsed.GetValueOrThrow();
        if (key.Kind != expected) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} expects a {expected} key and was activated with "
                + $"'{within}', which is a {key.Kind} key. A grain reached through the wrong key "
                + "shape would read another entity's tuples, which for this assembly means "
                + "answering an authorization question about the wrong object."
            );
        }

        return key;
    }

    /// <summary>The tenant a tenant-qualified authorization grain belongs to.</summary>
    /// <exception cref="InvalidOperationException">The grain is not tenant-qualified.</exception>
    public static Guid TenantOf(IAddressable grain) {
        var tenantId = grain.GetTenantId();

        _ = tenantId
            ?? throw new InvalidOperationException(
                $"{grain.GetType().Name} is a tenant-scoped grain but was activated with no tenant "
                + "qualification. Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<…>(…) — "
                + "ADR-002. Tuples are sharded by tenant (docs/plan/07 § Storage) and an unqualified "
                + "activation would put one tenant's grants on whatever shard the null tenant lands on."
            );

        if (!Guid.TryParse(tenantId, out var id)) {
            throw new InvalidOperationException(
                $"{grain.GetType().Name} was activated for tenant '{tenantId}', which is not a "
                + "GUID. Every authorization grain is tenant-qualified; there is no null-tenant "
                + "authorization grain."
            );
        }

        return id;
    }
}
