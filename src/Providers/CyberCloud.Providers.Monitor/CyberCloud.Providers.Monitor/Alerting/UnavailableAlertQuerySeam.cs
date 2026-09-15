namespace CyberCloud.Providers.Monitor.Alerting;

/// <summary>
///     The <see cref="IAlertQuerySeam" /> a host gets when it registers no other: every query is
///     refused, by name, with what a real one would do.
/// </summary>
/// <remarks>
///     ⚠ <b>A refusal and not a stub that answers "no samples", for the reason every seam in this
///     tree gives.</b> A seam that answered empty would report every rule <c>Ok</c> forever and the
///     tenant would learn their alerting did not work from the incident it did not page about. This
///     way the rule's observed state carries the sentence below, and the roadmap row that says ✅
///     says beside it what ✅ does not mean.
/// </remarks>
public sealed class UnavailableAlertQuerySeam : IAlertQuerySeam {
    /// <inheritdoc />
    public Task<Result<AlertQueryResult>> QueryAsync(AlertQuery query, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(query);

        return Task.FromResult(
            Result<AlertQueryResult>.Failure(
                ErrorCode.InternalError,
                $"No alert query seam is registered in this host, so the {MonitorAlertRules.Spell(query.Signal)} "
                + $"query for workspace '{query.WorkspacePath}' was not run and the rule keeps its state. A "
                + "real IAlertQuerySeam runs a MetricsQL query against the workspace's VictoriaMetrics "
                + "accountID or a SQL query against its ClickHouse database, resolved from the row the "
                + "workspace publishes — charts/managed/monitor-workspace/conformance.yaml § owed, "
                + "alert-rules-query-seam-is-refusing."
            )
        );
    }
}
