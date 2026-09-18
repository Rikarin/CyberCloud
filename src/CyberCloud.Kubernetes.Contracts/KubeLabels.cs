using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     The seven mandatory labels and two mandatory annotations of ADR-013 and docs/plan/09
///     § The command builder, plus the functions that derive their values.
/// </summary>
/// <remarks>
///     <para>
///         ADR-013:
///         <i>
///             "These labels are how billing attributes a pod, how the reconciler finds
///             orphans, how deletion is complete, and how a support engineer answers 'whose is this'.
///             Getting them on 99 % of objects is worth nothing; the 1 % is what you page about."
///         </i>
///         Which is why they are injected by <see cref="KubeCommand" /> rather than asked for, and
///         why <see cref="IsMandatory" /> exists — a caller may add labels and may not replace one of
///         these.
///     </para>
///     <para>
///         <b>Why the path is an annotation and the id is a label.</b> A label value caps at 63
///         characters (<see cref="LabelSyntax.MaxValueLength" />) with a restricted alphabet. A
///         canonical GUID is 36 characters drawn from <c>[0-9a-f-]</c> and is legal. A resource path
///         is neither — it is over 100 characters and it contains <c>/</c>. An annotation has no such
///         limit, so the path goes there. docs/plan/09 § The command builder decides this explicitly
///         "because it is exactly the kind of detail that becomes a two-day bug six months in".
///     </para>
/// </remarks>
public static class KubeLabels {
    /// <summary>The DNS-subdomain prefix every Cyber Cloud label and annotation carries.</summary>
    /// <remarks>
    ///     ⚠ Lower-case, and that is a rule rather than a style. A label key's prefix is a DNS-1123
    ///     subdomain, which forbids upper case — see <see cref="LabelSyntax" />. <c>CyberCloud.io</c>
    ///     would be rejected at admission on every object we write.
    /// </remarks>
    public const string Prefix = "cybercloud.io";

    /// <summary><c>cybercloud.io/tenant-id</c> — the owning tenant, canonical GUID.</summary>
    public const string TenantId = Prefix + "/tenant-id";

    /// <summary><c>cybercloud.io/subscription-id</c> — the billing boundary, canonical GUID.</summary>
    public const string SubscriptionId = Prefix + "/subscription-id";

    /// <summary><c>cybercloud.io/resource-group</c> — the lifecycle boundary's name.</summary>
    public const string ResourceGroup = Prefix + "/resource-group";

    /// <summary><c>cybercloud.io/resource-id</c> — the resource GUID. The drift-detection join key.</summary>
    public const string ResourceId = Prefix + "/resource-id";

    /// <summary>
    ///     <c>cybercloud.io/resource-type</c> — the provider namespace and type, lower-cased with
    ///     <c>.</c> kept and <c>/</c> replaced by <c>_</c>. See <see cref="ResourceTypeValue" />.
    /// </summary>
    public const string ResourceType = Prefix + "/resource-type";

    /// <summary><c>cybercloud.io/api-version</c> — the api-version the desired state was written at.</summary>
    public const string ApiVersion = Prefix + "/api-version";

    /// <summary><c>cybercloud.io/managed-by</c> — always <see cref="ManagedByValue" />.</summary>
    /// <remarks>
    ///     This is the one with a constant value, and it is the selector every informer filters on
    ///     (docs/plan/09 § Observing) and the one a validating admission policy keys off — both
    ///     bindings in <c>charts/bundle/cybercloud-admission/policies.yaml</c> select namespaces by
    ///     it, and <c>MandatoryLabelAdmissionPolicyTests</c> holds the file to this constant. It
    ///     exists so that "objects we manage" is a label selector rather than a list.
    /// </remarks>
    public const string ManagedBy = Prefix + "/managed-by";

    /// <summary>The only legal value of <see cref="ManagedBy" />.</summary>
    public const string ManagedByValue = "cybercloud";

    /// <summary>
    ///     <c>cybercloud.io/managed-by=cybercloud</c> — the selector docs/plan/09 § Observing filters
    ///     every shared informer by.
    /// </summary>
    public const string ManagedBySelector = ManagedBy + "=" + ManagedByValue;

    /// <summary>
    ///     <c>cybercloud.io/resource-path</c> — the full resource id path. An <b>annotation</b>,
    ///     because the path is too long and contains <c>/</c>.
    /// </summary>
    public const string ResourcePathAnnotation = Prefix + "/resource-path";

    /// <summary>
    ///     <c>cybercloud.io/reconcile-hash</c> — <c>sha256:…</c> over the desired body. An
    ///     <b>annotation</b>; it is 71 characters and would not fit in a label.
    /// </summary>
    /// <remarks>
    ///     docs/plan/09 § The command builder: "of the desired body — cheap no-op detection". It is
    ///     computed over the body <i>before</i> the mandatory labels and annotations are injected, so
    ///     that the hash of a body is a property of what the provider asked for and does not depend on
    ///     the hash annotation it is about to carry. See <see cref="ReconcileHash" />.
    /// </remarks>
    public const string ReconcileHashAnnotation = Prefix + "/reconcile-hash";

    /// <summary>The seven keys, in the order ADR-013 lists them.</summary>
    /// <remarks>
    ///     Hand-written rather than reflected, for the reason <c>ErrorCode.All</c> gives: a list you
    ///     have to edit is a list a reviewer sees. <c>KubeLabelTests</c> asserts the count is seven
    ///     and that every one is a legal Kubernetes label key.
    /// </remarks>
    public static ImmutableArray<string> Mandatory { get; } = [
        TenantId,
        SubscriptionId,
        ResourceGroup,
        ResourceId,
        ResourceType,
        ApiVersion,
        ManagedBy
    ];

    /// <summary>The two mandatory annotation keys.</summary>
    public static ImmutableArray<string> MandatoryAnnotations { get; } =
        [ResourcePathAnnotation, ReconcileHashAnnotation];

    // ── A second writer on an object — the co-owned apply, issue #89 ─────────────────────────────

    /// <summary>
    ///     <c>cybercloud.io/fragment.{writerId}</c> — the fragment one co-writer has applied onto an
    ///     object another resource owns, as canonical JSON. An <b>annotation</b>, one per co-writer.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why a co-writer's fragment is written down on the object.</b> Every co-writer of one
    ///         object applies under <i>one</i> shared field manager
    ///         (<see cref="CoWriterFieldManager" />), because the fields a peering has to reach —
    ///         <c>Vpc.spec.vpcPeerings</c>, <c>Vpc.spec.staticRoutes</c> — are atomic lists, and two
    ///         managers cannot each own part of an atomic list: the second apply is a conflict on the
    ///         whole. One manager's apply is the whole set of fields it owns, so each co-writer's apply
    ///         has to carry every <i>other</i> co-writer's fragment too, or it prunes them. This
    ///         annotation is how the builder knows what the others applied without asking them: it
    ///         merges every stored fragment with the caller's and applies the union. It is the same
    ///         bookkeeping <c>metadata.managedFields</c> keeps per manager, kept per co-writer because
    ///         the co-writers share a manager.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bookkeeping, not desired state.</b> The grain holds the co-writer's desired body;
    ///         this records what was last applied, and the next apply overwrites it. ADR-001's rule
    ///         that desired state does not live in the target cluster's etcd is about where the truth
    ///         is, and the truth stays in the grain.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Two costs of keeping it on the object, named so nobody discovers them in
    ///             production.
    ///         </b> First, size: the API server caps an object's annotations at 256 KiB in
    ///         total, and every co-writer's whole fragment sits there — a peering's is a few hundred
    ///         bytes, so a <c>Vpc</c> holds hundreds before the cap bites, but a co-writer that stored
    ///         a large slice would be refused by the API server on the <i>next</i> co-writer's apply,
    ///         which is the one that carries the union. Second, lifetime: a fragment is withdrawn by
    ///         its co-writer and by nothing else. The builder refuses a corrupt one rather than
    ///         pruning it and never drops a stale one, so a fragment left by a co-writer whose grain
    ///         vanished without withdrawing is re-applied by every other co-writer of that object,
    ///         forever. <c>DriftScanner</c> is the one thing that finds it — a fragment whose writer
    ///         no grain owns is an orphan finding naming the slice — and removing it is a person's
    ///         call, as an orphan object's is.
    ///     </para>
    /// </remarks>
    public const string FragmentAnnotationPrefix = Prefix + "/fragment.";

    /// <summary>
    ///     <c>cybercloud.io/fragment-hash.{writerId}</c> — <c>sha256:…</c> over one co-writer's
    ///     fragment. The co-owned mode's <see cref="ReconcileHashAnnotation" />, keyed per fragment so
    ///     two co-writers' hashes never overwrite each other and never touch the owner's.
    /// </summary>
    public const string FragmentHashAnnotationPrefix = Prefix + "/fragment-hash.";

    /// <summary>
    ///     <c>cybercloud.io/fragment-path.{writerId}</c> — the co-writing resource's path. The
    ///     co-owned mode's <see cref="ResourcePathAnnotation" />, so a support engineer reading the
    ///     object can answer "whose is this slice" as well as "whose is this object".
    /// </summary>
    public const string FragmentPathAnnotationPrefix = Prefix + "/fragment-path.";

    /// <summary>The <see cref="FragmentAnnotationPrefix" /> key for one co-writer.</summary>
    /// <param name="writer">The co-writing resource's GUID.</param>
    public static string FragmentAnnotation(Guid writer) => FragmentAnnotationPrefix + GuidValue(writer);

    /// <summary>The <see cref="FragmentHashAnnotationPrefix" /> key for one co-writer.</summary>
    /// <param name="writer">The co-writing resource's GUID.</param>
    public static string FragmentHashAnnotation(Guid writer) => FragmentHashAnnotationPrefix + GuidValue(writer);

    /// <summary>The <see cref="FragmentPathAnnotationPrefix" /> key for one co-writer.</summary>
    /// <param name="writer">The co-writing resource's GUID.</param>
    public static string FragmentPathAnnotation(Guid writer) => FragmentPathAnnotationPrefix + GuidValue(writer);

    /// <summary>
    ///     Whether <paramref name="key" /> is one of the three per-fragment annotations, which a
    ///     caller may not set by hand in either mode because the builder writes them.
    /// </summary>
    /// <param name="key">An annotation key.</param>
    public static bool IsFragmentAnnotation(string? key) =>
        key is not null
        && (key.StartsWith(FragmentAnnotationPrefix, StringComparison.Ordinal)
            || key.StartsWith(FragmentHashAnnotationPrefix, StringComparison.Ordinal)
            || key.StartsWith(FragmentPathAnnotationPrefix, StringComparison.Ordinal));

    /// <summary>Reads the co-writer's GUID off a <see cref="FragmentAnnotationPrefix" /> key.</summary>
    /// <param name="key">An annotation key.</param>
    /// <param name="writer">The GUID the key carries, when it is a fragment key.</param>
    /// <returns><c>true</c> when the key is <c>cybercloud.io/fragment.{guid}</c> with a parseable GUID.</returns>
    public static bool TryReadFragmentWriter(string? key, out Guid writer) {
        writer = Guid.Empty;

        return key is not null
            && key.StartsWith(FragmentAnnotationPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(key.AsSpan(FragmentAnnotationPrefix.Length), "D", out writer);
    }

    /// <summary>
    ///     <c>cybercloud/{ownerType}/{ownerId}</c> — the one field manager every co-writer of one
    ///     object applies under, named for the object's <b>owning</b> resource.
    /// </summary>
    /// <param name="ownerTypeValue">The owner's <see cref="ResourceType" /> label value, as read off the object.</param>
    /// <param name="ownerIdValue">The owner's <see cref="ResourceId" /> label value, as read off the object.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Named for the owner and not for the co-writer, and that is the design decision
    ///             of the co-owned mode.
    ///         </b> A manager per co-writer reads as the obvious shape and it does
    ///         not converge on the fields a peering has to reach: <c>Vpc.spec.vpcPeerings</c> and
    ///         <c>Vpc.spec.staticRoutes</c> declare no <c>x-kubernetes-list-type</c>, so each is one
    ///         atomic value with one set of owners, and a second manager applying the list with its
    ///         own entry added is a <c>FieldManagerConflict</c> on the whole list — forever, because
    ///         <c>KubeCommand.Force</c> is unreachable on purpose. One manager per co-owned object,
    ///         applying the union of every fragment, is the shape that converges; the per-fragment
    ///         annotations above are what let each co-writer know the others' fragments to include.
    ///     </para>
    ///     <para>
    ///         Distinct from the owner's own <c>cybercloud/{provider}</c>, so the owner's apply never
    ///         prunes a co-writer's fields and a co-writer's apply never prunes the owner's. Both
    ///         values come off the live object's labels rather than from the caller, so a co-writer
    ///         cannot name an owner the object does not have. Bounded by construction: 11 characters
    ///         of prefix, a label value of at most 63, a slash, and a 36-character GUID is at most
    ///         111 of the API server's 128.
    ///     </para>
    /// </remarks>
    public static string CoWriterFieldManager(string ownerTypeValue, string ownerIdValue) =>
        "cybercloud/" + ownerTypeValue + "/" + ownerIdValue;

    /// <summary>
    ///     Reads the owner back out of a <see cref="CoWriterFieldManager" /> name — the inverse, for
    ///     the checks that ask whether a command's manager names the owner the command claims.
    /// </summary>
    /// <param name="manager">A field manager name.</param>
    /// <param name="ownerTypeValue">The owner's <see cref="ResourceType" /> label value the name carries.</param>
    /// <param name="ownerId">The owner's GUID the name carries.</param>
    /// <returns>
    ///     <c>true</c> when the name is <c>cybercloud/{ownerType}/{ownerId}</c> with a non-empty type
    ///     and a parseable, non-empty GUID. An owner's own <c>cybercloud/{provider}</c> has one segment
    ///     after the prefix and is <c>false</c>.
    /// </returns>
    public static bool TryReadCoWriterFieldManager(string? manager, out string ownerTypeValue, out Guid ownerId) {
        ownerTypeValue = string.Empty;
        ownerId = Guid.Empty;

        const string prefix = "cybercloud/";

        if (manager is null || !manager.StartsWith(prefix, StringComparison.Ordinal)) {
            return false;
        }

        var rest = manager.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');

        if (slash <= 0 || rest[(slash + 1)..].IndexOf('/') >= 0) {
            return false;
        }

        if (!Guid.TryParseExact(rest[(slash + 1)..], "D", out ownerId) || ownerId == Guid.Empty) {
            ownerId = Guid.Empty;
            return false;
        }

        ownerTypeValue = rest[..slash].ToString();
        return true;
    }

    /// <summary>
    ///     The six of <see cref="Mandatory" /> whose value cannot change for the life of a resource
    ///     — everything except <see cref="ApiVersion" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             This exists for one job: labelling a nested template that a controller copies
    ///             once and that an API server will not let anyone change afterwards.
    ///         </b> The seven are
    ///         written into an object's own <c>metadata.labels</c>, which is mutable on every kind, so
    ///         nothing there needs this distinction. A <c>StatefulSet</c>'s
    ///         <c>spec.volumeClaimTemplates</c> is a different field with a different rule, and
    ///         <c>IKubeCommandBuilder.WithTemplateLabels</c> is the only caller.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <see cref="ApiVersion" /> is excluded because it is per-request, and that single
    ///             fact is what makes the difference between a one-time migration and a resource that can
    ///             never be reconciled again.
    ///         </b> <c>KubeCommandBuilder</c> stamps it from the api-version
    ///         of the request that caused the reconcile, so it changes whenever a tenant calls at a
    ///         newer version. Measured against <c>rancher/k3s:v1.35.7-k3s1</c> — the pin the
    ///         cluster-backed conformance lane uses — an apply that changes <i>anything</i> under a
    ///         live <c>StatefulSet</c>'s <c>spec.volumeClaimTemplates</c> is rejected:
    ///         <i>
    ///             "spec: Forbidden: updates to statefulset spec for fields other than 'replicas',
    ///             'ordinals', 'template', 'updateStrategy', 'revisionHistoryLimit',
    ///             'persistentVolumeClaimRetentionPolicy' and 'minReadySeconds' are forbidden"
    ///         </i>
    ///         . A claim template carrying <c>api-version</c> would therefore brick the resource on
    ///         the tenant's first call at a new api-version — a rejected apply does not heal, so every
    ///         later reconcile is rejected too. The other six are fixed by the resource's identity and
    ///         its path, so a template stamped with them is stable forever after it is written once.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<string> LifetimeStable { get; } = [
        TenantId,
        SubscriptionId,
        ResourceGroup,
        ResourceId,
        ResourceType,
        ManagedBy
    ];

    /// <summary>Whether <paramref name="key" /> is one of <see cref="LifetimeStable" />.</summary>
    /// <param name="key">A label key.</param>
    public static bool IsLifetimeStable(string? key) =>
        key is not null && LifetimeStable.Contains(key, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="key" /> is one of the seven a caller may not set or replace.</summary>
    public static bool IsMandatory(string? key) => key is not null && Mandatory.Contains(key, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="key" /> is one of the two annotations a caller may not replace.</summary>
    public static bool IsMandatoryAnnotation(string? key) =>
        key is not null && MandatoryAnnotations.Contains(key, StringComparer.Ordinal);

    // ── Objects attributed to a resource group rather than to a resource ────────────────────────

    /// <summary>
    ///     The provider namespace the platform keeps for itself. No provider may declare it.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Reserving it is what makes <see cref="IsGroupScoped(string)" /> un-forgeable, and
    ///         without the reservation the predicate would be a suggestion.
    ///     </b> A provider cannot set
    ///     <see cref="ResourceType" /> — it is one of the seven, injected by
    ///     <see cref="KubeCommandBuilder" /> from the resource's own type, and
    ///     <see cref="IsMandatory" /> rejects an attempt to override it. So the only way an object
    ///     could claim to be group-attributed is for its <i>resource</i> to be of a type in this
    ///     namespace, and <c>ProviderRegistry.Build</c> refuses to register one. The two halves
    ///     together are why a check keyed on this label cannot be talked into ignoring a real
    ///     object with a wrong id.
    /// </remarks>
    public const string ReservedNamespace = "CyberCloud.Resources";

    /// <summary>
    ///     <c>CyberCloud.Resources/resourceGroups</c> — the type a resource group's own cluster
    ///     objects carry.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A resource group is not a resource, and this type exists so that saying so is a
    ///             label rather than a convention.
    ///         </b> A group has no GUID —
    ///         <c>ResourceGroupDescriptor</c> carries a name, a subscription and a tenant, and no id —
    ///         and it is a structural segment of a resource path rather than a registered provider
    ///         type. But its namespace is a real object in a real cluster that ADR-013 requires all
    ///         seven labels on, so <c>resource-type</c> needs a value, and the honest one names what
    ///         the object belongs to.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Three consumers need to recognise this and none of them may depend on the
    ///             writer.
    ///         </b> <c>NamespaceEnsurer</c> stamps it; <c>DriftScanner</c> must not report the
    ///         namespace as an orphan of a resource grain that will never exist; and
    ///         <c>ProviderConformanceTests</c> must not hold a group-attributed object to the
    ///         triggering resource's id. It lives here, in the label vocabulary, because that is the
    ///         one assembly all three already reference.
    ///     </para>
    /// </remarks>
    public static ResourceTypeName ResourceGroupType { get; } = new(ReservedNamespace, "resourceGroups");

    /// <summary>The <see cref="ResourceType" /> value <see cref="ResourceGroupType" /> renders to.</summary>
    public static string ResourceGroupTypeValue { get; } = ResourceTypeValue(ResourceGroupType);

    /// <summary>
    ///     Whether a <see cref="ResourceType" /> label value marks an object owned by a resource
    ///     <i>group</i> rather than by a resource.
    /// </summary>
    /// <param name="resourceTypeValue">The object's <c>cybercloud.io/resource-type</c> label.</param>
    /// <remarks>
    ///     ⚠ Such an object's <see cref="ResourceId" /> label is a <b>derived</b> GUID naming the
    ///     group, so it will never match a resource grain and must never be compared to one. See
    ///     <see cref="ResourceGroupType" /> for why this cannot be forged.
    /// </remarks>
    public static bool IsGroupScoped(string? resourceTypeValue) =>
        string.Equals(resourceTypeValue, ResourceGroupTypeValue, StringComparison.Ordinal);

    /// <summary>
    ///     <see cref="IsGroupScoped(string)" />, read off a label set.
    /// </summary>
    /// <param name="labels">An object's labels, as emitted.</param>
    public static bool IsGroupScoped(IReadOnlyDictionary<string, string>? labels) =>
        labels is not null
        && labels.TryGetValue(ResourceType, out var value)
        && IsGroupScoped(value);

    /// <summary>A GUID as a label value — the canonical hyphenated form, 36 characters.</summary>
    /// <remarks>
    ///     ⚠ The <c>D</c> format and not <c>N</c>, matching <see cref="Core.Resources.ResourceId.Path" />
    ///     and the API surface. Both are legal label values; using two spellings across the platform
    ///     would make a label selector for a resource depend on which code wrote the object.
    /// </remarks>
    public static string GuidValue(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    ///     The <see cref="ResourceType" /> value: the type lower-cased with <c>/</c> replaced by
    ///     <c>_</c> — for example <c>cybercloud.dbforpostgresql_servers_databases</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/09 § The command builder: "Resource type is lowercased and <c>/</c> replaced
    ///         by <c>_</c>" — because <c>/</c> is not in the label-value alphabet and because the
    ///         provider namespace is case-preserving on the wire, so an un-folded value would give one
    ///         type two spellings and therefore two label selectors.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The result is not guaranteed to fit in 63 characters and this function does not
    ///             truncate.
    ///         </b>
    ///         <see cref="ResourceTypeName" /> permits a namespace of two or more
    ///         segments and a type path of up to three, each up to 63 characters, so a legal type can
    ///         be several hundred characters long. Truncating would map two distinct types onto one
    ///         label value, which silently breaks orphan detection and billing attribution — the two
    ///         things ADR-013 says the labels are for. <see cref="KubeCommand" /> validates the value
    ///         and fails the command with a message naming the type and the limit. This is a gap in
    ///         ADR-013's label set: it bounds GUIDs and the path and says nothing about the type.
    ///     </para>
    /// </remarks>
    /// <param name="type">The fully qualified resource type.</param>
    public static string ResourceTypeValue(ResourceTypeName type) =>
        type.IsEmpty
            ? string.Empty
            : AsciiLower(type.Namespace) + "_" + AsciiLower(type.Type).Replace('/', '_');

    /// <summary>
    ///     <c>sha256:{hex}</c> over <paramref name="body" /> in UTF-8 — the
    ///     <see cref="ReconcileHashAnnotation" /> value.
    /// </summary>
    /// <param name="body">The desired body, as it will be sent.</param>
    public static string ReconcileHash(string body) {
        ArgumentNullException.ThrowIfNull(body);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(body), hash);

        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    ///     ASCII-only lower-casing, for the reason <c>ResourceTypeName.AsciiLower</c> gives — a
    ///     canonicaliser that folds two distinct code points onto one produces two resources with one
    ///     label value.
    /// </summary>
    static string AsciiLower(string value) {
        var buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        for (var i = 0; i < value.Length; i++) {
            var c = value[i];
            buffer[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return new(buffer);
    }
}
