using CyberCloud.Authorization.Contracts;
using Orleans.Concurrency;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.Billing.Grains;

/// <summary>
///     <see cref="IInvoiceQueryGrain" /> — Stateless, key <c>tenant/{tenantId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read <see cref="IInvoiceQueryGrain" /> first</b> for who may read. This class is the
///         order: one <c>read</c> check on the tenant, fully consistent, then the billing account.
///     </para>
///     <para>
///         ⚠ <b>The check is <see cref="Consistency.FullyConsistent" /></b>, where the cost query's is
///         not, because the answer is a whole tenant's invoices rather than rows filtered one by one: a
///         revoked tenant reader served from the check cache would read every invoice the cache still
///         remembers them for. <c>BudgetTests</c> found the same cached answer outliving a grant the
///         other way round.
///     </para>
///     <para>
///         ⚠ <b>The tenant's ReBAC object id is spelled here a second time</b> —
///         <c>{tenant:N}</c> — because the spelling that writes it is <c>ReBacScopeAuthorizer</c>'s, in
///         an assembly this module may not reference. <c>InvoiceVisibilityTests</c> grants through a
///         tuple written the scope authorizer's way.
///     </para>
/// </remarks>
[StatelessWorker]
public sealed class InvoiceQueryGrain(IGrainFactory grains) : Grain, IInvoiceQueryGrain {
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = BillingGrainKeys.TenantOf(this);
        _ = BillingGrainKeys.Decode(this, GrainKeyKind.Tenant);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ImmutableArray<Invoice>>> ListAsync(CostCaller caller) {
        ArgumentNullException.ThrowIfNull(caller);

        var address = new InvoiceAddress(tenantId, string.Empty);

        if (!await MayReadAsync(caller)) {
            return Result<ImmutableArray<Invoice>>.Failure(NotFound(address));
        }

        var listed = await Account().ListInvoicesAsync();
        if (listed.TryGetError(out var error)) {
            return Result<ImmutableArray<Invoice>>.Failure(error);
        }

        // The account holds them in the order they were finalized, which is oldest first; a person
        // looking for last month's invoice reads from the top.
        return Result<ImmutableArray<Invoice>>.Success([.. listed.GetValueOrThrow().Reverse()]);
    }

    /// <inheritdoc />
    public async Task<Result<Invoice>> GetAsync(CostCaller caller, string number) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(number);

        var address = new InvoiceAddress(tenantId, number);

        if (!await MayReadAsync(caller)) {
            return Result<Invoice>.Failure(NotFound(address));
        }

        var read = await Account().GetInvoiceAsync(number);

        // ⚠ The account's own sentence names the tenant by id. The refusal above names the address, so
        // an absent number answers with the address too, and the two cannot be told apart.
        return read.TryGetError(out var error) && error.Code == ErrorCode.ResourceNotFound
            ? Result<Invoice>.Failure(NotFound(address))
            : read;
    }

    /// <summary>One <c>read</c> check on the tenant. A check that could not be answered is a no.</summary>
    async Task<bool> MayReadAsync(CostCaller caller) {
        var subject = SubjectRef.Create(caller.SubjectType, caller.SubjectId);
        if (subject.TryGetError(out _)) {
            return false;
        }

        var tenantObject = tenantId.ToString("N", CultureInfo.InvariantCulture);

        var checkedRead = await Tenant()
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(ObjectTypes.Tenant, tenantObject))
            .CheckAsync(Permissions.Read, subject.GetValueOrThrow(), Consistency.FullyConsistent);

        return checkedRead.TryGetValue(out var answer) && answer.Allowed;
    }

    IBillingAccountGrain Account() => Tenant().GetGrain<IBillingAccountGrain>(GrainKeys.Tenant(tenantId));

    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    static Error NotFound(InvoiceAddress address) => new(ErrorCode.ResourceNotFound, $"'{address.Path}' does not exist.");
}
