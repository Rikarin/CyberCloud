using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The per-tenant table docs/plan/08 § The resource-graph projection specifies: its database,
///     its name, its DDL and one row of it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             <c>ReplacingMergeTree(version)</c>, ordered by <c>resource_id</c> — the engine is the
///             idempotency.
///         </b> Every event becomes an <c>INSERT</c>; two rows for one resource collapse
///         at merge time to the one with the highest <c>version</c>, and a reader that says
///         <c>FINAL</c> sees that collapse before the merge happens. The projector also refuses to
///         insert a version at or below the one it can already read, which keeps a replay from
///         writing rows the merge would only throw away — but that check is a race between two
///         silos and the engine is not, so the engine is the guarantee and the check is the
///         economy.
///     </para>
///     <para>
///         ⚠ <b>The three columns the plan's list does not name, and why each is here.</b>
///         <c>change</c> is the event kind, because a list of "what happened to this tenant's
///         resources in the last hour" is the audit sink docs/plan/04 § Streams lists as a consumer,
///         and it costs one <c>LowCardinality(String)</c>. <c>is_deleted</c> is set by a
///         <c>Deleted</c> event and by a <c>SoftDeleted</c> one, and read as a filter, because a
///         resource that is gone — or parked, which docs/plan/08 § Soft delete puts "in no
///         listing" — must leave the list without leaving the table: a <c>DELETE</c> in ClickHouse
///         is a mutation, which is asynchronous and expensive, and a tombstone row under the same
///         engine is neither. The two tombstones differ in <c>change</c> and in <c>access</c>: the
///         hard one has no readers, the parked one's readers are the subscription's holders, so
///         "what can I restore in this subscription" is <c>is_deleted = 1 AND change = 'SoftDeleted'</c>
///         under the same access filter as the list. <c>access</c> is the column docs/plan/07
///         § ListObjects says the index maintains — see <see cref="ResourceGraphRow.Access" /> for
///         what it holds.
///     </para>
///     <para>
///         The database is <c>tenant_{tenantId:N}</c>, following docs/plan/05 § Every store's
///         "database-per-tenant" and the Monitor workspace's <c>ws_{id:N}</c>: keyed on the GUID so
///         two tenants cannot collide and a rename cannot move it, prefixed because a ClickHouse
///         identifier may not start with a digit. Logs, traces and metering rollups land in the same
///         database when their writers arrive, which is what makes "a tenant's ClickHouse" one
///         thing to back up (docs/plan/05 § Backup) and one thing to grant.
///     </para>
/// </remarks>
public static class ResourceGraphTable {
    /// <summary>The table's name inside the tenant's database.</summary>
    public const string Name = "resource_graph";

    /// <summary>The tenant's database — <c>tenant_{tenantId:N}</c>.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string Database(Guid tenantId) => string.Create(CultureInfo.InvariantCulture, $"tenant_{tenantId:N}");

    /// <summary>The qualified table — <c>tenant_{tenantId:N}.resource_graph</c>.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string Qualified(Guid tenantId) => Database(tenantId) + "." + Name;

    /// <summary><c>CREATE DATABASE IF NOT EXISTS</c> for one tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string CreateDatabase(Guid tenantId) => "CREATE DATABASE IF NOT EXISTS " + Database(tenantId);

    /// <summary>
    ///     <c>CREATE TABLE IF NOT EXISTS</c> for one tenant — the plan's columns, in its order, then
    ///     the three this file explains.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    public static string CreateTable(Guid tenantId) =>
        $"""
         CREATE TABLE IF NOT EXISTS {Qualified(tenantId)} (
             resource_id UUID,
             tenant_id UUID,
             subscription_id UUID,
             resource_group String,
             provider LowCardinality(String),
             type LowCardinality(String),
             name String,
             api_version LowCardinality(String),
             provisioning_state LowCardinality(String),
             location LowCardinality(String),
             cluster_id UUID,
             tags Map(String, String),
             created_at DateTime64(3, 'UTC'),
             modified_at DateTime64(3, 'UTC'),
             desired_hash String,
             version UInt64,
             change LowCardinality(String),
             is_deleted UInt8,
             access Array(String),
             projected_at DateTime64(3, 'UTC')
         )
         ENGINE = ReplacingMergeTree(version)
         ORDER BY resource_id
         """;

    /// <summary>The highest version the table holds for one resource, or nothing.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string SelectVersion(Guid tenantId) =>
        $"SELECT max(version) AS version FROM {Qualified(tenantId)} WHERE resource_id = {{resource_id:UUID}} FORMAT JSONEachRow";

    /// <summary><c>INSERT … FORMAT JSONEachRow</c>, ready for one row's JSON to follow on the next line.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string Insert(Guid tenantId) => $"INSERT INTO {Qualified(tenantId)} FORMAT JSONEachRow\n";

    /// <summary>One resource's current row, collapsed, or nothing.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string SelectRow(Guid tenantId) =>
        $"SELECT * FROM {Qualified(tenantId)} FINAL WHERE resource_id = {{resource_id:UUID}} FORMAT JSONEachRow";
}

/// <summary>One row of the projection — the event's columns plus the three the table adds.</summary>
/// <remarks>
///     ⚠ <b>The names are the column names and the JSON is the wire.</b> This record is serialized
///     with <see cref="ResourceGraphJson.Options" /> straight into <c>INSERT … FORMAT JSONEachRow</c>
///     and read back from <c>SELECT … FORMAT JSONEachRow</c>, so a property renamed here is a column
///     the insert no longer fills. Snake case is ClickHouse's convention and docs/plan/08's spelling.
/// </remarks>
public sealed record ResourceGraphRow {
    public Guid ResourceId { get; init; }

    public Guid TenantId { get; init; }

    public Guid SubscriptionId { get; init; }

    public string ResourceGroup { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string ApiVersion { get; init; } = string.Empty;

    public string ProvisioningState { get; init; } = string.Empty;

    public string Location { get; init; } = string.Empty;

    public Guid ClusterId { get; init; }

    public ImmutableDictionary<string, string> Tags { get; init; } = ImmutableDictionary<string, string>.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ModifiedAt { get; init; }

    public string DesiredHash { get; init; } = string.Empty;

    public long Version { get; init; }

    public string Change { get; init; } = string.Empty;

    /// <summary>
    ///     1 after a <c>Deleted</c> or a <c>SoftDeleted</c> event; a listing filters on 0, and
    ///     <see cref="Change" /> tells the two apart.
    /// </summary>
    public byte IsDeleted { get; init; }

    /// <summary>
    ///     Who may <c>read</c> this resource, as subject strings — <c>user:{id}</c>,
    ///     <c>servicePrincipal:{id}</c>, or a userset such as <c>group:{id}#member</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Grantees, not members — the usersets are stored as usersets, and the reader
    ///             expands the caller instead.
    ///         </b> The column holds every principal
    ///         <c>ICheckGrain.ListRoleAssignmentsAsync(includeInherited: true)</c> reports at the
    ///         resource: a role written on the resource, its group, its subscription or its tenant,
    ///         since every role on <c>CyberCloudSchema</c> implies <c>reader</c> and <c>read</c> is
    ///         <c>Rel(reader)</c>. A group appears as <c>group:{id}#member</c> and is <b>not</b>
    ///         expanded to its members, because a membership change would then have to rewrite
    ///         every row the group can read — the fan-out the Leopard index exists to avoid. The
    ///         list query does the expansion on the other side: it reads the caller's closed usersets
    ///         from <c>IMembershipIndexGrain</c> in one read, docs/plan/07 § The Leopard index, and
    ///         asks <c>hasAny(access, [caller, …usersets])</c>. That is the same two halves
    ///         <c>ListObjectsEvaluator</c> uses, on a column instead of a walk.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Recomputed on every resource-changed event and on nothing else, which is less
    ///             than docs/plan/07 promises.
    ///         </b> That section wants the column "recomputed from
    ///         <c>ListObjects</c> on relation changes"; a role assigned on a resource group after its
    ///         resources were projected reaches the column when each resource next changes, not when
    ///         the role is written. docs/plan/08 § The resource-graph projection records that as owed,
    ///         with the shape that closes it.
    ///     </para>
    /// </remarks>
    public ImmutableArray<string> Access { get; init; } = [];

    public DateTimeOffset ProjectedAt { get; init; }
}
