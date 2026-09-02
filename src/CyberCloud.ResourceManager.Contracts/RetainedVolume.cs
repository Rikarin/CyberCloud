using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     One <c>PersistentVolumeClaim</c> a converged teardown deliberately left behind, together with
///     the evidence that proves it belongs to the resource that named it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This type exists because a purge is the one path on which the platform is supposed to
///         destroy a tenant's data, and a volume named by pattern rather than by ownership is how the
///         wrong one goes.</b> docs/plan/08 § Soft delete keeps a soft-deleted resource's claims on
///         purpose — <i>"deleting a <c>StatefulSet</c> does not delete the
///         <c>PersistentVolumeClaim</c>s its <c>volumeClaimTemplate</c> created"</i> — and that is
///         exactly what makes a restore work. Ending the window has to remove precisely those and
///         nothing else, so a provider does not hand the manager a name: it hands over a name
///         <i>and</i> the labels the object must be carrying for the delete to be allowed.
///         <c>VolumeReclaimer</c> reads the claim back, checks every pair in
///         <see cref="OwnedBy" /> against the stored object, and refuses the whole reclaim when one
///         disagrees. A refusal is a failed purge with a reason, never a silent skip and never a
///         delete.
///     </para>
///     <para>
///         ⚠ <b>A label <i>selector</i> is the mechanism this wants and cannot have yet, which is why
///         the shape is "name, then verify" rather than "select, then delete".</b> Two things are
///         missing independently. <c>KubeCommandBuilder.Inject</c> writes ADR-013's seven labels into
///         the top-level <c>metadata.labels</c> and does not descend into a nested
///         <c>volumeClaimTemplate</c>, so a claim the <c>StatefulSet</c> controller creates carries
///         none of them; and <see cref="IKubeClusterConnection" /> has <c>ApplyAsync</c>,
///         <c>GetAsync</c> and <c>DeleteAsync</c> and <b>no list member at all</b>, so even a fully
///         labelled claim could not be found by selector today. What a claim <i>does</i> carry is the
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
        Group = "",
        Version = "v1",
        Kind = "PersistentVolumeClaim",
        Plural = "persistentvolumeclaims"
    };

    /// <summary>
    ///     The name Kubernetes gives a claim created from a <c>volumeClaimTemplate</c>:
    ///     <c>{volume}-{set}-{ordinal}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the naming rule and not a guess, and it is the reason a provider can answer
    ///     at all.</b> The <c>StatefulSet</c> controller composes a claim's name from the template's
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
    ///     ⚠ <b>Ordinals <c>0 … replicas-1</c>, which is every claim of a set that was never scaled
    ///     down.</b> A set scaled from three replicas to one leaves the claims of ordinals 1 and 2
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
                    new() {
                        Kind = ClaimKind, Namespace = ns, Name = NameFor(volume, set, ordinal)
                    },
                    ownedBy,
                    reason
                )
            );
        }

        return volumes.ToImmutable();
    }
}
