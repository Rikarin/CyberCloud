using CyberCloud.Core;

namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     <c>ListObjects</c> — docs/plan/07 § ListObjects. "Which objects of type T may this subject
///     <c>permission</c>?" — the reverse direction of <see cref="ICheckGrain" />, and the one that
///     lets a listing stop asking <c>Check</c> once per member.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> none · <b>Key</b> <c>rel/list/{type}/{id}</c>,
///         tenant-qualified, and the object in the key is the <b>subject</b> being listed for.
///         Build it with <c>GrainKeys.ListObjects</c>. It holds no state: every answer is a walk, and
///         the walk starts from the subject's reverse index, which is why the subject is the key.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The signature is not docs/plan/07 § ListObjects', for the reasons
///             <see cref="ICheckGrain" /> gives.
///         </b> The document writes
///         <c>ListObjectsAsync(objectType, permission, subject, ContinuationToken? ct)</c>; here the
///         subject is the key and the rest travels in a <see cref="ListObjectsRequest" />, which also
///         carries the one bound the document does not name — <see cref="ListObjectsRequest.Within" />
///         — and the page size the document assumes.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It reads the half of the store that is allowed to be stale, and that is the
///             document's own trade.
///         </b> docs/plan/07 § Storage: the reverse index "is written together"
///         with the forward one, "object first, then subject", and "a subject index missing an entry
///         costs a <c>ListObjects</c> a miss, not a <c>Check</c> an incorrect answer". The window is
///         exact: between step 3 and step 5 of <c>TupleStoreGrain.ApplyAsync</c> a tuple is in the
///         forward half and not yet in the reverse one, so a <c>FullyConsistent</c> check — or any
///         check of a tuple the Leopard index does not cover — allows it while this grain cannot
///         find it, and a write that dies there stays that way until
///         <c>ITupleStoreGrain.SweepAsync</c> replays it. A miss hides an object the caller may
///         see; nothing this grain reads can show one they may not, because a reverse entry exists
///         only after its forward tuple does. The Leopard index (<see cref="IMembershipIndexGrain" />)
///         is written a step later still, at step 6, and it is on the check path too: a
///         <c>MinimizeLatency</c> or <c>AtLeastAsFresh</c> check of an indexed membership takes
///         the closure's <c>false</c> without walking, so for such a tuple the window closes
///         at step 6 for the check and this grain alike, and
///         <c>MembershipIndexGrainTests.AWriteThatDiesBeforeTheIndexLeavesADenyUntilTheSweeperReplaysIt</c>
///         is the deny.
///     </para>
///     <para>
///         ⚠ <b>Answers are never cached here.</b> The check cache is keyed by object; a listing's
///         answer is a set that any tuple write in the tenant can change, and docs/plan/07
///         § Caching across requests' whole-tenant invalidation would empty such a cache on every
///         role assignment. The bound on cost is <see cref="ListObjectsRequest.Within" />, not a
///         cache — and the fast list docs/plan/07 § ListObjects describes is the resource-graph
///         projection, which this grain is meant to maintain rather than replace.
///     </para>
/// </remarks>
[Alias("CyberCloud.Authorization.IListObjectsGrain")]
public interface IListObjectsGrain : IGrainWithStringKey {
    /// <summary>
    ///     One page of the objects of <see cref="ListObjectsRequest.ObjectType" /> on which this
    ///     subject has <see cref="ListObjectsRequest.Permission" />.
    /// </summary>
    /// <param name="request">What to list, and the bounds.</param>
    /// <remarks>
    ///     <para>
    ///         The walk is docs/plan/07 § ListObjects' algorithm: start from the subject's reverse
    ///         index, collect the objects it touches, then walk the rewrite tree backwards — a
    ///         direct tuple reaches its object, a userset the subject is in reaches everything
    ///         written against that userset, <c>Rel(x)</c> carries a reached relation to the names
    ///         computed from it, and <c>From(ts, c)</c> carries a reached parent to every child
    ///         pointing at it. Exact for rewrites built from <c>This</c>, <c>Rel</c>, <c>From</c> and
    ///         union; a rewrite with an intersection or an exclusion in reach is over-approximated
    ///         and every candidate is then re-checked forward, which is the document's "each page is
    ///         <c>Check</c>-verified".
    ///     </para>
    ///     <para>
    ///         ⚠ A <c>Result</c> failure is a question that was not answerable —
    ///         <see cref="ErrorCode.SchemaInvalid" /> for a type or name the schema does not define,
    ///         <see cref="ErrorCode.InvalidRequestBody" /> for a malformed scope. A walk that hit a
    ///         cap is a <i>successful</i> page with no objects and an <see cref="ListObjectsPage.Outcome" />
    ///         that says which cap; see <see cref="ListObjectsOutcome" /> for why it is empty rather
    ///         than partial.
    ///     </para>
    /// </remarks>
    Task<Result<ListObjectsPage>> ListObjectsAsync(ListObjectsRequest request);
}
