using CyberCloud.Core.Contracts;
using Orleans.Multitenant;

namespace CyberCloud.Silo.Host.Hello;

/// <summary>
///     <see cref="IHelloGrain" /> — tenant-scoped, one state in each tier.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two <c>[PersistentState]</c>s with different provider names is the whole point.</b>
///         docs/plan/05 § The two tiers is a claim about two independent stores, and a grain that
///         binds only one of them cannot tell whether the other is wired at all. Here the hot state
///         goes to Redis (<see cref="StorageTiers.Hot" />) and the durable state to the tenant's
///         PostgreSQL shard (<see cref="StorageTiers.Durable" />), in the same call, from the same
///         activation.
///     </para>
///     <para>
///         It deliberately does <b>not</b> use <c>GrainKeys</c>: none of ADR-002's eight key shapes
///         is "a scratch grain for a bring-up test", and minting a ninth for it would put a test
///         fixture into the identifier vocabulary of the platform. Its key is a free-form string
///         within the tenant, which is exactly what <c>Orleans.Multitenant</c> qualifies.
///     </para>
/// </remarks>
public sealed class HelloGrain(
    [PersistentState("hello-hot", StorageTiers.Hot)] IPersistentState<HelloState> hot,
    [PersistentState("hello-durable", StorageTiers.Durable)] IPersistentState<HelloState> durable,
    ILocalSiloDetails silo)
    : Grain, IHelloGrain
{
    string tenantId = string.Empty;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // ⚠ The refusal, not a default. A null tenant here means the caller used a bare
        // IGrainFactory.GetGrain, so the storage layer would place this grain on the null-tenant
        // shard and under the null-tenant Redis hash tag — i.e. in the platform's own state, under a
        // key that looks tenant-scoped. ADR-002.
        tenantId = this.GetTenantId()
            ?? throw new InvalidOperationException(
                "IHelloGrain is tenant-scoped and was activated with no tenant qualification. Reach "
                + "it with IGrainFactory.ForTenant(tenantId).GetGrain<IHelloGrain>(key).");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<HelloRoundTrip> SayHelloAsync(string greeting)
    {
        ArgumentNullException.ThrowIfNull(greeting);

        hot.State = new HelloState { Greeting = greeting, Writes = hot.State.Writes + 1 };
        durable.State = new HelloState { Greeting = greeting, Writes = durable.State.Writes + 1 };

        // Sequential rather than Task.WhenAll: a grain is single-threaded and two concurrent
        // WriteStateAsync calls on the same activation are two reentrant calls into the runtime.
        await hot.WriteStateAsync();
        await durable.WriteStateAsync();

        return Snapshot();
    }

    /// <inheritdoc />
    public async Task<HelloRoundTrip> ReadBackAsync()
    {
        await hot.ReadStateAsync();
        await durable.ReadStateAsync();

        return Snapshot();
    }

    /// <inheritdoc />
    public Task<string> SiloAddressAsync() => Task.FromResult(silo.SiloAddress.ToString());

    /// <inheritdoc />
    public Task<string> SiloAddressOfAsync(string otherKey)
    {
        // ForTenant with THIS grain's own tenant. Reaching a sibling with a bare GetGrain would
        // address the null-tenant copy of it, and AddCyberCloudTenantSeparation would not object,
        // because the target's tenant would be "no tenant" rather than another tenant's.
        var other = GrainFactory.ForTenant(tenantId).GetGrain<IHelloGrain>(otherKey);

        return other.SiloAddressAsync();
    }

    /// <inheritdoc />
    public Task DeactivateAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    HelloRoundTrip Snapshot() => new()
    {
        HotGreeting = hot.State.Greeting,
        DurableGreeting = durable.State.Greeting,
        HotWrites = hot.State.Writes,
        DurableWrites = durable.State.Writes,
        TenantId = tenantId,
        SiloAddress = silo.SiloAddress.ToString(),
    };
}

/// <summary>The stored half of <see cref="HelloGrain" />, one instance per tier.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Silo.Host.Hello.HelloState")]
public sealed record HelloState
{
    /// <summary>The last greeting written.</summary>
    [Id(0)] public string Greeting { get; init; } = string.Empty;

    /// <summary>How many times this tier has been written.</summary>
    [Id(1)] public int Writes { get; init; }
}
