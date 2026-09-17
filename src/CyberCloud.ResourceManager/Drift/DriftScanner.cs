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
///     <c>cybercloud.io/reconcile-hash</c> — docs/plan/09 § The command builder. ⚠ For a resource
///     that co-writes rather than owns — a peering — it is the hash of its <i>fragment</i>, which is
///     what <c>ApplyOutcome.ReconcileHash</c> reports from a co-owned apply and what the object
///     carries as <c>cybercloud.io/fragment-hash.{writer}</c>; the owner's hash is over the owner's
///     body and would never match. One member serves both because no resource in the tree does both:
///     the day one owns objects and co-writes others, this gains a second hash.
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
///         ⚠ <b>A second writer on an object joins on a second key.</b> Issue #89's co-owned apply
///         (docs/plan/09 § A second writer on an object) puts a peering's slice on its parent's
///         <c>Vpc</c> under <c>cybercloud.io/fragment.{writer}</c> beside the owner's labels, so a
///         co-writing resource owns no object carrying its resource-id label. The join therefore
///         runs twice: the resource-id label finds what a resource owns, and
///         <see cref="ClusterObjectRecord.Fragments" /> finds what it co-writes. A co-writer with
///         neither is a stray; one whose <c>fragment-hash.{writer}</c> differs from its desired
///         hash is diverged; and a fragment whose writer no grain owns is an orphan naming the
///         slice rather than the object — the objects are their owners' and stay — which is the one
///         place a fragment left behind by a co-writer that never withdrew is found, because the
///         apply path carries every stored fragment forward verbatim and prunes none.
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
        var byWriter = new Dictionary<Guid, List<(ClusterObjectRecord Record, FragmentRecord Fragment)>>();

        foreach (var record in objects.IsDefault ? [] : objects) {
            // ⚠ NOT EVERY LABELLED OBJECT BELONGS TO A RESOURCE, and the join below assumes one does.
            // The platform writes a resource group's namespace itself and stamps it with a resource-id
            // DERIVED FROM THE GROUP — see NamespaceEnsurer, and the reason it is not the id of
            // whichever resource created it: that resource can be deleted while the namespace and
            // everything else in the group lives on. No resource grain will ever carry that GUID, so
            // without this line every namespace on the cluster becomes a permanent orphan finding —
            // "they are running and nothing is metering them" — about the one object on the cluster
            // that costs nothing and that the platform put there on purpose. A scan whose findings are
            // mostly its own normal operation is a scan nobody reads.
            if (KubeLabels.IsGroupScoped(record.ResourceType)) {
                continue;
            }

            if (!byResource.TryGetValue(record.ResourceId, out var list)) {
                list = [];
                byResource[record.ResourceId] = list;
            }

            list.Add(record);

            // ⚠ THE SECOND JOIN KEY. A co-writing resource — a peering — owns no object at all: its
            // slice rides on its parent's Vpc under a fragment annotation keyed by ITS GUID, beside
            // the owner's labels. Joining on the resource-id label alone would find nothing for it
            // and call every converged peering a stray, forever. So each fragment is indexed by its
            // writer too, and a resource is looked up in both.
            foreach (var fragment in record.Fragments.IsDefault ? [] : record.Fragments) {
                if (!byWriter.TryGetValue(fragment.Writer, out var slices)) {
                    slices = [];
                    byWriter[fragment.Writer] = slices;
                }

                slices.Add((record, fragment));
            }
        }

        var known = new HashSet<Guid>();
        var findings = ImmutableArray.CreateBuilder<DriftFinding>();

        foreach (var resource in expected.IsDefault ? [] : expected) {
            known.Add(resource.ResourceId);

            byResource.TryGetValue(resource.ResourceId, out var owned);
            byWriter.TryGetValue(resource.ResourceId, out var coWritten);

            if ((owned is null || owned.Count == 0) && (coWritten is null || coWritten.Count == 0)) {
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
                                + "labelled object on this cluster carries its resource-id or its "
                                + "fragment. Its objects were deleted outside the platform."
                        }
                    );
                }

                continue;
            }

            var diverged = (owned ?? [])
                .Where(x => !string.Equals(x.ReconcileHash, resource.DesiredHash, StringComparison.Ordinal))
                .Select(x => x.Target)
                .ToList();

            // ⚠ A co-writer's slice is judged by ITS hash, not the object's. The object's
            // reconcile-hash is the owner's, over the owner's body; the co-writer's is
            // fragment-hash.{writer}, over its fragment alone — which is what its ExpectedResource
            // carries as DesiredHash. Comparing a peering against the Vpc's hash would report every
            // peering as diverged the moment its parent re-rendered anything.
            diverged.AddRange(
                (coWritten ?? [])
                    .Where(x => !string.Equals(x.Fragment.Hash, resource.DesiredHash, StringComparison.Ordinal))
                    .Select(x => x.Record.Target)
            );

            if (diverged.Count > 0) {
                var total = (owned?.Count ?? 0) + (coWritten?.Count ?? 0);

                findings.Add(
                    new() {
                        Kind = DriftKind.Diverged,
                        ResourceId = resource.ResourceId,
                        ResourcePath = resource.ResourcePath,
                        Objects = [.. diverged],
                        Detail = $"{diverged.Count.ToString(CultureInfo.InvariantCulture)} of "
                            + $"{total.ToString(CultureInfo.InvariantCulture)} objects carry a "
                            + $"reconcile-hash or fragment-hash other than '{resource.DesiredHash}'."
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

        foreach (var pair in byWriter) {
            if (known.Contains(pair.Key)) {
                continue;
            }

            // ⚠ THE OTHER ORPHAN, AND THE ONLY THING THAT EVER FINDS IT. A co-writer withdraws its
            // fragment on teardown; a co-writer whose grain vanished without withdrawing — a silo
            // lost mid-delete, grain state wiped by hand — leaves a fragment that every other
            // co-writer's apply carries forward verbatim, forever, because the union is what keeps
            // the shared manager from pruning anyone. Nothing in the apply path prunes it (a
            // fragment is refused when corrupt, never dropped when stale), so the scan is where it
            // is named. The objects are the OWNER's and are not orphaned; the finding says which
            // slice is.
            findings.Add(
                new() {
                    Kind = DriftKind.Orphan,
                    ResourceId = pair.Key,
                    ResourcePath = pair.Value[0].Fragment.Path,
                    Objects = [.. pair.Value.Select(x => x.Record.Target)],
                    Detail = $"{pair.Value.Count.ToString(CultureInfo.InvariantCulture)} object(s) carry "
                        + $"a fragment of resource {pair.Key:D}'s and no resource grain owns it. The "
                        + "objects are their owners'; the fragment is a slice no co-writer will ever "
                        + "withdraw, re-applied by every other co-writer of the same object until it "
                        + "is removed by hand."
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
