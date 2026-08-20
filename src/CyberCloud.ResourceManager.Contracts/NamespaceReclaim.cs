using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Everything a namespace holds, of every kind, whether or not this platform wrote it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a different question from <see cref="IClusterObjectInventory" />, and the
///         difference is the whole safety of a namespace delete.</b> That one lists objects carrying
///         <c>cybercloud.io/managed-by=cybercloud</c>, because a drift scan compares what the platform
///         wrote against what it meant to write. Deleting a namespace asks the opposite question —
///         <i>is there anything here that is not ours</i> — and a label-selected listing cannot answer
///         it, because the objects it has to find are exactly the ones the selector excludes: a
///         tenant's own <c>PersistentVolumeClaim</c>, a <c>Secret</c> an operator added, a
///         <c>StatefulSet</c> from a chart nobody registered. Reusing the drift inventory here gives a
///         listing that is empty precisely when the delete is most dangerous.
///     </para>
///     <para>
///         ⚠ <b>The shipped implementation refuses, for the same reason the drift inventory's does.</b>
///         An empty listing says <i>this namespace holds nothing</i>, which is a licence to run a
///         recursive delete over a tenant's live data. A failure says <i>do not conclude anything</i>.
///         <c>UnavailableNamespaceInventory</c> is the one that ships.
///     </para>
///     <para>
///         ⚠ <b>What a real implementation costs, stated so that nobody starts it by accident.</b>
///         There is no single Kubernetes call that lists a namespace's contents. It is a discovery of
///         every served <c>APIResource</c> that is namespaced — the built-ins and every CRD the
///         cluster happens to have — followed by a list per kind, and it has to stay correct as CRDs
///         come and go. The informer bridge of docs/plan/09 § Observing does not deliver it:
///         <c>IClusterConnectionGrain.WatchAsync</c> watches one named <c>GroupVersionKind</c> under a
///         label selector, which is the drift inventory's shape and not this one's.
///     </para>
/// </remarks>
public interface INamespaceInventory {
    /// <summary>
    ///     Every namespaced object in <paramref name="ns" /> on <paramref name="clusterId" />.
    /// </summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The occupants. ⚠ It must be the <i>whole</i> namespace: a partial listing that omits a kind
    ///     reads as an absence, and an absence here authorizes a delete.
    /// </returns>
    Task<Result<ImmutableArray<NamespaceOccupant>>> ListAllAsync(
        Guid clusterId,
        string ns,
        CancellationToken cancellationToken = default
    );
}

/// <summary>One object found in a namespace, as the reclaim decision sees it.</summary>
/// <remarks>
///     ⚠ Deliberately smaller than <see cref="ClusterObjectRecord" />. A reclaim decision has to name
///     what it refused over and to tell the platform's objects from everybody else's, and nothing
///     more. Carrying the resource id or the reconcile hash would suggest the decision joins against
///     the control plane, and that join is blind to exactly the unlabelled objects this type exists
///     to see.
/// </remarks>
public readonly record struct NamespaceOccupant {
    /// <summary>The kind, for the refusal message. For example <c>PersistentVolumeClaim</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The object's name.</summary>
    public required string Name { get; init; }

    /// <summary>Its labels, as read from the cluster.</summary>
    public required ImmutableDictionary<string, string> Labels { get; init; }

    /// <summary>
    ///     Whether the object carries <c>cybercloud.io/managed-by=cybercloud</c> — whether this
    ///     platform wrote it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see langword="false" /> does not mean "not ours". It means "nobody can tell".</b> The
    ///     <c>PersistentVolumeClaim</c>s a <c>StatefulSet</c>'s <c>volumeClaimTemplate</c> creates are
    ///     made by the StatefulSet controller from a template the platform renders <i>without</i>
    ///     labels — <c>KubeCommandBuilder</c> injects the seven into the top-level
    ///     <c>metadata.labels</c> and does not walk into a nested template — so they carry none of
    ///     ADR-013's seven. docs/plan/08 § Soft delete makes those exact claims the thing a restore
    ///     restores from. An unmanaged object is therefore either somebody else's or the most
    ///     safety-critical thing in the namespace, and <see cref="NamespaceReclaim" /> treats both the
    ///     same way: it stops.
    /// </remarks>
    public bool IsManaged =>
        Labels.TryGetValue(KubeLabels.ManagedBy, out var value)
        && string.Equals(value, KubeLabels.ManagedByValue, StringComparison.Ordinal);
}

/// <summary>
///     Whether a resource group's namespace on one cluster may be deleted — the evidence rule, as a
///     value that cannot be forged.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The whole point of this type is that <see cref="Deletable" /> is unreachable except
///         through <see cref="Decide" />.</b> The constructor is private and there is no initializer,
///         so a caller cannot assemble a permissive verdict, and <c>default(NamespaceReclaim)</c>
///         carries <see cref="Deletable" /> <see langword="false" /> and an empty
///         <see cref="Namespace" /> that <c>NamespaceEnsurer.DeleteAsync</c> refuses to match. The
///         delete is a recursive delete of a tenant's live data; the difference between "a bool
///         somebody passed" and "a verdict a rule produced" is the difference between a review item
///         and a compile-time one.
///     </para>
///     <para>
///         ⚠ <b>The rule is "completely empty", and not "empty of foreign objects".</b> The weaker
///         rule — delete when nothing lacks <c>managed-by</c> — is the one that destroys a tenant,
///         three ways at once. It deletes the objects of a resource whose membership was never
///         recorded (docs/plan/08 § Soft delete: nothing in the write path calls
///         <c>IResourceGroupGrain.BeginCreateAsync</c>, so every group's member list is empty and
///         "empty" is not evidence of anything). It deletes the objects of a resource that is live and
///         simply not being deleted. And it deletes the volumes of every resource inside its recovery
///         window, because a parked resource's data plane <i>is</i> torn down and its claims are what
///         a restore restores from — turning every restore in the group into a lie. Requiring the
///         namespace to hold nothing at all closes all three with one condition, and it is the
///         condition an operator can check by eye.
///     </para>
///     <para>
///         ⚠ <b>What this rule does <i>not</i> close is the race, and it cannot from here.</b> A
///         resource created between the listing and the delete has its objects destroyed. Closing that
///         needs the group to stop accepting members before the evidence is read — a group-delete
///         choreography that seals the group first, which docs/plan/06 § Two-phase create's reverse
///         order describes and which no method on <c>IResourceGroupGrain</c> yet performs.
///     </para>
/// </remarks>
public readonly struct NamespaceReclaim : IEquatable<NamespaceReclaim> {
    /// <summary>How many occupants a refusal names before it stops listing them.</summary>
    /// <remarks>
    ///     A refusal is read by a human deciding what to do about a namespace, and a message carrying
    ///     four hundred object names is one nobody reads. The count is always exact; the names are a
    ///     sample.
    /// </remarks>
    public const int NamedOccupants = 5;

    NamespaceReclaim(
        bool deletable,
        bool operatorReclaimable,
        Guid clusterId,
        string ns,
        ImmutableArray<string> refusals
    ) {
        Deletable = deletable;
        OperatorReclaimable = operatorReclaimable;
        ClusterId = clusterId;
        Namespace = ns;
        Refusals = refusals;
    }

    /// <summary>The namespace holds nothing and the group holds no members. Only then.</summary>
    public bool Deletable { get; }

    /// <summary>
    ///     The platform is finished with this namespace and something else is still in it, so a person
    ///     has to decide.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the honest answer for every group that ever ran a stateful type, and it is not
    ///     a rare case.</b> The group holds no members and every remaining object is unlabelled — which
    ///     is what a purged <c>StatefulSet</c>-backed resource leaves behind, because docs/plan/08
    ///     § Soft delete records that a purge still leaves the volumes and <c>IResourceReconciler</c>
    ///     has no member that asks for them to go. So the namespace is not empty, never becomes empty,
    ///     and <see cref="Deletable" /> is never <see langword="true" /> for it. Reporting that to an
    ///     operator is the feature; deleting it is a data-loss bug wearing a cleanup's clothes.
    /// </remarks>
    public bool OperatorReclaimable { get; }

    /// <summary>The cluster the verdict was reached about.</summary>
    public Guid ClusterId { get; }

    /// <summary>The namespace the verdict was reached about.</summary>
    public string Namespace { get; }

    /// <summary>Why not, in the order the evidence was weighed. Empty when <see cref="Deletable" />.</summary>
    public ImmutableArray<string> Refusals { get; }

    /// <summary>
    ///     Weighs the two pieces of evidence and answers.
    /// </summary>
    /// <param name="clusterId">The cluster the namespace is on.</param>
    /// <param name="ns">The namespace — <c>ReconcileDriver.NamespaceFor</c>'s output.</param>
    /// <param name="members">
    ///     Every member of the group, including the ones in
    ///     <see cref="ProvisioningState.Deleting" /> — <c>IResourceGroupGrain.ListAsync</c>.
    /// </param>
    /// <param name="occupants">
    ///     Every object in the namespace, of every kind — <see cref="INamespaceInventory.ListAllAsync" />.
    ///     ⚠ A partial listing produces a wrong <see langword="true" />, which is why the seam's only
    ///     shipped implementation fails rather than returning an empty array.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>Both pieces are required and neither is redundant, which is easy to get wrong in
    ///     either direction.</b> The member list alone is worthless today, because nothing records
    ///     membership. The namespace listing alone would authorize a delete during the seconds between
    ///     a group's last object going and its grain state being cleared. Together they say: the
    ///     control plane believes the group is empty, <i>and</i> the cluster agrees.
    /// </remarks>
    public static NamespaceReclaim Decide(
        Guid clusterId,
        string ns,
        IReadOnlyList<ResourceGroupMember> members,
        ImmutableArray<NamespaceOccupant> occupants
    ) {
        ArgumentException.ThrowIfNullOrEmpty(ns);
        ArgumentNullException.ThrowIfNull(members);

        var refusals = ImmutableArray.CreateBuilder<string>();

        if (members.Count > 0) {
            var deleting = members.Count(x => x.State == ProvisioningState.Deleting);

            refusals.Add(
                $"The resource group still holds {members.Count} member(s), {deleting} of them "
                + "Deleting, so the control plane has not finished with this namespace. "
                + "docs/plan/06 § Two-phase create keeps a resource whose teardown failed listed and "
                + "Deleting rather than silently gone, and a namespace delete would take its objects "
                + "anyway: "
                + Sample(members.Select(x => x.CanonicalPath))
            );
        }

        if (occupants.Length > 0) {
            var managed = occupants.Count(x => x.IsManaged);
            var foreign = occupants.Length - managed;

            refusals.Add(
                $"The namespace '{ns}' holds {occupants.Length} object(s) — {managed} written by this "
                + $"platform and {foreign} carrying no {KubeLabels.ManagedBy}. Deleting a namespace is "
                + "a recursive delete of all of them, and an unlabelled object is either somebody "
                + "else's or a volume claim a StatefulSet made, which docs/plan/08 § Soft delete keeps "
                + "on purpose so that a restore has something to restore from: "
                + Sample(occupants.Select(x => $"{x.Kind}/{x.Name}"))
            );
        }

        return new(
            refusals.Count == 0,
            members.Count == 0 && occupants.Length > 0 && occupants.All(x => !x.IsManaged),
            clusterId,
            ns,
            refusals.ToImmutable()
        );
    }

    /// <summary>Every refusal, on one line, for a message or a log.</summary>
    public string Explain() =>
        Refusals.IsDefaultOrEmpty ? string.Empty : string.Join(" ", Refusals);

    /// <inheritdoc />
    public bool Equals(NamespaceReclaim other) =>
        Deletable == other.Deletable
        && OperatorReclaimable == other.OperatorReclaimable
        && ClusterId == other.ClusterId
        && string.Equals(Namespace, other.Namespace, StringComparison.Ordinal)
        && Explain().Equals(other.Explain(), StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NamespaceReclaim other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Deletable, OperatorReclaimable, ClusterId, Namespace, Refusals.Length);

    /// <summary>Whether two verdicts agree.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    public static bool operator ==(NamespaceReclaim left, NamespaceReclaim right) => left.Equals(right);

    /// <summary>Whether two verdicts disagree.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    public static bool operator !=(NamespaceReclaim left, NamespaceReclaim right) => !left.Equals(right);

    static string Sample(IEnumerable<string> names) {
        var taken = names.Order(StringComparer.Ordinal).Take(NamedOccupants + 1).ToList();

        return taken.Count > NamedOccupants
            ? string.Join(", ", taken.Take(NamedOccupants)) + ", and more"
            : string.Join(", ", taken);
    }
}
