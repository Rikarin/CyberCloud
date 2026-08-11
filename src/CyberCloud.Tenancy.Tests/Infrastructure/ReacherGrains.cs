using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;

namespace CyberCloud.Tenancy.Tests.Infrastructure;

/// <summary>
///     A grain that makes a grain call on behalf of a test — the only way to put a <i>grain</i> on
///     the calling side of the cross-tenant filter.
/// </summary>
/// <remarks>
///     ⚠ <b>This type exists because of a specific property of the mechanism under test.</b>
///     <c>Orleans.Multitenant</c>'s <c>TenantSeparatingCallFilter</c> is an
///     <c>IIncomingGrainCallFilter</c> that reads <c>context.SourceId</c>, and returns without
///     consulting the authorizer when the source is absent, a client or a system target. A test
///     calling a grain directly is a <i>client</i>, so it can never exercise the deny path. Routing
///     the call through this grain is what makes the source a grain with a tenant — which is exactly
///     the "bug in one provider" that docs/plan/04 § Silo composition says the separation catches.
/// </remarks>
[Alias("CyberCloud.Tenancy.Tests.IReacherGrain")]
public interface IReacherGrain : IGrainWithStringKey {
    /// <summary>Calls <c>ITenantGrain.GetAsync</c> on a grain named by its <b>raw physical key</b>.</summary>
    /// <param name="physicalKey">The whole key, tenant prefix and all.</param>
    Task<string> ReachTenantByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>ISubscriptionGrain.GetAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<string> ReachSubscriptionByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>IResourceIndexGrain.GetAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<string> ReachIndexByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>IQuotaGrain.GetUsageAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<string> ReachQuotaByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>IResourceGroupGrain.ListAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<int> ReachResourceGroupByRawKeyAsync(string physicalKey);

    /// <summary>Calls <c>IEmailIndexGrain.GetAsync</c> by raw physical key.</summary>
    /// <param name="physicalKey">The whole key.</param>
    Task<string> ReachEmailIndexByRawKeyAsync(string physicalKey);

    /// <summary>Reaches the null-tenant tenant directory — the edge the authorizer allows.</summary>
    Task<int> ReachTenantDirectoryAsync();

    /// <summary>Reaches the null-tenant shard map — the other allowed edge.</summary>
    Task<long> ReachShardMapAsync();

    /// <summary>The tenant this grain is qualified to, as Orleans.Multitenant reports it.</summary>
    Task<string?> MyTenantAsync();
}

/// <inheritdoc />
public sealed class ReacherGrain : Grain, IReacherGrain {
    /// <inheritdoc />
    public async Task<string> ReachTenantByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<ITenantGrain>(physicalKey).GetAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().Slug : $"<{reached.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachSubscriptionByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<ISubscriptionGrain>(physicalKey).GetAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().DisplayName : $"<{reached.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachIndexByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<IResourceIndexGrain>(physicalKey).GetAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().IndexedValue : $"<{reached.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<string> ReachQuotaByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<IQuotaGrain>(physicalKey)
            .GetUsageAsync(QuotaMeter.Vcpu);

        return reached.IsSuccess
            ? reached.GetValueOrThrow().Committed.ToString("0.##", null)
            : $"<{reached.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<int> ReachResourceGroupByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<IResourceGroupGrain>(physicalKey).ListAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().Count : -1;
    }

    /// <inheritdoc />
    public async Task<string> ReachEmailIndexByRawKeyAsync(string physicalKey) {
        var reached = await GrainFactory.GetGrain<IEmailIndexGrain>(physicalKey).GetAsync();
        return reached.IsSuccess ? reached.GetValueOrThrow().IndexedValue : $"<{reached.Error!.Code}>";
    }

    /// <inheritdoc />
    public async Task<int> ReachTenantDirectoryAsync() {
        var count = await GrainFactory
            .GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .CountAsync();

        return count.IsSuccess ? count.GetValueOrThrow() : -1;
    }

    /// <inheritdoc />
    public async Task<long> ReachShardMapAsync() {
        var snapshot = await GrainFactory
            .GetGrain<IShardMapGrain>(GrainKeys.ShardMap())
            .GetSnapshotAsync(0);

        return snapshot.IsSuccess ? snapshot.GetValueOrThrow().Version : -1;
    }

    /// <inheritdoc />
    public Task<string?> MyTenantAsync() => Task.FromResult(AddressableExtensions.GetTenantId(this));
}
