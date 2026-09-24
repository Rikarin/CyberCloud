using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Contracts;

/// <summary>Whether a domain may send beyond itself — the gate doc 17 § Deliverability requires.</summary>
/// <remarks>
///     ⚠ <b>Three states and not a flag</b>, because the two reasons to close the gate are answered
///     by different people and the SMTP refusal has to say which: DNS is the tenant's to fix,
///     suspension is the abuse desk's. <see cref="Held" /> is the default, so a domain nothing has
///     verified yet sends nothing beyond itself.
/// </remarks>
public enum MailSending {
    /// <summary>The SPF, DKIM and DMARC records do not all verify. Local delivery only.</summary>
    Held = 0,

    /// <summary>They verify and nothing suspends the domain. Authenticated senders may relay.</summary>
    Open = 1,

    /// <summary>The platform's abuse desk has suspended the domain or its tenant. Local delivery only.</summary>
    Suspended = 2
}

/// <summary>
///     The platform's side of every mail domain's DNS: where its MX points, what its SPF includes,
///     where MTA-STS and TLS-RPT reports go. Configuration, bound from <see cref="Section" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>EVERY NAME HERE IS A SHARED FRONT DOOR, AND NONE OF THEM IS BUILT.</b> doc 17 §
///         Topology's inbound pool is what <see cref="InboundHost" /> names and its outbound pool is
///         what <see cref="SpfInclude" /> authorizes; both are
///         <c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-shared-pools-are-not-built</c>.
///         So a region that configures these is promising tenants records that point at hosts it has
///         to stand up — and a region that does not gets a refusal from <c>dnsRecords</c> naming this
///         section, never a placeholder the tenant would publish.
///     </para>
///     <para>
///         ⚠ <b>A plain record rather than <c>IOptions</c>.</b> <c>.Contracts</c> takes no
///         dependency beyond the resource manager's contracts; the application module binds it.
///     </para>
/// </remarks>
public sealed record MailPlatformOptions {
    /// <summary>The configuration section the application module binds.</summary>
    public const string Section = "CyberCloud:Mail";

    /// <summary>The shared inbound pool's hostname — every domain's MX.</summary>
    public string InboundHost { get; init; } = string.Empty;

    /// <summary>The domain whose SPF record lists the platform's outbound addresses.</summary>
    public string SpfInclude { get; init; } = string.Empty;

    /// <summary>The host that serves every domain's MTA-STS policy, which <c>mta-sts.{domain}</c> is a CNAME to.</summary>
    public string MtaStsHost { get; init; } = string.Empty;

    /// <summary>The mailbox TLS-RPT reports are sent to.</summary>
    public string TlsReportAddress { get; init; } = string.Empty;

    /// <summary>Tenants whose outbound mail the abuse desk has suspended, by tenant id.</summary>
    /// <remarks>
    ///     ⚠ <b>The suspension seam, and the smallest one that is real.</b> doc 17 § Deliverability
    ///     requires a human who can suspend a tenant within the hour. What this platform can enforce
    ///     today is the gate: a tenant listed here renders <see cref="MailSending.Suspended" /> on the
    ///     next pass and on every <c>verify</c>. What it cannot do is make the next pass happen — a
    ///     converged domain is not reconciled again until something writes it — so the hour is not
    ///     met by this list alone: <c>charts/managed/mail/conformance.yaml § owed</c>,
    ///     <c>suspension-has-no-trigger</c>.
    ///     <para>
    ///         ⚠ <b>Not <c>TenantStatus.Suspended</c>, and the reason is docs/plan/06 § Tenant
    ///         lifecycle.</b> That status is what an overdue invoice sets, and its row says the data
    ///         plane keeps running — <i>"suspending a tenant should not take their production down
    ///         without notice"</i>. Stopping a tenant's outbound mail because a payment is late would
    ///         be exactly that. An abuse suspension is a different decision by a different person, so
    ///         it is a different list.
    ///     </para>
    /// </remarks>
    public ImmutableArray<Guid> SuspendedTenants { get; init; } = [];

    /// <summary>Whether every host a record names is configured.</summary>
    public bool IsConfigured =>
        InboundHost.Length > 0 && SpfInclude.Length > 0 && MtaStsHost.Length > 0 && TlsReportAddress.Length > 0;

    /// <summary>Whether a tenant's outbound mail is suspended.</summary>
    /// <param name="tenantId">The domain's tenant.</param>
    public bool Suspends(Guid tenantId) => SuspendedTenants.Contains(tenantId);
}

/// <summary>One record a domain has to publish.</summary>
/// <param name="Role">Which requirement it satisfies — one of <see cref="MailDnsRecords.Roles" />.</param>
/// <param name="Name">The fully-qualified owner name, without the trailing dot.</param>
/// <param name="Kind"><c>MX</c>, <c>TXT</c> or <c>CNAME</c>.</param>
/// <param name="Value">The value, exactly as it must appear.</param>
/// <param name="GatesSending">Whether sending stays held until this record verifies.</param>
/// <param name="Purpose">What the record is for, for a person.</param>
public readonly record struct MailDnsRecord(
    string Role,
    string Name,
    string Kind,
    string Value,
    bool GatesSending,
    string Purpose
);

/// <summary>What resolving one record found.</summary>
/// <param name="Resolved">
///     Whether the resolver answered at all. <see langword="false" /> is a timeout or a server
///     failure; an empty <paramref name="Values" /> with <see langword="true" /> is an answer that
///     the name has no such record.
/// </param>
/// <param name="Values">
///     The answers: a TXT record's strings joined, an MX as <c>preference exchange</c>, a CNAME's
///     target — each without a trailing dot.
/// </param>
/// <param name="Error">Why the resolver did not answer, when it did not.</param>
public readonly record struct MailDnsAnswer(bool Resolved, ImmutableArray<string> Values, string Error);

/// <summary>One record's verification.</summary>
/// <param name="Record">What was required.</param>
/// <param name="Status">One of <see cref="MailDnsRecords.Verified" />, <c>missing</c>, <c>mismatch</c> or <c>unresolvable</c>.</param>
/// <param name="Found">What the resolver answered, for the tenant to compare.</param>
/// <param name="Detail">Why, in a sentence.</param>
public readonly record struct MailDnsCheck(MailDnsRecord Record, string Status, ImmutableArray<string> Found, string Detail);

/// <summary>
///     The records a mail domain must publish, and whether what the DNS answers matches them.
/// </summary>
/// <remarks>
///     <para>
///         [17 § Deliverability](../../../../docs/plan/17-communication-and-email.md) generates SPF,
///         DKIM and DMARC per domain and says <i>"the platform will not enable sending until the DNS
///         records verify"</i>. <see cref="TryRequired" /> is the generation and
///         <see cref="Verify" /> is the comparison; both are pure, and the resolving between them is
///         the implementation assembly's <c>IMailDnsResolver</c>.
///     </para>
///     <para>
///         ⚠ <b>Seven records, and three of them gate.</b> SPF, DKIM and DMARC decide whether a
///         receiver accepts mail <i>from</i> the domain, so they are the gate. The MX decides whether
///         mail reaches the domain, and a domain can send before it receives. MTA-STS (its TXT and
///         its policy host) and TLS-RPT harden inbound TLS and report on it; they are returned so a
///         tenant publishes them once rather than twice, and reported by <c>verify</c>, and they
///         never hold sending.
///     </para>
/// </remarks>
public static class MailDnsRecords {
    /// <summary>The MX role.</summary>
    public const string Mx = "mx";

    /// <summary>The SPF role.</summary>
    public const string Spf = "spf";

    /// <summary>The DKIM role.</summary>
    public const string Dkim = "dkim";

    /// <summary>The DMARC role.</summary>
    public const string Dmarc = "dmarc";

    /// <summary>The MTA-STS TXT role.</summary>
    public const string MtaSts = "mtaSts";

    /// <summary>The MTA-STS policy host role.</summary>
    public const string MtaStsHost = "mtaStsHost";

    /// <summary>The TLS-RPT role.</summary>
    public const string TlsRpt = "tlsRpt";

    /// <summary>Every role, in the order the records are returned.</summary>
    public static ImmutableArray<string> Roles { get; } = [Mx, Spf, Dkim, Dmarc, MtaSts, MtaStsHost, TlsRpt];

    /// <summary>A record that verifies.</summary>
    public const string Verified = "verified";

    /// <summary>A name with no record of the kind.</summary>
    public const string Missing = "missing";

    /// <summary>A record that exists and says something else.</summary>
    public const string Mismatch = "mismatch";

    /// <summary>A name the resolver could not answer for.</summary>
    public const string Unresolvable = "unresolvable";

    /// <summary>
    ///     The seven records for one domain and one DKIM key.
    /// </summary>
    /// <param name="domain">The mail domain — the body's <c>properties.domain</c>, never the resource's name.</param>
    /// <param name="dkimPrivateKeyPem">
    ///     The PKCS#8 PEM the vault holds — <see cref="MailDomains.DkimPrivateKeyField" />, read back
    ///     rather than freshly generated.
    /// </param>
    /// <param name="platform">The platform's hosts.</param>
    /// <param name="records">The seven records, or empty when the key does not parse.</param>
    /// <returns><see langword="true" /> when the key parsed and the records could be derived.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The SPF record ends <c>-all</c> and not <c>~all</c>.</b> A soft fail asks the
    ///         receiver to accept the message anyway and mark it, which means an attacker forging
    ///         this domain gets delivery. ⚠ What <see cref="Verify" /> requires of a published SPF is
    ///         less than this value: one <c>v=spf1</c> record that includes the platform. A tenant who
    ///         also sends through somebody else has to be able to say so in the same record.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The DMARC policy is <c>quarantine</c> rather than <c>reject</c></b>. A domain
    ///         publishing <c>p=reject</c> on day one loses legitimate mail from every forwarder and
    ///         mailing list it uses, which is the failure mode that makes an operator turn DMARC off
    ///         entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The MTA-STS <c>id</c> is a hash of the policy, not a timestamp.</b> A sender
    ///         re-fetches the policy only when the id changes, so an id minted per call would make
    ///         every <c>dnsRecords</c> a record the tenant has to republish — and the policy's
    ///         <c>mode</c> is <c>testing</c>, because <c>enforce</c> promises a certificate on the MX
    ///         that the unbuilt inbound pool cannot yet present.
    ///     </para>
    /// </remarks>
    public static bool TryRequired(
        string domain,
        string dkimPrivateKeyPem,
        MailPlatformOptions platform,
        out ImmutableArray<MailDnsRecord> records
    ) {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentNullException.ThrowIfNull(platform);

        if (!MailDomains.TryDkimPublicKey(dkimPrivateKeyPem, out var published)) {
            records = [];

            return false;
        }

        records = [
            new(
                Mx,
                domain,
                "MX",
                "10 " + platform.InboundHost,
                false,
                "Delivery — where the internet hands mail for this domain to the platform."
            ),
            new(
                Spf,
                domain,
                "TXT",
                "v=spf1 include:" + platform.SpfInclude + " -all",
                true,
                "SPF — which hosts may send as this domain. Anything else is a forgery."
            ),
            new(
                Dkim,
                MailDomains.DkimSelector + "._domainkey." + domain,
                "TXT",
                "v=DKIM1; k=rsa; p=" + published,
                true,
                "DKIM — the public half of the key this domain's outbound mail is signed with."
            ),
            new(
                Dmarc,
                "_dmarc." + domain,
                "TXT",
                "v=DMARC1; p=quarantine; rua=mailto:dmarc@" + domain + "; fo=1",
                true,
                "DMARC — what a receiver should do when SPF and DKIM both fail, and where to report."
            ),
            new(
                MtaSts,
                "_mta-sts." + domain,
                "TXT",
                "v=STSv1; id=" + MtaStsPolicyId(domain, platform),
                false,
                "MTA-STS — tells senders a policy exists and which version it is."
            ),
            new(
                MtaStsHost,
                "mta-sts." + domain,
                "CNAME",
                platform.MtaStsHost,
                false,
                "MTA-STS — where senders fetch the policy, over HTTPS."
            ),
            new(
                TlsRpt,
                "_smtp._tls." + domain,
                "TXT",
                "v=TLSRPTv1; rua=mailto:" + platform.TlsReportAddress,
                false,
                "TLS-RPT — where senders report failed TLS to this domain's MX."
            )
        ];

        return true;
    }

    /// <summary>The MTA-STS policy <c>mta-sts.{domain}/.well-known/mta-sts.txt</c> must serve.</summary>
    /// <param name="domain">The mail domain.</param>
    /// <param name="platform">The platform's hosts.</param>
    public static string MtaStsPolicy(string domain, MailPlatformOptions platform) {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentNullException.ThrowIfNull(platform);

        return "version: STSv1\r\nmode: testing\r\nmx: " + platform.InboundHost + "\r\nmax_age: 604800\r\n";
    }

    /// <summary>The policy id: the first sixteen hex digits of the policy's SHA-256.</summary>
    static string MtaStsPolicyId(string domain, MailPlatformOptions platform) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(MtaStsPolicy(domain, platform))))[..16];

    /// <summary>The records as zone-file lines, ready to paste into a zone.</summary>
    /// <param name="records">What <see cref="TryRequired" /> returned.</param>
    /// <remarks>
    ///     ⚠ <b>A TXT value longer than 255 bytes is split into quoted strings</b>, because that is
    ///     the most one character-string can carry (RFC 1035 § 3.3). A 2048-bit DKIM key's record is
    ///     about 410, so its line always has two; a resolver joins them back, which is why
    ///     <see cref="Verify" /> compares joined values.
    /// </remarks>
    public static string ZoneFile(ImmutableArray<MailDnsRecord> records) {
        var zone = new StringBuilder();

        foreach (var record in records) {
            zone.Append(CultureInfo.InvariantCulture, $"{record.Name}. 3600 IN {record.Kind} ");

            zone.Append(
                record.Kind switch {
                    "TXT" => string.Join(" ", Chunks(record.Value).Select(static x => "\"" + x.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"")),
                    _ => record.Value + "."
                }
            );

            zone.Append('\n');
        }

        return zone.ToString();
    }

    /// <summary>
    ///     Compares what the DNS answered with what is required, record by record, and decides the gate.
    /// </summary>
    /// <param name="records">The required records.</param>
    /// <param name="answer">
    ///     The resolver's answer for a name and a kind. Called once per record; the caller resolves.
    /// </param>
    /// <param name="checks">One check per record, in the same order.</param>
    /// <returns>
    ///     <see langword="true" /> when every record that <see cref="MailDnsRecord.GatesSending" />
    ///     verified.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Each role has its own rule, and exact comparison is right for only one.</b>
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>DKIM</b> is exact on the key: the published <c>p=</c> must be the public half of
    ///             the key the signer holds, whatever the other tags say. This is the one that fails
    ///             silently everywhere else — see <see cref="MailDomains" /> on the mint-once rule.
    ///         </item>
    ///         <item>
    ///             <b>SPF</b> must be exactly one <c>v=spf1</c> record (RFC 7208 § 4.5 makes two a
    ///             permanent error at every receiver) that includes the platform.
    ///         </item>
    ///         <item>
    ///             <b>DMARC</b> must be exactly one <c>v=DMARC1</c> record with a <c>p=</c> tag. The
    ///             policy the tenant chooses is theirs; <c>none</c> still makes the domain's
    ///             alignment reports arrive, which is what the gate needs to exist for.
    ///         </item>
    ///         <item>
    ///             <b>MX</b> is verified by any exchange equal to the inbound pool, and the MTA-STS
    ///             and TLS-RPT records by their version tag and their target.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static bool Verify(
        ImmutableArray<MailDnsRecord> records,
        Func<MailDnsRecord, MailDnsAnswer> answer,
        out ImmutableArray<MailDnsCheck> checks
    ) {
        ArgumentNullException.ThrowIfNull(answer);

        var results = ImmutableArray.CreateBuilder<MailDnsCheck>(records.Length);

        foreach (var record in records) {
            results.Add(Check(record, answer(record)));
        }

        checks = results.MoveToImmutable();

        return checks.Where(static x => x.Record.GatesSending).All(static x => x.Status == Verified);
    }

    static MailDnsCheck Check(MailDnsRecord record, MailDnsAnswer answer) {
        if (!answer.Resolved) {
            return new(record, Unresolvable, [], "The resolver did not answer: " + answer.Error);
        }

        var values = answer.Values.IsDefault ? [] : answer.Values;

        if (values.Length == 0) {
            return new(record, Missing, values, $"{record.Name} has no {record.Kind} record.");
        }

        return record.Role switch {
            Dkim => CheckDkim(record, values),
            Spf => CheckSingle(record, values, "v=spf1", x => Terms(x).Contains("include:" + Include(record), StringComparer.OrdinalIgnoreCase), "does not include the platform"),
            Dmarc => CheckSingle(record, values, "v=DMARC1", static x => Tags(x).ContainsKey("p"), "has no p= policy"),
            MtaSts => CheckSingle(record, values, "v=STSv1", static x => Tags(x).ContainsKey("id"), "has no id="),
            TlsRpt => CheckSingle(record, values, "v=TLSRPTv1", static x => Tags(x).ContainsKey("rua"), "has no rua="),
            Mx => values.Any(x => SameHost(x.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty, record.Value.Split(' ')[^1]))
                ? new(record, Verified, values, "An exchange is the platform's inbound pool.")
                : new(record, Mismatch, values, "No exchange is the platform's inbound pool, so mail for the domain goes elsewhere."),
            _ => values.Any(x => SameHost(x, record.Value))
                ? new(record, Verified, values, "The name points at the platform's host.")
                : new(record, Mismatch, values, "The name points somewhere other than the platform's host.")
        };
    }

    static MailDnsCheck CheckDkim(MailDnsRecord record, ImmutableArray<string> values) {
        var wanted = Tags(record.Value)["p"];
        var keys = values.Select(static x => Tags(x).GetValueOrDefault("p", string.Empty)).ToArray();

        return keys.Any(x => string.Equals(x, wanted, StringComparison.Ordinal))
            ? new(record, Verified, values, "The published key is the public half of the key the signer holds.")
            : new(
                record,
                Mismatch,
                values,
                "The published p= is not the public half of the key the signer holds, so every message "
                + "signed with it fails DKIM. Publish the value dnsRecords returns."
            );
    }

    static MailDnsCheck CheckSingle(
        MailDnsRecord record,
        ImmutableArray<string> values,
        string version,
        Func<string, bool> rule,
        string failure
    ) {
        var candidates = values
            .Where(x => x.TrimStart().StartsWith(version, StringComparison.OrdinalIgnoreCase)
                && (x.TrimStart().Length == version.Length || x.TrimStart()[version.Length] is ' ' or ';'))
            .ToArray();

        return candidates.Length switch {
            0 => new(record, Missing, values, $"{record.Name} has no {version} record."),
            > 1 => new(
                record,
                Mismatch,
                values,
                $"{record.Name} has {candidates.Length.ToString(CultureInfo.InvariantCulture)} {version} records, and a "
                + "receiver treats more than one as a permanent error. Publish exactly one."
            ),
            _ => rule(candidates[0])
                ? new(record, Verified, values, $"{record.Name} carries one {version} record that satisfies the platform.")
                : new(record, Mismatch, values, $"{record.Name}'s {version} record {failure}.")
        };
    }

    /// <summary>The SPF include the required record names.</summary>
    static string Include(MailDnsRecord record) =>
        Terms(record.Value).First(static x => x.StartsWith("include:", StringComparison.Ordinal))["include:".Length..];

    static string[] Terms(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A tag-list record (DKIM, DMARC, MTA-STS, TLS-RPT) as a dictionary, whitespace removed from values.</summary>
    static Dictionary<string, string> Tags(string value) {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0) {
                continue;
            }

            var tag = part[..equals].Trim();
            var tagValue = string.Concat(part[(equals + 1)..].Where(static x => !char.IsWhiteSpace(x)));

            tags.TryAdd(tag, tagValue);
        }

        return tags;
    }

    static bool SameHost(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('.'), b.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Chunks(string value) {
        for (var i = 0; i < value.Length; i += 255) {
            yield return value.Substring(i, Math.Min(255, value.Length - i));
        }
    }

    // ── The two action bodies ─────────────────────────────────────────────────────────────────

    /// <summary>What <c>dnsRecords</c> and <c>verify</c> answer — one nested object per role.</summary>
    /// <param name="includeVerification">
    ///     <see langword="true" /> for <c>verify</c>, which adds <c>/sendingEnabled</c> and each
    ///     record's <c>status</c>, <c>found</c> and <c>detail</c>.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>One nested object per role rather than an array of records</b>, because this
    ///     platform's schema has no array of objects (<c>SchemaKind.Array</c>'s remarks), and seven
    ///     named members type better in every SDK than seven lines of text would.
    /// </remarks>
    public static ResourceSchema ResponseSchema(bool includeVerification) {
        var properties = new List<SchemaProperty> {
            new("/domain", SchemaKind.Text, true, Description: "The mail domain the records are for."),
            new(
                "/zoneFile",
                SchemaKind.Text,
                true,
                Description: "Every record as a zone-file line, ready to paste into a zone."
            ),
            new(
                "/mtaStsPolicy",
                SchemaKind.Text,
                true,
                Description: "The policy https://mta-sts.{domain}/.well-known/mta-sts.txt must serve."
            )
        };

        if (includeVerification) {
            properties.Add(
                new(
                    "/sendingEnabled",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether mail may leave the domain: SPF, DKIM and DMARC verify and the "
                    + "platform has not suspended it. Held mail is refused at RCPT TO, not queued."
                )
            );
            properties.Add(
                new(
                    "/sending",
                    SchemaKind.Text,
                    true,
                    Description: "held, open or suspended — why sendingEnabled is what it is."
                ) { AllowedValues = ["held", "open", "suspended"] }
            );
        }

        properties.Add(new("/records", SchemaKind.Nested, true, Description: "One member per record the domain must publish."));

        foreach (var role in Roles) {
            var at = "/records/" + role;

            properties.Add(new(at, SchemaKind.Nested, true, Description: RoleDescription(role)));
            properties.Add(new(at + "/name", SchemaKind.Text, true, Description: "The owner name, fully qualified."));
            properties.Add(new(at + "/type", SchemaKind.Text, true, Description: "MX, TXT or CNAME.") { AllowedValues = ["MX", "TXT", "CNAME"] });
            properties.Add(new(at + "/value", SchemaKind.Text, true, Description: "The value to publish, exactly."));
            properties.Add(new(at + "/gatesSending", SchemaKind.Boolean, true, Description: "Whether sending is held until this record verifies."));

            if (includeVerification) {
                properties.Add(
                    new(at + "/status", SchemaKind.Text, true, Description: "What resolving it found.") {
                        AllowedValues = [Verified, Missing, Mismatch, Unresolvable]
                    }
                );
                properties.Add(
                    new(at + "/found", SchemaKind.Array, true, Description: "What the DNS answered for the name.") {
                        ElementKind = SchemaKind.Text
                    }
                );
                properties.Add(new(at + "/detail", SchemaKind.Text, true, Description: "Why, in a sentence."));
            }
        }

        return ResourceSchema.Of([.. properties]);
    }

    static string RoleDescription(string role) =>
        role switch {
            Mx => "The MX record — where mail for the domain is delivered.",
            Spf => "The SPF record. Gates sending.",
            Dkim => "The DKIM public key. Gates sending.",
            Dmarc => "The DMARC policy. Gates sending.",
            MtaSts => "The MTA-STS policy announcement.",
            MtaStsHost => "The MTA-STS policy host.",
            _ => "The TLS-RPT reporting address."
        };

    /// <summary>The <c>dnsRecords</c> body.</summary>
    /// <param name="domain">The mail domain.</param>
    /// <param name="records">The required records.</param>
    /// <param name="platform">The platform's hosts.</param>
    public static string RecordsJson(string domain, ImmutableArray<MailDnsRecord> records, MailPlatformOptions platform) =>
        Envelope(domain, records, platform, null, null).ToJsonString();

    /// <summary>The <c>verify</c> body.</summary>
    /// <param name="domain">The mail domain.</param>
    /// <param name="checks">One check per required record.</param>
    /// <param name="platform">The platform's hosts.</param>
    /// <param name="sending">The gate the checks and the suspension seam decided.</param>
    public static string VerificationJson(
        string domain,
        ImmutableArray<MailDnsCheck> checks,
        MailPlatformOptions platform,
        MailSending sending
    ) =>
        Envelope(domain, [.. checks.Select(static x => x.Record)], platform, checks, sending).ToJsonString();

    static JsonObject Envelope(
        string domain,
        ImmutableArray<MailDnsRecord> records,
        MailPlatformOptions platform,
        ImmutableArray<MailDnsCheck>? checks,
        MailSending? sending
    ) {
        var byRole = new JsonObject();

        for (var i = 0; i < records.Length; i++) {
            var record = records[i];
            var entry = new JsonObject {
                ["name"] = record.Name,
                ["type"] = record.Kind,
                ["value"] = record.Value,
                ["gatesSending"] = record.GatesSending
            };

            if (checks is { } verified) {
                var check = verified[i];

                entry["status"] = check.Status;
                entry["found"] = new JsonArray([.. check.Found.Select(static x => (JsonNode?)JsonValue.Create(x))]);
                entry["detail"] = check.Detail;
            }

            byRole[record.Role] = entry;
        }

        var envelope = new JsonObject {
            ["domain"] = domain,
            ["zoneFile"] = ZoneFile(records),
            ["mtaStsPolicy"] = MtaStsPolicy(domain, platform)
        };

        if (sending is { } gate) {
            envelope["sendingEnabled"] = gate == MailSending.Open;
            envelope["sending"] = gate.ToString().ToLowerInvariant();
        }

        envelope["records"] = byRole;

        return envelope;
    }

    /// <summary>The gate for a verification result and a tenant.</summary>
    /// <param name="verified">What <see cref="Verify" /> returned.</param>
    /// <param name="tenantId">The domain's tenant.</param>
    /// <param name="platform">The platform's configuration, which carries the suspensions.</param>
    /// <remarks>⚠ Suspension wins over verification: a suspended domain with perfect records sends nothing.</remarks>
    public static MailSending Gate(bool verified, Guid tenantId, MailPlatformOptions platform) {
        ArgumentNullException.ThrowIfNull(platform);

        return platform.Suspends(tenantId) ? MailSending.Suspended : verified ? MailSending.Open : MailSending.Held;
    }
}
