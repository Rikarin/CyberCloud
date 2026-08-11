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

/// <summary>Records every <c>resource-changed</c> event step 10 emitted.</summary>
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
