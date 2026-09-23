using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

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

    /// <summary>
    ///     The body shape the type declares — a <c>location</c> and one leaf under <c>properties</c>,
    ///     which is the smallest schema that has both halves of a published body.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A real schema rather than <see cref="ResourceSchema.Empty" />, because the snapshot
    ///         this suite renders is projected through it.
    ///     </b> <see cref="ProjectedSnapshot" /> runs the
    ///     real <c>ResourceProjection.Project</c> over these pointers, and an empty list would make
    ///     that projection the whole-superset escape hatch — a passthrough, which is the shape the
    ///     hand-written snapshot had and the reason issue #72 went unseen here. The gateway itself
    ///     never reads the schema: <c>ValidateStage</c>'s remarks say the JSON Schema check is the
    ///     manager's, so no request body in this suite is validated against it.
    /// </remarks>
    public static ResourceSchema Schema { get; } = ResourceSchema.Of(
        [
            new("/location", SchemaKind.Text, true),
            new("/properties", SchemaKind.Nested),
            new("/properties/sku", SchemaKind.Text)
        ]
    );

    /// <summary>The pointers <see cref="Schema" /> declares, as the manager hands them to a grain.</summary>
    public static ImmutableArray<string> Pointers { get; } = [.. Schema.Properties.Select(static x => x.JsonPointer)];

    /// <inheritdoc />
    public ImmutableArray<ResourceTypeRegistration> Types { get; } = [
        new() {
            Type = new("CyberCloud.DBforPostgreSQL", "servers"),
            ApiVersions = [
                new(ApiVersion.Parse(OlderVersion), Schema),
                new(ApiVersion.Parse(TheVersion), Schema)
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
        return Result<TypeResolution>.Success(new(Types[0], version, Schema));
    }
}

/// <summary>
///     A <see cref="ResourceSnapshot" /> in the shape the real grain produces, built by the real
///     projection.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE FIX FOR THE DOUBLE THAT ANSWERED A DIFFERENT QUESTION.</b> Issue #72:
///         <c>ResponseBodies.WriteResource</c> nested the body it was handed under a second
///         <c>properties</c> member and served <c>location</c> twice, and this suite — which renders
///         that body constantly — saw nothing, because the snapshot its substitute manager handed
///         back was hand-written in the shape the writer expected rather than the shape the grain
///         produces. A substitute and the real thing with two shapes under one name is the failure
///         class this repository keeps finding, and the only repair that holds is to stop
///         hand-writing the shape.
///     </para>
///     <para>
///         So this does what <c>ResourceGrain.Snapshot</c> does, with the same function: parse the
///         superset, project it through the type's declared pointers with
///         <c>ResourceProjection.Project</c>, and carry the body's <c>location</c> into
///         <see cref="ResourceSnapshot.Location" /> the way the write path's step 9 does. The
///         projection is the grain's own code, shared rather than copied, so a change to what the
///         grain projects changes what this suite renders on the same commit.
///     </para>
///     <para>
///         ⚠ <see cref="Superset" /> deliberately carries a member no version declares.
///         <c>ResourceBodyShapeTests</c> asserts it never reaches the wire, which is what proves this
///         class projects rather than passes the text through.
///     </para>
/// </remarks>
static class ProjectedSnapshot {
    /// <summary>
    ///     The superset every default snapshot is projected from: the declared <c>location</c> and
    ///     <c>sku</c>, plus a leaf no api-version declares.
    /// </summary>
    public const string Superset =
        """{"location":"eu-central","properties":{"sku":"gp1","onlyInANewerVersion":true}}""";

    /// <summary>Builds a snapshot for a path, projected from <paramref name="superset" />.</summary>
    /// <param name="path">The resource's address; also where its name is read from.</param>
    /// <param name="superset">The stored document, as the grain would hold it.</param>
    /// <param name="provisioningState">The state to report.</param>
    public static ResourceSnapshot Of(
        string path,
        string superset = Superset,
        ProvisioningState provisioningState = ProvisioningState.Succeeded
    ) {
        var stored = (JsonObject)JsonNode.Parse(superset)!;
        var projected = ResourceProjection.Project(stored, OneTypeRegistry.Pointers);

        return new() {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Path = path,
            Type = OneTypeRegistry.TheType.ToString(),
            Name = path[(path.LastIndexOf('/') + 1)..],
            ApiVersion = OneTypeRegistry.TheVersion,
            ProvisioningState = provisioningState,
            Body = projected.ToJsonString(),
            Etag = "etag-1",
            Location = stored["location"]?.GetValue<string>() ?? ""
        };
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
        static request => Result<ResourceSnapshot>.Success(ProjectedSnapshot.Of(request.Path));

    /// <summary>What the three write paths answer. Default: a <c>202</c>.</summary>
    public Func<WriteRequest, Result<WriteAccepted>> OnWrite { get; set; } =
        request => Result<WriteAccepted>.Success(
            new() {
                OperationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                RetryAfterSeconds = 10,
                Resource = ProjectedSnapshot.Of(request.Path, provisioningState: ProvisioningState.Creating)
            }
        );

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> WriteAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnWrite);

    /// <inheritdoc />
    public Task<Result<ResourceSnapshot>> ReadAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnRead);

    /// <inheritdoc />
    public Task<Result<WriteAccepted>> DeleteAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
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
        request => Result<ResourceListPage>.Success(
            new() { Resources = [ProjectedSnapshot.Of(request.Path + "/main")] }
        );

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>This side of the seam proves routing and nothing else.</b> The filter that decides
    ///     what a listing may contain is <c>ResourceManagerService.ListAsync</c>'s, and a route added
    ///     only here would demonstrate a <c>200</c> with a body this class wrote — see the remarks on
    ///     <c>ReconcileThroughTheRealHostTests</c>, which is the suite that meets this one at
    ///     <see cref="IResourceManager" />.
    /// </remarks>
    public Task<Result<ResourceListPage>> ListAsync(
        ListRequest request,
        CancellationToken cancellationToken = default
    ) {
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
    public Task<Result<WriteAccepted>> PurgeAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnWrite);

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Refuses rather than recording, because NO GATEWAY ROUTE MAY REACH IT.</b>
    ///     <c>PurgeExpiredAsync</c> is the clock-driven half of purge and takes no
    ///     <c>CallerContext</c> — it authorizes nothing, by design — so a route that reached it would
    ///     be an unauthenticated destroy. This fake is the gateway's view of the resource manager, and
    ///     a fake that answered <c>202</c> here would let exactly that wiring mistake go green.
    /// </remarks>
    public Task<Result<WriteAccepted>> PurgeExpiredAsync(
        ExpiredPurgeRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<WriteAccepted>.Failure(
                ErrorCode.InternalError,
                "The gateway reached PurgeExpiredAsync. That member has no caller and runs no "
                + "authorization check — it is driven by an expired recovery window and by nothing "
                + "else — so a request that reaches it is an unauthenticated purge."
            )
        );

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Enqueues before it records.</b> A sabotage that routed <c>GET</c> here was survived by an
    ///     assertion of <c>Status.ShouldNotBe(200)</c> — this fake answers <c>202</c>, and 202 ≠ 200, so
    ///     the assertion held for a reason unrelated to what it claimed. <see cref="Actions" /> is what
    ///     lets a test ask the only question that matters: whether dispatch was reached at all.
    /// </remarks>
    public Task<Result<WriteAccepted>> ActionAsync(
        WriteRequest request,
        CancellationToken cancellationToken = default
    ) {
        actions.Enqueue(request.Action);

        return Record(request, OnWrite);
    }

    /// <summary>Every operation this manager was asked about, in order.</summary>
    public ConcurrentQueue<Guid> Operations { get; } = new();

    /// <summary>What <see cref="GetOperationAsync" /> answers. Default: a running operation.</summary>
    public Func<Guid, Result<OperationStatus>> OnGetOperation { get; set; } =
        operationId => Result<OperationStatus>.Success(
            new() {
                OperationId = operationId,
                State = OperationState.Running,
                ResourcePath = "/tenants/x/subscriptions/y/resourceGroups/prod/providers/N/t/main",
                ResourceId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            }
        );

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
        static id => Result<OperationStatus>.Failure(
            ErrorCode.ResourceNotFound,
            $"'/operations/{id:D}' does not exist."
        );

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
///     ⚠
///     <b>
///         The same substitution <see cref="RecordingResourceManager" /> is, and the same warning
///         applies to it.
///     </b> A scope route proven only against this fake proves that stage 6 admits the
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
    ///     ⚠
    ///     <b>
    ///         Throws, because nothing in the gateway may reach it and this is how that is
    ///         asserted.
    ///     </b> <c>IScopeManager.CreateTenantAsync</c> is the platform-operator bootstrap
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

    /// <summary>
    ///     Every parent path a collection listing was asked about, in order.
    /// </summary>
    /// <remarks>
    ///     ⚠ Separate from <see cref="Paths" /> for the reason <c>RecordingResourceManager.Collections</c>
    ///     is: the manager is addressed at the <i>parent</i> — a tenant or a subscription — whose
    ///     path is also a valid item address, so a test looking in <see cref="Paths" /> could not
    ///     tell a listing from a read of the parent.
    /// </remarks>
    public ConcurrentQueue<string> Collections { get; } = new();

    /// <summary>Every list request as it arrived, in order — for <c>$top</c> and <c>$skipToken</c> assertions.</summary>
    public ConcurrentQueue<ScopeListRequest> ListRequests { get; } = new();

    /// <summary>What <see cref="ListAsync" /> answers. Default: one subscription under the parent.</summary>
    public Func<ScopeListRequest, Result<ScopeListPage>> OnList { get; set; } =
        request => Result<ScopeListPage>.Success(
            new() {
                Items = [
                    new() {
                        Path = request.ParentPath + "/subscriptions/" + GatewayHarness.Subscription.ToString("D"),
                        Kind = ScopeKind.Subscription,
                        Name = "Default",
                        Type = ScopeTypeNames.Subscription
                    }
                ]
            }
        );

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>This side of the seam proves routing and nothing else</b>, as the resource fake's
    ///     <c>ListAsync</c> does: which members a page holds is <c>ScopeManagerService.ListAsync</c>'s
    ///     question, asserted in <c>CyberCloud.ResourceManager.Tests.ScopeManagerServiceTests</c>.
    /// </remarks>
    public Task<Result<ScopeListPage>> ListAsync(
        ScopeListRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        Collections.Enqueue(request.ParentPath);
        ListRequests.Enqueue(request);
        callers.Enqueue(request.Caller);

        return Task.FromResult(OnList(request));
    }

    /// <summary>What <see cref="DeleteAsync" /> answers. Default: the group went.</summary>
    public Func<ScopeRequest, Result> OnDelete { get; set; } = static _ => Result.Success;

    /// <inheritdoc />
    public Task<Result> DeleteAsync(ScopeRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        return Task.FromResult(OnDelete(request));
    }

    Task<Result<ScopeSnapshot>> Record(ScopeRequest request, Func<ScopeRequest, Result<ScopeSnapshot>> answer) {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        return Task.FromResult(answer(request));
    }
}

/// <summary>
///     A role assignment manager that records every path it was asked about and answers from a
///     script.
/// </summary>
/// <remarks>
///     ⚠ <b>The same substitution <see cref="RecordingScopeManager" /> is, with the same warning.</b>
///     A role assignment route proven against this fake proves that stage 6 admits the address —
///     ahead of the resource grammar it overlaps — and that stage 8 hands it to this manager with
///     the token's tenant. Whether <c>assignRole</c> is checked and whether a tuple is written is
///     <c>RoleAssignmentService</c>'s, driven through the real engine in
///     <c>test/CyberCloud.Isolation</c>.
/// </remarks>
sealed class RecordingRoleAssignmentManager : IRoleAssignmentManager {
    readonly ConcurrentQueue<string> paths = new();
    readonly ConcurrentQueue<CallerContext> callers = new();

    /// <summary>Every role assignment path this manager was asked about, in order.</summary>
    public IReadOnlyCollection<string> Paths => paths;

    /// <summary>The caller the gateway built for each of those requests, in order.</summary>
    public IReadOnlyCollection<CallerContext> Callers => callers;

    /// <summary>The body the gateway handed over for each request, in order.</summary>
    public ConcurrentQueue<string> Bodies { get; } = new();

    /// <summary>What <see cref="AssignAsync" /> answers. Default: a grant that was written.</summary>
    public Func<RoleAssignmentRequest, Result<RoleAssignmentSnapshot>> OnAssign { get; set; } =
        static request => Result<RoleAssignmentSnapshot>.Success(Snapshot(request, true));

    /// <summary>What <see cref="ReadAsync" /> answers. Default: a grant that exists.</summary>
    public Func<RoleAssignmentRequest, Result<RoleAssignmentSnapshot>> OnRead { get; set; } =
        static request => Result<RoleAssignmentSnapshot>.Success(Snapshot(request, false));

    /// <summary>What <see cref="RevokeAsync" /> answers. Default: the grant went.</summary>
    public Func<RoleAssignmentRequest, Result> OnRevoke { get; set; } = static _ => Result.Success;

    /// <summary>
    ///     What <see cref="ListAsync" /> answers. Default: one direct row and one inherited from the
    ///     tenant, with no next page.
    /// </summary>
    public Func<RoleAssignmentListRequest, Result<RoleAssignmentPage>> OnList { get; set; } =
        static request => Result<RoleAssignmentPage>.Success(Page(request));

    /// <summary>Every collection request this manager was asked to list, in order.</summary>
    public ConcurrentQueue<RoleAssignmentListRequest> Listings { get; } = new();

    /// <inheritdoc />
    public Task<Result<RoleAssignmentPage>> ListAsync(
        RoleAssignmentListRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        Listings.Enqueue(request);
        return Task.FromResult(OnList(request));
    }

    static RoleAssignmentPage Page(RoleAssignmentListRequest request) {
        var collection = RoleAssignmentCollectionId.ParsePath(request.Path).GetValueOrThrow();
        var direct = collection.Member(new("reader", "user", "7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d"));
        var ancestor = RoleAssignmentId.OnScope(
            ScopeId.Tenant(collection.TenantId),
            new("owner", "user", "0a1b2c3d4e5f4a6b8c9d0e1f2a3b4c5d")
        );

        return new() {
            Assignments = [
                new() {
                    Path = direct.Path,
                    Name = direct.Name.Render(),
                    Scope = direct.ScopePath,
                    RoleDefinitionId = direct.Name.Role,
                    PrincipalType = direct.Name.PrincipalType,
                    PrincipalId = direct.Name.PrincipalId
                },
                new() {
                    Path = ancestor.Path,
                    Name = ancestor.Name.Render(),
                    Scope = ancestor.ScopePath,
                    RoleDefinitionId = ancestor.Name.Role,
                    PrincipalType = ancestor.Name.PrincipalType,
                    PrincipalId = ancestor.Name.PrincipalId,
                    Inherited = true
                }
            ]
        };
    }

    /// <inheritdoc />
    public Task<Result<RoleAssignmentSnapshot>> AssignAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnAssign);

    /// <inheritdoc />
    public Task<Result<RoleAssignmentSnapshot>> ReadAsync(
        RoleAssignmentRequest request,
        CancellationToken cancellationToken = default
    ) =>
        Record(request, OnRead);

    /// <inheritdoc />
    public Task<Result> RevokeAsync(RoleAssignmentRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        Bodies.Enqueue(request.Body);
        return Task.FromResult(OnRevoke(request));
    }

    static RoleAssignmentSnapshot Snapshot(RoleAssignmentRequest request, bool created) {
        var parsed = RoleAssignmentId.ParsePath(request.Path).GetValueOrThrow();

        return new() {
            Path = parsed.Path,
            Name = parsed.Name.Render(),
            Scope = parsed.ScopePath,
            RoleDefinitionId = parsed.Name.Role,
            PrincipalType = parsed.Name.PrincipalType,
            PrincipalId = parsed.Name.PrincipalId,
            Created = created
        };
    }

    Task<Result<RoleAssignmentSnapshot>> Record(
        RoleAssignmentRequest request,
        Func<RoleAssignmentRequest, Result<RoleAssignmentSnapshot>> answer
    ) {
        ArgumentNullException.ThrowIfNull(request);
        paths.Enqueue(request.Path);
        callers.Enqueue(request.Caller);
        Bodies.Enqueue(request.Body);
        return Task.FromResult(answer(request));
    }
}

/// <summary>
///     A resource graph query that records every request it was asked and answers from a script.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         The same substitution <see cref="RecordingRoleAssignmentManager" /> is, with the same
///         warning.
///     </b> A query route proven against this fake proves that stage 6 admits the address
///     under its reserved namespace and that stage 8 hands the caller, the text and the page
///     parameters over and renders the page. Whether the KQL translates, whether the access column
///     is applied and whether ClickHouse answers is <c>ResourceGraphQueryService</c>'s, driven
///     against the real store in <c>CyberCloud.ResourceGraph.Tests</c> and, over this very
///     pipeline, in <c>ResourceGraphQueryEndToEndTests</c>.
/// </remarks>
sealed class RecordingResourceGraphQuery : IResourceGraphQuery {
    /// <summary>Every request, in order.</summary>
    public ConcurrentQueue<ResourceGraphQueryRequest> Requests { get; } = new();

    /// <summary>
    ///     What <see cref="QueryAsync" /> answers. Default: two rows of <c>name</c> and
    ///     <c>location</c>, and a next page.
    /// </summary>
    public Func<ResourceGraphQueryRequest, Result<ResourceGraphQueryPage>> OnQuery { get; set; } =
        request => Result<ResourceGraphQueryPage>.Success(
            new() {
                Columns = [new("name", "string"), new("location", "string")],
                Rows = [
                    """{"name":"pg-main","location":"eu-central"}""", """{"name":"pg-replica","location":"eu-west"}"""
                ],
                Continuation = "2.0123456789abcdef"
            }
        );

    /// <inheritdoc />
    public Task<Result<ResourceGraphQueryPage>> QueryAsync(
        ResourceGraphQueryRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(request);
        return Task.FromResult(OnQuery(request));
    }
}

/// <summary>
///     An <see cref="IPolicyManager" /> that records what stage 8 handed it — issue #46's routing
///     suite asserts the address, the caller and the body, and never a decision.
/// </summary>
sealed class RecordingPolicyManager : IPolicyManager {
    /// <summary>Every item request, in order.</summary>
    public ConcurrentQueue<(string Verb, PolicyRequest Request)> Requests { get; } = new();

    /// <summary>Every collection request, in order.</summary>
    public ConcurrentQueue<PolicyListRequest> Listings { get; } = new();

    /// <summary>What <see cref="PutAsync" /> answers. Default: an object that was created.</summary>
    public Func<PolicyRequest, Result<PolicyObjectSnapshot>> OnPut { get; set; } =
        static request => Result<PolicyObjectSnapshot>.Success(Snapshot(request.Path, true));

    /// <summary>What <see cref="ReadAsync" /> answers. Default: an object that exists.</summary>
    public Func<PolicyRequest, Result<PolicyObjectSnapshot>> OnRead { get; set; } =
        static request => Result<PolicyObjectSnapshot>.Success(Snapshot(request.Path, false));

    /// <summary>What <see cref="DeleteAsync" /> answers. Default: it went.</summary>
    public Func<PolicyRequest, Result> OnDelete { get; set; } = static _ => Result.Success;

    /// <summary>What <see cref="ListAsync" /> answers. Default: an empty last page.</summary>
    public Func<PolicyListRequest, Result<PolicyListPage>> OnList { get; set; } =
        static _ => Result<PolicyListPage>.Success(new());

    /// <inheritdoc />
    public Task<Result<PolicyObjectSnapshot>> PutAsync(PolicyRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(("PUT", request));
        return Task.FromResult(OnPut(request));
    }

    /// <inheritdoc />
    public Task<Result<PolicyObjectSnapshot>> ReadAsync(PolicyRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(("GET", request));
        return Task.FromResult(OnRead(request));
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(PolicyRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Enqueue(("DELETE", request));
        return Task.FromResult(OnDelete(request));
    }

    /// <inheritdoc />
    public Task<Result<PolicyListPage>> ListAsync(PolicyListRequest request, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(request);
        Listings.Enqueue(request);
        return Task.FromResult(OnList(request));
    }

    static PolicyObjectSnapshot Snapshot(string path, bool created) {
        var address = PolicyAddress.ParsePath(path).GetValueOrThrow();

        return new() {
            Path = address.Path,
            Name = address.Name,
            Type = address.Kind == PolicyObjectKind.Definition ? PolicyAddress.DefinitionTypeName : PolicyAddress.AssignmentTypeName,
            Scope = address.Scope.Path,
            Properties = """{"displayName":"recorded"}""",
            Created = created
        };
    }
}
