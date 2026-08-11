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
    ///     (docs/plan/09 § Observing) and the one a validating admission policy keys off. It exists so
    ///     that "objects we manage" is a label selector rather than a list.
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

    /// <summary>Whether <paramref name="key" /> is one of the seven a caller may not set or replace.</summary>
    public static bool IsMandatory(string? key) => key is not null && Mandatory.Contains(key, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="key" /> is one of the two annotations a caller may not replace.</summary>
    public static bool IsMandatoryAnnotation(string? key) =>
        key is not null && MandatoryAnnotations.Contains(key, StringComparer.Ordinal);

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
