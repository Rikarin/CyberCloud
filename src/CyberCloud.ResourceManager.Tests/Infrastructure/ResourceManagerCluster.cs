using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.ResourceManager.Actions;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Concurrent;
using System.Globalization;

namespace CyberCloud.ResourceManager.Tests.Infrastructure;

/// <summary>
///     An <see cref="IResourceAuthorizer" /> a test can drive — the enforcement seam, switchable.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The real <c>ReBacResourceAuthorizer</c> is <i>not</i> what most of these tests use, and
///         the reason is what they are testing.</b> The property under test in the write path is
///         <b>when</b> the check happens relative to quota and the index claim, and <b>which answer</b>
///         a refusal produces. Standing up a whole ReBAC schema and tuple set to get a "no" would test
///         docs/plan/07's engine, which has its own suite, and would make a step-ordering failure look
///         like an authorization-schema failure.
///     </para>
///     <para>
///         The one thing this must reproduce exactly is the 404-versus-403 rule, and it does: it takes
///         the same two permissions, and it answers <see cref="ErrorCode.ResourceNotFound" /> unless
///         the caller can read. <c>EnforcementSeamTests</c> asserts both branches against
///         <i>this</i> and the shape of the rule against <c>ReBacResourceAuthorizer</c>'s own message.
///     </para>
/// </remarks>
public sealed class SwitchableAuthorizer : IResourceAuthorizer {
    /// <summary>Permissions the caller holds. Empty means they hold everything.</summary>
    public static ConcurrentDictionary<string, bool> Granted { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether <see cref="Granted" /> is consulted at all.</summary>
    public static bool Restricted { get; set; }

    /// <summary>Every <c>(actionPermission, readPermission)</c> pair the write path asked about.</summary>
    public static ConcurrentQueue<string> Asked { get; } = new();

    /// <summary>
    ///     Resources this caller cannot read at all — a <c>404</c> whatever permission is asked for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Keyed on the resource's GUID and not on a permission, because a listing's filter is
    ///     the one caller that varies the answer <i>per resource</i>.</b> <see cref="Granted" /> is a
    ///     permission set, so with it alone every member of a group gets the same verdict and a
    ///     filter that returned everything or nothing would pass — which is exactly the shape of a
    ///     check that answers a narrower question than it appears to.
    /// </remarks>
    public static ConcurrentDictionary<Guid, bool> Hidden { get; } = new();

    /// <summary>Lets everything through again.</summary>
    public static void Reset() {
        Granted.Clear();
        Asked.Clear();
        Hidden.Clear();
        Restricted = false;
    }

    /// <summary>Grants exactly these permissions and refuses everything else.</summary>
    /// <param name="permissions">What the caller holds.</param>
    public static void GrantOnly(params string[] permissions) {
        Granted.Clear();
        foreach (var permission in permissions) {
            Granted[permission] = true;
        }

        Restricted = true;
    }

    /// <inheritdoc />
    public Task<Result> AuthorizeAsync(
        ResourceId id,
        string actionPermission,
        string readPermission,
        CallerContext caller,
        bool fullyConsistent = false,
        CancellationToken cancellationToken = default
    ) {
        Asked.Enqueue(actionPermission);

        // ⚠ Before the permission set, and it answers the canonical 404 without consulting it. A
        // resource the caller cannot see is not a resource they hold no permission on — it is one
        // whose existence they are not told about, which is the whole of docs/plan/07 § The
        // enforcement seam and the property a listing has to reproduce for every member.
        if (id.Id != Guid.Empty && Hidden.ContainsKey(id.Id)) {
            return Task.FromResult(Result.Failure(ErrorCode.ResourceNotFound, $"'{id.Path}' does not exist."));
        }

        if (!Restricted || Granted.ContainsKey(actionPermission)) {
            return Task.FromResult(Result.Success);
        }

        // ⚠ THE RULE, REPRODUCED EXACTLY. 404 unless the caller can read; 403 only when they can read
        // but not act — docs/plan/07 § The enforcement seam.
        if (Granted.ContainsKey(readPermission)) {
            return Task.FromResult(
                Result.Failure(
                    ErrorCode.AuthorizationFailed,
                    $"'{caller}' can read '{id.Path}' but does not have '{actionPermission}' on it."
                )
            );
        }

        return Task.FromResult(Result.Failure(ErrorCode.ResourceNotFound, $"'{id.Path}' does not exist."));
    }
}

/// <summary>An <see cref="IPolicyEvaluator" /> a test can make deny or modify.</summary>
public sealed class SwitchablePolicyEvaluator : IPolicyEvaluator {
    /// <summary>What to decide. Defaults to the honest "no engine ran".</summary>
    public static PolicyDecision Next { get; set; } = PolicyDecision.NotSupported;

    /// <summary>How many times the write path asked.</summary>
    public static int Asked { get; private set; }

    /// <summary>Back to the default.</summary>
    public static void Reset() {
        Next = PolicyDecision.NotSupported;
        Asked = 0;
    }

    /// <inheritdoc />
    public Task<PolicyDecision> EvaluateAsync(
        ResourceId id,
        string apiVersion,
        string body,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        Asked++;
        return Task.FromResult(Next);
    }
}

/// <summary>An <see cref="ILockResolver" /> a test can set a lock on.</summary>
/// <remarks>
///     ⚠ Stands in for the inherited scopes the shipped <c>ResourceScopeLockResolver</c> cannot read —
///     see that type's remarks on what is stubbed. A lock set here is what a resource-group or
///     subscription lock <i>would</i> resolve to once those grains carry one.
/// </remarks>
public sealed class SwitchableLockResolver : ILockResolver {
    /// <summary>The lock every scope reports.</summary>
    public static LockLevel Level { get; set; } = LockLevel.None;

    /// <summary>Clears it.</summary>
    public static void Reset() => Level = LockLevel.None;

    /// <inheritdoc />
    public Task<Result<LockLevel>> ResolveAsync(ResourceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LockLevel>.Success(Level));
}

/// <summary>
///     An <see cref="IResourceRelationWriter" /> that keeps step 8's edge set in a dictionary.
/// </summary>
/// <remarks>
///     ⚠ <b>A double, for the same reason <see cref="SwitchableAuthorizer" /> is one.</b> What these
///     tests assert about step 8 is <i>when</i> it runs relative to the index claim and the durable
///     write, and <i>whether</i> a delete removes what a create wrote — both of which are properties
///     of the write path. Whether the tuple the real writer produces is one <c>CyberCloudSchema</c>
///     can walk is a property of the schema, and the isolation suite drives the real
///     <c>ReBacResourceRelationWriter</c> against the real engine to answer it.
/// </remarks>
public sealed class RecordingRelationWriter : IResourceRelationWriter {
    /// <summary>Every resource that currently has a parent edge, against the parent it points at.</summary>
    public static ConcurrentDictionary<Guid, string> Edges { get; } = new();

    /// <summary>Every call made, as <c>link</c>/<c>unlink</c>, in order.</summary>
    public static ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Whether linking fails, so the write path's rollback can be driven.</summary>
    public static bool FailLink { get; set; }

    /// <summary>
    ///     The direct role assignments each resource carries, as <c>{role}@{subject}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Seeded by a test and dropped by the double, because docs/plan/08 § Soft delete asks for
    ///     a test that a grant written directly on a resource is <i>absent</i> after a restore.</b> A
    ///     double with no assignments at all could not tell "the drop ran" from "there was nothing to
    ///     drop", which is the shape of a test that passes for the wrong reason.
    /// </remarks>
    public static ConcurrentDictionary<Guid, List<string>> Assignments { get; } = new();

    /// <summary>Whether re-parenting and assignment drops fail, so the retry can be driven.</summary>
    public static bool FailReparent { get; set; }

    /// <summary>Forgets everything.</summary>
    public static void Reset() {
        Edges.Clear();
        Calls.Clear();
        Assignments.Clear();
        FailLink = false;
        FailReparent = false;
    }

    /// <inheritdoc />
    public Task<Result> LinkToParentAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        Calls.Enqueue("link");

        if (FailLink) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        Edges[id.Id] = SubjectOf(id, parentId);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> UnlinkFromParentAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        Calls.Enqueue("unlink");

        // ⚠ REMOVES THE EDGE ONLY WHEN IT IS THE ONE THIS CALL NAMES, WHICH THIS DOUBLE DID NOT USED
        // TO DO — AND THE OMISSION HID A REAL DEFECT.
        //
        // The real writer builds a tuple and deletes THAT tuple: called against a resource whose edge
        // names something else, it removes nothing and reports success. A double that removed
        // unconditionally agrees with a correct writer and with a writer that unlinks the wrong
        // subject, so the purge path — which must unlink `subscription:` and not the resource group —
        // could aim at either and no test could tell. Measured, not predicted: sabotaging the purge to
        // call this method instead of UnlinkFromSubscriptionAsync left the whole suite green.
        if (Edges.TryGetValue(id.Id, out var subject)
            && string.Equals(subject, SubjectOf(id, parentId), StringComparison.Ordinal)) {
            Edges.TryRemove(id.Id, out _);
        }

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> ReparentToSubscriptionAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        Calls.Enqueue("reparent-to-subscription");

        if (FailReparent) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        // ⚠ ONE ASSIGNMENT, NOT AN ADD-THEN-REMOVE PAIR, WHICH IS WHAT MAKES "never parentless"
        // ASSERTABLE. The real writer writes the new edge and then deletes the old one, so the
        // resource holds one parent before and one after; a double that removed first would let a
        // test pass over an implementation that leaves the resource unreachable in between.
        Edges[id.Id] = SubscriptionSubject(id);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> ReparentFromSubscriptionAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        Calls.Enqueue("reparent-from-subscription");

        if (FailReparent) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        Edges[id.Id] = SubjectOf(id, parentId);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> UnlinkFromSubscriptionAsync(ResourceId id, CancellationToken cancellationToken = default) {
        Calls.Enqueue("unlink-subscription");

        // ⚠ Removes the edge ONLY when it actually names the subscription. The real writer deletes a
        // specific tuple, so calling this on a resource whose edge names its group is a no-op there —
        // and a double that removed unconditionally would hide a purge that unlinked the wrong one.
        if (Edges.TryGetValue(id.Id, out var subject)
            && string.Equals(subject, SubscriptionSubject(id), StringComparison.Ordinal)) {
            Edges.TryRemove(id.Id, out _);
        }

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<int>> DropDirectRoleAssignmentsAsync(
        ResourceId id,
        CancellationToken cancellationToken = default
    ) {
        Calls.Enqueue("drop-assignments");

        if (FailReparent) {
            return Task.FromResult(Result<int>.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        return Task.FromResult(
            Result<int>.Success(Assignments.TryRemove(id.Id, out var held) ? held.Count : 0)
        );
    }

    /// <summary>
    ///     The subject the real writer would aim the edge at, <b>with its object type</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A double that always recorded the group would agree with a writer that always wrote
    ///         the group, which is the bug this pair exists to surface</b> — so the branch is mirrored
    ///         here even though this class writes no tuples.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The type prefix is what makes soft delete's re-parent assertable at all.</b> A
    ///         soft-deleted resource's edge names <c>subscription:{sub:N}</c> and a child's names
    ///         <c>resource:{parent:N}</c>; recorded as bare GUIDs the two are indistinguishable strings,
    ///         so a re-parent that aimed at the wrong object type would keep every existing assertion
    ///         green while meaning something else. docs/plan/08 § Soft delete.
    ///     </para>
    /// </remarks>
    static string SubjectOf(ResourceId id, Guid parentId) =>
        id.Parent is null
            ? "resourceGroup:"
            + id.SubscriptionId.ToString("N", CultureInfo.InvariantCulture)
            + "-"
            + id.ResourceGroup
            : "resource:" + parentId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>The subject a soft-deleted resource's edge names.</summary>
    static string SubscriptionSubject(ResourceId id) =>
        "subscription:" + id.SubscriptionId.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Records every <c>resource-changed</c> event step 11 emitted.</summary>
public sealed class RecordingChangeSink : IResourceChangedSink {
    /// <summary>Everything published.</summary>
    public static ConcurrentQueue<ResourceChangedEvent> Published { get; } = new();

    /// <summary>Whether publishing fails, so the "a failed publish does not fail the write" rule can be checked.</summary>
    public static bool Fail { get; set; }

    /// <summary>Forgets everything.</summary>
    public static void Reset() {
        Published.Clear();
        Fail = false;
    }

    /// <inheritdoc />
    public Task<Result> PublishAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default) {
        if (Fail) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the projector is down"));
        }

        Published.Enqueue(change);
        return Task.FromResult(Result.Success);
    }
}

/// <summary>
///     An <see cref="IInterestAuthorizer" /> a test flips at will. Stands in for the enforcement seam
///     the connection grain asks.
/// </summary>
/// <remarks>
///     ⚠ <b>Static, like every other double here, because the silo resolves its own services.</b> The
///     grain runs inside the <c>TestCluster</c> and the test drives it from outside; the two share a
///     process and nothing else, which is the same constraint <see cref="TestClock" /> documents.
///     <para>
///         ⚠ It stands in for <c>ResourceManagerInterestAuthorizer</c>, whose own behaviour —
///         forwarding to <see cref="IResourceManager.ReadAsync" /> and copying the answer — is a
///         property of the read path and is asserted by the tests of the read path. Standing up a
///         ReBAC schema to get a "no" here would make a re-check failure look like an
///         authorization-schema failure.
///     </para>
/// </remarks>
public sealed class ScriptedInterestAuthorizer : IInterestAuthorizer {
    /// <summary>Which paths are readable. Everything else answers the canonical <c>404</c>.</summary>
    public static ConcurrentDictionary<string, bool> Readable { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times the seam was asked.</summary>
    public static int Asked { get; private set; }

    /// <summary>Forgets every grant and the count.</summary>
    public static void Reset() {
        Readable.Clear();
        Asked = 0;
    }

    /// <summary>Makes a path readable.</summary>
    /// <param name="resourcePath">The path.</param>
    public static void Grant(string resourcePath) => Readable[resourcePath] = true;

    /// <summary>Takes a path away — the relation change a re-check has to notice.</summary>
    /// <param name="resourcePath">The path.</param>
    public static void Revoke(string resourcePath) => Readable.TryRemove(resourcePath, out _);

    /// <inheritdoc />
    public Task<Result> CanReadAsync(
        CallerContext caller,
        string resourcePath,
        CancellationToken cancellationToken = default
    ) {
        Asked++;

        return Task.FromResult(
            Readable.ContainsKey(resourcePath)
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{resourcePath}' does not exist.")
        );
    }
}

/// <summary>The clock the silo reads, shared with the test so it can be advanced.</summary>
/// <remarks>
///     ⚠ Static for the same reason <c>CyberCloud.Kubernetes.Tests</c>'s is: the silo runs in this
///     process but resolves its own services, and the 60-minute timeout is caused by <i>time
///     passing</i> — there is nothing to call to make it happen, and the alternative is a test nobody
///     runs. Advancing is monotonic and forward only.
/// </remarks>
public sealed class TestClock : IClock {
    /// <summary>The one instance the silo resolves.</summary>
    public static TestClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Puts time back to the start of a test.</summary>
    public void Reset() => UtcNow = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>
///     An in-process Orleans cluster with the resource manager wired as production wires it.
/// </summary>
/// <remarks>
///     ⚠ <b>In-memory storage and in-memory reminders, which is a deviation from ADR-018.</b> See this
///     project's <c>.csproj</c> for exactly what that costs and what is owed. Everything else —
///     grains, the call filter, the write path, the registry — is the production wiring.
/// </remarks>
public sealed class ResourceManagerCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant every test writes into.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for the cross-tenant 404.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The subscription every test writes into.</summary>
    public static Guid Subscription { get; } = Guid.Parse("33333333-3333-4333-8333-333333333333");

    /// <summary>
    ///     A second subscription in <see cref="Tenant" />, with a quota budget of its own.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="Subscription" /> is shared by every class in the collection and its Vcpu
    ///     quota is a finite, cumulative budget that nothing gives back.</b> A converged create
    ///     commits its lease for the rest of the run, so the suite's committed total only ever climbs
    ///     — it sat at 99 of 100 before this existed. That makes the budget a hidden coupling between
    ///     unrelated classes: a class that adds a couple of creates pushes a class that runs after it
    ///     into <c>QuotaExceeded</c>, and the failure lands in whichever test happened to be last
    ///     rather than in the one that spent the quota. <c>ParentExistenceTests</c> did exactly that
    ///     to <c>InheritedLockTests</c>.
    ///     <para>
    ///         A class whose fixtures create resources it does not need counted should address this
    ///         one instead. It is a real subscription in the same tenant with the same
    ///         <c>prod</c> resource group, so nothing else about the write path changes.
    ///     </para>
    /// </remarks>
    public static Guid IsolatedSubscription { get; } = Guid.Parse("44444444-4444-4444-8444-444444444444");

    /// <summary>The cluster's grain factory. ⚠ Tenant-unaware — a client, in the filter's terms.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>
    ///     The write path, constructed on the <b>client</b> side of the cluster.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Built here rather than resolved from the silo, and that is the faithful shape rather
    ///     than a convenience.</b> <c>IResourceManager</c> is documented as a service held by the
    ///     gateway, and docs/plan/03 and docs/plan/10 make the gateway an Orleans <i>client</i> — so
    ///     the grain factory it holds is <c>TestCluster.GrainFactory</c>, not the silo's.
    ///     <c>TestCluster.ServiceProvider</c> is the client's container and never sees what
    ///     <c>ISiloBuilder.ConfigureServices</c> registered, which is why resolving from it fails.
    ///     <para>
    ///         The silo still runs <c>AddCyberCloudResourceManager</c>, because
    ///         <c>OperationGrain</c> resolves <c>ReconcileDriver</c> from the silo's container. So both
    ///         halves of the production wiring are exercised, on the side each really lives on.
    ///     </para>
    /// </remarks>
    public IResourceManager Manager { get; private set; } = null!;

    /// <summary>The built registry.</summary>
    public IProviderRegistry Registry { get; private set; } = null!;

    /// <summary>A tenant-qualified grain factory.</summary>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>The quota grain steps 6 and 9 use.</summary>
    public IQuotaGrain Quota(Guid tenant, Guid subscription) =>
        For(tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

    /// <summary>The path index step 7 claims in.</summary>
    public IResourceIndexGrain Index(ResourceId address) =>
        For(address.TenantId).GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address));

    /// <summary>
    ///     The resource group grain whose membership the write and delete paths maintain.
    /// </summary>
    /// <param name="address">Any address in the group. Only its subscription and group name are read.</param>
    /// <remarks>
    ///     ⚠ Taken from an <b>address</b> rather than from a subscription and a name, so a test cannot
    ///     assert against a different group than the one it wrote into by mistyping a string — which
    ///     is a membership test that passes because it found nothing where it was looking.
    /// </remarks>
    public IResourceGroupGrain Group(ResourceId address) =>
        For(address.TenantId)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(address.SubscriptionId, address.ResourceGroup));

    /// <summary>
    ///     The same group's registry of parked resources — docs/plan/08 § Soft delete.
    /// </summary>
    /// <param name="address">Any address in the group. Only its subscription and group name are read.</param>
    /// <remarks>
    ///     ⚠ Taken from an <b>address</b> for <see cref="Group" />'s reason, and built from the same
    ///     two values, so a case cannot assert against the registry of one group and the membership
    ///     of another — which is a soft-delete test that passes because both lookups found nothing.
    /// </remarks>
    public IParkedResourceRegistryGrain Parked(ResourceId address) =>
        For(address.TenantId)
            .GetGrain<IParkedResourceRegistryGrain>(
                GrainKeys.ParkedResourceRegistry(address.SubscriptionId, address.ResourceGroup)
            );

    /// <summary>
    ///     The same group's expiry sweeper — the clock behind
    ///     <c>IResourceManager.PurgeExpiredAsync</c>, issue #12.
    /// </summary>
    /// <param name="address">Any address in the group. Only its subscription and group name are read.</param>
    /// <remarks>
    ///     ⚠ Built from the same two values as <see cref="Parked" /> for that helper's reason, and it
    ///     matters more here than anywhere else: a case that swept one group and asserted against the
    ///     registry of another would report a sweep that purged nothing and a registry that still
    ///     holds everything, which is exactly what a broken sweeper looks like.
    /// </remarks>
    public IExpirySweeperGrain Sweeper(ResourceId address) =>
        For(address.TenantId)
            .GetGrain<IExpirySweeperGrain>(
                GrainKeys.ExpirySweeper(address.SubscriptionId, address.ResourceGroup)
            );

    /// <summary>The resource grain.</summary>
    public IResourceGrain Resource(Guid tenant, Guid resourceId) =>
        For(tenant).GetGrain<IResourceGrain>(GrainKeys.Resource(resourceId));

    /// <summary>The operation grain.</summary>
    public IOperationGrain Operation(Guid tenant, Guid operationId) =>
        For(tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

    /// <summary>
    ///     A SignalR connection's grain. docs/plan/10 § SignalR.
    /// </summary>
    /// <remarks>
    ///     ⚠ Reached the way the gateway's hub reaches it — <c>ForTenant</c> from the token's tenant,
    ///     then the key <c>ConnectionGrainKeys</c> builds. That the suite can do this at all is the
    ///     change: while the type was declared in <c>CyberCloud.Gateway.Host</c> no silo could
    ///     activate it, and the only way to exercise it was to construct the class directly.
    /// </remarks>
    public IConnectionGrain Connection(Guid tenant, string connectionId) =>
        For(tenant).GetGrain<IConnectionGrain>(ConnectionGrainKeys.Connection(connectionId));

    /// <summary>Builds an address in the test tenant and subscription.</summary>
    /// <param name="name">The resource name. DNS-1123, per docs/plan/06 § Identifiers.</param>
    /// <param name="group">The resource group.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public static ResourceId Address(string name, string group = "prod", Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, group, ConformingReconciler.TypeName, name, Guid.Empty);

    /// <summary>Builds an address of the <b>soft-deletable</b> type.</summary>
    /// <param name="name">The resource name.</param>
    /// <param name="group">The resource group.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    /// <remarks>
    ///     ⚠ A separate helper rather than a parameter on <see cref="Address" />, so that a soft-delete
    ///     test cannot be written against the hard-delete type by forgetting an argument — which is a
    ///     test that passes for the wrong reason, and the failure this whole area is most prone to.
    /// </remarks>
    public static ResourceId VaultAddress(string name, string group = "prod", Guid? tenant = null) =>
        new(tenant ?? Tenant, Subscription, group, TestingProvider.VaultTypeName, name, Guid.Empty);

    /// <summary>A caller in the test tenant.</summary>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    /// <param name="subject">The subject id.</param>
    public static CallerContext Caller(Guid? tenant = null, string subject = "alice") =>
        new() {
            TenantId = tenant ?? Tenant,
            SubjectType = "user",
            SubjectId = subject,
            CorrelationId = "test"
        };

    /// <summary>Puts every switchable double back to its default.</summary>
    public static void ResetDoubles() {
        FakeWorld.Reset();
        SwitchableAuthorizer.Reset();
        SwitchablePolicyEvaluator.Reset();
        SwitchableLockResolver.Reset();
        RecordingRelationWriter.Reset();
        RecordingChangeSink.Reset();
        ScriptedInterestAuthorizer.Reset();
        RecordingClusterRegistrar.Reset();
        TestClock.Instance.Reset();
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        Registry = ProviderRegistry.Build([new TestingProvider()]);

        // ⚠ Step 1 of the write path reads ISubscriptionGrain and answers 404 for a subscription
        // this tenant does not have, so the suite's subscriptions are created before anything is
        // written into them. OtherTenant gets one too — the cross-tenant tests must be refused by the
        // TENANT comparison, and a subscription that did not exist would refuse them one line earlier
        // for the wrong reason.
        await CreateSubscriptionAsync(Tenant, Subscription);
        await CreateSubscriptionAsync(Tenant, IsolatedSubscription);
        await CreateSubscriptionAsync(OtherTenant, Subscription);

        // ⚠ AND EVERY METER IS PUT OUT OF THE WAY, BECAUSE THE DEFAULT BUDGET WAS A LANDMINE.
        //
        // A converged create commits its lease for the rest of the run, so the suite's committed total
        // only ever climbs and the default Vcpu limit of 100 was a scarce global that every class
        // shared. IsolatedSubscription was added as a place for a class to go, and it did not hold:
        // three separate classes stepped on the budget in one night — SoftDeletePathTests,
        // ActionDispatchTests and ClusterAttachTests, each written on its own branch, each green
        // alone. Every time, the failure landed in OperationTests and ParentEdgeStepTests, which spend
        // nothing, and named quota rather than the class that spent it.
        //
        // ⚠ The design was backwards. Every class depended implicitly on a scarce resource, and the
        // one test that actually wants scarcity got it by accident. Lifting the limits inverts that:
        // quota stops being ambient, and a test that wants a refusal asks for one.
        //
        // Nothing that asserts a refusal is weakened by this, which was checked rather than assumed:
        //   • ExceedingAMeterIsRefusedAtStepSixAndNamesTheMeter reads usage.Limit and requests
        //     Limit + 1, so it refuses at any limit; `/properties/size` carries no schema maximum, so
        //     the oversized body still reaches step 6 rather than failing validation at step 2.
        //   • ConnectionGrainTests' QuotaExceeded is ConnectionLimits.StreamsPerConnection, which is
        //     not a subscription meter at all.
        //   • InheritedLockTests asserts an error is NOT QuotaExceeded, which a lifted limit can only
        //     make more robust.
        //
        // ProviderTestCluster.LiftQuotaAsync does the same for the conformance harness and its remarks
        // carry the same argument, arrived at independently.
        await LiftQuotaAsync(Tenant, Subscription);
        await LiftQuotaAsync(Tenant, IsolatedSubscription);
        await LiftQuotaAsync(OtherTenant, Subscription);

        Manager = new ResourceManagerService(
            Registry,
            new SwitchableAuthorizer(),
            new RecordingRelationWriter(),
            new SwitchableLockResolver(),
            new SwitchablePolicyEvaluator(),
            new RecordingChangeSink(),
            cluster.GrainFactory,
            // ⚠ THE TWO HANDLERS TestingProvider DECLARES, AND DELIBERATELY NOT ITS THIRD ACTION.
            // `restart` and `listKeys` name handlers and run; `orphaned` names none, which is the
            // shape every action in the catalogue had before handlers existed and is the refusal
            // ActionDispatcher must produce by name. The secret resolver stays the refusing one:
            // ListKeysHandler returns a constant, so the containment suite can search for an exact
            // string rather than for whatever a vault double happened to hold.
            new ActionDispatcher(
                ActionHandlers(),
                new NoClusterConnectionFactory(),
                new UnavailableSecretResolver()
            ),
            NullLogger<ResourceManagerService>.Instance
        );
    }

    /// <summary>The container the action dispatcher resolves handlers from.</summary>
    static ServiceProvider ActionHandlers() {
        var services = new ServiceCollection();

        services.AddSingleton<RestartHandler>();
        services.AddSingleton<ListKeysHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     Creates a tenant's subscription and the resource group the suite addresses, so step 1 can
    ///     find them and so the lock walk has scopes above the resource to read.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">Which subscription — see <see cref="IsolatedSubscription" />.</param>
    async Task CreateSubscriptionAsync(Guid tenant, Guid subscription) {
        var created = await For(tenant)
            .GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription))
            .CreateAsync("tests");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var made = await For(tenant)
            .GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, "prod"))
            .CreateAsync(tenant, "eu-west-1");

        made.IsSuccess.ShouldBeTrue(made.Error?.Message);
    }

    /// <summary>Puts every quota meter out of the way for one subscription.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="subscription">The subscription.</param>
    /// <remarks>
    ///     ⚠ <b>Every declared meter, rather than the ones today's tests happen to draw.</b> Lifting
    ///     only <see cref="QuotaMeter.Vcpu" /> would put the budget back the day a class declared
    ///     another, and the failure would name quota rather than the class — which is the whole defect
    ///     this closes. <c>ProviderTestCluster.LiftQuotaAsync</c> reached the same rule for the
    ///     conformance harness.
    /// </remarks>
    async Task LiftQuotaAsync(Guid tenant, Guid subscription) {
        var quota = For(tenant).GetGrain<IQuotaGrain>(GrainKeys.Subscription(subscription));

        foreach (var meter in Enum.GetValues<QuotaMeter>()) {
            if (meter == QuotaMeter.Unknown) {
                continue;
            }

            var set = await quota.SetLimitAsync(meter, 1_000_000m);
            set.IsSuccess.ShouldBeTrue(set.Error?.Message);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            // ⚠ The operation grain is IRemindable and RegisterOrUpdateReminder throws without a
            // reminder service. In-memory rather than Redis (docs/plan/04 § Reminders) — see the
            // .csproj on what that does and does not prove.
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(services => {
                    services.AddSingleton<IClock>(TestClock.Instance);

                    // The doubles go in FIRST so the production wiring's TryAdd keeps them.
                    services.AddSingleton<IResourceAuthorizer, SwitchableAuthorizer>();
                    services.AddSingleton<IPolicyEvaluator, SwitchablePolicyEvaluator>();
                    services.AddSingleton<ILockResolver, SwitchableLockResolver>();
                    services.AddSingleton<IResourceChangedSink, RecordingChangeSink>();
                    services.AddSingleton<IResourceRelationWriter, RecordingRelationWriter>();

                    // The connection grain's seam. docs/plan/10 § SignalR — per-subscribe, never
                    // per-connect, and re-checked on relation changes.
                    services.AddSingleton<IInterestAuthorizer, ScriptedInterestAuthorizer>();

                    // ⚠ The cluster-attach seam. Recording rather than grain-backed, because there
                    // is no ClusterConnectionGrain in this harness — CyberCloud.Kubernetes is a
                    // module this suite does not compose. What is under test is the DRIVER's rule:
                    // a report becomes an attach only after the pass converges.
                    services.AddSingleton<IClusterConnectionRegistrar, RecordingClusterRegistrar>();

                    // A cap of two, so the interest limit is reachable in a test rather than after
                    // 200 subscribes. docs/plan/10 § Rate limiting.
                    services.AddSingleton(new ConnectionLimits { StreamsPerConnection = 2 });

                    services.AddSingleton<ConformingReconciler>();

                    // ⚠ Registered by hand beside its sibling, because this harness does not run
                    // DiscoveringProviderBuilder. A reconciler the container cannot resolve does not
                    // fail at silo start — the driver reports the pass InProgress forever, so the
                    // create never converges, the quota is never committed and every downstream
                    // assertion fails somewhere else entirely. Adding a type here is adding a line
                    // here.
                    services.AddSingleton<SoftDeletableReconciler>();
                    services.AddSingleton<IResourceProvider, TestingProvider>();
                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudResourceManager();

            // Orleans' own separation is deliberately NOT wired here. The write path is a client and
            // is therefore outside TenantSeparatingCallFilter by construction — see
            // CyberCloud.Tenancy/TenancySiloBuilderExtensions.cs § the residue. Cross-tenant refusal is
            // the manager's own check (ResolveAsync's tenant comparison), and
            // WritePathTests.ACrossTenantPathIs404 is what proves it rather than proving Orleans'.
        }
    }
}

/// <summary>Binds <see cref="ResourceManagerCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class ResourceManagerSuite : ICollectionFixture<ResourceManagerCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "resource-manager-cluster";
}

/// <summary>
///     An <see cref="IClusterConnectionRegistrar" /> a test reads back, and can make fail.
/// </summary>
/// <remarks>
///     ⚠ <b>Static state, like the rest of this harness's doubles</b>, because Orleans constructs it
///     inside the silo's container and a test has no other handle on the instance. Reset per test
///     class through <see cref="Reset" />.
/// </remarks>
public sealed class RecordingClusterRegistrar : IClusterConnectionRegistrar {
    /// <summary>Every descriptor attached, in order.</summary>
    public static ConcurrentBag<ClusterConnectionDescriptor> Attached { get; } = [];

    /// <summary>When set, every attach fails with this message.</summary>
    public static string? FailWith { get; set; }

    /// <summary>Forgets everything.</summary>
    public static void Reset() {
        Attached.Clear();
        FailWith = null;
    }

    /// <inheritdoc />
    public Task<Result> AttachAsync(
        ClusterConnectionDescriptor descriptor,
        CancellationToken cancellationToken = default
    ) {
        if (FailWith is { } failure) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, failure));
        }

        Attached.Add(descriptor);
        return Task.FromResult(Result.Success);
    }
}
