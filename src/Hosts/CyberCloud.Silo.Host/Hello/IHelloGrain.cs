namespace CyberCloud.Silo.Host.Hello;

/// <summary>
///     The hello-world tenant-scoped grain of docs/plan/24 § Phase 0's exit criterion.
/// </summary>
/// <remarks>
///     <para>
///         "A hello-world tenant-scoped grain round-trips through both storage tiers." This is that
///         grain, and it is the only grain in this host that exists solely to be a test subject.
///         It lives here rather than in a <c>.Contracts</c> assembly because nothing outside the
///         silo and its exit-criterion test may ever call it — a wire contract for it would be a
///         wire contract we then have to keep.
///     </para>
///     <para>
///         ⚠ <b>Tenant-scoped, so it is reached with <c>IGrainFactory.ForTenant(id)</c>.</b> Reaching
///         it with a bare <c>GetGrain</c> activates it with no tenant qualification, and
///         <see cref="HelloGrain.OnActivateAsync" /> refuses — ADR-002. That refusal is half of what
///         the criterion is worth checking: "tenant-scoped" has to mean the storage key carries the
///         tenant, not that a comment says so.
///     </para>
/// </remarks>
public interface IHelloGrain : IGrainWithStringKey
{
    /// <summary>
    ///     Writes the greeting to <b>both</b> tiers and returns what each one holds afterwards.
    /// </summary>
    /// <param name="greeting">The greeting to store.</param>
    Task<HelloRoundTrip> SayHelloAsync(string greeting);

    /// <summary>
    ///     Re-reads both tiers from their stores and returns what came back.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the half that makes it a round trip.</b> Returning the in-memory
    ///     <c>IPersistentState.State</c> would pass against a storage provider that silently drops
    ///     every write; <c>ReadStateAsync()</c> goes to Redis and to PostgreSQL and replaces the
    ///     in-memory copy with whatever is actually there.
    /// </remarks>
    Task<HelloRoundTrip> ReadBackAsync();

    /// <summary>The address of the silo this activation is on.</summary>
    /// <remarks>
    ///     docs/plan/24 § Phase 0 asks for a <b>two-silo</b> cluster, and the only way a test can
    ///     tell one silo from two is to ask an activation where it is. Two grains whose keys place
    ///     them on different silos, both reachable from one client, is the evidence.
    /// </remarks>
    Task<string> SiloAddressAsync();

    /// <summary>
    ///     Calls a sibling <see cref="IHelloGrain" /> in the same tenant and reports where it is.
    /// </summary>
    /// <param name="otherKey">The sibling's within-tenant key.</param>
    /// <returns>The sibling's silo address.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the only method here that a client cannot fake.</b> A client talking to two
    ///     gateways proves two processes are listening; it does not prove they are one cluster. A
    ///     <i>grain</i> reaching a grain the placement director put on the other silo is a message
    ///     through the grain directory and across the silo-to-silo connection, which is exactly the
    ///     thing a single silo cannot do and two disjoint clusters cannot do either.
    /// </remarks>
    Task<string> SiloAddressOfAsync(string otherKey);

    /// <summary>Asks the runtime to deactivate this activation, so the next call re-reads storage.</summary>
    Task DeactivateAsync();
}

/// <summary>What both tiers hold, after a write or after a re-read.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Silo.Host.Hello.HelloRoundTrip")]
public sealed record HelloRoundTrip
{
    /// <summary>The greeting the hot tier holds.</summary>
    [Id(0)] public required string HotGreeting { get; init; }

    /// <summary>The greeting the durable tier holds.</summary>
    [Id(1)] public required string DurableGreeting { get; init; }

    /// <summary>How many writes the hot tier has seen.</summary>
    [Id(2)] public required int HotWrites { get; init; }

    /// <summary>How many writes the durable tier has seen.</summary>
    [Id(3)] public required int DurableWrites { get; init; }

    /// <summary>The tenant the activation is qualified with, as the storage layer sees it.</summary>
    [Id(4)] public required string TenantId { get; init; }

    /// <summary>The silo that answered.</summary>
    [Id(5)] public required string SiloAddress { get; init; }
}
