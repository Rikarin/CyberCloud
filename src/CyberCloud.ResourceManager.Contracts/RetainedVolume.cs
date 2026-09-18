using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     One <c>PersistentVolumeClaim</c> a converged teardown deliberately left behind, together with
///     the evidence that proves it belongs to the resource that named it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This type exists because a purge is the one path on which the platform is supposed to
///             destroy a tenant's data, and a volume named by pattern rather than by ownership is how the
///             wrong one goes.
///         </b> docs/plan/08 § Soft delete keeps a soft-deleted resource's claims on
///         purpose —
///         <i>
///             "deleting a <c>StatefulSet</c> does not delete the
///             <c>PersistentVolumeClaim</c>s its <c>volumeClaimTemplate</c> created"
///         </i> — and that is
///         exactly what makes a restore work. Ending the window has to remove precisely those and
///         nothing else, so a provider does not hand the manager a name: it hands over a name
///         <i>and</i> the labels the object must be carrying for the delete to be allowed.
///         <c>VolumeReclaimer</c> reads the claim back, checks every pair in
///         <see cref="OwnedBy" /> against the stored object, and refuses the whole reclaim when one
///         disagrees. A refusal is a failed purge with a reason, never a silent skip and never a
///         delete.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A label <i>selector</i> is the mechanism this wants and cannot have yet, which is why
///             the shape is "name, then verify" rather than "select, then delete".
///         </b> Two things are
///         missing independently. <c>KubeCommandBuilder.Inject</c> writes ADR-013's seven labels into
///         the top-level <c>metadata.labels</c> and does not descend into a nested
///         <c>volumeClaimTemplate</c>, so a claim the <c>StatefulSet</c> controller creates carries
///         none of them; and <see cref="IKubeClusterConnection" /> had <c>ApplyAsync</c>,
///         <c>GetAsync</c> and <c>DeleteAsync</c> and <b>no list member at all</b>, so even a fully
///         labelled claim could not be found by selector.
///         <para>
///             ⚠ <b>Both halves have since moved and neither changes this shape.</b>
///             <c>IKubeCommandBuilder.WithTemplateLabels</c> stamps six of the seven onto a template,
///             and <c>IKubeClusterConnection.ListNamespaceAsync</c> exists — but it enumerates a
///             whole namespace for a resource-group delete and is a discovery plus a list per served
///             kind, which is the wrong instrument for "the claims of one resource" by two orders of
///             magnitude. <c>IKubeClusterConnection.ListAsync</c> is the selector-scoped list, and
///             it arrived for the operator that names its own claims rather than for this type: the
///             shape is still "name, then verify", because <see cref="Listed" /> turns a listing into
///             names and <c>VolumeReclaimer</c> still reads each one back before it acts. A listing
///             says what to look at; the stored object says whose it is.
///         </para>
///         What a claim <i>does</i> carry is the
///         set's <c>spec.selector.matchLabels</c>, which Kubernetes copies onto every claim its
///         <c>volumeClaimTemplate</c> produces — written by the provider, onto objects created from
///         the provider's own document. That is the evidence <see cref="OwnedBy" /> is built from,
///         and it is available now. When the seven labels reach the template, a provider moves
///         <see cref="OwnedBy" /> to <c>cybercloud.io/resource-id</c> and every caller of this type
///         stays as it is.
///     </para>
/// </remarks>
/// <param name="Claim">
///     The claim's address. Must be a namespaced <c>v1 PersistentVolumeClaim</c> in the resource's
///     own namespace — <c>VolumeReclaimer</c> refuses anything else before it reads a thing.
/// </param>
/// <param name="OwnedBy">
///     Labels the stored object must carry, exactly, for the claim to be removed. Never empty: a
///     claim with no evidence behind it is a name, and a name is not ownership.
/// </param>
/// <param name="Reason">
///     What the claim holds, in a sentence an operator reading a failed purge can act on — for
///     example <c>"the registry's image layers"</c>.
/// </param>
public readonly record struct RetainedVolume(
    ObjectRef Claim,
    ImmutableDictionary<string, string> OwnedBy,
    string Reason
) {
    /// <summary>The kind a retained volume is always addressed as.</summary>
    /// <remarks>
    ///     ⚠ Carried here rather than left to each provider so that the guard can compare against one
    ///     value. A provider that hands over a <c>StatefulSet</c> by mistake is refused on kind
    ///     rather than obeyed.
    /// </remarks>
    public static GroupVersionKind ClaimKind { get; } = new() {
        Group = "", Version = "v1", Kind = "PersistentVolumeClaim", Plural = "persistentvolumeclaims"
    };

    /// <summary>
    ///     The name Kubernetes gives a claim created from a <c>volumeClaimTemplate</c>:
    ///     <c>{volume}-{set}-{ordinal}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         This is the naming rule and not a guess, and it is the reason a provider can answer
    ///         at all.
    ///     </b> The <c>StatefulSet</c> controller composes a claim's name from the template's
    ///     <c>metadata.name</c>, the set's name and the pod's ordinal, which is why a claim outlives
    ///     the set that made it and why nothing else can recreate the name. It is still only a
    ///     <i>name</i> — <see cref="OwnedBy" /> is what makes acting on it safe.
    /// </remarks>
    /// <param name="volume">The <c>volumeClaimTemplate</c>'s <c>metadata.name</c>.</param>
    /// <param name="set">The <c>StatefulSet</c>'s object name.</param>
    /// <param name="ordinal">The pod ordinal, from zero.</param>
    public static string NameFor(string volume, string set, int ordinal) {
        ArgumentException.ThrowIfNullOrEmpty(volume);
        ArgumentException.ThrowIfNullOrEmpty(set);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        return $"{volume}-{set}-{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    ///     Every claim one <c>volumeClaimTemplate</c> made for a set of <paramref name="replicas" />
    ///     pods, in ordinal order.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Ordinals <c>0 … replicas-1</c>, which is every claim of a set that was never scaled
    ///         down.
    ///     </b> A set scaled from three replicas to one leaves the claims of ordinals 1 and 2
    ///     behind — that is the same Kubernetes behaviour this whole file is about, one level in —
    ///     and the desired body a purge reads names only the replica count the resource ended on. So
    ///     those claims are <b>not</b> reclaimed, and this is deliberate rather than overlooked:
    ///     probing past the count means deleting objects nothing in the desired state accounts for,
    ///     and the guard would be the only thing standing between that and a tenant's data. It is
    ///     recorded as owed in docs/plan/08 § Soft delete rather than guessed at here.
    /// </remarks>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="volume">The <c>volumeClaimTemplate</c>'s <c>metadata.name</c>.</param>
    /// <param name="set">The <c>StatefulSet</c>'s object name.</param>
    /// <param name="replicas">The set's replica count.</param>
    /// <param name="ownedBy">The set's <c>spec.selector.matchLabels</c>.</param>
    /// <param name="reason">What the volume holds.</param>
    public static ImmutableArray<RetainedVolume> OfSet(
        string ns,
        string volume,
        string set,
        int replicas,
        ImmutableDictionary<string, string> ownedBy,
        string reason
    ) {
        ArgumentException.ThrowIfNullOrEmpty(ns);
        ArgumentNullException.ThrowIfNull(ownedBy);
        ArgumentOutOfRangeException.ThrowIfNegative(replicas);

        if (ownedBy.IsEmpty) {
            throw new ArgumentException(
                $"The claims of '{set}' were declared with no ownership labels. A claim named without "
                + "evidence is a name, and VolumeReclaimer refuses to delete on a name — see "
                + "RetainedVolume.OwnedBy.",
                nameof(ownedBy)
            );
        }

        var volumes = ImmutableArray.CreateBuilder<RetainedVolume>(replicas);

        for (var ordinal = 0; ordinal < replicas; ordinal++) {
            volumes.Add(
                new(
                    new() { Kind = ClaimKind, Namespace = ns, Name = NameFor(volume, set, ordinal) },
                    ownedBy,
                    reason
                )
            );
        }

        return volumes.ToImmutable();
    }

    /// <summary>
    ///     The claims an operator created and labelled, found by listing rather than predicted by
    ///     name.
    /// </summary>
    /// <param name="listed">
    ///     What <see cref="IKubeClusterConnection.ListAsync" /> answered for
    ///     <see cref="ClaimKind" /> under <paramref name="ownedBy" />'s selector.
    /// </param>
    /// <param name="ns">The resource's namespace. Anything listed elsewhere is dropped.</param>
    /// <param name="ownedBy">
    ///     The labels every claim must carry — the same pairs the selector asked for, so that the
    ///     guard re-checks what the listing claimed.
    /// </param>
    /// <param name="reason">What the volumes hold.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The other way a provider can answer <c>RetainedVolumesAsync</c>, and it exists
    ///             because a name cannot be computed for every operator.
    ///         </b> <see cref="OfSet" /> works when Kubernetes names the claims from a template.
    ///         CloudNativePG names them from an instance serial that only the operator advances —
    ///         a failover replaces instance 2 with instance 3, so a two-instance server may hold
    ///         <c>main-1</c> and <c>main-3</c> — and the only record of which serials exist is the
    ///         cluster itself. So the provider asks, and hands back what it was told, still carrying
    ///         the labels a reader must find on the object before acting on it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A listing's labels are not the evidence; the stored object's are.</b> The
    ///         listing narrows what to read; <see cref="CheckOwnership" /> runs against the body
    ///         the API server holds when the claim is next read, and a claim whose labels changed
    ///         in between is refused there.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<RetainedVolume> Listed(
        IEnumerable<KubeObjectSummary> listed,
        string ns,
        ImmutableDictionary<string, string> ownedBy,
        string reason
    ) {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentException.ThrowIfNullOrEmpty(ns);
        ArgumentNullException.ThrowIfNull(ownedBy);

        if (ownedBy.IsEmpty) {
            throw new ArgumentException(
                "The listed claims were declared with no ownership labels. A claim named without "
                + "evidence is a name, and VolumeReclaimer refuses to delete on a name — see "
                + "RetainedVolume.OwnedBy.",
                nameof(ownedBy)
            );
        }

        var volumes = ImmutableArray.CreateBuilder<RetainedVolume>();

        foreach (var summary in listed) {
            if (summary.Kind.Kind != ClaimKind.Kind
                || !string.Equals(summary.Namespace, ns, StringComparison.Ordinal)
                || summary.Name.Length == 0) {
                continue;
            }

            volumes.Add(new(new() { Kind = ClaimKind, Namespace = ns, Name = summary.Name }, ownedBy, reason));
        }

        return volumes.ToImmutable();
    }

    /// <summary>
    ///     The label selector that finds every claim carrying <paramref name="ownedBy" />.
    /// </summary>
    /// <param name="ownedBy">The labels, as <see cref="OwnedBy" /> carries them.</param>
    /// <returns><c>key=value</c> pairs joined with commas, in key order so the string is stable.</returns>
    public static string Selector(ImmutableDictionary<string, string> ownedBy) {
        ArgumentNullException.ThrowIfNull(ownedBy);

        return string.Join(
            ",",
            ownedBy.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + "=" + x.Value)
        );
    }

    /// <summary>
    ///     Whether this claim is even addressable as one of the resource's volumes, or the refusal
    ///     that says why not.
    /// </summary>
    /// <param name="context">The resource the claim was declared for.</param>
    /// <returns><see langword="null" /> when it is addressable, otherwise the error to fail with.</returns>
    /// <remarks>
    ///     ⚠ <b>The namespace check is the one that matters most and is the cheapest.</b> A
    ///     namespace is per resource group per cluster, so a claim outside
    ///     <see cref="ReconcileContext.Namespace" /> belongs to a different resource group and
    ///     possibly a different tenant. Nothing a provider can compute from its own desired body
    ///     should ever land outside it, and the one bug that would — a name built from a field a
    ///     caller controls — is exactly the one that reaches another tenant's disks.
    /// </remarks>
    public Error? CheckAddress(ReconcileContext context) {
        if (Claim is null || Claim.Name.Length == 0) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared a retained volume with no name. A purge acts only on "
                + "claims it can address, and refuses rather than guessing."
            );
        }

        if (Claim.Kind != ClaimKind) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared '{Claim.Name}' as a retained volume of kind "
                + $"'{Claim.Kind}'. A purge removes {ClaimKind.Kind}s and "
                + "nothing else — a teardown is what removes workloads, and it has already run."
            );
        }

        if (!string.Equals(Claim.Namespace, context.Namespace, StringComparison.Ordinal)) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared a retained volume '{Claim.Name}' in namespace "
                + $"'{Claim.Namespace}' and the resource lives in '{context.Namespace}'. A "
                + "namespace is one resource group on one cluster, so a claim outside it belongs to "
                + "somebody else and this purge will not touch it."
            );
        }

        if (OwnedBy is null || OwnedBy.IsEmpty) {
            return new(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' declared '{Claim.Name}' as a retained volume with no "
                + "ownership labels. A claim named without evidence is a name, and a purge that acts "
                + "on a name is how the wrong tenant's disk goes — see RetainedVolume.OwnedBy."
            );
        }

        return null;
    }

    /// <summary>
    ///     Whether the object the API server is holding really is the resource's, or the refusal that
    ///     says which label disagreed.
    /// </summary>
    /// <param name="json">The claim, exactly as the API server returned it.</param>
    /// <param name="context">The resource the claim was declared for.</param>
    /// <returns><see langword="null" /> when every label agrees, otherwise the error to fail with.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read from the STORED object, never from the document we would have written.</b>
    ///         The claim was created by a controller and not by this platform, so the only thing
    ///         that can say whom it belongs to is what the API server has. Checking a rendered copy
    ///         would be checking our own arithmetic twice.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Held here rather than in <c>VolumeReclaimer</c> because two callers now need
    ///             it and a guard written twice drifts.
    ///         </b> The reclaimer runs it before a delete; a
    ///         provider whose operator owns its claims runs it before a detach and again before an
    ///         adopt, and a claim that fails it in either place is left exactly as it was found.
    ///     </para>
    /// </remarks>
    public Error? CheckOwnership(string json, ReconcileContext context) {
        JsonNode? root;

        try {
            root = JsonNode.Parse(json);
        } catch (JsonException error) {
            return new(
                ErrorCode.InternalError,
                $"The API server's copy of '{Claim}' did not parse as JSON, so its ownership "
                + $"cannot be checked and it will not be touched: {error.Message}"
            );
        }

        var metadata = root?["metadata"] as JsonObject;
        var labels = metadata?["labels"] as JsonObject;

        // ⚠ ABSENT IS NOT EMPTY. A claim with no labels at all is not one a controller produced for
        // this platform — Kubernetes copies a set's selector onto every claim it creates, and an
        // operator stamps its own — so it is somebody else's object wearing a name we predicted.
        if (labels is null) {
            return new(
                ErrorCode.InternalError,
                $"'{Claim}' carries no labels, so nothing connects it to '{context.Id.Path}'. "
                + "A claim created from this resource's volumeClaimTemplate carries the set's "
                + "selector; this one does not, so it belongs to something else and the purge "
                + "refuses it."
            );
        }

        var stored = metadata?["namespace"]?.GetValue<string>();

        if (stored is { Length: > 0 } && !string.Equals(stored, context.Namespace, StringComparison.Ordinal)) {
            return new(
                ErrorCode.InternalError,
                $"'{Claim}' came back from the API server in namespace '{stored}' rather than "
                + $"'{context.Namespace}'. The purge refuses a claim it did not address."
            );
        }

        foreach (var (key, expected) in OwnedBy) {
            var actual = labels[key] switch {
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                null => null,
                var other => other.ToString()
            };

            if (actual is null) {
                return new(
                    ErrorCode.InternalError,
                    $"'{Claim}' does not carry '{key}', which '{context.Id.Path}' says every "
                    + "claim of its own carries. The purge refuses a volume it cannot prove it owns."
                );
            }

            if (!string.Equals(actual, expected, StringComparison.Ordinal)) {
                return new(
                    ErrorCode.InternalError,
                    $"'{Claim}' carries '{key}={actual}' and '{context.Id.Path}' owns only "
                    + $"claims carrying '{key}={expected}'. Something else in this namespace has the "
                    + "name this purge predicted, so the purge stops rather than destroying it."
                );
            }
        }

        return null;
    }
}
