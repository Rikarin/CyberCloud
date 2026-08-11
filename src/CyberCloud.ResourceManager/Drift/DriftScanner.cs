using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Drift;

/// <summary>
///     What the resource manager believes a cluster should be holding — the other side of the drift
///     diff.
/// </summary>
/// <param name="ResourceId">The resource's GUID.</param>
/// <param name="ResourcePath">Its address.</param>
/// <param name="DesiredHash">
///     The hash of its desired body, which is what the objects carry as
///     <c>cybercloud.io/reconcile-hash</c> — docs/plan/09 § The command builder.
/// </param>
/// <param name="ProvisioningState">
///     ⚠ Load-bearing for the diff. A resource in <see cref="ProvisioningState.Creating" /> whose
///     objects are not there yet is not a stray — it is a resource being created.
/// </param>
public readonly record struct ExpectedResource(
    Guid ResourceId,
    string ResourcePath,
    string DesiredHash,
    ProvisioningState ProvisioningState
);

/// <summary>
///     The per-cluster drift diff. docs/plan/08 § The reconcile loop.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § The reconcile loop:
///         <i>
///             "drift detection is <b>per-cluster, not per-resource</b>. The cluster's informer bridge
///             holds a live view; an hourly per-cluster reminder diffs labelled objects against the
///             resource grains that own them (the <c>cybercloud.io/resource-id</c> label from ADR-013
///             is what makes this a hash join rather than a scan) and pokes only what diverged. It also
///             surfaces the two things nothing else would find: <b>orphans</b> (labelled objects whose
///             resource grain is gone — deleted and billed for) and <b>strays</b> (resources whose
///             objects vanished — someone <c>kubectl delete</c>d production)."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>What is implemented and what is not.</b> The <i>diff</i> is here, is pure, and is
///         tested: given a cluster's labelled objects and the resources that should own them, it
///         computes orphans, strays and divergences by hash join on the resource-id label. The
///         <i>inventory</i> — the live informer view of a real API server — is
///         <see cref="IClusterObjectInventory" />, and the shipped implementation refuses rather than
///         reporting an empty cluster. So this scanner cannot run against a real cluster in this
///         build, and the half that is missing is named rather than stubbed silently.
///     </para>
///     <para>
///         ⚠ <b>The scan reports; it does not repair.</b> "Pokes only what diverged" is a second step
///         — re-driving the affected resources — and it is deliberately not here: a repair loop that
///         acted on a partial inventory would delete objects it merely failed to see. Reporting first
///         means the numbers can be watched before anything acts on them.
///     </para>
/// </remarks>
public sealed class DriftScanner(IClock clock) {
    /// <summary>
    ///     Diffs a cluster's labelled objects against the resources that should own them.
    /// </summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="objects">
    ///     Every object carrying <c>cybercloud.io/managed-by=cybercloud</c>, from
    ///     <see cref="IClusterObjectInventory" />. ⚠ Must be the <i>whole</i> inventory: a partial one
    ///     reports the missing part as strays.
    /// </param>
    /// <param name="expected">The resources the manager believes are placed on this cluster.</param>
    /// <returns>
    ///     What differs. An empty <see cref="DriftReport.Findings" /> means the cluster and the grains
    ///     agree.
    /// </returns>
    public DriftReport Scan(
        Guid clusterId,
        ImmutableArray<ClusterObjectRecord> objects,
        ImmutableArray<ExpectedResource> expected
    ) {
        var byResource = new Dictionary<Guid, List<ClusterObjectRecord>>();

        foreach (var record in objects.IsDefault ? [] : objects) {
            if (!byResource.TryGetValue(record.ResourceId, out var list)) {
                list = [];
                byResource[record.ResourceId] = list;
            }

            list.Add(record);
        }

        var known = new HashSet<Guid>();
        var findings = ImmutableArray.CreateBuilder<DriftFinding>();

        foreach (var resource in expected.IsDefault ? [] : expected) {
            known.Add(resource.ResourceId);

            if (!byResource.TryGetValue(resource.ResourceId, out var owned) || owned.Count == 0) {
                // ⚠ A resource that is mid-flight is not a stray. Creating means the reconciler has
                // not applied yet; Deleting means it is on its way out and its objects going is the
                // goal. Reporting either would produce a scan whose findings are mostly its own
                // platform's normal operation, which is a scan nobody reads.
                if (resource.ProvisioningState is ProvisioningState.Succeeded or ProvisioningState.Failed) {
                    findings.Add(
                        new() {
                            Kind = DriftKind.Stray,
                            ResourceId = resource.ResourceId,
                            ResourcePath = resource.ResourcePath,
                            Objects = [],
                            Detail = $"'{resource.ResourcePath}' is {resource.ProvisioningState} and no "
                                + "labelled object on this cluster carries its resource-id. Its objects "
                                + "were deleted outside the platform."
                        }
                    );
                }

                continue;
            }

            var diverged = owned
                .Where(x => !string.Equals(x.ReconcileHash, resource.DesiredHash, StringComparison.Ordinal))
                .ToImmutableArray();

            if (diverged.Length > 0) {
                findings.Add(
                    new() {
                        Kind = DriftKind.Diverged,
                        ResourceId = resource.ResourceId,
                        ResourcePath = resource.ResourcePath,
                        Objects = [.. diverged.Select(x => x.Target)],
                        Detail = $"{diverged.Length.ToString(CultureInfo.InvariantCulture)} of "
                            + $"{owned.Count.ToString(CultureInfo.InvariantCulture)} objects carry a "
                            + $"reconcile-hash other than '{resource.DesiredHash}'."
                    }
                );
            }
        }

        foreach (var pair in byResource) {
            if (known.Contains(pair.Key)) {
                continue;
            }

            // ⚠ An orphan is the expensive one: labelled objects whose resource grain is gone. Nobody
            // is billed for it and nobody is watching it, and it keeps running.
            findings.Add(
                new() {
                    Kind = DriftKind.Orphan,
                    ResourceId = pair.Key,
                    ResourcePath = pair.Value[0].ResourcePath,
                    Objects = [.. pair.Value.Select(x => x.Target)],
                    Detail = $"{pair.Value.Count.ToString(CultureInfo.InvariantCulture)} labelled "
                        + $"objects carry resource-id {pair.Key:D} and no resource grain owns it. They "
                        + "are running and nothing is metering them."
                }
            );
        }

        return new() {
            ClusterId = clusterId,
            ScannedAt = clock.UtcNow,
            ObjectsSeen = objects.IsDefault ? 0 : objects.Length,
            ResourcesSeen = expected.IsDefault ? 0 : expected.Length,
            Findings = findings.ToImmutable()
        };
    }
}
