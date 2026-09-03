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
///         ⚠ <b>Every implementation refuses rather than reporting an empty namespace, and that rule
///         did not relax when a real one arrived.</b> An empty listing says <i>this namespace holds
///         nothing</i>, which is a licence to run a recursive delete over a tenant's live data. A
///         failure says <i>do not conclude anything</i>. <c>ConnectionNamespaceInventory</c> is the
///         one that ships and it refuses on a cluster it cannot reach, on a kind it could not list,
///         and on a listing it could not finish. <c>UnavailableNamespaceInventory</c> stays in the
///         tree for a host that wants the seam explicitly unavailable.
///     </para>
///     <para>
///         ⚠ <b>What it costs, stated so that nobody puts it on a hot path.</b> There is no single
///         Kubernetes call that lists a namespace's contents. It is a discovery of every served
///         <c>APIResource</c> that is namespaced — the built-ins and every CRD the cluster happens to
///         have — followed by a list per kind, so on a busy cluster it is a hundred round trips. The
///         informer bridge of docs/plan/09 § Observing does not deliver it:
///         <c>IClusterConnectionGrain.WatchAsync</c> watches one named <c>GroupVersionKind</c> under a
///         label selector, which is the drift inventory's shape and not this one's. The work lives
///         behind <c>IKubeClusterConnection.ListNamespaceAsync</c>, because discovery is a Kubernetes
///         concern and this assembly may not see one.
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
    ///     ⚠ <b><see langword="false" /> does not mean "not ours". It means "nobody can tell".</b> An
    ///     unmanaged object is either somebody else's or the most safety-critical thing in the
    ///     namespace, and <see cref="NamespaceReclaim" /> treats both the same way: it stops.
    ///     <para>
    ///         ⚠ <b>The <c>PersistentVolumeClaim</c>s a <c>StatefulSet</c>'s
    ///         <c>volumeClaimTemplate</c> creates used to be the headline example of that, and as of
    ///         <c>IKubeCommandBuilder.WithTemplateLabels</c> they are not — with two qualifications
    ///         that matter here.</b> A claim created from a template stamped with
    ///         <see cref="KubeLabels.LifetimeStable" /> carries <c>managed-by</c> and reads as
    ///         <see cref="IsManaged" />. But the StatefulSet controller labels a claim once, when it
    ///         creates it, and never revisits one — so <b>every claim that already exists is still
    ///         unlabelled and always will be</b> — and only the families that declared a template
    ///         path render one. docs/plan/08 § Soft delete makes those exact claims the thing a
    ///         restore restores from, so the conservative reading of an unlabelled claim is the one
    ///         to keep.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That change moved a namespace of leftover volumes out of
    ///         <see cref="NamespaceReclaim.OperatorReclaimable" /> and into neither verdict, and it
    ///         has been repaired.</b> The flag used to require <i>every</i> occupant to be unmanaged,
    ///         so a group whose only remaining objects were its own now-labelled claims satisfied
    ///         neither it nor <see cref="NamespaceReclaim.Deletable" /> and reported as a plain
    ///         refusal. It now asks only what its own remarks always said it asked — the control
    ///         plane has no members left and the cluster still holds something — and who wrote the
    ///         leftovers informs the refusal text instead of gating the verdict.
    ///         <c>src/Providers/README.md § Namespaces</c>.
    ///     </para>
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
///         ⚠ <b>"Nothing at all" turned out to be literally unsatisfiable, and the rule is now
///         "nothing but what Kubernetes itself puts in every namespace" — <see cref="IsAmbient" /> is
///         the exception list and it is three entries long.</b> The gap was invisible while
///         <see cref="INamespaceInventory" /> had no implementation: with nothing producing
///         occupants, the only lists the rule was ever weighed against were the empty ones tests
///         supplied, and against a real API server <see cref="Deletable" /> would have been
///         <see langword="false" /> forever. None of the three failures above reopens — every entry
///         on that list is an object no restore reads and no tenant can own.
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

    /// <summary>
    ///     Whether an occupant is one Kubernetes puts in <b>every</b> namespace by itself, and
    ///     therefore not evidence that anything is using the namespace.
    /// </summary>
    /// <param name="occupant">The object found in the namespace.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS PREDICATE EXISTS BECAUSE "THE NAMESPACE HOLDS NOTHING AT ALL" IS
    ///         UNREACHABLE AGAINST A REAL API SERVER, AND THAT WAS NOT KNOWN UNTIL SOMETHING COULD
    ///         LIST.</b> The rule was written against an inventory that refused, so nothing ever
    ///         produced an occupant list to test it with. Two controllers in every conformant
    ///         cluster make the literal rule impossible to satisfy: the service-account controller
    ///         creates <c>ServiceAccount/default</c> in every namespace and <b>recreates it if it is
    ///         deleted</b>, and the root-CA publisher creates
    ///         <c>ConfigMap/kube-root-ca.crt</c> in every namespace and does the same. A namespace
    ///         that has finished being used therefore holds exactly these two, forever, and
    ///         <see cref="Deletable" /> would never once be <see langword="true" /> in production
    ///         while being <see langword="true" /> in every unit test that supplied an empty array.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The allowance is by kind AND name, never by kind alone, for everything the
    ///         control plane names.</b> <c>default</c> and <c>kube-root-ca.crt</c> are names
    ///         Kubernetes reserves, so no tenant object can wear one; a kind-wide exemption for
    ///         <c>ServiceAccount</c> or <c>ConfigMap</c> would hide a tenant's own, which is exactly
    ///         the class of object this whole file exists to refuse over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Event</c> is the one kind-wide entry, and it is the one kind that carries no
    ///         state.</b> An event is a timestamped sentence about something that already happened,
    ///         the API server expires it on its own within the hour, and nothing restores from one.
    ///         Without this entry a group could only be reclaimed during the gaps between its own
    ///         teardown events expiring, which is a delete that succeeds or fails depending on how
    ///         long the operator waited. It covers both spellings — core <c>v1</c> and
    ///         <c>events.k8s.io/v1</c> are two views of one store and
    ///         <see cref="NamespaceOccupant" /> carries only the kind, which is <c>Event</c> in both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is deliberately NOT here: the <c>default-token-*</c> <c>Secret</c>.</b>
    ///         Kubernetes stopped auto-creating it in 1.24, so on a supported cluster there is none
    ///         to exempt — and the only rule that would match one is a name prefix, which a tenant
    ///         can occupy. A <c>Secret</c> is the object in this list it would hurt most to delete by
    ///         accident, so an old cluster that still has one reports as an occupant and a person
    ///         decides. <c>src/Providers/README.md § Namespaces</c>.
    ///     </para>
    /// </remarks>
    public static bool IsAmbient(NamespaceOccupant occupant) =>
        occupant.Kind switch {
            "Event" => true,
            "ServiceAccount" => string.Equals(occupant.Name, "default", StringComparison.Ordinal),
            "ConfigMap" => string.Equals(occupant.Name, "kube-root-ca.crt", StringComparison.Ordinal),
            _ => false
        };

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

    /// <summary>
    ///     The namespace holds nothing but what Kubernetes puts in every namespace, and the group
    ///     holds no members. Only then.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>"Nothing but" and not "nothing", and the difference is <see cref="IsAmbient" />.</b>
    ///     The rule as first written was "nothing at all", which no conformant cluster can satisfy —
    ///     see that predicate for the two controllers that make it so. Everything else about the rule
    ///     is unchanged: one tenant object, of any kind, labelled or not, and this is
    ///     <see langword="false" />.
    /// </remarks>
    public bool Deletable { get; }

    /// <summary>
    ///     The platform is finished with this namespace and something else is still in it, so a person
    ///     has to decide.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the honest answer for every group that ever ran a stateful type, and it is not
    ///     a rare case.</b> The group holds no members and the namespace still holds something —
    ///     which is what a purged <c>StatefulSet</c>-backed resource leaves behind, because
    ///     docs/plan/08 § Soft delete records that a purge still leaves the volumes. So the namespace
    ///     is not empty, never becomes empty, and <see cref="Deletable" /> is never
    ///     <see langword="true" /> for it. Reporting that to an operator is the feature; deleting it
    ///     is a data-loss bug wearing a cleanup's clothes.
    ///     <para>
    ///         ⚠ <b>It does not ask who wrote the leftovers, and it used to.</b> Requiring every
    ///         occupant to be unmanaged was equivalent while nothing this platform wrote outlived its
    ///         resource; once a <c>volumeClaimTemplate</c>'s claims started carrying
    ///         <c>managed-by</c>, a group whose only remains were its own disks satisfied neither
    ///         verdict and came back as an unclassified refusal. See <see cref="Decide" />.
    ///     </para>
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

        // ⚠ THE OCCUPANTS THAT COUNT, WHICH IS NOT ALL OF THEM. See IsAmbient: two objects are put
        // in every namespace by Kubernetes itself and recreated when deleted, so weighing them would
        // make Deletable unreachable on every real cluster while leaving it reachable in every test
        // that passes an empty array. `occupants` may be `default` — a caller's unassigned field —
        // and `IsDefaultOrEmpty` is what keeps that from throwing here rather than at the delete.
        var significant = occupants.IsDefaultOrEmpty
            ? []
            : occupants.Where(x => !IsAmbient(x)).ToImmutableArray();

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

        if (significant.Length > 0) {
            var managed = significant.Count(x => x.IsManaged);
            var foreign = significant.Length - managed;

            refusals.Add(
                $"The namespace '{ns}' holds {significant.Length} object(s) — {managed} written by "
                + $"this platform and {foreign} carrying no {KubeLabels.ManagedBy}. Deleting a "
                + "namespace is a recursive delete of all of them, and an unlabelled object is either "
                + "somebody else's or a volume claim a StatefulSet made, which docs/plan/08 § Soft "
                + "delete keeps on purpose so that a restore has something to restore from: "
                + Sample(significant.Select(x => $"{x.Kind}/{x.Name}"))
            );
        }

        return new(
            refusals.Count == 0,

            // ⚠ THE PREDICATE THAT USED TO REQUIRE EVERY OCCUPANT TO BE UNMANAGED, AND WHY THAT WAS
            // WRONG RATHER THAN MERELY NARROW. It was written when nothing this platform wrote could
            // outlive its resource, so "the leftovers are all somebody else's" and "the platform is
            // finished here" were the same sentence. `WithTemplateLabels` ended that: a purged
            // StatefulSet-backed resource now leaves behind its own LABELLED claims, so a group whose
            // only remains are its own volumes satisfied neither this nor Deletable and reported as a
            // plain refusal — the one case an operator most needs told about, silently reclassified
            // as noise. What this flag means is what its own remarks always said: the control plane
            // has no members left, and the cluster still holds something, so a person decides. Who
            // wrote the leftovers is in the refusal text, where it informs the decision instead of
            // gating it.
            members.Count == 0 && significant.Length > 0,
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
