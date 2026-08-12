using CyberCloud.Core.Time;
using System.Collections.Concurrent;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     An <see cref="IResourceAuthorizer" /> a conformance run can tighten.
/// </summary>
/// <remarks>
///     ⚠ <b>A double, and the conformance suite is the right place for one.</b> What a provider must
///     pass is the reconciler contract and the resource lifecycle; standing up a ReBAC schema and a
///     tuple set to get a "yes" would make every provider's conformance run depend on
///     docs/plan/07's engine, and a schema defect would read as a provider defect. The
///     <b>isolation</b> suite is where the real <c>ReBacResourceAuthorizer</c> is driven, on purpose,
///     because there the engine <i>is</i> the thing under test.
///     <para>
///         It still reproduces the 404-versus-403 rule exactly, because the conformance suite asserts
///         the cross-tenant 404 and would otherwise be asserting nothing.
///     </para>
/// </remarks>
public sealed class PermissiveAuthorizer : IResourceAuthorizer {
    /// <summary>Permissions the caller holds. Empty means everything.</summary>
    public ConcurrentDictionary<string, bool> Granted { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether <see cref="Granted" /> is consulted at all.</summary>
    public bool Restricted { get; set; }

    /// <summary>Lets everything through again.</summary>
    public void Reset() {
        Granted.Clear();
        Restricted = false;
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
        ArgumentNullException.ThrowIfNull(caller);

        if (!Restricted || Granted.ContainsKey(actionPermission)) {
            return Task.FromResult(Result.Success);
        }

        return Task.FromResult(
            Granted.ContainsKey(readPermission)
                ? Result.Failure(
                    ErrorCode.AuthorizationFailed,
                    $"'{caller}' can read '{id.Path}' but does not have '{actionPermission}' on it."
                )
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{id.Path}' does not exist.")
        );
    }
}

/// <summary>An <see cref="ILockResolver" /> a test can set an inherited lock on.</summary>
/// <remarks>
///     ⚠ Stands in for the resource-group, subscription and management-group scopes the shipped
///     <c>ResourceScopeLockResolver</c> cannot read — see that type's remarks. The lock step of the
///     conformance list ("create → … → lock → delete → gone") is about the manager honouring a lock,
///     not about where the lock was found.
/// </remarks>
public sealed class SettableLockResolver : ILockResolver {
    /// <summary>The lock every scope reports.</summary>
    public LockLevel Level { get; set; } = LockLevel.None;

    /// <summary>Clears it.</summary>
    public void Reset() => Level = LockLevel.None;

    /// <inheritdoc />
    public Task<Result<LockLevel>> ResolveAsync(ResourceId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<LockLevel>.Success(Level));
}

/// <summary>
///     An <see cref="IResourceRelationWriter" /> that keeps the edge set in a dictionary.
/// </summary>
/// <remarks>
///     ⚠ <b>A double, and a <i>recording</i> one rather than a no-op.</b> What a provider must pass is
///     the resource lifecycle, and standing up a ReBAC tuple store for that would make every
///     provider's conformance run depend on docs/plan/07's engine. But "the write path links, and the
///     delete unlinks" is a lifecycle property — a resource whose delete left an edge behind is a leak
///     in every tenant that ever deleted anything — so the edges are recorded and the conformance list
///     asserts the set is empty once the resource is gone. The <b>real</b>
///     <c>ReBacResourceRelationWriter</c> over the real schema is driven by the isolation suite, which
///     is where the engine is the thing under test.
/// </remarks>
public sealed class RecordingRelationWriter : IResourceRelationWriter {
    readonly ConcurrentDictionary<Guid, string> edges = new();

    /// <summary>Every resource that currently has a parent edge, against the parent it points at.</summary>
    public IReadOnlyDictionary<Guid, string> Edges => edges;

    /// <summary>Whether writes fail, so the write path's rollback can be driven.</summary>
    public bool Fail { get; set; }

    /// <summary>
    ///     The direct role assignments each resource carries, as <c>{role}@{subject}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Seeded by a test so that "the drop ran" can be told from "there was nothing to drop" —
    ///     docs/plan/08 § Soft delete asks for a test that a grant written directly on a resource is
    ///     absent after a restore, and a double that never held one could not make it.
    /// </remarks>
    public ConcurrentDictionary<Guid, List<string>> Assignments { get; } = new();

    /// <summary>Forgets every edge.</summary>
    public void Reset() {
        edges.Clear();
        Assignments.Clear();
        Fail = false;
    }

    /// <inheritdoc />
    public Task<Result> LinkToParentAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        if (Fail) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        // ⚠ The same branch the real writer takes — a double that always recorded the group would
        // agree with a writer that always wrote it, and agree wrongly.
        edges[id.Id] = SubjectOf(id, parentId);

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> UnlinkFromParentAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        // ⚠ Removes the edge only when it is the one this call names — the real writer deletes one
        // specific tuple, so a double that removed unconditionally would agree with a writer that
        // unlinked the wrong subject. See the sibling double in CyberCloud.ResourceManager.Tests for
        // the defect that hid.
        if (edges.TryGetValue(id.Id, out var subject)
            && string.Equals(subject, SubjectOf(id, parentId), StringComparison.Ordinal)) {
            edges.TryRemove(id.Id, out _);
        }

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> ReparentToSubscriptionAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        if (Fail) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        // ⚠ ONE ASSIGNMENT AND NOT A REMOVE-THEN-ADD, which is what makes "the resource is never
        // parentless" assertable rather than assumed — docs/plan/08 § Soft delete chose re-parenting
        // over dropping the edge for exactly that property.
        edges[id.Id] = SubscriptionSubject(id);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> ReparentFromSubscriptionAsync(
        ResourceId id,
        Guid parentId,
        CancellationToken cancellationToken = default
    ) {
        if (Fail) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the tuple store is down"));
        }

        edges[id.Id] = SubjectOf(id, parentId);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> UnlinkFromSubscriptionAsync(ResourceId id, CancellationToken cancellationToken = default) {
        // ⚠ Removes the edge only when it actually names the subscription, because the real writer
        // deletes one specific tuple. A double that removed unconditionally would let a purge that
        // unlinked the wrong subject pass.
        if (edges.TryGetValue(id.Id, out var subject)
            && string.Equals(subject, SubscriptionSubject(id), StringComparison.Ordinal)) {
            edges.TryRemove(id.Id, out _);
        }

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<int>> DropDirectRoleAssignmentsAsync(
        ResourceId id,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result<int>.Success(Assignments.TryRemove(id.Id, out var held) ? held.Count : 0));

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
            ? "resourceGroup:" + id.SubscriptionId.ToString("N") + "-" + id.ResourceGroup
            : "resource:" + parentId.ToString("N");

    /// <summary>The subject a soft-deleted resource's edge names.</summary>
    static string SubscriptionSubject(ResourceId id) => "subscription:" + id.SubscriptionId.ToString("N");
}

/// <summary>Records every <c>resource-changed</c> event step 11 emitted.</summary>
public sealed class RecordingChanges : IResourceChangedSink {
    /// <summary>Everything published.</summary>
    public ConcurrentQueue<ResourceChangedEvent> Published { get; } = new();

    /// <summary>Forgets everything.</summary>
    public void Reset() => Published.Clear();

    /// <inheritdoc />
    public Task<Result> PublishAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default) {
        Published.Enqueue(change);
        return Task.FromResult(Result.Success);
    }
}

/// <summary>The clock the silo and the test share.</summary>
/// <remarks>
///     ⚠ Shared rather than injected per call for the reason <c>CyberCloud.ResourceManager.Tests</c>'
///     own clock gives: the silo runs in this process but resolves its own services, and the reconcile
///     scheduler's behaviour is caused by <i>time passing</i> — there is nothing to call to make that
///     happen. Advancing is forward only.
/// </remarks>
public sealed class ConformanceClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Puts time back to the start of a run.</summary>
    public void Reset() => UtcNow = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
}
