using CyberCloud.Authorization.Contracts;
using System.Collections.Concurrent;

namespace CyberCloud.ResourceManager.Tests.Infrastructure;

/// <summary>
///     An <see cref="IScopeAuthorizer" /> whose verdict a test sets — the scope path's twin of
///     <see cref="SwitchableAuthorizer" />, for the same reason and with the same warning.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A double, so that what is under test is the listing's shape and not the engine's
///         verdict.</b> Whether the real engine hides the right subscriptions is
///         <c>ReBacScopeAuthorizer</c>'s question, asked against <c>CyberCloudSchema</c> in
///         <c>test/CyberCloud.Isolation</c> and against a scripted <c>ListObjects</c> in
///         <c>ReBacScopeAuthorizerTests</c>. What this reproduces exactly is the two-state answer
///         of <see cref="ScopeCollectionVisibility" /> and the per-scope <c>404</c>, because a
///         listing has to handle both and a double that answered one shape would test half the
///         method.
///     </para>
///     <para>
///         ⚠ <b>Static state, like every other double in this harness</b>, so a test class resets
///         it through <see cref="ResourceManagerCluster.ResetDoubles" /> before it starts.
///     </para>
/// </remarks>
public sealed class SwitchableScopeAuthorizer : IScopeAuthorizer {
    /// <summary>Scopes this caller cannot read at all — the canonical <c>404</c>, whatever is asked.</summary>
    public static ConcurrentDictionary<ScopeId, bool> Hidden { get; } = new();

    /// <summary>
    ///     Whether <see cref="ListReadableAsync" /> answers, or declines so the listing asks per
    ///     member. Off by default, for the reason <see cref="SwitchableAuthorizer.AnswersCollections" />
    ///     is: the fallback is the path the real seam takes past its cap, and a suite that only ever
    ///     ran the batch path would have it rotting untested.
    /// </summary>
    public static bool AnswersCollections { get; set; }

    /// <summary>Every scope <see cref="AuthorizeAsync" /> was asked about, in order.</summary>
    public static ConcurrentQueue<ScopeId> Asked { get; } = new();

    /// <summary>Every <c>(parent, candidates)</c> pair the listing asked about, in order.</summary>
    public static ConcurrentQueue<(ScopeId Parent, int Candidates)> CollectionsAsked { get; } = new();

    /// <summary>Lets everything through again and forgets what was asked.</summary>
    public static void Reset() {
        Hidden.Clear();
        Asked.Clear();
        CollectionsAsked.Clear();
        AnswersCollections = false;
    }

    /// <inheritdoc />
    public Task<Result> AuthorizeAsync(
        ScopeId scope,
        string actionPermission,
        string readPermission,
        CallerContext caller,
        bool fullyConsistent = false,
        CancellationToken cancellationToken = default
    ) {
        Asked.Enqueue(scope);

        return Task.FromResult(
            Hidden.ContainsKey(scope)
                // ⚠ The same sentence the real seam and the manager produce, because the identity of
                // the three is the property a listing must preserve.
                ? Result.Failure(ErrorCode.ResourceNotFound, $"'{scope.Path}' does not exist.")
                : Result.Success
        );
    }

    /// <inheritdoc />
    public Task<Result> AuthorizePlatformAsync(
        string permission,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result.Success);

    /// <inheritdoc />
    public Task<ScopeCollectionVisibility> ListReadableAsync(
        ScopeId parent,
        IReadOnlyList<ScopeId> candidates,
        string readPermission,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        CollectionsAsked.Enqueue((parent, candidates.Count));

        return Task.FromResult(
            AnswersCollections
                ? ScopeCollectionVisibility.Of(candidates.Where(x => !Hidden.ContainsKey(x)))
                : ScopeCollectionVisibility.Unanswered
        );
    }
}

/// <summary>
///     An <see cref="IScopeRelationWriter" /> that writes nothing and succeeds.
/// </summary>
/// <remarks>
///     ⚠ The scope path's step 8 goes into the tuple store the real engine walks, and this harness
///     has no engine; the edge it would write is what <c>test/CyberCloud.Isolation</c>'s
///     <c>ScopeCreationTests</c> drives through the real <c>ReBacScopeRelationWriter</c>. Here it
///     only has to not fail, so that a create reaches the grain and the tenant's listing gains the
///     entry a listing test then reads.
/// </remarks>
public sealed class NoOpScopeRelationWriter : IScopeRelationWriter {
    /// <inheritdoc />
    public Task<Result> LinkToParentAsync(ScopeId scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success);

    /// <inheritdoc />
    public Task<Result> GrantOwnerAsync(
        ScopeId scope,
        string subjectType,
        string subjectId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result.Success);
}

/// <summary>
///     An <see cref="IListObjectsGrain" /> that answers from a script — what
///     <c>ReBacScopeAuthorizerTests</c> puts behind the real <c>ReBacScopeAuthorizer</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A grain in the test assembly rather than the real <c>ListObjectsGrain</c>, because
///         this harness hosts no authorization engine</b> — <c>CyberCloud.ResourceManager.Tests</c>
///         references the contracts and not <c>CyberCloud.Authorization</c>, so the interface has
///         exactly one implementation in the silo and it is this one. What that buys is the one
///         case the real engine cannot be made to produce on demand: a walk that hit its cap, which
///         the seam has to turn into "ask per member" and never into an empty page. Whether the
///         real walk finds the right subscriptions is <c>test/CyberCloud.Isolation</c>'s.
///     </para>
///     <para>
///         ⚠ <b>Static script, like every other double here</b>, because Orleans constructs the
///         activation and a test has no other handle on it. One page, whatever the request's
///         continuation: the cases that need paging are the walk's own, in
///         <c>CyberCloud.Authorization.Tests</c>.
///     </para>
/// </remarks>
public sealed class ScriptedListObjectsGrain : Grain, IListObjectsGrain {
    /// <summary>The object ids the next answer lists, on the requested type.</summary>
    public static ConcurrentBag<string> Objects { get; } = [];

    /// <summary>How the next answer ends. <see cref="ListObjectsOutcome.Complete" /> by default.</summary>
    public static ListObjectsOutcome Outcome { get; set; } = ListObjectsOutcome.Complete;

    /// <summary>When set, the next call fails outright with this message rather than answering.</summary>
    public static string? FailWith { get; set; }

    /// <summary>Every request received, in order.</summary>
    public static ConcurrentQueue<ListObjectsRequest> Requests { get; } = new();

    /// <summary>Forgets everything and answers an empty, complete page.</summary>
    public static void Reset() {
        Objects.Clear();
        Requests.Clear();
        Outcome = ListObjectsOutcome.Complete;
        FailWith = null;
    }

    /// <inheritdoc />
    public Task<Result<ListObjectsPage>> ListObjectsAsync(ListObjectsRequest request) {
        Requests.Enqueue(request);

        if (FailWith is { } message) {
            return Task.FromResult(Result<ListObjectsPage>.Failure(ErrorCode.SchemaInvalid, message));
        }

        // ⚠ Empty on a cap, as the real grain is: an object past the cap is one Check would deny
        // and the ones before it may depend on it — ListObjectsOutcome's own remarks.
        var objects = Outcome == ListObjectsOutcome.Complete
            ? Objects.Order(StringComparer.Ordinal)
                .Select(id => CyberCloud.Authorization.Contracts.ObjectRef.Of(request.ObjectType, id))
                .ToList()
            : [];

        return Task.FromResult(
            Result<ListObjectsPage>.Success(
                new() {
                    Objects = objects,
                    Outcome = Outcome,
                    CapDetail = Outcome == ListObjectsOutcome.Complete ? "" : "scripted"
                }
            )
        );
    }
}
