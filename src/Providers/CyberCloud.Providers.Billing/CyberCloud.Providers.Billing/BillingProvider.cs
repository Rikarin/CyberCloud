namespace CyberCloud.Providers.Billing;

/// <summary>
///     Cost management as a tenant-facing provider — one resource type, <c>CyberCloud.Billing/budgets</c>,
///     over the budget grain <c>CyberCloud.Billing</c> runs, and no cluster anywhere.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/24 § Phase 3's <c>Billing</c> row — "rating, invoicing, PSP, tax service, cost
///         views, budgets" — publishes one type. The rest of the row is not a resource by design:
///         rating and invoicing are a module, the cost view is a query under the reserved
///         <c>CyberCloud.CostManagement</c> namespace (<c>CostQueryAddress</c>), and the PSP and tax
///         service are seams. docs/plan/22 § Rating lists <c>priceLists</c>, <c>invoices</c>,
///         <c>paymentMethods</c> and <c>commitments</c> as resources too; docs/plan/22 § What is owed
///         says which of those should become one and why none did here.
///     </para>
///     <para>
///         ⚠ <b>No <c>RequiresCluster</c>, no <c>Chart</c>, no <c>ClusterId</c></b> — the second
///         clusterless family, after <c>CyberCloud.Communication</c>. Every reconcile gets a
///         <see langword="null" /> <c>ReconcileContext.Cluster</c> and never looks at it.
///     </para>
///     <para>
///         ⚠ <b>One meter, the count.</b> A budget draws nothing a subscription holds; it is a row in a
///         grain and an hourly reminder, and the resource count is the only quota family it touches.
///     </para>
/// </remarks>
public sealed class BillingProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => Budgets.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(Budgets.TypePath)
            .ApiVersion(Budgets.V2026, Budgets.Schema2026)
            .Reconciler<BudgetReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // Issue #41: the only way a caller reads the figures and the thresholds that fired.
            .Action(
                Budgets.StatusAction,
                ActionKind.Post,
                "read",
                response: Budgets.StatusResponse,
                handler: typeof(BudgetStatusHandler)
            )
            .Display(
                "Budget",
                "Budgets",
                // ⚠ NOT `billing`, for the reason CyberCloud.Communication/services could not be
                // `communication`: CliEmitter derives the group key from the namespace's last segment,
                // and `cyc billing billing` would have two meanings.
                "budget",
                "A spending limit for a resource group or a subscription, per month, quarter or year, with "
                + "thresholds on the actual cost and on the forecast that alert through a sending service."
            )
            .SupportsTags();
    }
}
