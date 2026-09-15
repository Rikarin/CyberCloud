using CyberCloud.Authorization.Contracts;
using CyberCloud.Authorization.Evaluation;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using Orleans.Concurrency;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Authorization.Grains;

/// <summary>
///     <see cref="IListObjectsGrain" /> — Entity, no tier, key <c>rel/list/{type}/{id}</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Stateless, and <see cref="ReentrantAttribute">reentrant</see> because it is.</b>
///         Every call builds its own <see cref="ListObjectsEvaluator" />, reads through it, and
///         drops it; nothing on the activation is written after activation. So two listings for
///         one subject — a portal with two blades open — interleave rather than queue, and neither
///         can observe the other. The one thing that would make reentrancy wrong is a field a call
///         mutates, and the remarks are here so the next field gets a second look.
///     </para>
///     <para>
///         ⚠ <b>Not on the durable tier and not on the hot tier.</b> <c>durable-grains.txt</c>
///         asks "can this be rebuilt", and here there is nothing to rebuild: an answer is a walk over
///         two indexes that are durable in their own grains. The hot tier holds caches, and
///         <see cref="IListObjectsGrain" />'s remarks say why there is no cache here.
///     </para>
///     <para>
///         <b>The token is read before the walk, so it is a floor.</b> The reverse index and the
///         Leopard index the walk reads may already contain writes past that version; they cannot
///         lack one at or before it, because the version is bumped after every grain has landed
///         (<c>TupleStoreGrain</c>, step 7).
///         A caller that wants to know a revoke is reflected compares
///         <see cref="ListObjectsPage.Token" /> to the one the revoke returned.
///     </para>
/// </remarks>
[Reentrant]
public sealed class ListObjectsGrain(AuthorizationSchema schema, AuthorizationLimits limits)
    : Grain, IListObjectsGrain {
    Guid tenantId;
    ObjectRef subjectObject = new();

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = AuthorizationGrainKeys.TenantOf(this);
        subjectObject = AuthorizationGrainKeys.DecodeObject(this, GrainKeyKind.ListObjects);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ListObjectsPage>> ListObjectsAsync(ListObjectsRequest request) {
        if (request is null) {
            return Result<ListObjectsPage>.Failure(ErrorCode.InvalidRequestBody, "A ListObjects request is required.");
        }

        var subject = SubjectRef.Create(subjectObject.Type, subjectObject.Id, request.SubjectRelation);
        if (subject.TryGetError(out var subjectError)) {
            return Result<ListObjectsPage>.Failure(subjectError);
        }

        var token = await StoreGrain().GetTokenAsync();
        if (token.TryGetError(out var tokenError)) {
            return Result<ListObjectsPage>.Failure(tokenError);
        }

        // One index reader for the walk and for its verifying check, so a slice is read once.
        var index = new MembershipIndexReader(schema, new GrainMembershipIndexStore(GrainFactory, tenantId));

        var evaluator = new ListObjectsEvaluator(
            schema,
            new GrainRelationReader(GrainFactory, tenantId, false),
            new GrainReverseRelationReader(GrainFactory, tenantId, index),
            limits,
            index
        );

        var evaluated = await evaluator.EvaluateAsync(subject.GetValueOrThrow(), request, CancellationToken.None);
        if (evaluated.TryGetError(out var error)) {
            return Result<ListObjectsPage>.Failure(error);
        }

        var evaluation = evaluated.GetValueOrThrow();
        var (page, continuation) = Page(evaluation.Objects, request);

        return Result<ListObjectsPage>.Success(
            new() {
                Objects = page,
                Continuation = continuation,
                Outcome = evaluation.Outcome,
                CapDetail = evaluation.CapDetail,
                Token = token.GetValueOrThrow(),
                PairsReached = evaluation.PairsReached,
                ReverseReads = evaluation.ReverseReads,
                ForwardReads = evaluation.ForwardReads,
                IndexReads = evaluation.IndexReads,
                Verified = evaluation.Verified
            }
        );
    }

    /// <summary>
    ///     One page out of the ordered answer: everything after the continuation, up to the page
    ///     size, and the last id as the next continuation when more remain.
    /// </summary>
    /// <remarks>
    ///     The continuation names the last id <i>returned</i>, which is right here and wrong for
    ///     <c>IResourceManager.ListAsync</c> — that endpoint filters after paging, so its token has
    ///     to name the last member <i>examined</i>. This answer is filtered before it is paged, so
    ///     the two are the same object and no page can come back empty with a next page behind it.
    /// </remarks>
    static (IReadOnlyList<ObjectRef> Page, string Continuation) Page(
        IReadOnlyList<ObjectRef> objects,
        ListObjectsRequest request
    ) {
        var size = request.EffectivePageSize;
        List<ObjectRef> page = new(Math.Min(size, objects.Count));
        var more = false;

        foreach (var candidate in objects) {
            if (string.CompareOrdinal(candidate.Id, request.Continuation) <= 0) {
                continue;
            }

            if (page.Count == size) {
                more = true;
                break;
            }

            page.Add(candidate);
        }

        return (page, more ? page[^1].Id : string.Empty);
    }

    ITupleStoreGrain StoreGrain() =>
        GrainFactory.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenantId));
}
