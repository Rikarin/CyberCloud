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
///     The hash of its desired body, which is what the objects it <b>owns</b> carry as
///     <c>cybercloud.io/reconcile-hash</c> — docs/plan/09 § The command builder. ⚠ Judges owned
///     objects only. What a resource co-writes is judged by <paramref name="Fragments" />, never by
///     this: a co-writer's slice carries <c>fragment-hash.{writer}</c>, over that fragment alone, and
///     the owner's <c>reconcile-hash</c> beside it is over the owner's body.
/// </param>
/// <param name="ProvisioningState">
///     ⚠ Load-bearing for the diff. A resource in <see cref="ProvisioningState.Creating" /> whose
///     objects are not there yet is not a stray — it is a resource being created.
/// </param>
/// <param name="Fragments">
///     The fragments this resource is expected to hold on objects it does not own — one per object,
///     each with the hash the co-owned apply reported for it. Empty for the resource that owns
///     everything it applies, which is every type but a peering.
///     <para>
///         ⚠ <b>One hash per object, because the first co-writer in the tree writes two.</b> A
///         peering's two fragments are mirror images — each names the <i>other</i> network's
///         <c>Vpc</c>, each route points at the other end of the link — so they hash differently, and
///         a single desired hash held against both called every converged peering diverged on every
///         scan (the #31 review's finding; the real-cluster test had compared the last apply's hash
///         and asserted only strays and orphans). Keyed by the object, the scan also tells the two
///         states a single hash could not: an expected fragment whose object carries none of this
///         writer's is a slice that went missing, and a fragment of this writer's on an object it is
///         not expected on is a slice left behind — a peering whose <c>remoteNetwork</c> was changed
///         under a declared-but-unenforced <c>Immutable</c> leaves one on the old remote, and this is
///         the only place it is named. Both are <see cref="DriftKind.Diverged" />, since the grain
///         exists and the objects are their owners'.
///     </para>
/// </param>
public readonly record struct ExpectedResource(
    Guid ResourceId,
    string ResourcePath,
    string DesiredHash,
    ProvisioningState ProvisioningState,
    ImmutableArray<ExpectedFragment> Fragments = default
);

/// <summary>One slice a co-writing resource is expected to hold on one object it does not own.</summary>
/// <param name="Target">The owner's object. Compared as a value: kind, namespace and name.</param>
/// <param name="Hash">
///     The <c>cybercloud.io/fragment-hash.{writer}</c> the object should carry — what
///     <c>ApplyOutcome.ReconcileHash</c> reported for the co-owned apply onto this object.
/// </param>
public readonly record struct ExpectedFragment(ObjectRef Target, string Hash);

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
///         neither is a stray; one whose <c>fragment-hash.{writer}</c> on an object differs from
///         the hash <see cref="ExpectedResource.Fragments" /> expects <i>on that object</i> is
///         diverged, as is one expected on an object that carries none of its slices or found on
///         an object it is not expected on; and a fragment whose writer no grain owns is an orphan
///         naming the slice rather than the object — the objects are their owners' and stay. The
///         apply path carries every stored fragment forward verbatim and prunes none, so the scan is
///         the one place a fragment nobody will withdraw is found: as an orphan when its writer's
///         grain is gone, as a diverged slice left behind when the grain is still there and no
///         longer places it on that object.
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
                .Select(static x => x.Target)
                .ToList();

            var reasons = new List<string>();

            if (diverged.Count > 0) {
                reasons.Add(
                    $"{diverged.Count.ToString(CultureInfo.InvariantCulture)} of "
                    + $"{(owned?.Count ?? 0).ToString(CultureInfo.InvariantCulture)} owned objects carry a "
                    + $"reconcile-hash other than '{resource.DesiredHash}'"
                );
            }

            // ⚠ A co-writer's slice is judged by ITS hash ON THAT OBJECT, not by the object's and
            // not by one hash for every object. The object's reconcile-hash is the owner's, over the
            // owner's body; the co-writer's is fragment-hash.{writer}, over its fragment alone — and
            // a peering's two fragments are mirror images with two hashes, so a single desired hash
            // held against both called every converged peering diverged on every scan. The join is
            // therefore per object, which also names the slice that went missing from an object it
            // is expected on and the slice left behind on one it no longer is.
            var expectedFragments = resource.Fragments.IsDefault ? [] : resource.Fragments;
            var missing = new List<ObjectRef>();
            var leftBehind = new List<ObjectRef>();
            var changed = new List<ObjectRef>();

            foreach (var (record, fragment) in coWritten ?? []) {
                var onThisObject = expectedFragments.Where(x => x.Target == record.Target).ToList();

                if (onThisObject.Count == 0) {
                    leftBehind.Add(record.Target);
                } else if (!onThisObject.Exists(x => string.Equals(x.Hash, fragment.Hash, StringComparison.Ordinal))) {
                    changed.Add(record.Target);
                }
            }

            foreach (var slice in expectedFragments) {
                if (!(coWritten ?? []).Exists(x => x.Record.Target == slice.Target)) {
                    missing.Add(slice.Target);
                }
            }

            if (changed.Count > 0) {
                reasons.Add(
                    $"{changed.Count.ToString(CultureInfo.InvariantCulture)} of "
                    + $"{expectedFragments.Length.ToString(CultureInfo.InvariantCulture)} co-written objects carry a "
                    + "fragment-hash other than the one expected on that object"
                );
            }

            if (missing.Count > 0) {
                reasons.Add(
                    $"{missing.Count.ToString(CultureInfo.InvariantCulture)} of "
                    + $"{expectedFragments.Length.ToString(CultureInfo.InvariantCulture)} co-written objects carry no "
                    + "fragment of this resource's — the slice went missing from an object it is expected on"
                );
            }

            if (leftBehind.Count > 0) {
                reasons.Add(
                    $"{leftBehind.Count.ToString(CultureInfo.InvariantCulture)} object(s) carry a fragment of this "
                    + "resource's that its desired state does not place there — a slice left behind, which no "
                    + "co-writer will withdraw and every other co-writer's apply carries forward"
                );
            }

            diverged.AddRange(changed);
            diverged.AddRange(missing);
            diverged.AddRange(leftBehind);

            if (diverged.Count > 0) {
                findings.Add(
                    new() {
                        Kind = DriftKind.Diverged,
                        ResourceId = resource.ResourceId,
                        ResourcePath = resource.ResourcePath,
                        Objects = [.. diverged],
                        Detail = string.Join("; ", reasons) + "."
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
                    Objects = [.. pair.Value.Select(static x => x.Target)],
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
                    Objects = [.. pair.Value.Select(static x => x.Record.Target)],
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
