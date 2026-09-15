namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Answers one rule's query against one workspace's store. The seam between the evaluator and
///     VictoriaMetrics or ClickHouse.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THIS IS WHY THE EVALUATOR IS A GRAIN ON A REMINDER AND NOT <c>vmalert</c>, WHICH IS
///             WHAT docs/plan/16 § The stack NAMES.
///         </b> <c>vmalert</c> evaluates rules it reads from a file or a CRD and posts firing alerts
///         to an Alertmanager-shaped endpoint; wiring it means a per-workspace <c>VMRule</c> object,
///         a receiver that turns an Alertmanager webhook into a grain call, and NATS in between —
///         three things, none of which exists, in front of the one that carries the product: a
///         notification through docs/plan/17. The platform already has a scheduler every module uses
///         (docs/plan/04 § Reminders) and a sending module with idempotency and suppression built
///         in, so the evaluation is a grain that asks a question on a schedule and sends on the
///         answer. What that leaves open is exactly this interface, and it is narrow on purpose.
///     </para>
///     <para>
///         ⚠ <b>The default is a refusal, not a stub that answers zero.</b> An evaluator whose seam
///         returned "no samples" for every rule would report every rule <c>Ok</c> forever and page
///         nobody, which is the silent failure docs/plan/16 § Cost and retention honesty exists to
///         forbid. <c>UnavailableAlertQuerySeam</c> fails every query by name, the rule records the
///         error and stays where it is, and the observed state says so. The real seam — a PromQL
///         <c>/api/v1/query</c> against the workspace's <c>accountID</c> and a SQL query against its
///         database, resolved from the row the workspace publishes — is
///         <c>charts/managed/monitor-workspace/conformance.yaml § owed</c>,
///         <c>alert-rules-query-seam-is-refusing</c>.
///     </para>
///     <para>
///         ⚠ <b>What a real implementation owes.</b> Tenant isolation first: the query runs under
///         the workspace's own <c>accountID</c> or database and never under a path the expression
///         can choose — a MetricsQL expression cannot name an account, but a SQL one can name a
///         database, and the seam must refuse or rewrite that. Then the limits docs/plan/16 § Alerts
///         makes mandatory: honour <see cref="AlertQuery.Lookback" /> as a hard range, and cancel on
///         the token, which the evaluator sets to <c>IAlertEvaluatorGrain.QueryTimeout</c>.
///     </para>
/// </remarks>
public interface IAlertQuerySeam {
    /// <summary>Runs one query and returns what it matched.</summary>
    /// <param name="query">What to ask, of which store, for which tenant.</param>
    /// <param name="cancellationToken">Cancels the query. Set by the evaluator to its timeout.</param>
    /// <returns>
    ///     The samples, or a failure naming why the store could not answer. ⚠ An empty result is a
    ///     success — the query ran and matched nothing — and is not the same as a failure.
    /// </returns>
    Task<Result<AlertQueryResult>> QueryAsync(AlertQuery query, CancellationToken cancellationToken = default);
}
