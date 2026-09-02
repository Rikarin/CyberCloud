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
                new("listKeys", ActionKind.Post, "listKeys", true),
                // ⚠ THE TWO THE PLATFORM SYNTHESISES, COPIED HERE RATHER THAN BUILT HERE, AND THE
                // COPY IS WHAT THIS FIXTURE CAN HONESTLY OFFER. ProviderBuilder.SoftDeleteActionsOf
                // appends exactly these two to every type declaring SupportsSoftDelete; this
                // assembly holds no ProviderBuilder — GatewayIsolationTests is why — so the fixture
                // states the same shape by hand. What that means for a reader: a gateway case using
                // these proves the ROUTE (stage 6 admits a declared action, dispatch forwards it),
                // and proves nothing about whether the registry declares them. That half is
                // RegistryDeclarationTests, and the two meet at ResourceTypeRegistration.Actions.
                new(SoftDeletePolicy.RestoreAction, ActionKind.Post, "write", false) { LongRunning = true },
                new(SoftDeletePolicy.PurgeAction, ActionKind.Post, SoftDeletePolicy.DefaultPurgePermission, false) {
                    LongRunning = true
                }
            ],
            SoftDeleteDays = 7,
            PurgePermission = SoftDeletePolicy.DefaultPurgePermission
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

    /// <summary>Every collection path this manager was asked to list, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Paths" />, and it has to be.</b> A collection path is not a
    ///     <c>ResourceId</c> — <c>ResourceCollectionId</c>'s remarks give the grammar — so a test that
    ///     looked for it in <see cref="Paths" /> would be asking whether a resource path that cannot
    ///     exist was dispatched, and would pass whatever the gateway did.
    /// </remarks>
    public ConcurrentQueue<string> Collections { get; } = new();

    /// <summary>What <see cref="ListAsync" /> answers. Default: one resource.</summary>
    public Func<ListRequest, Result<ResourceListPage>> OnList { get; set; } =
        request => Result<ResourceListPage>.Success(new() {
            Resources = [new() { Path = request.Path + "/main", Name = "main" }]
        });

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>This side of the seam proves routing and nothing else.</b> The filter that decides
    ///     what a listing may contain is <c>ResourceManagerService.ListAsync</c>'s, and a route added
    ///     only here would demonstrate a <c>200</c> with a body this class wrote — see the remarks on
    ///     <c>ReconcileThroughTheRealHostTests</c>, which is the suite that meets this one at
    ///     <see cref="IResourceManager" />.
    /// </remarks>
    public Task<Result<ResourceListPage>> ListAsync(ListRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        Collections.Enqueue(request.Path);
        callers.Enqueue(request.Caller);

        return Task.FromResult(OnList(request));
    }

    /// <summary>Every action name this manager was asked to run, in order.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Paths" />, because the path does not carry the action.</b>
    ///     <c>GatewayRouter.ResolveAction</c> strips the last segment before building the address, so
    ///     "was this dispatched as an action" is not answerable from the path a test can see.
    /// </remarks>
    public IReadOnlyCollection<string> Actions => actions;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Answers from <see cref="OnWrite" />, because a restore is a long-running operation. It
    ///     used to answer from <see cref="OnRead" />, when a soft delete tore nothing down and a
    ///     restore therefore had nothing to apply; it now re-applies the resource's stored desired
    ///     state and answers <c>202</c> like every other write.
    /// </remarks>
    public Task<Result<WriteAccepted>> RestoreAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnWrite);

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

/// <summary>
///     A scope manager that records every scope path it was asked about and answers from a script.
/// </summary>
/// <remarks>
///     ⚠ <b>The same substitution <see cref="RecordingResourceManager" /> is, and the same warning
///     applies to it.</b> A scope route proven only against this fake proves that stage 6 admits the
///     path and that stage 8 hands it to the right manager. It proves nothing about whether the real
///     <c>ScopeManagerService</c> checks a permission, writes a parent edge, or creates anything —
///     the two meet at <c>IScopeManager</c> and neither this suite nor the manager's own covers the
///     join. <c>test/CyberCloud.Isolation</c> and <c>CyberCloud.AppHost.Tests</c> are where the real
///     one is driven.
/// </remarks>
sealed class RecordingScopeManager : IScopeManager {
    readonly ConcurrentQueue<string> paths = new();
    readonly ConcurrentQueue<CallerContext> callers = new();

    /// <summary>Every scope path this manager was asked about, in order.</summary>
    public IReadOnlyCollection<string> Paths => paths;

    /// <summary>The caller the gateway built for each of those requests, in order.</summary>
    public IReadOnlyCollection<CallerContext> Callers => callers;

    /// <summary>What <see cref="CreateAsync" /> answers. Default: a group that was created.</summary>
    public Func<ScopeRequest, Result<ScopeSnapshot>> OnCreate { get; set; } =
        request => Result<ScopeSnapshot>.Success(
            new() {
                Path = request.Path,
                Kind = ScopeKind.ResourceGroup,
                Name = "prod",
                Type = ScopeTypeNames.ResourceGroup,
                Location = "eu-central",
                Created = true
            }
        );

    /// <summary>What <see cref="ReadAsync" /> answers. Default: a group that exists.</summary>
    public Func<ScopeRequest, Result<ScopeSnapshot>> OnRead { get; set; } =
        request => Result<ScopeSnapshot>.Success(
            new() {
                Path = request.Path,
                Kind = ScopeKind.ResourceGroup,
                Name = "prod",
                Type = ScopeTypeNames.ResourceGroup,
                Location = "eu-central"
            }
        );

    /// <inheritdoc />
    public Task<Result<ScopeSnapshot>> CreateAsync(
        ScopeRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnCreate);

    /// <inheritdoc />
    public Task<Result<ScopeSnapshot>> ReadAsync(
        ScopeRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnRead);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Throws, because nothing in the gateway may reach it and this is how that is
    ///     asserted.</b> <c>IScopeManager.CreateTenantAsync</c> is the platform-operator bootstrap
    ///     path and there is no route to it — a fake that answered politely would let a future
    ///     dispatch line reach it and every test would still pass.
    /// </remarks>
    public Task<Result<ScopeSnapshot>> CreateTenantAsync(
        TenantCreateRequest request,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) =>
        throw new InvalidOperationException(
            "The gateway reached IScopeManager.CreateTenantAsync. Nothing in the request pipeline "
            + "may: stage 3 resolves the tenant from the token and refuses any path naming a "
            + "different one, so a tenant-create route cannot exist without breaching that boundary."
        );

    Task<Result<ScopeSnapshot>> Record(ScopeRequest request, Func<ScopeRequest, Result<ScopeSnapshot>> answer) {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        return Task.FromResult(answer(request));
    }
}
