using CyberCloud.Authorization.Contracts;
using Orleans.Multitenant;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     A grain that makes a grain call on behalf of a test — the only way to put a <i>grain</i> on
///     the calling side of the cross-tenant filter.
/// </summary>
/// <remarks>
///     ⚠ <b>The same type, for the same reason, as <c>CyberCloud.Tenancy.Tests</c>'s.</b>
///     <c>Orleans.Multitenant</c>'s <c>TenantSeparatingCallFilter</c> reads
///     <c>context.SourceId</c> and returns without consulting the authorizer when the source is
///     absent, a client or a system target. A test calling a grain directly is a <i>client</i>, so
///     it can never exercise the deny path; routing the call through this grain makes the source a
///     grain with a tenant. That is exactly the "bug in one provider" docs/plan/04 § Silo
///     composition says the separation catches — and for an authorization store it is the difference
///     between a leak and a leak of everyone's permissions.
/// </remarks>
[Alias("CyberCloud.Authorization.Tests.IAuthorizationReacherGrain")]
public interface IAuthorizationReacherGrain : IGrainWithStringKey {
    /// <summary>Calls <c>IObjectRelationsGrain.ReadAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key, tenant prefix and all.</param>
    Task<int> ReachObjectRelationsByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>ISubjectRelationsGrain.ListAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<int> ReachSubjectRelationsByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>ICheckGrain.CheckAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="subject">The subject, in the tuple grammar.</param>
    Task<bool> ReachCheckByRawKeyAsync(string physicalKey, string permission, string subject);

    /// <summary>Calls <c>ITupleStoreGrain.GetTokenAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<long> ReachTupleStoreByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>ITupleStoreGrain.WriteAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    /// <param name="tuple">The tuple to try to write into somebody else's tenant.</param>
    Task<bool> WriteThroughTupleStoreByRawKeyAsync(string physicalKey, string tuple);

    /// <summary>The tenant this grain is qualified to, as Orleans.Multitenant reports it.</summary>
    Task<string?> MyTenantAsync();
}

/// <inheritdoc />
public sealed class AuthorizationReacherGrain : Grain, IAuthorizationReacherGrain {
    /// <inheritdoc />
    public async Task<int> ReachObjectRelationsByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<IObjectRelationsGrain>(physicalKey).ReadAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().Count : -1;
    }

    /// <inheritdoc />
    public async Task<int> ReachSubjectRelationsByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<ISubjectRelationsGrain>(physicalKey).ListAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().Count : -1;
    }

    /// <inheritdoc />
    public async Task<bool> ReachCheckByRawKeyAsync(
        string physicalKey,
        string permission,
        string subject
    ) {
        var reached = await GrainFactory.GetGrain<ICheckGrain>(physicalKey)
            .CheckAsync(permission, SubjectRef.Parse(subject).GetValueOrThrow(), Consistency.FullyConsistent);

        return reached.IsSuccess && reached.GetValueOrThrow().Allowed;
    }

    /// <inheritdoc />
    public async Task<long> ReachTupleStoreByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<ITupleStoreGrain>(physicalKey).GetTokenAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().Version : -1;
    }

    /// <inheritdoc />
    public async Task<bool> WriteThroughTupleStoreByRawKeyAsync(string physicalKey, string tuple) {
        var written = await GrainFactory.GetGrain<ITupleStoreGrain>(physicalKey)
            .WriteAsync(RelationTuple.Parse(tuple).GetValueOrThrow());

        return written.IsSuccess;
    }

    /// <inheritdoc />
    public Task<string?> MyTenantAsync() => Task.FromResult(AddressableExtensions.GetTenantId(this));
}
