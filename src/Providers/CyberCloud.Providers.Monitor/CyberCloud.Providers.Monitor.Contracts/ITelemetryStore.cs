using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Runs one of the platform's own read statements against one workspace's ClickHouse database.
///     The seam between a component's views and the telemetry store.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The workspace, not a database name, and that is the tenancy argument.</b> An
///         implementation derives the database from <see cref="TelemetryQuery.Workspace" /> through
///         <see cref="MonitorWorkspaces.Database" />, so the caller never spells it; and the caller
///         gets the workspace's GUID from the platform's index (<c>ActionContext.Parent</c>), never
///         from a request or from an object in the tenant's namespace. A seam that took a database
///         string would be one careless caller away from reading another tenant's telemetry.
///     </para>
///     <para>
///         ⚠ <b>The statement is the platform's and the values are bound.</b> Every statement a
///         caller passes is a constant in this assembly with <c>{name:Type}</c> placeholders, and
///         <see cref="TelemetryQuery.Parameters" /> carries what the tenant typed — a trace id, a
///         window — bound server-side, never spliced into the text.
///     </para>
///     <para>
///         ⚠ <b>The default is a refusal.</b> A host with no store configured answers every query
///         with a failure naming the configuration keys, so a view says the store is not wired rather
///         than "no requests" — the silent zero docs/plan/16 § Cost and retention honesty forbids.
///     </para>
/// </remarks>
public interface ITelemetryStore {
    /// <summary>Runs one statement and returns its rows.</summary>
    /// <param name="query">The workspace, the statement and its bound values.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>
    ///     One object per row, or a failure whose message is fit for the caller: it names the budget a
    ///     query ran past, or says the store could not answer, and never quotes the statement.
    /// </returns>
    Task<Result<ImmutableArray<JsonObject>>> QueryAsync(TelemetryQuery query, CancellationToken cancellationToken = default);
}

/// <summary>One statement against one workspace's database.</summary>
/// <param name="Workspace">
///     The workspace, with its GUID resolved — the database is <see cref="MonitorWorkspaces.Database" />
///     of it.
/// </param>
/// <param name="Sql">
///     The statement, with <c>{name:Type}</c> placeholders and unqualified table names. It must end in
///     <c>FORMAT JSONEachRow</c>.
/// </param>
/// <param name="Parameters">The placeholders' values, by name.</param>
public sealed record TelemetryQuery(
    ResourceId Workspace,
    string Sql,
    IReadOnlyDictionary<string, string> Parameters
);
