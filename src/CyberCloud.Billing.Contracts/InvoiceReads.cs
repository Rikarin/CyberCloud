using System.Collections.Immutable;

namespace CyberCloud.Billing.Contracts;

/// <summary>
///     A tenant's finalized invoices, read on behalf of a caller the ReBAC engine is asked about first.
///     docs/plan/22 § What is owed, <c>billing-http-surface</c>, issue #41.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Stateless · <b>Tier</b> none · <b>Key</b> <c>tenant/{tenantId:N}</c>,
///         tenant-qualified — the billing account's key, on another interface.
///     </para>
///     <para>
///         ⚠ <b>Who may see a tenant's invoices is <c>read</c> on the tenant, and nothing narrower.</b>
///         docs/plan/22 left the question open ("who may see a tenant's invoices is not a resource-group
///         reader"), and the answer here is the smallest one the schema already has: an owner,
///         contributor or reader granted at the tenant. A reader of one subscription does not see the
///         invoice, because an invoice carries every attached subscription's lines and the tenant's
///         legal profile. A billing-reader role that reaches invoices without reaching resources is a
///         schema change and is owed (docs/plan/20 § What is owed, <c>billing-reader-role</c>).
///     </para>
///     <para>
///         ⚠ <b>Checked here and not in the gateway</b>, for the reason <see cref="ICostQueryGrain" />
///         gives — docs/plan/10 § Request pipeline's one enforcement seam. A caller who may not read the
///         tenant gets <see cref="ErrorCode.ResourceNotFound" />, the sentence an invoice number that
///         does not exist gets, so a refusal says nothing about whether the tenant has invoices.
///     </para>
///     <para>
///         ⚠ <b>Finalized invoices only.</b> A draft is rated on every read (<c>PreviewAsync</c>) and needs
///         the issuer; the portal shows the running month through the cost query instead, and a draft
///         over HTTP is owed with the rest of the account's surface.
///     </para>
/// </remarks>
[Alias("CyberCloud.Billing.IInvoiceQueryGrain")]
public interface IInvoiceQueryGrain : IGrainWithStringKey {
    /// <summary>Every finalized invoice, newest first.</summary>
    /// <param name="caller">Who is asking. Must be able to read the tenant.</param>
    Task<Result<ImmutableArray<Invoice>>> ListAsync(CostCaller caller);

    /// <summary>One finalized invoice, by the number printed on it.</summary>
    /// <param name="caller">Who is asking. Must be able to read the tenant.</param>
    /// <param name="number">The invoice number, compared ordinally.</param>
    Task<Result<Invoice>> GetAsync(CostCaller caller, string number);
}

/// <summary>What the gateway's dispatch stage holds to read invoices — the grain, qualified once.</summary>
public interface IInvoiceReader {
    /// <summary>Lists the tenant's finalized invoices.</summary>
    /// <param name="tenantId">The token's tenant. Qualifies the grain call and keys the account.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<ImmutableArray<Invoice>>> ListAsync(Guid tenantId, CostCaller caller, CancellationToken cancellationToken = default);

    /// <summary>Reads one finalized invoice.</summary>
    /// <param name="tenantId">The token's tenant.</param>
    /// <param name="caller">Who is asking.</param>
    /// <param name="number">The number printed on it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<Invoice>> GetAsync(Guid tenantId, CostCaller caller, string number, CancellationToken cancellationToken = default);
}
