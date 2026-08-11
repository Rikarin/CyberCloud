namespace CyberCloud.Providers.DBforPostgreSQL;

/// <summary>
///     Managed PostgreSQL — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"PostgreSQL — <c>CyberCloud.DBforPostgreSQL/servers</c>
///         · M1 · 1.2 EM"</i>, on CloudNativePG. This is the first provider that is a feature rather
///         than an instrument — <c>CyberCloud.Providers.Sample</c> is deliberately trivial and stays
///         that way (docs/plan/24 § Phase 1, docs/plan/25 § R1).
///     </para>
///     <para>
///         ⚠ <b>Six of docs/plan/12 § The pattern, once's eight pieces are here or next door, and the
///         two that are not are named rather than implied.</b> The chart (1), its generated schema
///         (2), this registration (3), the reconciler (4) and the conformance manifest (8) exist;
///         monitoring (6) is one annotated boolean rendered into the <c>Cluster</c> CR's
///         <c>spec.monitoring.enablePodMonitor</c>, which is the correction that document records, and
///         backup (7) is the <c>backup</c> block rendering into barman-cloud rather than the
///         unread <c>backup.yaml</c> the same document calls under-specified. <b>Piece 5 —
///         credential provisioning into the tenant's Vault — is not built</b>, because there is no
///         OpenBao integration and <c>ISecretResolver</c>'s only implementation refuses. What that
///         costs is on <c>PostgresServers.ClusterJson</c>, in the place a reader hits it.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately not declared:</b> no <c>servers/databases</c>,
///         <c>servers/roles</c> or <c>servers/firewallRules</c> — each is its own type with its own
///         reconciler, and declaring one with no reconciler puts a type in the registry that answers
///         <c>202</c> and converges nothing; no <c>regenerateKeys</c>, for the reason on
///         <c>PostgresServers.ListKeysAction</c>; and no <c>SupportsSoftDelete</c>, which is the one
///         omission this type has an argument against — see below.
///     </para>
///     <para>
///         ⚠ <b><c>SupportsSoftDelete</c> is exactly what docs/plan/06 § Tags, locks asks for on a type
///         carrying data</b> — <i>"a dropped production database is not a support ticket you want to
///         have to say no to"</i>, with 7 days named. It is not declared because nothing in the
///         manager reads <c>SoftDeleteDays</c>: <c>DeleteTearsDownTheDataPlaneAndTheResourceIsGone</c>
///         asserts the objects are gone and the name is released, and no recovery path exists to
///         restore them from. Declaring a recovery window the platform does not honour would be a
///         promise made to the one type whose users would test it. This is a
///         <c>CyberCloud.ResourceManager</c> gap that the first data-carrying provider surfaces, which
///         is the measurement docs/plan/25 § R1 asks a provider to produce.
///     </para>
/// </remarks>
public sealed class PostgresProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => PostgresServers.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(PostgresServers.TypePath)
            .ApiVersion(PostgresServers.V2026, PostgresServers.Schema2026)
            .Reconciler<PostgresServerReconciler>()
            // ⚠ ONE UNIT OF `resources`, AND THE THREE METERS THIS TYPE OBVIOUSLY DRAWS ARE NOT
            // DECLARED, WHICH IS A REGISTRY GAP RATHER THAN AN OVERSIGHT.
            //
            // docs/plan/06 § Quota's families are vcpu, memoryGb, storageGb, publicIps, clusters and
            // resources, and `Meter(meter, amountPointer, fallback)` reserves the NUMBER it finds at
            // a JSON Pointer. A PostgreSQL server draws all three of vcpu, memoryGb and storageGb —
            // and none of them is a number in this body: `storage.size` is "20Gi", `sizing.cpu` is
            // "500m", and the usual case supplies neither because `sizing.preset` names them
            // indirectly. So the amounts exist and are not addressable, and declaring
            // `Meter(StorageGb, "/properties/storage/size")` would reserve against a pointer holding
            // a string, which the manager cannot read as a quantity.
            //
            // Closing it needs one of: a unit on MeterRegistration so a pointer can be read as a
            // Kubernetes quantity; or a derived-amount seam the registry can still generate from.
            // Both are changes to CyberCloud.ResourceManager and neither belongs in a provider —
            // docs/plan/25 § R1's "commits to CyberCloud.ResourceManager made by a provider PR" is
            // the number this note exists to keep at zero while still recording the finding.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                PostgresServers.ListKeysAction,
                ActionKind.Post,
                PostgresServers.ListKeysPermission,
                secret: true,
                response: PostgresServers.ListKeysResponse
            )
            .Display(
                "PostgreSQL server",
                "PostgreSQL servers",
                shortName: "postgres",
                summary: "A managed PostgreSQL cluster on CloudNativePG, with replication, PgBouncer "
                + "and backup to the tenant's object store."
            )
            // docs/plan/12 § The pattern, once, piece 1 — and ADR-012's fifth surface, which is the
            // one binding that ties this registration to charts/managed/postgres. See the remarks on
            // PostgresServers.ChartName for why this is a declaration and not a render path.
            .Chart(PostgresServers.ChartName)
            .SupportsTags()
            .RequiresCluster(PostgresServers.ClusterIdPointer);
    }
}
