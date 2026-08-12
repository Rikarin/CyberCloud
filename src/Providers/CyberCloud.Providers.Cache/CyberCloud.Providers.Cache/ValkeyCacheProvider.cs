namespace CyberCloud.Providers.Cache;

/// <summary>
///     Managed Valkey — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The catalogue: <i>"Valkey — <c>CyberCloud.Cache/redis</c> · M1 · 1.0
///         EM"</i>, on <c>spotahome/redis-operator</c> (ADR-010 clause 1). ADR-011 rejects Redis ≥ 7.4
///         as RSALv2 / SSPL; the product is <b>Valkey</b> (BSD-3) and the path is the Azure-parity one
///         — see the remarks on <c>ValkeyCaches</c> for why those two differ on purpose.
///     </para>
///     <para>
///         ⚠ <b>Six of docs/plan/12 § The pattern, once's eight pieces are here or next door, and the
///         two that are not are named rather than implied.</b> The chart (1), its generated schema
///         (2), this registration (3), the reconciler (4) and the conformance manifest (8) exist;
///         monitoring (6) is the operator's own exporter, switched by one annotated boolean rendered
///         into <c>spec.redis.exporter.enabled</c> and <c>spec.sentinel.exporter.enabled</c>, which is
///         the correction docs/plan/12 records — ask the operator for the scrape objects wherever it
///         accepts the request. <b>Piece 5 — credential provisioning into the tenant's Vault — is not
///         built</b>, because there is no OpenBao integration and <c>ISecretResolver</c>'s only
///         implementation refuses. What that costs is on <c>ValkeyCaches.RedisFailoverJson</c>, in the
///         place a reader hits it, and it costs more here than it did on the first provider.
///     </para>
///     <para>
///         ⚠ <b>Piece 7, backup, is not declared at all, and that is this type's answer to the
///         question docs/plan/12 leaves open.</b> That document asks whether piece 7 means <i>a policy
///         file some platform backup service reads</i> or <i>the service is backed up by whatever
///         mechanism its operator already provides</i>, and says the evidence points at the second.
///         This provider is the case the first reading has no answer for: <b>a cache is not backed
///         up</b>. <c>persistence</c> is a restart-survival setting, not a backup, and offering a
///         backup on a resource whose own product page says it is not durable would be the promise
///         docs/plan/12 warns produces "a support incident waiting to happen". A second service
///         declining piece 7 for a stated reason is worth more to that open question than a third one
///         implementing it.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately not declared besides:</b> no <c>regenerateKeys</c>, for the
///         reason on <c>ValkeyCaches.ListKeysAction</c>; no <c>SupportsSoftDelete</c>, because nothing
///         in the manager reads <c>SoftDeleteDays</c> and a recovery window the platform does not
///         honour is a promise made to the users who would test it — the same finding
///         <c>PostgresProvider</c> recorded, now with a second instance, which is what docs/plan/25
///         § R1 asks a provider to produce.
///     </para>
/// </remarks>
public sealed class ValkeyCacheProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => ValkeyCaches.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(ValkeyCaches.TypePath)
            .ApiVersion(ValkeyCaches.V2026, ValkeyCaches.Schema2026)
            .Reconciler<ValkeyCacheReconciler>()
            // ⚠ ONE UNIT OF `resources`, AND THE MEMORY METER THIS TYPE MOST OBVIOUSLY DRAWS IS STILL
            // NOT DECLARABLE. This is the SECOND provider to hit it, which is the point of recording it
            // again rather than pointing at the first.
            //
            // docs/plan/06 § Quota's families are vcpu, memoryGb, storageGb, publicIps, clusters and
            // resources, and `Meter(meter, amountPointer, fallback)` reserves the NUMBER it finds at a
            // JSON Pointer. A cache is the memoryGb family's headline consumer and the amount is not a
            // number in this body: `sizing.memory` is "4Gi", `persistence.size` is "8Gi", and the usual
            // case supplies neither because `sizing.preset` names them indirectly. Postgres reported
            // the same shape for vcpu, memoryGb and storageGb.
            //
            // Two providers, five meters, zero declarable: the missing piece is a UNIT on
            // MeterRegistration so a pointer can be read as a Kubernetes quantity, or a derived-amount
            // seam the registry can still generate from. Both are changes to CyberCloud.ResourceManager
            // and neither belongs in a provider PR — docs/plan/25 § R1's "commits to
            // CyberCloud.ResourceManager made by a provider PR" is the number this note keeps at zero
            // while still recording the finding.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                ValkeyCaches.ListKeysAction,
                ActionKind.Post,
                ValkeyCaches.ListKeysPermission,
                secret: true,
                response: ValkeyCaches.ListKeysResponse
            )
            .Display(
                "Valkey cache",
                "Valkey caches",
                shortName: "valkey",
                summary: "A managed Valkey cache with Sentinel failover, on spotahome/redis-operator. "
                + "Valkey rather than Redis, per ADR-011; every Redis client works against it."
            )
            // docs/plan/12 § The pattern, once, piece 1 — and ADR-012's fifth surface, which is the one
            // binding that ties this registration to charts/managed/valkey.
            .Chart(ValkeyCaches.ChartName)
            .SupportsTags()
            .RequiresCluster(ValkeyCaches.ClusterIdPointer);
    }
}
