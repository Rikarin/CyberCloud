using CyberCloud.Core.Contracts;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Mail.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Mail/domains/mailboxes</c>: one address a domain
///     accepts mail for, the password its owner signs in with, its quota, its aliases and where it
///     forwards.
/// </summary>
/// <remarks>
///     <para>
///         [17 § Resource model](../../../../docs/plan/17-communication-and-email.md):
///         <i>"mailboxes/{local} → quota, aliases, forwarding, password (Vault), Sieve rules"</i>.
///         All of it is here but the Sieve rules, which a tenant edits over ManageSieve today —
///         <c>charts/managed/mail-mailbox/conformance.yaml § owed</c>, <c>sieve-rules-are-not-a-property</c>.
///     </para>
///     <para>
///         ⚠ <b>THE NAME IS NOT THE LOCAL PART, FOR THE REASON THE DOMAIN'S NAME IS NOT THE DOMAIN.</b>
///         doc 17 spells the type <c>mailboxes/{local}</c>, and <c>john.doe</c> is a local part the
///         platform's DNS-1123 name rule refuses (<c>MailDomains.TypePath</c> has the argument). So
///         the local part is a required, immutable property beside an ordinary name.
///     </para>
///     <para>
///         ⚠ <b>A MAILBOX OWNS NO OBJECT. IT IS FOUR KEYS OF ITS DOMAIN'S MAILBOX <c>Secret</c>.</b>
///         Dovecot needs one password file per login and Postfix one map of every address, and both
///         run in the domain's pod — so a mailbox is a slice of <see cref="MailDomains.UsersSecretName" />,
///         written as a second writer through <c>ReconcileContext.CoWriter</c> (docs/plan/09 § A
///         second writer on an object), the shape <c>CyberCloud.Network/virtualNetworks/peerings</c>
///         established. The kubelet refreshes the mounted files, Dovecot opens them per login, and
///         the Postfix start script rebuilds its maps when their checksum moves
///         (<see cref="MailDomains.PostfixStartScript" />). No pod restarts for a new mailbox.
///     </para>
///     <para>
///         ⚠ <b>TWO MAILBOXES CANNOT CLAIM ONE ADDRESS, AND THE CO-WRITER'S MERGE IS WHAT REFUSES
///         IT.</b> Every address a mailbox answers for — its own and each alias — is written as
///         <c>{local}.claim</c> holding the mailbox's GUID. Two mailboxes naming one address write
///         one key with two values, which <c>FragmentMerge</c> refuses by name, atomically under the
///         object's <c>resourceVersion</c>. A check that read the other keys first and then wrote
///         would have been a race between two creates; this is not.
///     </para>
///     <para>
///         ⚠ <b>THE PASSWORD IS A VAULT HANDLE AND NEVER A VALUE.</b> <c>passwordRef</c> names a
///         field in the tenant's own vault; the reconciler resolves it, hashes it
///         (<see cref="Sha512Crypt" />) and writes the hash — the plaintext exists in one local for
///         one pass and in no body, no grain, no object. An empty handle is a mailbox that receives
///         and cannot sign in. ⚠ Changing the password is changing the vault field and re-applying
///         the mailbox: the handle did not change, so nothing else tells the platform to look.
///     </para>
///     <para>
///         ⚠ <b>ONE CEILING THIS SHAPE HAS, NAMED:</b> every co-writer's fragment is also stored as
///         an annotation on the <c>Secret</c>, and Kubernetes caps an object's annotations at 256 KiB.
///         A mailbox's fragment is roughly 600 bytes, so a domain holds about four hundred mailboxes
///         before the API server refuses the next one. <c>charts/managed/mail-mailbox/conformance.yaml
///         § owed</c>, <c>four-hundred-mailboxes-per-domain</c>.
///     </para>
/// </remarks>
public static class MailMailboxes {
    /// <summary>The type path, under <see cref="MailDomains.TypePath" />.</summary>
    public const string TypePath = MailDomains.TypePath + "/mailboxes";

    /// <summary>The api-version, the domain's.</summary>
    public const string V2026 = MailDomains.V2026;

    /// <summary>The chart that documents the slice a mailbox writes.</summary>
    public const string ChartName = "managed/mail-mailbox";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(MailDomains.ProviderNamespace, TypePath);

    /// <summary>A vault handle, <c>path#field[@version]</c>, or nothing.</summary>
    public const string OptionalSecretRefPattern = @"([^#@\s]+#[^#@\s]+(@[^#@\s]+)?)?";

    /// <summary>
    ///     A forwarding address. ⚠ Enforced per element by the reconciler, not the schema — see
    ///     <see cref="MailDomains.HostnamePattern" /> for why an array cannot carry a pattern here.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No whitespace, no comma, no colon, and that is a security property rather than a
    ///     style.</b> An address lands in a Postfix alias map, where a comma is a list separator and a
    ///     newline is a new entry; an address that could carry either could add a forwarding rule for
    ///     somebody else's mailbox.
    /// </remarks>
    public const string ForwardAddressPattern =
        """^[A-Za-z0-9._%+-]{1,64}@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$""";

    /// <summary>The body shape of api-version <see cref="V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the mailbox is billed in. The domain's."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The mailbox's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the domain's back end runs in. Must be the domain's."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/localPart",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The part of the address before the @, for example alice. The domain "
                    + "supplies the rest. Lower case letters, digits, dots, hyphens and underscores."
                ) {
                    Pattern = MailDomains.LocalPartPattern,
                    MaxLength = 64,
                    Immutable = true,
                    // ⚠ Required and patterned, so `./build.sh Charts` needs a default that satisfies
                    // the pattern — see /properties/domain on MailDomains for the same constraint.
                    // `postmaster` because RFC 5321 § 4.5.1 requires every domain to have one.
                    DefaultJson = "\"postmaster\"",
                    ExampleJson = "\"alice\""
                },
                new(
                    "/properties/quota",
                    SchemaKind.Text,
                    Description: "The most this mailbox may store, in Kubernetes quantity form, for "
                    + "example 5Gi. Empty means the domain's storage.mailboxQuota."
                ) { Pattern = MailDomains.OptionalQuantityPattern, DefaultJson = "\"\"" },
                new(
                    "/properties/passwordRef",
                    SchemaKind.Text,
                    Description: "A vault handle — path#field, optionally @version — whose value is the "
                    + "password this mailbox signs in to IMAP and submission with. Resolved and hashed "
                    + "when the mailbox is applied; the value never enters this body. The path must be "
                    + "under your tenant's vault prefix, tenants/<tenantId>/. Empty means the mailbox "
                    + "receives mail and nobody can sign in to it."
                ) {
                    Pattern = OptionalSecretRefPattern,
                    MaxLength = 512,
                    Widget = WidgetHint.SecretRef,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/aliases",
                    SchemaKind.Array,
                    Description: "Other local parts of the same domain that deliver here. An alias "
                    + "another mailbox already answers for is refused by name."
                ) { ElementKind = SchemaKind.Text, DefaultJson = "[]" },
                new(
                    "/properties/forwardTo",
                    SchemaKind.Array,
                    Description: "Addresses every message is also sent on to. Forwarding leaves the "
                    + "domain, so it is held with the rest of the domain's outbound mail until its "
                    + "DNS records verify."
                ) { ElementKind = SchemaKind.Text, DefaultJson = "[]" },
                new(
                    "/properties/keepCopy",
                    SchemaKind.Boolean,
                    Description: "With forwardTo set, whether this mailbox keeps a copy as well. "
                    + "Without forwardTo it has no effect."
                ) { DefaultJson = "true" }
            ]
        );

    // ── Reading a desired body ────────────────────────────────────────────────────────────────

    /// <summary>The local part a body asks for.</summary>
    public static string LocalPart(JsonElement desired) => Text(desired, "localPart").ToLowerInvariant();

    /// <summary>The quota override, or empty for the domain's.</summary>
    public static string Quota(JsonElement desired) => Text(desired, "quota");

    /// <summary>The password handle as the body spells it, or empty.</summary>
    public static string PasswordRef(JsonElement desired) => Text(desired, "passwordRef");

    /// <summary>The aliases, lower-cased, de-duplicated and sorted.</summary>
    /// <remarks>⚠ Sorted, so that two bodies naming the same aliases in two orders write one fragment.</remarks>
    public static ImmutableArray<string> Aliases(JsonElement desired) => List(desired, "aliases", lower: true);

    /// <summary>The forwarding addresses, de-duplicated and sorted.</summary>
    public static ImmutableArray<string> ForwardTo(JsonElement desired) => List(desired, "forwardTo", lower: false);

    /// <summary>Whether a forwarding mailbox keeps a copy.</summary>
    public static bool KeepCopy(JsonElement desired) =>
        Property(desired, "keepCopy") is not { ValueKind: JsonValueKind.False };

    /// <summary>
    ///     What in a body the schema could not check and the reconciler must: every alias and every
    ///     forwarding address, one at a time.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     <see langword="null" /> when the body is acceptable; otherwise the sentence and the JSON
    ///     Pointer of the first bad element.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Terminal, and checked before anything is resolved or written.</b> A bad alias can
    ///     never converge on any pass, and an address with a newline in it would be a line of
    ///     somebody else's Postfix map — see <see cref="ForwardAddressPattern" />.
    /// </remarks>
    public static (string Message, string Target)? Problem(JsonElement desired) {
        var local = LocalPart(desired);
        var aliases = Raw(desired, "aliases");

        for (var i = 0; i < aliases.Length; i++) {
            var alias = aliases[i].ToLowerInvariant();

            if (!Regex.IsMatch(alias, MailDomains.LocalPartPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) {
                return ($"The alias '{aliases[i]}' is not a local part: lower case letters, digits, dots, "
                    + "hyphens and underscores, starting and ending with a letter or digit.", Pointer("aliases", i));
            }

            if (alias == local) {
                return ($"The alias '{aliases[i]}' is the mailbox's own local part.", Pointer("aliases", i));
            }
        }

        var forwards = Raw(desired, "forwardTo");

        for (var i = 0; i < forwards.Length; i++) {
            if (forwards[i].Length > 254
                || !Regex.IsMatch(forwards[i], ForwardAddressPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) {
                return ($"'{forwards[i]}' is not an address mail can be forwarded to.", Pointer("forwardTo", i));
            }
        }

        return null;
    }

    /// <summary>
    ///     Parses the password handle, refusing a path outside the tenant's own vault prefix.
    /// </summary>
    /// <param name="spelled">The handle as the body spells it.</param>
    /// <param name="tenantId">The tenant whose mailbox carries it — the only tenant whose paths it may name.</param>
    /// <remarks>
    ///     ⚠ <b>THE SECOND PLACE IN THE TREE A TENANT-SPELLED VAULT PATH IS RESOLVED</b>, after
    ///     <c>VirtualMachines.ParseCloudInitRef</c>, and the rule is that one's for the same reason:
    ///     the platform resolves with one broad token, so the path is the whole discriminator, and a
    ///     handle naming <c>tenants/&lt;other&gt;/…</c> would hash another tenant's secret into a
    ///     password file this tenant can log in against — a password oracle for a value they cannot
    ///     read. Refused with <see cref="ErrorCode.AuthorizationFailed" />, naming the tenant's own
    ///     prefix and never whether the other path exists.
    /// </remarks>
    public static Result<SecretRef> ParsePasswordRef(string spelled, Guid tenantId) {
        if (string.IsNullOrWhiteSpace(spelled)) {
            return Result<SecretRef>.Success(new());
        }

        var hash = spelled.IndexOf('#', StringComparison.Ordinal);

        if (hash <= 0 || hash == spelled.Length - 1) {
            return Result<SecretRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{spelled}' is not a vault handle. Write path#field, optionally @version — the spelling "
                + "SecretRef prints.",
                "/properties/passwordRef"
            );
        }

        var path = spelled[..hash];
        var prefix = string.Create(CultureInfo.InvariantCulture, $"tenants/{tenantId:D}/");

        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) {
            return Result<SecretRef>.Failure(
                ErrorCode.AuthorizationFailed,
                $"passwordRef names '{path}', which is not under your tenant's vault prefix '{prefix}'. A "
                + "mailbox can only be given a password your own tenant holds.",
                "/properties/passwordRef"
            );
        }

        var rest = spelled[(hash + 1)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);

        return Result<SecretRef>.Success(
            new() { Path = path, Field = at < 0 ? rest : rest[..at], Version = at < 0 ? string.Empty : rest[(at + 1)..] }
        );
    }

    // ── What a mailbox writes ─────────────────────────────────────────────────────────────────

    /// <summary>The key of a mailbox's password file — what Dovecot opens for a login.</summary>
    /// <param name="localPart">The mailbox's local part.</param>
    public static string PasswdKey(string localPart) => localPart + ".passwd";

    /// <summary>The key of a mailbox's alias-map lines — what the Postfix start script concatenates.</summary>
    /// <param name="localPart">The mailbox's local part.</param>
    public static string VirtualKey(string localPart) => localPart + ".virtual";

    /// <summary>The key that claims one address for one mailbox.</summary>
    /// <param name="localPart">The address's local part — the mailbox's own, or an alias.</param>
    public static string ClaimKey(string localPart) => localPart + ".claim";

    /// <summary>The annotation the domain puts on its mailbox <c>Secret</c> naming the mail domain.</summary>
    /// <remarks>
    ///     ⚠ <b>How a mailbox learns its domain, and why it cannot ask.</b> The mailbox's address
    ///     carries the parent's <i>name</i> (<c>example-com</c>), never its
    ///     <c>properties.domain</c>, and a reconciler cannot read another resource's body without a
    ///     role grant the tenant would have to write (<c>IResourceView</c>). The domain's reconciler
    ///     already writes this object, so it says the domain here, and the mailbox reads it off the
    ///     object it is about to write onto anyway. An object without the annotation is a domain that
    ///     has not converged, and the mailbox waits.
    /// </remarks>
    public const string DomainAnnotation = "cybercloud.io/mail-domain";

    /// <summary>The Dovecot <c>passwd-file</c> line for a mailbox.</summary>
    /// <param name="address">The full address, which is the login.</param>
    /// <param name="passwordHash">
    ///     A <see cref="Sha512Crypt" /> hash, or <see langword="null" /> for a mailbox nobody signs in to.
    /// </param>
    /// <param name="quota">A Kubernetes quantity, or empty for the domain's default.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No password is not an empty password field — that means ANY password.</b> Dovecot's
    ///         <c>passwd-file</c> treats an empty second field as "no password check". A mailbox with
    ///         no handle is rendered <c>{CRYPT}!</c>, a hash no input produces, and <c>nologin=y</c>
    ///         besides; the cluster-backed suite logs in to one with <c>!</c>, an empty string and a
    ///         real password and is refused all three times. ⚠ Sabotage-tested on 2026-09-23 by
    ///         rendering the empty field: Dovecot 2.4.5 signed the password-less mailbox in with an
    ///         empty password (<c>MailDataPlaneTests</c> went red, as did <c>MailPasswordTests</c>);
    ///         2.3.21.1 happened to refuse it, which is why the test runs both majors.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The quota is written twice, once per Dovecot major.</b> 2.4 reads
    ///         <c>quota_storage_size</c>, 2.3 reads <c>quota_rule</c>, and each ignores the other's —
    ///         which is what lets a mailbox write one line without knowing its domain's version. Both
    ///         were read back with <c>doveadm quota get</c> against each image.
    ///     </para>
    /// </remarks>
    public static string PasswdLine(string address, string? passwordHash, string quota) {
        ArgumentException.ThrowIfNullOrEmpty(address);

        var line = new StringBuilder(address)
            .Append(':')
            .Append(passwordHash is null ? "{CRYPT}!" : "{SHA512-CRYPT}" + passwordHash)
            .Append("::::::");

        var fields = new List<string>();

        if (passwordHash is null) {
            fields.Add("nologin=y");
        }

        if (quota.Length > 0) {
            var size = MailDomains.DovecotSize(quota);

            fields.Add("userdb_quota_storage_size=" + size);
            fields.Add("userdb_quota_rule=*:storage=" + size);
        }

        return line.Append(string.Join(' ', fields)).Append('\n').ToString();
    }

    /// <summary>The Postfix alias-map lines for a mailbox: itself or its forwards, then each alias.</summary>
    /// <param name="domain">The mail domain.</param>
    /// <param name="localPart">The mailbox's local part.</param>
    /// <param name="aliases">The aliases, validated.</param>
    /// <param name="forwardTo">The forwarding addresses, validated.</param>
    /// <param name="keepCopy">Whether a forwarding mailbox keeps a copy.</param>
    /// <remarks>
    ///     ⚠ <b>The mailbox always has a line of its own, even when it forwards nowhere.</b>
    ///     <c>alice@d alice@d</c> is a no-op for Postfix, and it is what stops a domain's catch-all
    ///     (<c>@d catchall@d</c>) from swallowing mail for a mailbox that exists: an address with its
    ///     own entry never falls through to the domain's.
    /// </remarks>
    public static string VirtualLines(
        string domain,
        string localPart,
        ImmutableArray<string> aliases,
        ImmutableArray<string> forwardTo,
        bool keepCopy
    ) {
        var address = localPart + "@" + domain;
        var targets = forwardTo.IsEmpty
            ? [address]
            : keepCopy ? [address, .. forwardTo] : forwardTo.ToArray();

        var lines = new StringBuilder()
            .Append(address).Append(' ').Append(string.Join(", ", targets)).Append('\n');

        foreach (var alias in aliases) {
            lines.Append(alias).Append('@').Append(domain).Append(' ').Append(address).Append('\n');
        }

        return lines.ToString();
    }

    /// <summary>The fragment a mailbox co-writes onto its domain's mailbox <c>Secret</c>.</summary>
    /// <param name="mailboxId">The mailbox resource's GUID — the claim's value.</param>
    /// <param name="localPart">The mailbox's local part.</param>
    /// <param name="passwdLine">What <see cref="PasswdLine" /> rendered.</param>
    /// <param name="virtualLines">What <see cref="VirtualLines" /> rendered.</param>
    /// <param name="aliases">The aliases, each of which is claimed.</param>
    public static string FragmentJson(
        Guid mailboxId,
        string localPart,
        string passwdLine,
        string virtualLines,
        ImmutableArray<string> aliases
    ) {
        var claim = Base64(mailboxId.ToString("D", CultureInfo.InvariantCulture));
        var data = new JsonObject {
            [PasswdKey(localPart)] = Base64(passwdLine),
            [VirtualKey(localPart)] = Base64(virtualLines),
            [ClaimKey(localPart)] = claim
        };

        foreach (var alias in aliases) {
            data[ClaimKey(alias)] = claim;
        }

        return new JsonObject { ["data"] = data }.ToJsonString();
    }

    /// <summary>Whether the mailbox <c>Secret</c> read back carries a fragment's every key, as written.</summary>
    /// <param name="objectJson">The <c>Secret</c> as the API server returned it.</param>
    /// <param name="fragmentJson">What <see cref="FragmentJson" /> rendered.</param>
    public static bool Carries(string objectJson, string fragmentJson) {
        try {
            var data = JsonNode.Parse(objectJson)?["data"] as JsonObject;
            var wanted = JsonNode.Parse(fragmentJson)?["data"] as JsonObject;

            return data is not null
                && wanted is not null
                && wanted.All(x => data[x.Key]?.GetValue<string>() == x.Value?.GetValue<string>());
        } catch (JsonException) {
            return false;
        }
    }

    /// <summary>The mail domain the domain's reconciler wrote onto its mailbox <c>Secret</c>, or empty.</summary>
    /// <param name="objectJson">The <c>Secret</c> as the API server returned it.</param>
    public static string DomainOf(string objectJson) {
        try {
            return JsonNode.Parse(objectJson)?["metadata"]?["annotations"]?[DomainAnnotation]?.GetValue<string>()
                ?? string.Empty;
        } catch (JsonException) {
            return string.Empty;
        }
    }

    /// <summary>A valid desired body, for tests and the conformance case.</summary>
    /// <param name="clusterId">The domain's cluster.</param>
    /// <param name="localPart">The local part.</param>
    /// <param name="passwordRef">The password handle, or empty.</param>
    /// <param name="quota">The quota override, or empty.</param>
    /// <param name="aliases">The aliases.</param>
    /// <param name="forwardTo">The forwarding addresses.</param>
    /// <param name="keepCopy">Whether a forwarding mailbox keeps a copy.</param>
    /// <param name="location">The billing region.</param>
    public static string Body(
        Guid clusterId,
        string localPart = "alice",
        string passwordRef = "",
        string quota = "",
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? forwardTo = null,
        bool keepCopy = true,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["localPart"] = localPart,
                ["quota"] = quota,
                ["passwordRef"] = passwordRef,
                ["aliases"] = new JsonArray([.. (aliases ?? []).Select(static x => (JsonNode?)JsonValue.Create(x))]),
                ["forwardTo"] = new JsonArray([.. (forwardTo ?? []).Select(static x => (JsonNode?)JsonValue.Create(x))]),
                ["keepCopy"] = keepCopy
            }
        }.ToJsonString();

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static string Pointer(string array, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"/properties/{array}/{index}");

    static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string name) =>
        Property(desired, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    static string[] Raw(JsonElement desired, string name) =>
        Property(desired, name) is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray().Where(static x => x.ValueKind is JsonValueKind.String).Select(static x => x.GetString() ?? string.Empty)]
            : [];

    static ImmutableArray<string> List(JsonElement desired, string name, bool lower) =>
        [
            .. Raw(desired, name)
                .Where(static x => x.Length > 0)
                .Select(x => lower ? x.ToLowerInvariant() : x)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
        ];
}
