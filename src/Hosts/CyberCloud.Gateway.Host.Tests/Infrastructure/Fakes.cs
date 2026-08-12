using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Tests.Infrastructure;

/// <summary>A clock a test moves by hand.</summary>
sealed class FakeClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>A registry with one type, one api-version and one action.</summary>
sealed class OneTypeRegistry : IProviderRegistry {
    /// <summary>The type every test uses.</summary>
    public static ResourceTypeName TheType { get; } = new("CyberCloud.DBforPostgreSQL", "servers");

    /// <summary>The api-version every test uses.</summary>
    public const string TheVersion = "2026-08-01";

    /// <summary>A version that is registered but older, to prove an old client keeps working.</summary>
    public const string OlderVersion = "2025-11-01";

    /// <inheritdoc />
    public ImmutableArray<ResourceTypeRegistration> Types { get; } = [
        new() {
            Type = new("CyberCloud.DBforPostgreSQL", "servers"),
            ApiVersions = [
                new(ApiVersion.Parse(OlderVersion), ResourceSchema.Empty),
                new(ApiVersion.Parse(TheVersion), ResourceSchema.Empty)
            ],
            Actions = [
                new("restart", ActionKind.Post, "write", false),
                // ⚠ A synchronous, secret-carrying action, because the 200-versus-202 branch and the
                // no-store header only exist for one. `restart` above stays long-running-shaped so
                // both branches of DispatchStage.ActionAsync are covered by real declarations.
                new("listKeys", ActionKind.Post, "listKeys", true)
            ]
        }
    ];

    /// <inheritdoc />
    public ImmutableArray<string> Namespaces { get; } = ["CyberCloud.DBforPostgreSQL"];

    /// <inheritdoc />
    public bool TryGetType(ResourceTypeName type, out ResourceTypeRegistration registration) {
        registration = Types[0];
        return type == TheType;
    }

    /// <inheritdoc />
    public Result<TypeResolution> Resolve(ResourceTypeName type, string? apiVersion) {
        if (type != TheType) {
            return Result<TypeResolution>.Failure(ErrorCode.InvalidResourceType, $"'{type}' is unknown.");
        }

        var version = ApiVersion.Parse(apiVersion is { Length: > 0 } supplied ? supplied : TheVersion);
        return Result<TypeResolution>.Success(new(Types[0], version, ResourceSchema.Empty));
    }
}

/// <summary>
///     A resource manager that records every path it was asked about and answers from a script.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Paths" /> is what the tenant test asserts against.</b> "No grain call was made
///     against tenant B" is checked two ways: this list must contain no path naming B, and the
///     substitute <c>IGrainFactory</c> the harness holds must have received no calls at all.
/// </remarks>
sealed class RecordingResourceManager : IResourceManager {
    readonly ConcurrentQueue<string> paths = new();
    readonly ConcurrentQueue<string> actions = new();
    readonly ConcurrentQueue<CallerContext> callers = new();

    /// <summary>Every resource path this manager was asked about, in order.</summary>
    public IReadOnlyCollection<string> Paths => paths;

    /// <summary>
    ///     The caller the gateway built for each of those requests, in order.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is where the impersonation assertion has to be made.</b>
    ///     <c>CallerContext.ImpersonatedBy</c> is what reaches the audit log, so "a header cannot
    ///     inject an operator" is only proven by looking at what came out of stage 3 — asserting on
    ///     the response would pass for a gateway that faithfully copied the header through.
    /// </remarks>
    public IReadOnlyCollection<CallerContext> Callers => callers;

    /// <summary>What <see cref="ReadAsync" /> answers. Default: a resource that exists.</summary>
    public Func<WriteRequest, Result<ResourceSnapshot>> OnRead { get; set; } =
        request => Result<ResourceSnapshot>.Success(new() { Path = request.Path, Name = "main" });

    /// <summary>What the three write paths answer. Default: a <c>202</c>.</summary>
    public Func<WriteRequest, Result<WriteAccepted>> OnWrite { get; set; } =
        request => Result<WriteAccepted>.Success(new() {
            OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RetryAfterSeconds = 10,
            Resource = new() { Path = request.Path, Name = "main" }
        });

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> WriteAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnWrite);

    /// <inheritdoc />
    public Task<Result<ResourceSnapshot>> ReadAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnRead);

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> DeleteAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnWrite);

    /// <summary>Every action name this manager was asked to run, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Paths" />, because the path does not carry the action.</b>
    ///     <c>GatewayRouter.ResolveAction</c> strips the last segment before building the address, so
    ///     "was this dispatched as an action" is not answerable from the path a test can see.
    /// </remarks>
    public IReadOnlyCollection<string> Actions => actions;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Answers from <see cref="OnRead" /> rather than <see cref="OnWrite" />, because a restore
    ///     returns a snapshot and not a <c>202</c> — docs/plan/08 § Soft delete makes it the one write
    ///     verb that is not long-running, since it moves two records and touches no data plane.
    /// </remarks>
    public Task<Result<ResourceSnapshot>> RestoreAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnRead);

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> PurgeAsync(WriteRequest request, CancellationToken cancellationToken = default) =>
        Record(request, OnWrite);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Enqueues before it records.</b> A sabotage that routed <c>GET</c> here was survived by an
    ///     assertion of <c>Status.ShouldNotBe(200)</c> — this fake answers <c>202</c>, and 202 ≠ 200, so
    ///     the assertion held for a reason unrelated to what it claimed. <see cref="Actions" /> is what
    ///     lets a test ask the only question that matters: whether dispatch was reached at all.
    /// </remarks>
    public Task<Result<WriteAccepted>> ActionAsync(WriteRequest request, CancellationToken cancellationToken = default) {
        actions.Enqueue(request.Action);

        return Record(request, OnWrite);
    }

    /// <summary>Every operation this manager was asked about, in order.</summary>
    public ConcurrentQueue<Guid> Operations { get; } = new();

    /// <summary>What <see cref="GetOperationAsync" /> answers. Default: a running operation.</summary>
    public Func<Guid, Result<OperationStatus>> OnGetOperation { get; set; } =
        operationId => Result<OperationStatus>.Success(new() {
            OperationId = operationId,
            State = OperationState.Running,
            ResourcePath = "/tenants/x/subscriptions/y/resourceGroups/prod/providers/N/t/main",
            ResourceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        });

    /// <inheritdoc />
    public Task<Result<OperationStatus>> GetOperationAsync(
        Guid operationId,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        Operations.Enqueue(operationId);
        return Task.FromResult(OnGetOperation(operationId));
    }

    Task<Result<T>> Record<T>(WriteRequest request, Func<WriteRequest, Result<T>> answer)
        where T : notnull {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        return Task.FromResult(answer(request));
    }
}

// ⚠ ScriptedInterestAuthorizer LIVED HERE AND NOW LIVES IN CyberCloud.ResourceManager.Tests.
// It stood in for the enforcement seam while the connection grain was declared in the gateway host
// and exercised by direct instantiation. The grain now has a silo to run in
// (CyberCloud.ResourceManager.Grains.ConnectionGrain) and is driven through a real TestCluster there,
// so the double moved to the suite that drives it. Nothing in this assembly asks the seam any more —
// which is the shape docs/plan/10 § What the gateway must never do describes.

/// <summary>
///     An operation reader a test scripts. Stands in for <c>TenantScopedOperationReader</c>, whose
///     own two boundaries — <c>ForTenant</c> and the resource-manager read — are asserted separately.
/// </summary>
/// <remarks>
///     ⚠ Hand-written rather than an NSubstitute proxy, because <c>IOperationReader</c> is
///     <c>internal</c> and Castle cannot proxy an internal interface without
///     <c>InternalsVisibleTo("DynamicProxyGenAssembly2")</c> on the gateway. Adding that attribute to
///     make a test framework happy would widen the production assembly's surface, which is the wrong
///     trade for eight lines.
/// </remarks>
sealed class ScriptedOperationReader : IOperationReader {
    /// <summary>What every read answers.</summary>
    public Func<Guid, Result<OperationStatus>> OnRead { get; set; } =
        id => Result<OperationStatus>.Failure(ErrorCode.ResourceNotFound, $"'/operations/{id:D}' does not exist.");

    /// <summary>Which operations were asked for.</summary>
    public List<Guid> Read { get; } = [];

    /// <inheritdoc />
    public Task<Result<OperationStatus>> ReadAsync(
        CallerContext caller,
        Guid operationId,
        CancellationToken cancellationToken = default
    ) {
        Read.Add(operationId);
        return Task.FromResult(OnRead(operationId));
    }
}
