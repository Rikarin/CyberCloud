// ⚠ For SecretRef, which the DKIM block below hands to ISecretWriter and ISecretResolver.

using CyberCloud.Core.Contracts;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Mail/domains</c>: the type, its api-version, its
///     body shape, the five Kubernetes objects it becomes, and the four DNS records a domain must
///     publish before the platform will send for it.
/// </summary>
/// <remarks>
///     <para>
///         [17 § <c>CyberCloud.Mail</c>](../../../../docs/plan/17-communication-and-email.md) · M2 ·
///         3.5 EM. ⚠
///         <b>
///             Azure has no equivalent, and doc 17 says why in a sentence worth keeping in
///             front of anyone changing this file
///         </b>:
///         <i>
///             "email hosting is mostly a reputation and
///             abuse-management problem"
///         </i>, and <i>"software is maybe 30 % of this product"</i>. Nothing
///         in this assembly is the hard part of the row it belongs to.
///     </para>
///     <para>
///         ⚠ <b>THE ROW WAS GATED ON AN OPERATIONAL COMMITMENT AND THAT COMMITMENT IS MADE.</b>
///         docs/plan/25 row 2 closed on 2026-08-11: the abuse desk is staffed, and doc 17 requires
///         <c>abuse@</c> monitored by a human
///         <b>
///             with the authority to suspend a tenant within the
///             hour
///         </b>. That is not a thing this code can assert, and it is the condition under which
///         this code is allowed to exist at all — docs/plan/25 § R7 is explicit that building the
///         module without staffing the desk is the one path that ends with the platform's address
///         blocks listed and its <i>own</i> transactional email — OTPs, alerts, invoices — failing
///         with them.
///     </para>
///     <para>
///         ⚠
///         <b>
///             DOVECOT, NOT CYRUS, AND THE OPEN QUESTION THIS ROW IS FILED UNDER DOES NOT ACTUALLY
///             BLOCK IT.
///         </b> docs/plan/25 row 4 still reads as open and its "blocks" column says
///         <i>"the mail module's shape"</i>. Doc 17 answers the question in the other direction and
///         says so twice:
///         <i>
///             "This is a recommendation, not a countermand — if there is a reason for
///             Cyrus that is not visible here, the rest of the design is unchanged,
///             <b>
///                 because the seam
///                 is LMTP and IMAP either way
///             </b>"
///         </i>, and docs/plan/25's own § Corrections lists
///         <i>"Cyrus → Dovecot"</i> among six decisions it calls <b>settled</b>. So the row is open
///         as a preference and closed as a constraint: what a Cyrus answer would change is
///         <see cref="ImapImageRepository" /> and the <c>ConfigMap</c>'s contents, and nothing else
///         in this file — not a port, not an object, not a schema property. The declaration is built
///         so that stays true.
///     </para>
///     <para>
///         ⚠ <b>SHARED FRONT DOORS, PER-TENANT BACK ENDS — AND ONLY THE BACK END IS THIS TYPE.</b>
///         doc 17 § Topology answers the brief's
///         <i>
///             "standalone instance per tenant? It would need
///             separate IP due to ports"
///         </i> with: per-tenant instance yes, separate IP
///         <b>
///             only for
///             outbound and only above a volume threshold
///         </b>. Inbound port 25, submission and IMAP are
///         all SHARED pools, because the recipient domain and the authenticated credential already
///         disambiguate the tenant. A resource of this type is therefore the
///         <i>
///             Dovecot back end and
///             its per-tenant volume
///         </i>, reachable over LMTP from the shared inbound pool. ⚠
///         <b>
///             The
///             shared pools are not built and are not this type
///         </b> — see § What is not built below.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE DKIM KEYPAIR IS THE ONE THING HERE THAT COULD NOT BE GENERATED IN A RECONCILE
///             PASS, AND IT IS THE MOST IMPORTANT SENTENCE IN THIS FILE.
///         </b> A reconciler is called
///         repeatedly and must be idempotent (docs/plan/08 § The reconcile loop, clause 1). A key
///         generated per pass would be a different key each pass: the <c>Secret</c> would never
///         converge, and — far worse — the public key already published in the tenant's DNS would
///         stop matching the private key the signer uses, so every message the domain sent would
///         fail DKIM at the receiver while the platform reported the domain healthy. That is the
///         failure this platform's mint-once rule exists for: <see cref="GenerateCredentials" />
///         produces a new key on every call and <b>what reaches a manifest is never that key</b> — it
///         is whatever <c>ISecretWriter.MintAsync</c>'s <c>cas=0</c> left in the vault on the first
///         pass, read back through <c>ISecretResolver</c>. <c>CyberCloud.ContainerRegistry/registries</c>
///         established the shape; this is the first type where breaking it would be silently wrong
///         rather than loudly broken.
///     </para>
///     <para>
///         ⚠ <b>WHAT IS NOT BUILT, NAMED RATHER THAN IMPLIED.</b> doc 17 § Resource model lists
///         <c>mailboxes/{local}</c> and <c>groups/{name}</c> as child types and
///         <c>verify</c>, <c>sendTest</c> and <c>exportMailbox</c> as actions. This type declares
///         <b>none of them</b>, and the reasons are different for each:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>The two child types are shippable and are simply not in this pass.</b> Nothing
///             blocks them: <c>CyberCloud.Storage/accounts/buckets</c> and
///             <c>CyberCloud.ContainerService/managedClusters/agentPools</c> are the precedent, and
///             <see cref="ResourceId.Parent" /> and <c>OpenApiEmitter.PathOf</c> both carry the
///             interleaved shape already.
///         </item>
///         <item>
///             ⚠
///             <b>
///                 <c>verify</c> cannot be declared honestly, and that is a finding rather than a
///                 deferral.
///             </b> The action would answer
///             <i>
///                 "do this domain's SPF, DKIM, DMARC and MX
///                 records resolve"
///             </i>, which requires asking the public DNS — and
///             <b>
///                 this repository
///                 has no DNS resolution seam at all
///             </b>. <c>actions-without-handlers.txt</c> is not the
///             escape hatch: its rule is explicit that a line there is allowed only when the
///             action's api-version is <i>already published</i>, and <c>2026-08-01</c> of this type
///             is published by this very change. So the choice is a handler or no declaration, and
///             the honest answer is no declaration. ⚠ The half that <i>is</i> derivable is derived
///             and is not behind an action at all: <see cref="TryRequiredRecords" /> is a pure function
///             of the domain and the resolved DKIM key, so <i>"the exact records to add"</i> — doc
///             17's phrase — is available without asking anything. What is missing is only the
///             reading back, and the thing that would provide it, <c>CyberCloud.Network/dnsZones</c>,
///             is itself unbuilt.
///         </item>
///         <item>
///             <b><c>sendTest</c> and <c>exportMailbox</c> follow <c>verify</c> for the same reason</b>
///             — the first needs an outbound path through pools that do not exist, the second needs
///             the mailbox child type it exports.
///         </item>
///     </list>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/mail/values.yaml</c> is the other half — ADR-010 § Which end authors the
///         schema. Every property whose pointer begins <c>/properties/</c> and is not
///         <see cref="ClusterIdPointer" /> has a generated <c>@param</c> row in that file at the same
///         pointer.
///     </para>
/// </remarks>
public static class MailDomains {
    /// <summary>The provider namespace, as docs/plan/17 spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.Mail";

    /// <summary>The resource type. docs/plan/17 § Resource model.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THE RESOURCE'S NAME IS NOT THE MAIL DOMAIN, AND doc 17'S MODEL CANNOT BE BUILT AS
    ///             IT IS WRITTEN.
    ///         </b> That document spells this type <c>domains/{domain}</c> — the address
    ///         segment <i>is</i> the domain. It cannot be. <c>ResourceNaming</c> applies the
    ///         Kubernetes <b>DNS-1123 label</b> rule to every resource name on this platform:
    ///         <c>[a-z0-9]([-a-z0-9]*[a-z0-9])?</c>, 1–63 characters, <b>no dots</b> — because the
    ///         name becomes a Kubernetes object name and a label value. Every mail domain worth
    ///         hosting contains at least one dot, so <c>domains/example.com</c> is refused by the
    ///         platform's own address parser before any of this code runs.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rule is not this type's to relax</b>, and relaxing it would be the wrong fix
    ///         even if it were: the name is what
    ///         <see cref="ConfigMapName" /> and its siblings build every object name from, and a dot
    ///         is legal in an object name but <b>not</b> in the DNS-1123 <i>label</i> that a
    ///         <c>StatefulSet</c>'s pod names and its Service's DNS records are built from. A type
    ///         that allowed dots in the name would render objects the API server accepts and pods it
    ///         refuses to schedule.
    ///     </para>
    ///     <para>
    ///         So the domain is <b>a required, immutable property</b> and the name is an ordinary
    ///         resource name — <c>domains/example-com</c> with
    ///         <c>properties.domain = "example.com"</c> is the shape. ⚠ The cost is a fact that now
    ///         lives in two places and can disagree, which is exactly what the name-as-domain design
    ///         avoided; <c>Immutable</c> on the property is what stops the disagreement moving after
    ///         create, and it is why <see cref="TryRequiredRecords" /> takes the domain as an
    ///         argument rather than reading an address.
    ///     </para>
    /// </remarks>
    public const string TypePath = "domains";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/mail/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/mail";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    // ── The vault, and the mint-once rule this type depends on ────────────────────────────────

    /// <summary>The field the domain's DKIM private key is filed under.</summary>
    /// <remarks>
    ///     ⚠ <b>A PKCS#8 PEM, and the only copy.</b> There is no second place this key exists: the
    ///     <c>Secret</c> the signer mounts is rendered <i>from</i> the vault on every pass, so losing
    ///     the vault document means every message the domain has ever signed becomes unverifiable
    ///     against a key nobody can reproduce.
    /// </remarks>
    public const string DkimPrivateKeyField = "dkimPrivateKey";

    /// <summary>The field the Dovecot master password is filed under.</summary>
    /// <remarks>
    ///     The credential the shared inbound pool authenticates with when it hands a message to this
    ///     back end over LMTP, and the one the shared submission pool uses to check a mailbox
    ///     password. ⚠ It is not a tenant-visible credential and no action returns it.
    /// </remarks>
    public const string MasterPasswordField = "masterPassword";

    /// <summary>Both fields, in the order <see cref="GenerateCredentials" /> writes them.</summary>
    public static ImmutableArray<string> CredentialFields { get; } = [
        DkimPrivateKeyField,
        MasterPasswordField
    ];

    /// <summary>Where this type's one vault document lives.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static string SecretPath(ResourceId id) {
        ArgumentNullException.ThrowIfNull(id.Path);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tenants/{id.TenantId:D}/{ProviderNamespace}/{TypePath}/{id.Id:D}"
        );
    }

    /// <summary>The handle that reads one of a domain's secrets back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="field">One of <see cref="CredentialFields" />.</param>
    public static SecretRef CredentialRef(ResourceId id, string field) =>
        new() { Path = SecretPath(id), Field = field };

    /// <summary>
    ///     A fresh DKIM keypair and a fresh master password. ⚠ Never rendered — see the type's own
    ///     remarks on the mint-once rule, which this method is the reason for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Both fields in ONE document, because <c>cas=0</c> is per PATH.</b> Two paths would be
    ///     two independent races, and a pass interrupted between them would leave a domain whose
    ///     signer had a key from one attempt and whose LMTP listener had a password from another.
    ///     <para>
    ///         ⚠ <b>2048 bits and not 1024.</b> RFC 8301 deprecates keys below 1024 outright and the
    ///         major receivers treat 1024 as the floor rather than the norm; 2048 is what a published
    ///         <c>p=</c> tag can carry in one TXT string once split into 255-byte chunks, which
    ///         <see cref="TryRequiredRecords" /> does not have to do because a resolver rejoins them.
    ///         4096 is not offered: it does not fit a single UDP response and the fallback to TCP is
    ///         the thing that breaks on the receivers least likely to be configurable.
    ///     </para>
    /// </remarks>
    public static Dictionary<string, string> GenerateCredentials() {
        using var rsa = RSA.Create(DkimKeyBits);

        return new(StringComparer.Ordinal) {
            [DkimPrivateKeyField] = rsa.ExportPkcs8PrivateKeyPem(),
            [MasterPasswordField] = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength)
        };
    }

    /// <summary>The DKIM key size <see cref="GenerateCredentials" /> mints.</summary>
    public const int DkimKeyBits = 2048;

    /// <summary>The selector the published DKIM record sits under.</summary>
    /// <remarks>
    ///     ⚠ <b>Constant rather than rotating, and that is a limitation rather than a choice.</b> Key
    ///     rotation is two selectors live at once — publish the new record, switch the signer, retire
    ///     the old — and the platform cannot hold two live credentials for one resource, which is the
    ///     same constraint that keeps <c>regenerateKeys</c> undeclared on every other type in the
    ///     catalogue.
    /// </remarks>
    public const string DkimSelector = "cc";

    const int PasswordLength = 32;

    static ReadOnlySpan<char> PasswordAlphabet => "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The objects a domain IS ───────────────────────────────────────────────────────────────

    /// <summary>The <c>Secret</c> holding the DKIM key and the master password.</summary>
    public static GroupVersionKind SecretKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    /// <summary>The <c>ConfigMap</c> holding the Dovecot, Postfix and Rspamd configuration.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>The <c>Service</c> the shared pools reach this back end on.</summary>
    public static GroupVersionKind ServiceKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Service", Plural = "services" };

    /// <summary>The <c>StatefulSet</c> that runs Dovecot, Postfix and Rspamd.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A <c>StatefulSet</c> and not a <c>Deployment</c>, and the reason is the mail store
    ///         rather than the ordinal.
    ///     </b> A mailbox is a filesystem: two pods mounting one volume would
    ///     both write the same <c>mdbox</c> index, and Dovecot's own documentation is explicit that
    ///     concurrent access from separate hosts without a director corrupts it. A
    ///     <c>StatefulSet</c> with one replica and a claim template is one pod per volume by
    ///     construction; a <c>Deployment</c> with <c>replicas: 1</c> is one pod <i>usually</i>,
    ///     because a rolling update starts the new pod before the old one exits.
    /// </remarks>
    public static GroupVersionKind StatefulSetKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "StatefulSet", Plural = "statefulsets" };

    /// <summary>Prometheus Operator's <c>PodMonitor</c> — docs/plan/12 § The pattern, once, piece 6.</summary>
    public static GroupVersionKind PodMonitorKind { get; } = new() {
        Group = "monitoring.coreos.com", Version = "v1", Kind = "PodMonitor", Plural = "podmonitors"
    };

    /// <summary>
    ///     Where the rendered <c>StatefulSet</c> keeps its mail claim template, for
    ///     <c>IKubeCommandBuilder.WithTemplateLabels</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The claim holds every message this domain has ever received.</b> It is made by the
    ///     StatefulSet controller rather than applied by this provider, so ADR-013's seven mandatory
    ///     labels never reached it on their own. Declaring the path stamps
    ///     <see cref="KubeLabels.LifetimeStable" /> onto the template and the controller copies it
    ///     onto the claim.
    /// </remarks>
    public const string ClaimTemplatePath = "spec/volumeClaimTemplates";

    // ── Ports ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>LMTP. How the shared inbound pool delivers into this back end.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the seam doc 17 says survives a Cyrus answer.</b> The shared pool speaks LMTP
    ///     to whatever is behind this Service; nothing about the pool changes if the container on the
    ///     other side is Cyrus rather than Dovecot.
    /// </remarks>
    public const int LmtpPort = 24;

    /// <summary>IMAP, in-cluster and plaintext. ⚠ TLS is terminated at the shared front door.</summary>
    /// <remarks>
    ///     ⚠ <b>993 is deliberately not on this Service.</b> doc 17 § Topology puts IMAP behind a
    ///     shared front door
    ///     <i>
    ///         "with SNI per tenant domain. TLS certificates from cert-manager per
    ///         verified domain"
    ///     </i>, so the certificate belongs to that pool. A back end that also
    ///     terminated TLS would need the tenant's certificate mounted here, which is a second copy of
    ///     a private key for no gain.
    /// </remarks>
    public const int ImapPort = 143;

    /// <summary>ManageSieve — Pigeonhole's rule-editing protocol.</summary>
    public const int SievePort = 4190;

    /// <summary>Rspamd's normal worker, which the signer and the filter both talk to.</summary>
    public const int RspamdPort = 11333;

    /// <summary>Rspamd's controller, which serves the metrics the <c>PodMonitor</c> scrapes.</summary>
    public const int MetricsPort = 11334;

    // ── Versions and images ───────────────────────────────────────────────────────────────────

    /// <summary>The Dovecot minors this type offers.</summary>
    /// <remarks>
    ///     ⚠ <b>Unverified pins, and they are marked rather than presented as checked.</b> Every
    ///     version in this file was written from docs/plan/17 and the upstream projects' own release
    ///     naming, not from an API query on the day it was written — the discipline
    ///     <c>CyberCloud.ContainerService</c> established after two pins in that provider turned out
    ///     not to exist. They must be checked before this row ships.
    /// </remarks>
    public static ImmutableArray<string> Versions { get; } = ["2.3", "2.4"];

    /// <summary>The default Dovecot minor.</summary>
    public const string DefaultVersion = "2.4";

    /// <summary>Where the images come from.</summary>
    public const string ImageRegistry = "docker.io/cybercloud";

    /// <summary>Dovecot — IMAP, LMTP delivery and Pigeonhole.</summary>
    public const string ImapImageRepository = "dovecot";

    /// <summary>Postfix — the per-tenant submission and outbound relay.</summary>
    public const string MtaImageRepository = "postfix";

    /// <summary>Rspamd — filtering, DKIM signing and verification.</summary>
    public const string FilterImageRepository = "rspamd";

    /// <summary>The container names, which are also the component label values.</summary>
    public const string ImapComponent = "dovecot";

    /// <summary>The Postfix container's name.</summary>
    public const string MtaComponent = "postfix";

    /// <summary>The Rspamd container's name.</summary>
    public const string FilterComponent = "rspamd";

    // ── Sizing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sizing presets, as docs/plan/12 § Sizing spells the c1 family.</summary>
    /// <remarks>
    ///     ⚠ The same table <c>charts/managed/mail/templates/_helpers.tpl</c> carries, and
    ///     <c>MailSizingTests</c> is what keeps the two from drifting — nothing generates a Helm
    ///     template from a schema.
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["c1.nano"] = ("250m", "512Mi"),
            ["c1.micro"] = ("500m", "1Gi"),
            ["c1.small"] = ("1", "2Gi"),
            ["c1.medium"] = ("2", "4Gi"),
            ["c1.large"] = ("4", "8Gi"),
            ["c1.xlarge"] = ("8", "16Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The preset a body that names none gets.</summary>
    public const string DefaultPreset = "c1.micro";

    /// <summary>The mail volume size a body that names none gets.</summary>
    public const string DefaultStorageSize = "20Gi";

    /// <summary>The per-mailbox quota a body that names none gets.</summary>
    public const string DefaultMailboxQuota = "1Gi";

    /// <summary>The Rspamd score at or above which a message is rejected.</summary>
    public const int DefaultRejectThreshold = 15;

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>A hostname, for the relay list. ⚠ Enforced per element by nothing — see below.</summary>
    /// <remarks>
    ///     ⚠ <b>The third sighting of the gap <c>KafkaClusters.CidrPattern</c> reported.</b> The
    ///     registry would take this as <c>Pattern</c> on <see cref="Schema2026" />'s
    ///     <c>relayHosts</c> and enforce it per element; <c>./build.sh Charts</c> refuses
    ///     <c>@pattern</c> on a <c>{array}</c> while emitting <c>@enum</c> there as
    ///     <c>items.enum</c>, which is the same per-element shape. The full argument is at the Kafka
    ///     constant and is not repeated.
    /// </remarks>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         NO LOOKAHEAD, AND THAT IS A HARD CONSTRAINT ON EVERY PATTERN IN THIS PLATFORM RATHER
    ///         THAN A STYLE NOTE.
    ///     </b> This constant began as
    ///     <c>^(?=.{1,253}$)([A-Za-z0-9]…</c> — the standard way to bound a hostname's total length
    ///     while validating its labels. `./build.sh Charts` generates <c>values.schema.json</c> from
    ///     this schema and then runs <c>helm lint --strict</c>, and
    ///     <b>
    ///         Helm validates with Go's
    ///         regexp, which is RE2 and has no lookahead at all
    ///     </b>:
    ///     <i>
    ///         "invalid or unsupported Perl
    ///         syntax: `(?=`"
    ///     </i>. JSON Schema's own specification says ECMA-262, so a lookahead is legal
    ///     in the document and unusable by the one tool that reads it here.
    ///     <para>
    ///         ⚠ It is the only lookahead the tree has ever contained — every other provider's
    ///         patterns are quantity, CIDR and enum shapes that never needed one — so this is a
    ///         constraint nothing had met before rather than a rule anybody broke.
    ///     </para>
    ///     <para>
    ///         The total-length bound it was buying is now <c>MaxLength</c> on the property, which is
    ///         where a length belongs anyway: a reader sees <c>253</c> rather than deducing it from a
    ///         regex, and the emitted schema says <c>maxLength</c> rather than hiding it in a pattern
    ///         no portal could explain.
    ///     </para>
    /// </remarks>
    public const string HostnamePattern =
        "^([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\\.)+[A-Za-z]{2,63}$";

    /// <summary>The longest a fully-qualified domain name may be, from RFC 1035.</summary>
    public const int HostnameMaxLength = 253;

    /// <summary>A mailbox local part, for the catch-all.</summary>
    /// <remarks>
    ///     ⚠ Deliberately narrower than RFC 5321's <c>Local-part</c>, which permits quoted strings
    ///     containing spaces, <c>@</c> and control characters. A catch-all is a name this platform
    ///     creates rather than one it must accept from the wire, and a value that has to survive a
    ///     Dovecot configuration file, a Postfix map and a filesystem path is not the place to be
    ///     maximally permissive.
    /// </remarks>
    public const string LocalPartPattern = "^[a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?$";

    /// <summary>The same, but accepting the empty string. ⚠ What <c>catchAll</c> is declared with.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A pattern is applied to the WHOLE value, so a property whose default is <c>""</c> must
    ///         have a pattern that accepts <c>""</c>.
    ///     </b> Declaring
    ///     <see cref="LocalPartPattern" /> on <c>catchAll</c> made the type unloadable —
    ///     <c>ResourceSchema.Of</c> refused it as an incoherent declaration, at static
    ///     construction, which is the same check <c>OptionalQuantityPattern</c> exists to satisfy for
    ///     the sizing overrides. The failure is worth recording rather than quietly fixing: the
    ///     platform caught a property whose default value no body could ever have set.
    /// </remarks>
    public const string OptionalLocalPartPattern = "^([a-z0-9]([a-z0-9._-]{0,62}[a-z0-9])?)?$";

    // ── The schema ────────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape of api-version <see cref="V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the mail domain is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The domain's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the mail back end."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },

                // ── The chart's API surface, in the chart's own declaration order ───────────────
                new(
                    "/properties/domain",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The mail domain this resource hosts, for example example.com. "
                    + "Immutable: the DKIM record, the SPF record and the MX all name it, so "
                    + "changing it would invalidate every record the tenant has published."
                ) {
                    Pattern = HostnamePattern,
                    // ⚠ The total-length bound the pattern's lookahead used to carry — see
                    // HostnamePattern for why it cannot be in the regex.
                    MaxLength = HostnameMaxLength,
                    Immutable = true,
                    // ⚠ A DEFAULT IS REQUIRED HERE EVEN THOUGH THE PROPERTY IS, AND THE REASON IS THE
                    // CHART RATHER THAN THE API. `./build.sh Charts` generates values.yaml from this
                    // schema and refuses a value that its own `@pattern` rejects — a property with a
                    // pattern and no default is emitted as `""`, and `helm lint` would then reject
                    // the chart against the schema the same run generates. Every required patterned
                    // property in the tree carries a default that satisfies its own pattern; this is
                    // the first whose default cannot be a real value.
                    //
                    // ⚠ `example.com` and not a plausible-looking domain. RFC 2606 reserves it for
                    // documentation, so it is the one hostname that cannot belong to somebody — a
                    // placeholder that leaked into a running configuration would send mail for a
                    // domain nobody owns rather than for a domain somebody else does.
                    DefaultJson = "\"example.com\"",
                    ExampleJson = "\"example.com\""
                },
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Dovecot version. Minor upgrades are applied automatically in the "
                    + "maintenance window; a major upgrade is an explicit update to this field."
                ) { AllowedValues = [.. Versions], DefaultJson = "\"" + DefaultVersion + "\"" },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory for the back end, either by preset or explicitly."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. Mail back ends use the c1 "
                    + "family, which is 1 vCPU to 2 GiB and guaranteed rather than burstable."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"" + DefaultPreset + "\""
                },
                new(
                    "/properties/sizing/cpu",
                    SchemaKind.Text,
                    Description: "Explicit vCPU quantity in Kubernetes form, for example 500m or 2. "
                    + "Empty means take it from the preset."
                ) { Pattern = OptionalQuantityPattern, DefaultJson = "\"\"" },
                new(
                    "/properties/sizing/memory",
                    SchemaKind.Text,
                    Description: "Explicit memory quantity in Kubernetes form, for example 4Gi. "
                    + "Empty means take it from the preset."
                ) { Pattern = OptionalQuantityPattern, DefaultJson = "\"\"" },
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "The mail store."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The mail volume size, in Kubernetes quantity form. Holds every "
                    + "mailbox in the domain. Grows online; never shrinks."
                ) { Pattern = QuantityPattern, DefaultJson = "\"" + DefaultStorageSize + "\"" },
                new(
                    "/properties/storage/mailboxQuota",
                    SchemaKind.Text,
                    Description: "The default per-mailbox quota, in Kubernetes quantity form. A "
                    + "mailbox may override it. Enforced by Dovecot, not by the volume."
                ) { Pattern = QuantityPattern, DefaultJson = "\"" + DefaultMailboxQuota + "\"" },
                new(
                    "/properties/catchAll",
                    SchemaKind.Text,
                    Description: "The local part that receives mail addressed to no known mailbox. "
                    + "Empty means unrouted mail is rejected at RCPT TO, which is the default and "
                    + "the better answer for deliverability: a catch-all accepts every dictionary "
                    + "attack and turns the domain into a backscatter source."
                ) { Pattern = OptionalLocalPartPattern, DefaultJson = "\"\"" },
                new(
                    "/properties/relayHosts",
                    SchemaKind.Array,
                    Description: "Smart hosts to relay outbound mail through instead of delivering "
                    + "it directly. Empty means the platform's own outbound pool."
                ) {
                    // ⚠ Without ElementKind the array reaches an SDK as `object[]` and a CLI cannot
                    // type a repeated flag from it — ResourceSchema.Of refuses the declaration
                    // outright, which is how this was found.
                    ElementKind = SchemaKind.Text, DefaultJson = "[]"
                },
                new(
                    "/properties/dedicatedIp",
                    SchemaKind.Boolean,
                    Description: "Request a dedicated outbound IP with a warm-up schedule, rather "
                    + "than sharing the platform's warmed pool. Subject to a volume threshold and "
                    + "to approval; requesting it here does not by itself allocate one."
                ) { DefaultJson = "false" },
                new(
                    "/properties/filtering",
                    SchemaKind.Nested,
                    Description: "Spam and virus filtering."
                ),
                new(
                    "/properties/filtering/rejectThreshold",
                    SchemaKind.WholeNumber,
                    Description: "The Rspamd score at or above which a message is rejected outright "
                    + "rather than filed as junk."
                ) { Minimum = 5, Maximum = 30, DefaultJson = "15" },
                new(
                    "/properties/filtering/antivirus",
                    SchemaKind.Boolean,
                    Description: "Scan attachments with ClamAV through Rspamd. Adds roughly 1 GiB of "
                    + "resident memory for the signature database."
                ) { DefaultJson = "true" },
                new(
                    "/properties/sieve",
                    SchemaKind.Boolean,
                    Description: "Server-side rules through Dovecot's Pigeonhole, editable over "
                    + "ManageSieve."
                ) { DefaultJson = "true" }
            ]
        );

    // ── Reading a desired body ────────────────────────────────────────────────────────────────

    /// <summary>The CPU and memory one back end asks for, resolving the preset.</summary>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", DefaultPreset);
        var fallback = Presets.TryGetValue(preset, out var found)
            ? found
            : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    /// <summary>The mail domain a body hosts.</summary>
    /// <remarks>
    ///     ⚠ <b>Read from the body and never from the address</b> — see <see cref="TypePath" /> for
    ///     why the resource name cannot be the domain. A caller that substituted
    ///     <c>context.Id.Name</c> here would render a Postfix configuration accepting mail for
    ///     <c>example-com</c>, which is not a domain anybody sends to, and every message for the real
    ///     domain would be rejected as a relay attempt.
    /// </remarks>
    public static string Domain(JsonElement desired) => Root(desired, "domain", string.Empty);

    /// <summary>The Dovecot version a body asks for.</summary>
    public static string Version(JsonElement desired) => Root(desired, "version", DefaultVersion);

    /// <summary>The mail volume size a body asks for.</summary>
    public static string StorageSize(JsonElement desired) => Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>The default per-mailbox quota a body asks for.</summary>
    public static string MailboxQuota(JsonElement desired) =>
        Text(desired, "storage", "mailboxQuota", DefaultMailboxQuota);

    /// <summary>The catch-all local part, or empty when unrouted mail is rejected.</summary>
    public static string CatchAll(JsonElement desired) => Root(desired, "catchAll", string.Empty);

    /// <summary>The reject threshold a body asks for.</summary>
    public static int RejectThreshold(JsonElement desired) =>
        Number(desired, "filtering", "rejectThreshold", DefaultRejectThreshold);

    /// <summary>Whether the body asks for ClamAV.</summary>
    public static bool AntivirusEnabled(JsonElement desired) => Flag(desired, "filtering", "antivirus", true);

    /// <summary>Whether the body asks for Pigeonhole.</summary>
    public static bool SieveEnabled(JsonElement desired) => RootFlag(desired, "sieve", true);

    /// <summary>Whether the body asks for a dedicated outbound IP.</summary>
    public static bool DedicatedIpRequested(JsonElement desired) => RootFlag(desired, "dedicatedIp", false);

    /// <summary>The smart hosts a body asks to relay through, sorted and de-duplicated.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Sorted, so that two bodies naming the same hosts in different orders render the same
    ///         document.
    ///     </b> Without it the rendered Postfix configuration would differ by ordering
    ///     alone and every pass would report drift against the last one — the clause-1 failure this
    ///     platform has already found once, in <c>RabbitmqClusters.Plugins</c>.
    /// </remarks>
    public static ImmutableArray<string> RelayHosts(JsonElement desired) {
        if (desired.ValueKind is not JsonValueKind.Object
            || !desired.TryGetProperty("properties", out var properties)
            || properties.ValueKind is not JsonValueKind.Object
            || !properties.TryGetProperty("relayHosts", out var hosts)
            || hosts.ValueKind is not JsonValueKind.Array) {
            return [];
        }

        return [
            .. hosts.EnumerateArray()
                .Where(x => x.ValueKind is JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
        ];
    }

    // ── The DNS records a domain must publish ─────────────────────────────────────────────────

    /// <summary>One record a domain has to publish before the platform will send for it.</summary>
    /// <param name="Name">The fully-qualified owner name.</param>
    /// <param name="Kind">The record type — <c>TXT</c> or <c>MX</c>.</param>
    /// <param name="Value">The value, exactly as it must appear.</param>
    /// <param name="Purpose">Which of the four requirements this record satisfies.</param>
    public readonly record struct MailDnsRecord(string Name, string Kind, string Value, string Purpose);

    /// <summary>
    ///     The four records doc 17 § Deliverability requires, for one domain and one DKIM key.
    /// </summary>
    /// <param name="domain">The mail domain, which is the resource's own name.</param>
    /// <param name="dkimPrivateKeyPem">
    ///     The PKCS#8 PEM this domain's key was minted as — <see cref="DkimPrivateKeyField" />, read
    ///     back out of the vault rather than freshly generated.
    /// </param>
    /// <param name="inboundPoolHost">The shared inbound pool's hostname, which the MX names.</param>
    /// <param name="records">The four records, or empty when the key does not parse.</param>
    /// <returns>
    ///     <see langword="true" /> when the key parsed and the records could be derived.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A <c>Try</c> rather than a <c>Result&lt;T&gt;</c>, and that is the .Contracts
    ///             split rather than a style preference.
    ///         </b> <c>Result&lt;T&gt;</c> and
    ///         <c>ErrorCode</c> both live in <c>CyberCloud.Core</c>, which no provider's
    ///         <c>.Contracts</c> references — the failure <i>vocabulary</i> belongs to the
    ///         implementation assembly, and this one stays a pure derivation over its arguments.
    ///         <c>KubeQuantity.TryParse</c> and <c>NetworkAddressing.TryParse</c> are the same shape
    ///         for the same reason.
    ///     </para>
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A PURE FUNCTION OF ITS THREE ARGUMENTS, WHICH IS WHAT LETS DOC 17'S
    ///             <i>
    ///                 "with the
    ///                 exact records to add"
    ///             </i> BE ANSWERED WITHOUT A DNS QUERY.
    ///         </b> Everything the tenant
    ///         has to publish is derivable; only the reading-back is not, and that is the half this
    ///         repository has no seam for — see the type's own remarks on <c>verify</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The SPF record ends <c>-all</c> and not <c>~all</c>.</b> A soft fail asks the
    ///         receiver to accept the message anyway and mark it, which means an attacker forging
    ///         this domain gets delivery. doc 17 § Deliverability makes the platform's refusal to
    ///         send before the records verify the whole point of the gate; publishing a policy that
    ///         tells receivers to accept forgeries would give the gate nothing to protect.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The DMARC policy is <c>quarantine</c> rather than <c>reject</c>, and that is the
    ///             one place here that is deliberately weaker than it could be.
    ///         </b> A domain publishing
    ///         <c>p=reject</c> on day one loses legitimate mail from every forwarder and mailing list
    ///         it uses, which is the failure mode that makes an operator turn DMARC off entirely.
    ///         The honest default is a policy that quarantines, with the reports going somewhere a
    ///         human reads.
    ///     </para>
    /// </remarks>
    public static bool TryRequiredRecords(
        string domain,
        string dkimPrivateKeyPem,
        string inboundPoolHost,
        out ImmutableArray<MailDnsRecord> records
    ) {
        ArgumentException.ThrowIfNullOrEmpty(domain);
        ArgumentException.ThrowIfNullOrEmpty(inboundPoolHost);

        if (!TryDkimPublicKey(dkimPrivateKeyPem, out var published)) {
            records = [];

            return false;
        }

        records = [
            new(
                domain,
                "MX",
                string.Create(CultureInfo.InvariantCulture, $"10 {inboundPoolHost}."),
                "Delivery — where the internet hands mail for this domain to the platform."
            ),
            new(
                domain,
                "TXT",
                string.Create(CultureInfo.InvariantCulture, $"v=spf1 include:{inboundPoolHost} -all"),
                "SPF — which hosts may send as this domain. Anything else is a forgery."
            ),
            new(
                string.Create(CultureInfo.InvariantCulture, $"{DkimSelector}._domainkey.{domain}"),
                "TXT",
                string.Create(CultureInfo.InvariantCulture, $"v=DKIM1; k=rsa; p={published}"),
                "DKIM — the public half of the key this domain's outbound mail is signed with."
            ),
            new(
                string.Create(CultureInfo.InvariantCulture, $"_dmarc.{domain}"),
                "TXT",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"v=DMARC1; p=quarantine; rua=mailto:dmarc@{domain}; fo=1"
                ),
                "DMARC — what a receiver should do when SPF and DKIM both fail, and where to report."
            )
        ];

        return true;
    }

    /// <summary>
    ///     The <c>p=</c> tag of the DKIM record: the base64 SubjectPublicKeyInfo of the minted key.
    /// </summary>
    /// <param name="privateKeyPem">The PKCS#8 PEM read back out of the vault.</param>
    /// <param name="published">The base64 <c>p=</c> value, or empty when the key does not parse.</param>
    /// <returns><see langword="true" /> when the key parsed.</returns>
    /// <remarks>
    ///     ⚠ <b>Derived from the private key rather than stored alongside it.</b> Two stored halves
    ///     can disagree; a derivation cannot. This is the same reason the record list is a function
    ///     rather than a field.
    ///     <para>
    ///         ⚠ An empty key is <see langword="false" /> rather than an exception. It is minted once,
    ///         on the first reconcile pass, through <c>ISecretWriter</c>'s <c>cas=0</c> rule — so an
    ///         empty value means the mint did not happen or the vault document was replaced, which is
    ///         a state the caller reports rather than one this derivation throws over.
    ///     </para>
    /// </remarks>
    public static bool TryDkimPublicKey(string privateKeyPem, out string published) {
        published = string.Empty;

        if (string.IsNullOrWhiteSpace(privateKeyPem)) {
            return false;
        }

        using var rsa = RSA.Create();

        try {
            rsa.ImportFromPem(privateKeyPem);
        } catch (ArgumentException) {
            // Not a PEM this platform minted.
            return false;
        } catch (CryptographicException) {
            // A PEM, but not an RSA key this can export.
            return false;
        }

        published = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        return true;
    }

    // ── Names ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Secret</c> a domain owns.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string CredentialsSecretName(string name) => name + "-mail-credentials";

    /// <summary>The <c>ConfigMap</c> a domain owns.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ConfigMapName(string name) => name + "-mail-config";

    /// <summary>The <c>Service</c> a domain owns.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ServiceName(string name) => name + "-mail";

    /// <summary>The <c>StatefulSet</c> a domain owns.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string SetName(string name) => name + "-mail";

    /// <summary>The <c>PodMonitor</c> a domain owns.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string PodMonitorName(string name) => name + "-mail";

    /// <summary>The claim template's name, which prefixes every claim the controller makes.</summary>
    public const string MailVolumeName = "mail";

    // ── Refs ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Secret</c> a domain owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef CredentialsSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = CredentialsSecretName(name) };

    /// <summary>The <c>ConfigMap</c> a domain owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ConfigMapRef(string ns, string name) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ConfigMapName(name) };

    /// <summary>The <c>Service</c> a domain owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = ServiceName(name) };

    /// <summary>The <c>StatefulSet</c> a domain owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef SetRef(string ns, string name) =>
        new() { Kind = StatefulSetKind, Namespace = ns, Name = SetName(name) };

    /// <summary>The <c>PodMonitor</c> a domain owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PodMonitorRef(string ns, string name) =>
        new() { Kind = PodMonitorKind, Namespace = ns, Name = PodMonitorName(name) };

    /// <summary>
    ///     The claims a delete must leave behind — docs/plan/08 § Soft delete and
    ///     <c>VolumeReclaimer</c>.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The mail volume is the strongest case in the catalogue for retention and the entry is
    ///         deliberately unconditional.
    ///     </b> Every other type offering a <c>deleteClaim</c> flag is
    ///     offering to discard a store the tenant can rebuild — a replica, an index, a cache of
    ///     something authoritative elsewhere. A mailbox is the only copy of correspondence the tenant
    ///     did not author and cannot ask anybody to resend. There is no property here that turns
    ///     retention off, and that is the intended asymmetry rather than an unfinished schema.
    /// </remarks>
    public static ImmutableArray<RetainedVolume> RetainedClaims(string ns, string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // ⚠ ONE REPLICA, WHICH IS THE ONE CASE `OfSet`'s SCALED-DOWN CAVEAT CANNOT REACH. Its
        // remarks record that a set scaled from three to one leaves the claims of ordinals 1 and 2
        // unreclaimed, because the desired body names only the count the resource ended on. This
        // type has no replica property at all — MailDomains.StatefulSetKind says why — so the count
        // is 1 on every body that has ever existed and there is no earlier ordinal to strand.
        return RetainedVolume.OfSet(
            ns,
            MailVolumeName,
            SetName(name),
            1,
            SelectorLabels(name),
            "Every message this domain has received. A mail store is the only copy of "
            + "correspondence the tenant did not author, so it outlives the resource "
            + "unconditionally — see MailDomains.RetainedClaims."
        );
    }

    /// <summary>The five objects a domain is, in dependency order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ImmutableArray<ObjectRef> Objects(string ns, string name) => [
        CredentialsSecretRef(ns, name),
        ConfigMapRef(ns, name),
        ServiceRef(ns, name),
        SetRef(ns, name),
        PodMonitorRef(ns, name)
    ];

    /// <summary>A valid desired body, for tests and for the conformance case.</summary>
    /// <param name="clusterId">The cluster the back end is placed in.</param>
    /// <param name="domain">The mail domain. ⚠ Not the resource name — see <see cref="TypePath" />.</param>
    /// <param name="storageSize">The mail volume size.</param>
    /// <param name="mailboxQuota">The default per-mailbox quota.</param>
    /// <param name="preset">A sizing preset.</param>
    /// <param name="catchAll">The catch-all local part, or empty to reject unrouted mail.</param>
    /// <param name="sieve">Whether Pigeonhole is on.</param>
    /// <param name="antivirus">Whether ClamAV is on.</param>
    /// <param name="rejectThreshold">The Rspamd reject score.</param>
    /// <param name="location">The billing region.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Every property the schema declares is written, including the ones equal to their
    ///         defaults.
    ///     </b> A body that omitted them would test the accessors' fallbacks rather than the
    ///     rendering, and the two are different questions — <c>MailDeclarationTests</c> asks
    ///     the first one deliberately, with bodies that omit.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string domain = "example.com",
        string storageSize = DefaultStorageSize,
        string mailboxQuota = DefaultMailboxQuota,
        string preset = DefaultPreset,
        string catchAll = "",
        bool sieve = true,
        bool antivirus = true,
        int rejectThreshold = DefaultRejectThreshold,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["domain"] = domain,
                ["version"] = DefaultVersion,
                ["sizing"] = new JsonObject { ["preset"] = preset, ["cpu"] = string.Empty, ["memory"] = string.Empty },
                ["storage"] = new JsonObject { ["size"] = storageSize, ["mailboxQuota"] = mailboxQuota },
                ["catchAll"] = catchAll,
                ["relayHosts"] = new JsonArray(),
                ["dedicatedIp"] = false,
                ["filtering"] = new JsonObject { ["rejectThreshold"] = rejectThreshold, ["antivirus"] = antivirus },
                ["sieve"] = sieve
            }
        }.ToJsonString();

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The <c>Secret</c> document, from what the vault handed back.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="credentials">The resolved fields — never freshly generated ones.</param>
    /// <remarks>
    ///     ⚠ <b><c>stringData</c> rather than <c>data</c>.</b> The API server base64-encodes it, and
    ///     a PEM survives that unchanged; encoding here would mean this file and the API server both
    ///     had an opinion about the same transformation.
    /// </remarks>
    public static string CredentialsSecretJson(string name, IReadOnlyDictionary<string, string> credentials) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(credentials);

        var data = new JsonObject();

        foreach (var field in CredentialFields) {
            data[field] = credentials.TryGetValue(field, out var value) ? value : string.Empty;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = CredentialsSecretName(name) },
            ["type"] = "Opaque",
            ["stringData"] = data
        }.ToJsonString();
    }

    /// <summary>The <c>ConfigMap</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>No credential reaches this object.</b> The DKIM key and the master password are
    ///     mounted from the <c>Secret</c>; a <c>ConfigMap</c> is readable by anyone holding
    ///     <c>get configmaps</c>, which is a strictly weaker right than <c>get secrets</c>.
    /// </remarks>
    public static string ConfigMapJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ConfigMapName(name) },
            ["data"] = new JsonObject {
                ["dovecot.conf"] = DovecotConf(name, desired),
                ["main.cf"] = PostfixMainCf(name, desired),
                ["rspamd.conf"] = RspamdConf(desired)
            }
        }.ToJsonString();
    }

    /// <summary>Dovecot's configuration — LMTP in, IMAP out, Pigeonhole when asked for.</summary>
    /// <param name="name">The resource's own name. ⚠ Not the domain — see <see cref="TypePath" />.</param>
    /// <param name="desired">The validated desired body, which carries the domain.</param>
    public static string DovecotConf(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var domain = Domain(desired);
        var protocols = SieveEnabled(desired) ? "imap lmtp sieve" : "imap lmtp";

        var builder = new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append(CultureInfo.InvariantCulture, $"protocols = {protocols}\n")
            .Append("mail_location = mdbox:/srv/mail/%d/%n\n")
            .Append(CultureInfo.InvariantCulture, $"mail_home = /srv/mail/{domain}/%n\n")
            .Append("first_valid_uid = 1000\n")
            .Append("mail_plugins = $mail_plugins quota\n")
            .Append(CultureInfo.InvariantCulture, $"quota_rule = *:storage={MailboxQuota(desired)}\n")
            .Append("service lmtp {\n")
            .Append("  inet_listener lmtp {\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(LmtpPort)}\n")
            .Append("  }\n")
            .Append("}\n")
            .Append("service imap-login {\n")
            .Append("  inet_listener imap {\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(ImapPort)}\n")
            .Append("  }\n")
            // ⚠ The TLS listener is absent on purpose — see ImapPort's own remarks. The shared front
            // door terminates TLS with the tenant's certificate; a second termination here would
            // need a second copy of a private key.
            .Append("  inet_listener imaps {\n")
            .Append("    port = 0\n")
            .Append("  }\n")
            .Append("}\n");

        if (SieveEnabled(desired)) {
            builder
                .Append("service managesieve-login {\n")
                .Append("  inet_listener sieve {\n")
                .Append(CultureInfo.InvariantCulture, $"    port = {Text(SievePort)}\n")
                .Append("  }\n")
                .Append("}\n")
                .Append("plugin {\n")
                .Append("  sieve = file:/srv/mail/%d/%n/sieve;active=/srv/mail/%d/%n/.dovecot.sieve\n")
                .Append("}\n");
        }

        return builder.ToString();
    }

    /// <summary>Postfix's <c>main.cf</c> — submission and outbound for this domain.</summary>
    /// <param name="name">The resource's own name. ⚠ Not the domain — see <see cref="TypePath" />.</param>
    /// <param name="desired">The validated desired body, which carries the domain.</param>
    /// <remarks>
    ///     ⚠ <b>Every hostname below is the BODY's domain, never the resource's name.</b> Rendering
    ///     the name would give Postfix <c>virtual_mailbox_domains = example-com</c>, and mail for
    ///     <c>example.com</c> would be refused as a relay attempt — a total delivery outage whose
    ///     cause is one identifier that looks almost right.
    /// </remarks>
    public static string PostfixMainCf(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var domain = Domain(desired);
        var relays = RelayHosts(desired);
        var catchAll = CatchAll(desired);

        var builder = new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append(CultureInfo.InvariantCulture, $"myhostname = {domain}\n")
            .Append(CultureInfo.InvariantCulture, $"mydestination = {domain}\n")
            .Append(CultureInfo.InvariantCulture, $"virtual_mailbox_domains = {domain}\n")
            .Append(
                CultureInfo.InvariantCulture,
                $"virtual_transport = lmtp:inet:localhost:{Text(LmtpPort)}\n"
            )
            .Append(
                CultureInfo.InvariantCulture,
                $"smtpd_milters = inet:localhost:{Text(RspamdPort)}\n"
            )
            .Append("milter_default_action = tempfail\n");

        // ⚠ `relayhost` is written even when the list is EMPTY, so that an unfinished relay list is
        // "deliver directly" rather than whatever a previous body left behind. The same argument the
        // NATS chart's `loadBalancerSourceRanges` makes: an absent field is not a neutral value.
        builder.Append(
            CultureInfo.InvariantCulture,
            $"relayhost = {(relays.Length > 0 ? "[" + relays[0] + "]" : string.Empty)}\n"
        );

        if (relays.Length > 1) {
            builder.Append(
                CultureInfo.InvariantCulture,
                $"smtp_fallback_relay = {string.Join(", ", relays.Skip(1).Select(x => "[" + x + "]"))}\n"
            );
        }

        // ⚠ A catch-all turns `local_recipient_maps` OFF, which is what makes it a catch-all at all:
        // with the map in place Postfix rejects an unknown recipient at RCPT TO and never reaches
        // luser_relay. That is also why the empty default is the safer one — see the schema's own
        // description of what a catch-all costs in deliverability.
        builder.Append(
            catchAll.Length > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"luser_relay = {catchAll}@{domain}\nlocal_recipient_maps =\n"
                )
                : "local_recipient_maps = $virtual_mailbox_maps\n"
        );

        return builder.ToString();
    }

    /// <summary>Rspamd's configuration — scoring, DKIM signing, and ClamAV when asked for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RspamdConf(JsonElement desired) {
        var builder = new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append("actions {\n")
            .Append(
                CultureInfo.InvariantCulture,
                $"  reject = {Text(RejectThreshold(desired))};\n"
            )
            .Append(
                CultureInfo.InvariantCulture,
                $"  add_header = {Text(RejectThreshold(desired) / 3)};\n"
            )
            .Append("}\n")
            .Append("dkim_signing {\n")
            .Append(CultureInfo.InvariantCulture, $"  selector = \"{DkimSelector}\";\n")
            .Append(CultureInfo.InvariantCulture, $"  path = \"/etc/mail/{DkimPrivateKeyField}\";\n")
            .Append("}\n");

        if (AntivirusEnabled(desired)) {
            builder
                .Append("antivirus {\n")
                .Append("  clamav {\n")
                .Append("    type = \"clamav\";\n")
                .Append("    servers = \"/run/clamav/clamd.sock\";\n")
                .Append("  }\n")
                .Append("}\n");
        }

        return builder.ToString();
    }

    /// <summary>The <c>Service</c> document — how the shared pools reach this back end.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b><c>ClusterIP</c> at every setting, with no exposure property at all.</b> Every other
    ///     managed service in docs/plan/12 offers an optional external listener; this one must not,
    ///     because doc 17 § Topology puts the front doors in shared pools. A tenant-addressable
    ///     <c>LoadBalancer</c> here would be a second, unauthenticated way into the mail store that
    ///     bypasses the pool doing the rate limiting and the reputation management — which is the
    ///     entire product.
    /// </remarks>
    public static string ServiceJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var ports = new JsonArray { Port("lmtp", LmtpPort), Port("imap", ImapPort) };

        if (SieveEnabled(desired)) {
            ports.Add(Port("sieve", SievePort));
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ServiceName(name) },
            ["spec"] = new JsonObject { ["type"] = "ClusterIP", ["selector"] = Selector(name), ["ports"] = ports }
        }.ToJsonString();
    }

    /// <summary>The <c>StatefulSet</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>One replica, always, and it is not a property.</b> See <see cref="StatefulSetKind" />
    ///     for why: two pods on one mail store corrupt the index. Scaling a mail back end is a
    ///     Dovecot director and a shared filesystem, which is a different design rather than a
    ///     larger number here.
    /// </remarks>
    public static string StatefulSetJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);
        var resources = new JsonObject();

        // ⚠ No `resources` block at all when the preset did not resolve, rather than one with empty
        // strings — an empty quantity is not a quantity and the API server refuses the object. The
        // meter derivation in MailProvider refuses the write before it reaches here, so this branch
        // is the unreachable half of the same guard rather than a second policy.
        if (cpu.Length > 0 && memory.Length > 0) {
            var block = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };

            resources["requests"] = block.DeepClone();
            resources["limits"] = block;
        }

        var containers = new JsonArray {
            Container(ImapComponent, ImapImageRepository, Version(desired), resources),
            Container(MtaComponent, MtaImageRepository, Version(desired), null),
            Container(FilterComponent, FilterImageRepository, Version(desired), null)
        };

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = SetName(name) },
            ["spec"] = new JsonObject {
                ["replicas"] = 1,
                ["serviceName"] = ServiceName(name),
                ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject { ["labels"] = Selector(name) },
                    ["spec"] = new JsonObject {
                        ["containers"] = containers,
                        ["volumes"] = new JsonArray {
                            new JsonObject {
                                ["name"] = "config", ["configMap"] = new JsonObject { ["name"] = ConfigMapName(name) }
                            },
                            new JsonObject {
                                ["name"] = "credentials",
                                ["secret"] = new JsonObject {
                                    ["secretName"] = CredentialsSecretName(name),
                                    // ⚠ 0400. A DKIM private key readable by the whole pod is
                                    // readable by the Postfix and Rspamd containers that do not need
                                    // it, and by anything that later joins them.
                                    ["defaultMode"] = 256
                                }
                            }
                        }
                    }
                },
                ["volumeClaimTemplates"] = new JsonArray {
                    new JsonObject {
                        ["metadata"] = new JsonObject { ["name"] = MailVolumeName },
                        ["spec"] = new JsonObject {
                            ["accessModes"] = new JsonArray { "ReadWriteOnce" },
                            ["resources"] = new JsonObject {
                                ["requests"] = new JsonObject { ["storage"] = StorageSize(desired) }
                            }
                        }
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The <c>PodMonitor</c> document — docs/plan/12 § The pattern, once, piece 6.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string PodMonitorJson(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = PodMonitorName(name) },
            ["spec"] = new JsonObject {
                ["selector"] = new JsonObject { ["matchLabels"] = Selector(name) },
                ["podMetricsEndpoints"] =
                    new JsonArray { new JsonObject { ["port"] = "metrics", ["path"] = "/metrics" } }
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object as the API server returned it.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Containment, never equality</b> — src/Providers/README.md § Comparing an object you
    ///     read back. The API server adds defaults, a controller adds status, and an equality check
    ///     would report drift on every pass against an object nobody changed.
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;

        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "StatefulSet" => MatchesSet(document, desired),
            "Service" => MatchesService(document, desired),
            // A ConfigMap, a Secret and a PodMonitor are matched by the reconciler applying them and
            // reading them back at all: none carries a field a controller rewrites, so an apply that
            // landed is an object that matches.
            _ => true
        };
    }

    static bool MatchesSet(JsonObject document, JsonElement desired) {
        var spec = document["spec"] as JsonObject;
        var claims = spec?["volumeClaimTemplates"] as JsonArray;
        var size = (claims?.FirstOrDefault() as JsonObject)?["spec"]?["resources"]?["requests"]?["storage"];

        return spec?["replicas"]?.GetValue<int>() == 1
            && size?.GetValue<string>() == StorageSize(desired);
    }

    static bool MatchesService(JsonObject document, JsonElement desired) {
        if (document["spec"]?["ports"] is not JsonArray ports) {
            return false;
        }

        var expected = SieveEnabled(desired) ? 3 : 2;

        return ports.Count == expected;
    }

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The labels the <c>StatefulSet</c> selects on, the <c>Service</c> routes to, the
    ///     <c>PodMonitor</c> scrapes by, and <see cref="RetainedClaims" /> proves ownership with.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         ONE SOURCE, BECAUSE A <c>StatefulSet</c>'s <c>spec.selector</c> IS IMMUTABLE AFTER
    ///         CREATE.
    ///     </b> Four consumers deriving the same labels separately is four chances for one of
    ///     them to drift, and the failure that produces is silent: a <c>Service</c> selecting zero
    ///     pods is a legal <c>Service</c> with no endpoints and no error, and a
    ///     <see cref="RetainedVolume" /> whose <c>OwnedBy</c> did not match would make every purge
    ///     refuse. <c>charts/managed/mail/templates/_helpers.tpl</c> is the fifth spelling and the
    ///     one nothing generates — <c>MailSizingTests</c> holds it against this.
    /// </remarks>
    /// <param name="name">The resource's own name.</param>
    public static ImmutableDictionary<string, string> SelectorLabels(string name) =>
        ImmutableDictionary<string, string>.Empty
            .Add("app.kubernetes.io/name", "mail")
            .Add("app.kubernetes.io/instance", name)
            .Add("app.kubernetes.io/component", BackEndComponent)
            .Add("app.kubernetes.io/managed-by", "cybercloud");

    /// <summary>
    ///     The selector's <c>component</c> value. ⚠ One value for a pod running three daemons.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="ImapComponent" />, and the difference is the unit being labelled.</b>
    ///     The three container names describe processes <i>inside</i> one pod; a selector describes
    ///     the pod. Reusing the Dovecot container's name here would make the selector say the pod is
    ///     an IMAP server, which is a third of what it is.
    /// </remarks>
    public const string BackEndComponent = "backend";

    static JsonObject Selector(string name) {
        var labels = new JsonObject();

        foreach (var (key, value) in SelectorLabels(name)) {
            labels[key] = value;
        }

        return labels;
    }

    static JsonObject Port(string portName, int port) =>
        new() { ["name"] = portName, ["port"] = port, ["targetPort"] = port };

    static JsonObject Container(
        string component,
        string repository,
        string version,
        JsonObject? resources
    ) {
        var container = new JsonObject {
            ["name"] = component,
            ["image"] = string.Create(
                CultureInfo.InvariantCulture,
                $"{ImageRegistry}/{repository}:{version}"
            ),
            ["volumeMounts"] = new JsonArray {
                new JsonObject { ["name"] = "config", ["mountPath"] = "/etc/mail" },
                new JsonObject { ["name"] = "credentials", ["mountPath"] = "/etc/mail/secrets" },
                new JsonObject { ["name"] = MailVolumeName, ["mountPath"] = "/srv/mail" }
            }
        };

        if (resources is { Count: > 0 }) {
            container["resources"] = resources;
        }

        if (component == FilterComponent) {
            container["ports"] = new JsonArray {
                new JsonObject { ["name"] = "metrics", ["containerPort"] = MetricsPort }
            };
        }

        return container;
    }

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    static JsonElement? Section(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Section(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Root(JsonElement desired, string name, string fallback) =>
        Section(desired, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static bool RootFlag(JsonElement desired, string name, bool fallback) =>
        Section(desired, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };

    static string Text(JsonElement desired, string parent, string name, string fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string parent, string name, int fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
