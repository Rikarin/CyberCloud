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
///     body shape, the six Kubernetes objects it becomes, and the configuration of the three daemons
///     that run in them.
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
///         the Dovecot image and the <c>ConfigMap</c>'s contents, and nothing else
///         in this file — not a port, not an object, not a schema property. The declaration is built
///         so that stays true. (⚠ Since issue #34's second pass "what a Cyrus answer would change" is
///         <see cref="DovecotImage" /> and <see cref="DovecotConf" />.)
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
///         </b> — see <c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-shared-pools-are-not-built</c>.
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
///         ⚠ <b>WHAT THE DOMAIN IS NOW, AND WHAT IT STILL IS NOT.</b> doc 17 § Resource model lists
///         <c>mailboxes/{local}</c> and <c>groups/{name}</c> as child types and <c>verify</c>,
///         <c>sendTest</c> and <c>exportMailbox</c> as actions. As of issue #34's second pass:
///     </para>
///     <list type="bullet">
///         <item>
///             <b><see cref="MailMailboxes" /> is built</b>, as a second writer on this domain's
///             mailbox <c>Secret</c> — see its remarks. <c>groups</c> is not:
///             <c>charts/managed/mail/conformance.yaml § owed</c>, <c>groups</c>.
///         </item>
///         <item>
///             <b><c>dnsRecords</c> and <c>verify</c> are declared, with handlers.</b> The first
///             version of this file argued that <c>verify</c> could not be, because the repository had
///             no DNS resolution seam; <c>IMailDnsResolver</c> in the implementation assembly is that
///             seam, a hand-written RFC 1035 client proven against a real DNS server. The
///             <see cref="MailDnsRecords" /> half — what to publish and whether what is published
///             matches — stays a pure function here.
///         </item>
///         <item>
///             <b>The sending gate is built</b>: <see cref="MailSending" />, rendered into
///             <see cref="PostfixMainCf" />, decided on every reconcile and every <c>verify</c> from
///             the DNS and the platform's suspension seam. doc 17's <i>"the platform will not enable
///             sending until the DNS records verify"</i> is now a refusal at <c>RCPT TO</c>.
///         </item>
///         <item>
///             <b><c>sendTest</c> and <c>exportMailbox</c> are still not declared.</b> The first
///             needs the outbound pool to send through and the second a place to put an export;
///             neither exists, and <c>actions-without-handlers.txt</c> is not for an api-version this
///             type is still growing.
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
    ///         create, and it is why <see cref="MailDnsRecords.TryRequired" /> takes the domain as an
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

    // ── The two actions ───────────────────────────────────────────────────────────────────────

    /// <summary>The action that returns the records a domain must publish.</summary>
    public const string DnsRecordsAction = "dnsRecords";

    /// <summary>The action that resolves them and moves the sending gate.</summary>
    public const string VerifyAction = "verify";

    /// <summary>
    ///     <see cref="VerifyAction" />'s permission. ⚠ <c>write</c>, because a <c>verify</c> that finds
    ///     the records published opens the gate — see <c>MailVerifyHandler</c>.
    /// </summary>
    public const string VerifyPermission = "write";

    /// <summary>What <see cref="DnsRecordsAction" /> answers.</summary>
    public static ResourceSchema DnsRecordsResponse { get; } = MailDnsRecords.ResponseSchema(false);

    /// <summary>What <see cref="VerifyAction" /> answers.</summary>
    public static ResourceSchema VerifyResponse { get; } = MailDnsRecords.ResponseSchema(true);

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
    ///         <c>p=</c> tag can carry in one TXT record once split into 255-byte strings, which
    ///         <see cref="MailDnsRecords.ZoneFile" /> does and a resolver rejoins.
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
    ///     <para>
    ///         ⚠ <b>Constant rather than rotating, and since issue #34's second pass the reason is
    ///         narrower than it was.</b> The first cut said the platform cannot hold two live
    ///         credentials for one resource. It can: <c>cas=0</c> is per path, so a second selector's
    ///         key is a second mint at a second path, and the signer can be told about two keys. What
    ///         is missing is the <i>trigger</i>. Rotation is publish the new record, switch the signer,
    ///         retire the old — three steps a tenant takes over days — and each has to be desired
    ///         state, because a reconciler cannot remember where in the sequence it was. On this type
    ///         that means new properties, and docs/plan/08 § The provider registry makes adding a
    ///         field a new api-version, which no type in the catalogue has yet needed.
    ///     </para>
    ///     <para>
    ///         The design that fits is a child type — <c>domains/dkimKeys/{selector}</c>, whose name
    ///         is a DNS label exactly as a selector is, minted once per key resource, co-written onto
    ///         the credentials <c>Secret</c> the way a mailbox is onto the mailbox one, with at most
    ///         one key signing enforced by the co-writer merge refusing two values for one scalar.
    ///         <c>charts/managed/mail/conformance.yaml § owed</c>, <c>dkim-key-rotation</c>.
    ///     </para>
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
    //
    // ⚠ TWO NUMBERS PER PROTOCOL, AND THE SERVICE IS WHAT KEEPS THEM APART. Dovecot 2.4's image runs
    // as the unprivileged `vmail` user and cannot bind below 1024, so every Dovecot listener is in the
    // 31xxx range the upstream image itself uses, and the Service maps the conventional port onto it.
    // A shared pool dials 24 and 143 and never learns the difference.

    /// <summary>LMTP, on the Service. How the shared inbound pool delivers into this back end.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the seam doc 17 says survives a Cyrus answer.</b> The shared pool speaks LMTP
    ///     to whatever is behind this Service; nothing about the pool changes if the container on the
    ///     other side is Cyrus rather than Dovecot.
    /// </remarks>
    public const int LmtpPort = 24;

    /// <summary>LMTP, in the pod — where Dovecot listens and Postfix's virtual transport delivers.</summary>
    public const int LmtpContainerPort = 31024;

    /// <summary>IMAP, in-cluster and plaintext, on the Service. ⚠ TLS is terminated at the shared front door.</summary>
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

    /// <summary>IMAP, in the pod.</summary>
    public const int ImapContainerPort = 31143;

    /// <summary>ManageSieve — Pigeonhole's rule-editing protocol — on the Service.</summary>
    public const int SievePort = 4190;

    /// <summary>ManageSieve, in the pod.</summary>
    public const int SieveContainerPort = 34190;

    /// <summary>SMTP submission with <c>AUTH</c>, on the Service and in the pod alike.</summary>
    /// <remarks>
    ///     ⚠ <b>Plaintext <c>AUTH</c>, and it is only acceptable because of where this port is.</b>
    ///     doc 17 § Topology puts submission behind a <i>shared</i> pool that terminates TLS and
    ///     knows the tenant from the credential; this back end is what that pool relays to, over the
    ///     cluster network, on a <c>ClusterIP</c> Service. The pool is not built
    ///     (<c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-shared-pools-are-not-built</c>),
    ///     so today the port is reachable only from inside the cluster — and a password crossing the
    ///     pod network in clear is the cost that pool exists to remove.
    /// </remarks>
    public const int SubmissionPort = 587;

    /// <summary>Dovecot's SASL listener, which Postfix checks a submission password against.</summary>
    /// <remarks>
    ///     ⚠ <b>Bound to 127.0.0.1 and never on the Service.</b> It answers "is this password right"
    ///     for any mailbox of the domain, so anything that could reach it could test passwords
    ///     without ever connecting to a port that logs a login. The three containers share one
    ///     network namespace, which is the only reason Postfix can reach it at all.
    /// </remarks>
    public const int AuthContainerPort = 31234;

    /// <summary>Rspamd's proxy worker in milter mode — what Postfix hands every message to.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         11332 AND NOT 11333, AND THE FIRST CUT OF THIS TYPE HAD THEM THE WRONG WAY ROUND.
    ///     </b> The rendered <c>main.cf</c> named <c>inet:localhost:11333</c> as its milter, which is
    ///     Rspamd's <i>normal</i> worker and speaks Rspamd's HTTP protocol rather than the milter
    ///     protocol. With <c>milter_default_action = tempfail</c> beside it, every message would have
    ///     been deferred at the milter and never signed or delivered — invisible to every test that
    ///     read the rendered documents, and found the first time a Postfix ran against a Rspamd.
    /// </remarks>
    public const int MilterPort = 11332;

    /// <summary>Rspamd's normal worker. Not dialled by anything here; named so 11332 is not mistaken for it.</summary>
    public const int RspamdPort = 11333;

    /// <summary>Rspamd's controller, which serves the metrics the <c>PodMonitor</c> scrapes.</summary>
    public const int MetricsPort = 11334;

    // ── Versions and images ───────────────────────────────────────────────────────────────────

    /// <summary>The Dovecot minors this type offers.</summary>
    /// <remarks>
    ///     ⚠ <b>Both are rendered, and they are two configuration languages.</b> Dovecot 2.4 rewrote
    ///     its settings — <c>mail_location</c> became <c>mail_driver</c> and <c>mail_path</c>, a
    ///     <c>passdb</c> block is named by its driver, the quota plugin's <c>plugin {}</c> keys became
    ///     a <c>quota</c> filter — so <see cref="DovecotConf" /> renders one of two documents by
    ///     version rather than one document both read. Each is parsed by its own image's
    ///     <c>doveconf</c> in <c>MailDataPlaneTests</c>.
    /// </remarks>
    public static ImmutableArray<string> Versions { get; } = ["2.3", "2.4"];

    /// <summary>The default Dovecot minor.</summary>
    public const string DefaultVersion = "2.4";

    /// <summary>The Dovecot image for a minor.</summary>
    /// <param name="version">One of <see cref="Versions" />.</param>
    /// <remarks>
    ///     ⚠ <b>The projects' own images, verified against the registry on 2026-09-23.</b> The first
    ///     cut of this type named <c>docker.io/cybercloud/dovecot</c>, an image nothing builds.
    ///     <c>docker.io/dovecot/dovecot</c> publishes <c>2.4.5</c> (2026-08-28) and <c>2.3.21.1</c>
    ///     (2024-08-14, the last 2.3 release); both tags were read off Docker Hub's tag list and
    ///     pulled the same day. ⚠ The two images are different shapes: 2.4's runs as <c>vmail</c>
    ///     (uid 1000) from <c>/dovecot/sbin</c>, 2.3's as root from <c>/usr/sbin</c>. Both put the
    ///     binary on <c>PATH</c> and both define <c>vmail</c> as uid 1000, which is what lets one pod
    ///     spec serve either.
    /// </remarks>
    public static string DovecotImage(string version) =>
        version == "2.3" ? "docker.io/dovecot/dovecot:2.3.21.1" : "docker.io/dovecot/dovecot:2.4.5";

    /// <summary>Rspamd — filtering, DKIM signing and verification.</summary>
    /// <remarks>
    ///     The project's own image, verified against Docker Hub on 2026-09-23 and pulled the same
    ///     day. It runs as uid 11333, which reads the DKIM key through the pod's
    ///     <see cref="MailGroupId" /> rather than through ownership.
    /// </remarks>
    public const string RspamdImage = "docker.io/rspamd/rspamd:4.1.5";

    /// <summary>Postfix — submission and outbound relay.</summary>
    /// <remarks>
    ///     ⚠ <b>OURS, AND PUBLISHED BY NOTHING.</b> Postfix ships no image, and every third-party one
    ///     rewrites <c>main.cf</c> from its environment at start, which would fight
    ///     <see cref="PostfixMainCf" />. <c>deploy/images/mail-postfix/Dockerfile</c> is Debian 13's
    ///     postfix package and nothing else (3.10.13 on the day it was built); the cluster-backed
    ///     suite builds it and imports it into its own k3s, and no pipeline pushes it here —
    ///     <c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-postfix-image-is-not-published</c>.
    /// </remarks>
    public const string PostfixImage = "docker.io/cybercloud/postfix:1.0.0";

    /// <summary>The group every file the three containers share is readable by.</summary>
    /// <remarks>
    ///     ⚠ <b>1000, which is <c>vmail</c> in both Dovecot images</b>, and the pod's
    ///     <c>fsGroup</c>. Kubernetes adds it as a supplementary group to every container, which is
    ///     how Rspamd — uid 11333 — reads a DKIM key mounted <c>0440</c> without the key being
    ///     readable by anything else in the image.
    /// </remarks>
    public const int MailGroupId = 1000;

    /// <summary>The Dovecot container's name, which is also its component label.</summary>
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
        """^([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$""";

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
    ///     <para>
    ///     ⚠ Lower-cased, because DNS is case-insensitive and every file this type renders is not:
    ///     a body saying <c>Example.COM</c> would otherwise render a Postfix that accepts mail for one
    ///     spelling and a Dovecot home directory for another.
    ///     </para>
    /// </remarks>
    public static string Domain(JsonElement desired) =>
        Root(desired, "domain", string.Empty).ToLowerInvariant();

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
                .Where(static x => x.ValueKind is JsonValueKind.String)
                .Select(static x => x.GetString() ?? string.Empty)
                .Where(static x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
        ];
    }

    // ── The DKIM public key ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>p=</c> tag of the DKIM record: the base64 SubjectPublicKeyInfo of the minted key.
    /// </summary>
    /// <param name="privateKeyPem">The PKCS#8 PEM read back out of the vault.</param>
    /// <param name="published">The base64 <c>p=</c> value, or empty when the key does not parse.</param>
    /// <returns><see langword="true" /> when the key parsed.</returns>
    /// <remarks>
    ///     ⚠ <b>Derived from the private key rather than stored alongside it.</b> Two stored halves
    ///     can disagree; a derivation cannot. <see cref="MailDnsRecords.TryRequired" /> is the
    ///     consumer, and it is a function rather than a field for the same reason.
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

    /// <summary>The <c>Secret</c> holding the domain's DKIM key and master password.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string CredentialsSecretName(string name) => name + "-mail-credentials";

    /// <summary>The <c>Secret</c> the domain's mailboxes write themselves into.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string UsersSecretName(string name) => name + "-mail-users";

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

    /// <summary>The credentials <c>Secret</c> a domain owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef CredentialsSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = CredentialsSecretName(name) };

    /// <summary>The mailbox <c>Secret</c> a domain owns and its mailboxes co-write.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef UsersSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = UsersSecretName(name) };

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

    /// <summary>The six objects a domain is, in dependency order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Both <c>Secret</c>s before the <c>StatefulSet</c></b>, because the pod mounts both and
    ///     a container whose mount is missing sits in <c>ContainerCreating</c> rather than failing in
    ///     a way the loop would see — and the mailbox <c>Secret</c> before any mailbox can exist to
    ///     co-write it, which is why an empty one is applied at all.
    /// </remarks>
    public static ImmutableArray<ObjectRef> Objects(string ns, string name) => [
        CredentialsSecretRef(ns, name),
        UsersSecretRef(ns, name),
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
    /// <param name="version">The Dovecot minor.</param>
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
        string location = "eu-central",
        string version = DefaultVersion
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["domain"] = domain,
                ["version"] = version,
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

    /// <summary>
    ///     The mailbox <c>Secret</c> as the domain applies it: a name, a type, the mail domain as an
    ///     annotation, and no data.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body, which carries the domain.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>EMPTY, AND EMPTY IS THE WHOLE DESIGN.</b> Every key this object ever carries is a
    ///         mailbox's, written by that mailbox's reconciler as a second writer
    ///         (<c>ReconcileContext.CoWriter</c>, docs/plan/09 § A second writer on an object) under
    ///         the domain's shared co-writer manager — see <see cref="MailMailboxes" />. The domain's
    ///         own apply names no <c>data</c> at all, so server-side apply gives its field manager no
    ///         claim on a single key, and a domain re-applying this object never removes a mailbox.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <c>Secret</c> and not a <c>ConfigMap</c></b>, because what lands here is a
    ///         password hash per mailbox. It is mounted into the Dovecot and Postfix containers only —
    ///         not Rspamd, which has no use for it.
    ///     </para>
    /// </remarks>
    public static string UsersSecretJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject {
                ["name"] = UsersSecretName(name),
                // ⚠ How a mailbox learns the domain — MailMailboxes.DomainAnnotation says why it has to.
                ["annotations"] = new JsonObject { [MailMailboxes.DomainAnnotation] = Domain(desired) }
            },
            ["type"] = "Opaque"
        }.ToJsonString();
    }

    /// <summary>The <c>ConfigMap</c> document a desired body becomes, with sending in a given state.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="sending">
    ///     Whether this domain may send beyond itself — <see cref="MailSending" />, decided by the DNS
    ///     verification and the platform's suspension seam, never by the body.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>No credential reaches this object.</b> The DKIM key is mounted from the credentials
    ///     <c>Secret</c> and the mailbox hashes from the mailbox <c>Secret</c>; a <c>ConfigMap</c> is
    ///     readable by anyone holding <c>get configmaps</c>, which is a strictly weaker right than
    ///     <c>get secrets</c>.
    /// </remarks>
    public static string ConfigMapJson(string name, JsonElement desired, MailSending sending) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ConfigMapName(name) },
            ["data"] = new JsonObject {
                [DovecotConfKey] = DovecotConf(desired),
                [PostfixMainCfKey] = PostfixMainCf(desired, sending),
                [PostfixStartKey] = PostfixStartScript(desired),
                [RspamdProxyKey] = RspamdWorkerProxy(),
                [RspamdDkimKey] = RspamdDkimSigning(desired),
                [RspamdActionsKey] = RspamdActions(desired)
            }
        }.ToJsonString();
    }

    /// <summary>The <c>ConfigMap</c> key Dovecot reads.</summary>
    public const string DovecotConfKey = "dovecot.conf";

    /// <summary>The <c>ConfigMap</c> key the Postfix start script copies to <c>/etc/postfix/main.cf</c>.</summary>
    public const string PostfixMainCfKey = "main.cf";

    /// <summary>The <c>ConfigMap</c> key the Postfix container runs.</summary>
    public const string PostfixStartKey = "postfix-start.sh";

    /// <summary>The <c>ConfigMap</c> key mounted as Rspamd's <c>local.d/worker-proxy.inc</c>.</summary>
    public const string RspamdProxyKey = "rspamd-worker-proxy.inc";

    /// <summary>The <c>ConfigMap</c> key mounted as Rspamd's <c>local.d/dkim_signing.conf</c>.</summary>
    public const string RspamdDkimKey = "rspamd-dkim-signing.conf";

    /// <summary>The <c>ConfigMap</c> key mounted as Rspamd's <c>local.d/actions.conf</c>.</summary>
    public const string RspamdActionsKey = "rspamd-actions.conf";

    /// <summary>Where the <c>ConfigMap</c> is mounted in the Dovecot and Postfix containers.</summary>
    public const string ConfigDirectory = "/etc/mail/config";

    /// <summary>Where the mailbox <c>Secret</c> is mounted in the Dovecot and Postfix containers.</summary>
    /// <remarks>
    ///     ⚠ <b>One file per key, and the file names are the contract with <see cref="MailMailboxes" />.</b>
    ///     Dovecot opens <c>{local}.passwd</c> by the login's local part; Postfix's start script
    ///     lists <c>*.passwd</c> for the mailbox map and concatenates <c>*.virtual</c> for the alias
    ///     map. A key that is neither — <c>*.claim</c> — is read by nothing and exists so that two
    ///     mailboxes cannot both claim one address.
    /// </remarks>
    public const string UsersDirectory = "/etc/mail/users";

    /// <summary>Where the DKIM key is mounted in the Rspamd container, and only there.</summary>
    public const string DkimDirectory = "/etc/mail/dkim";

    /// <summary>The file the DKIM key is projected to — <see cref="DkimSelector" /> plus <c>.key</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THE PATH RSPAMD IS GIVEN MUST NOT BE VALID BASE64, AND THE FIRST PATH THIS TYPE
    ///             TRIED WAS.
    ///         </b> Rspamd's <c>dkim_signing</c> hands the configured <c>path</c> to
    ///         <c>rspamd_dkim_sign_key_load</c>, and a string that decodes as base64 is taken for an
    ///         inline key rather than a file name. <c>/etc/mail/secrets/dkimPrivateKey</c> is 32
    ///         characters of <c>[A-Za-z/]</c> — valid base64 — so Rspamd 4.1.5 signed every message
    ///         with <c>a=ed25519-sha256</c> under a key derived from the path's own bytes, reported
    ///         <c>DKIM_SIGNED</c>, and produced signatures no published record could ever verify.
    ///         Found by reading the <c>a=</c> tag of the first message a lab pod delivered; nothing
    ///         logs it. A dot is not in the base64 alphabet, so a <c>.key</c> suffix closes it.
    ///     </para>
    ///     <para>
    ///         ⚠ Projected under a new name through the volume's <c>items</c> rather than by renaming
    ///         the vault field, because the field name is <see cref="DkimPrivateKeyField" /> in every
    ///         vault document already minted.
    ///     </para>
    /// </remarks>
    public const string DkimKeyFile = DkimSelector + ".key";

    /// <summary>Dovecot's configuration for a body's version — LMTP and IMAP, Pigeonhole when asked for.</summary>
    /// <param name="desired">The validated desired body, which carries the domain and the version.</param>
    /// <remarks>
    ///     <para>
    ///         Every setting here was run against the real image before it was written down, and the
    ///         three that looked right and were not are worth keeping:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             ⚠ <b>The domain's quota is a TOP-LEVEL setting in 2.4, not the quota filter's.</b>
    ///             <c>quota "User quota" { quota_storage_size = … }</c> parses, and then a mailbox's
    ///             <c>userdb_quota_storage_size</c> is silently ignored, because a setting inside a
    ///             named filter outranks the userdb's override of the global one. <c>doveadm quota
    ///             get</c> showed every mailbox at the domain's limit until the default moved out.
    ///         </item>
    ///         <item>
    ///             ⚠ <b>The SASL listener takes <c>listen</c> in 2.4 and <c>address</c> in 2.3.</b>
    ///             2.4.5 refuses <c>address</c> as an unknown setting, and it is the one listener that
    ///             must not bind the pod's address — see <see cref="AuthContainerPort" />.
    ///         </item>
    ///         <item>
    ///             ⚠ <b><c>chroot =</c> on both login services.</b> A login process chroots by
    ///             default, which needs <c>CAP_SYS_CHROOT</c>; the 2.4 image carries it as a file
    ///             capability, and any pod that sets <c>allowPrivilegeEscalation: false</c> strips
    ///             file capabilities. Not chrooting a process that already runs as an unprivileged
    ///             user in its own container gives up nothing the container does not already provide.
    ///         </item>
    ///     </list>
    /// </remarks>
    public static string DovecotConf(JsonElement desired) =>
        Version(desired) == "2.3" ? DovecotConf23(desired) : DovecotConf24(desired);

    static string DovecotConf24(JsonElement desired) {
        var sieve = SieveEnabled(desired);
        var builder = new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append("dovecot_config_version = 2.4.0\n")
            .Append("dovecot_storage_version = 2.4.0\n")
            .Append("base_dir = /run/dovecot\n")
            .Append("state_dir = /run/dovecot\n")
            .Append("default_internal_user = vmail\n")
            .Append("default_internal_group = vmail\n")
            .Append("default_login_user = vmail\n")
            .Append("listen = *\n")
            .Append("log_path = /dev/stderr\n")
            .Append(CultureInfo.InvariantCulture, $"protocols = {(sieve ? "imap lmtp sieve" : "imap lmtp")}\n")
            .Append("ssl = no\n")
            .Append("auth_allow_cleartext = yes\n")
            .Append("auth_mechanisms = plain login\n")
            .Append("mail_driver = mdbox\n")
            .Append("mail_home = /srv/mail/%{user | domain}/%{user | username}\n")
            .Append("mail_path = ~/mdbox\n")
            .Append("mail_uid = vmail\n")
            .Append("mail_gid = vmail\n")
            .Append("first_valid_uid = 1000\n")
            .Append("passdb passwd-file {\n")
            .Append(CultureInfo.InvariantCulture, $"  passwd_file_path = {UsersDirectory}/%{{user | username | lower}}.passwd\n")
            .Append("}\n")
            .Append("userdb passwd-file {\n")
            .Append(CultureInfo.InvariantCulture, $"  passwd_file_path = {UsersDirectory}/%{{user | username | lower}}.passwd\n")
            .Append("}\n")
            .Append("mail_plugins {\n")
            .Append("  quota = yes\n")
            .Append("}\n")
            .Append(CultureInfo.InvariantCulture, $"quota_storage_size = {DovecotSize(MailboxQuota(desired))}\n")
            .Append("quota \"User quota\" {\n")
            .Append("}\n")
            .Append("protocol imap {\n")
            .Append("  mail_plugins {\n")
            .Append("    imap_quota = yes\n")
            .Append("  }\n")
            .Append("}\n");

        if (sieve) {
            builder
                .Append("protocol lmtp {\n")
                .Append("  mail_plugins {\n")
                .Append("    sieve = yes\n")
                .Append("  }\n")
                .Append("}\n")
                .Append("sieve_script personal {\n")
                .Append("  driver = file\n")
                .Append("  path = ~/sieve\n")
                .Append("  active_path = ~/.dovecot.sieve\n")
                .Append("}\n")
                .Append("service managesieve-login {\n")
                .Append("  chroot =\n")
                .Append("  inet_listener sieve {\n")
                .Append(CultureInfo.InvariantCulture, $"    port = {Text(SieveContainerPort)}\n")
                .Append("  }\n")
                .Append("}\n");
        }

        return builder
            .Append("service imap-login {\n")
            .Append("  chroot =\n")
            .Append("  inet_listener imap {\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(ImapContainerPort)}\n")
            .Append("  }\n")
            // ⚠ Disabled rather than omitted: the built-in default declares imaps on 993, which an
            // unprivileged Dovecot cannot bind — and see ImapPort for why TLS is not this pod's job.
            .Append("  inet_listener imaps {\n")
            .Append("    port = 0\n")
            .Append("  }\n")
            .Append("}\n")
            .Append("service lmtp {\n")
            .Append("  inet_listener lmtp {\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(LmtpContainerPort)}\n")
            .Append("  }\n")
            .Append("}\n")
            .Append("service auth {\n")
            .Append("  inet_listener sasl {\n")
            .Append("    listen = 127.0.0.1\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(AuthContainerPort)}\n")
            .Append("  }\n")
            .Append("}\n")
            .ToString();
    }

    static string DovecotConf23(JsonElement desired) {
        var sieve = SieveEnabled(desired);
        var builder = new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append("base_dir = /run/dovecot\n")
            .Append("state_dir = /run/dovecot/state\n")
            .Append("default_internal_user = vmail\n")
            .Append("default_internal_group = vmail\n")
            .Append("default_login_user = vmail\n")
            .Append("listen = *\n")
            .Append("log_path = /dev/stderr\n")
            .Append(CultureInfo.InvariantCulture, $"protocols = {(sieve ? "imap lmtp sieve" : "imap lmtp")}\n")
            .Append("ssl = no\n")
            .Append("disable_plaintext_auth = no\n")
            .Append("auth_mechanisms = plain login\n")
            .Append("mail_location = mdbox:~/mdbox\n")
            .Append("mail_home = /srv/mail/%Ld/%Ln\n")
            .Append("mail_uid = vmail\n")
            .Append("mail_gid = vmail\n")
            .Append("first_valid_uid = 1000\n")
            .Append("mail_plugins = $mail_plugins quota\n")
            .Append("passdb {\n")
            .Append("  driver = passwd-file\n")
            .Append(CultureInfo.InvariantCulture, $"  args = {UsersDirectory}/%Ln.passwd\n")
            .Append("}\n")
            .Append("userdb {\n")
            .Append("  driver = passwd-file\n")
            .Append(CultureInfo.InvariantCulture, $"  args = {UsersDirectory}/%Ln.passwd\n")
            .Append("}\n")
            .Append("plugin {\n")
            .Append("  quota = count:User quota\n")
            // The count driver refuses to start without it.
            .Append("  quota_vsizes = yes\n")
            .Append(CultureInfo.InvariantCulture, $"  quota_rule = *:storage={DovecotSize(MailboxQuota(desired))}\n");

        if (sieve) {
            builder.Append("  sieve = file:~/sieve;active=~/.dovecot.sieve\n");
        }

        builder
            .Append("}\n")
            .Append("protocol imap {\n")
            .Append("  mail_plugins = $mail_plugins imap_quota\n")
            .Append("}\n");

        if (sieve) {
            builder
                .Append("protocol lmtp {\n")
                .Append("  mail_plugins = $mail_plugins sieve\n")
                .Append("}\n")
                .Append("service managesieve-login {\n")
                .Append("  chroot =\n")
                .Append("  inet_listener sieve {\n")
                .Append(CultureInfo.InvariantCulture, $"    port = {Text(SieveContainerPort)}\n")
                .Append("  }\n")
                .Append("}\n");
        }

        return builder
            .Append("service imap-login {\n")
            .Append("  chroot =\n")
            .Append("  inet_listener imap {\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(ImapContainerPort)}\n")
            .Append("  }\n")
            .Append("  inet_listener imaps {\n")
            .Append("    port = 0\n")
            .Append("  }\n")
            .Append("}\n")
            .Append("service lmtp {\n")
            .Append("  inet_listener lmtp {\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(LmtpContainerPort)}\n")
            .Append("  }\n")
            .Append("}\n")
            .Append("service auth {\n")
            .Append("  inet_listener sasl {\n")
            .Append("    address = 127.0.0.1\n")
            .Append(CultureInfo.InvariantCulture, $"    port = {Text(AuthContainerPort)}\n")
            .Append("  }\n")
            .Append("}\n")
            .ToString();
    }

    /// <summary>A Kubernetes quantity as a Dovecot size: whole mebibytes, never zero.</summary>
    /// <param name="quantity">A quantity the schema accepted, for example <c>1Gi</c>.</param>
    /// <remarks>
    ///     ⚠ Dovecot's units are powers of 1024, so <c>M</c> is a mebibyte and <c>1Gi</c> is
    ///     <c>1024M</c>. A decimal quantity (<c>1G</c>) is rounded down to whole mebibytes; the
    ///     mebibyte is the unit because Dovecot has no smaller one that both majors print back.
    ///     ⚠ Never below <c>1M</c>, because Dovecot reads a zero limit as no limit at all — a
    ///     <c>100Ki</c> quota rounded down would be an unlimited mailbox.
    /// </remarks>
    public static string DovecotSize(string quantity) {
        var bytes = KubeQuantity.TryGibibytes(quantity, out var gibibytes) ? gibibytes * 1024m * 1024m * 1024m : 0m;
        var mebibytes = Math.Max(1m, Math.Floor(bytes / (1024m * 1024m)));

        return mebibytes.ToString("0", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>Postfix's <c>main.cf</c> — submission and outbound for this domain.</summary>
    /// <param name="desired">The validated desired body, which carries the domain.</param>
    /// <param name="sending">Whether mail may leave the domain — see <see cref="MailSending" />.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every hostname below is the BODY's domain, never the resource's name.</b> Rendering
    ///         the name would give Postfix <c>virtual_mailbox_domains = example-com</c>, and mail for
    ///         <c>example.com</c> would be refused as a relay attempt — a total delivery outage whose
    ///         cause is one identifier that looks almost right.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE SENDING GATE IS <c>smtpd_relay_restrictions</c>, AND IT IS THE ONLY LINE
    ///         <paramref name="sending" /> MOVES.</b> Mail for the domain's own mailboxes is always
    ///         accepted — <c>permit_auth_destination</c> — because receiving is not what reputation is
    ///         lost on. Mail for anywhere else is relayed for an authenticated sender only when
    ///         <see cref="MailSending.Open" />; otherwise it is refused at <c>RCPT TO</c> with a
    ///         sentence naming why, which is doc 17 § Deliverability's <i>"the platform will not
    ///         enable sending until the DNS records verify"</i> and its abuse desk's suspension, made
    ///         into something an SMTP client is told rather than a queue nobody reads. ⚠ Postfix
    ///         refuses to start an <c>smtpd</c> whose relay restrictions contain no terminal refusal,
    ///         which is why both closed spellings end in <c>reject</c> after the explanatory map.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The first cut rendered a catch-all as <c>luser_relay</c> and
    ///         <c>mydestination = {domain}</c> beside <c>virtual_mailbox_domains = {domain}</c>.</b>
    ///         <c>luser_relay</c> belongs to <c>local(8)</c> and does nothing for a virtual domain, and
    ///         Postfix warns that a domain listed in both is misconfigured. The catch-all is now an
    ///         <c>@domain</c> line in the alias map the start script builds — see
    ///         <see cref="PostfixStartScript" />.
    ///     </para>
    /// </remarks>
    public static string PostfixMainCf(JsonElement desired, MailSending sending) {
        var domain = Domain(desired);
        var relays = RelayHosts(desired);

        var builder = new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append("compatibility_level = 3.6\n")
            .Append(CultureInfo.InvariantCulture, $"myhostname = {domain}\n")
            .Append(CultureInfo.InvariantCulture, $"mydomain = {domain}\n")
            .Append("myorigin = $mydomain\n")
            .Append("mydestination = localhost\n")
            .Append("inet_interfaces = all\n")
            .Append("mynetworks = 127.0.0.0/8\n")
            .Append("maillog_file = /dev/stdout\n")
            .Append(CultureInfo.InvariantCulture, $"virtual_mailbox_domains = {domain}\n")
            .Append("virtual_mailbox_maps = texthash:/etc/postfix/mailboxes\n")
            .Append("virtual_alias_maps = texthash:/etc/postfix/virtual\n")
            .Append(CultureInfo.InvariantCulture, $"virtual_transport = lmtp:inet:127.0.0.1:{Text(LmtpContainerPort)}\n")
            .Append("smtpd_sasl_type = dovecot\n")
            .Append(CultureInfo.InvariantCulture, $"smtpd_sasl_path = inet:127.0.0.1:{Text(AuthContainerPort)}\n")
            .Append(CultureInfo.InvariantCulture, $"smtpd_milters = inet:127.0.0.1:{Text(MilterPort)}\n")
            .Append("non_smtpd_milters = $smtpd_milters\n")
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
                $"smtp_fallback_relay = {string.Join(", ", relays.Skip(1).Select(static x => "[" + x + "]"))}\n"
            );
        }

        builder.Append(
            CultureInfo.InvariantCulture,
            $"smtpd_relay_restrictions = {RelayRestrictions(domain, sending)}\n"
        );

        // ⚠ THE SECOND HALF OF THE GATE, AND THE ONE A SUBMISSION TEST CANNOT SEE. A mailbox's
        // forwardTo is expanded by the alias map AFTER smtpd accepted the recipient as local, so the
        // relay restrictions above never judge it — forwarded mail would leave a held domain through
        // the smtp transport. Deferring that transport holds it in the queue instead, and a reload
        // without this line lets the next queue run deliver it: held, not lost and not bounced.
        if (sending != MailSending.Open) {
            builder.Append("defer_transports = smtp\n");
        }

        return builder.ToString();
    }

    /// <summary>The relay restrictions for one state of the gate.</summary>
    /// <param name="domain">The mail domain, for the refusal's sentence.</param>
    /// <param name="sending">The gate.</param>
    public static string RelayRestrictions(string domain, MailSending sending) =>
        sending switch {
            MailSending.Open => "permit_sasl_authenticated, reject_unauth_destination",
            MailSending.Suspended =>
                "permit_auth_destination, check_recipient_access static:{REJECT 5.7.1 Outbound mail for "
                + domain + " is suspended by the platform's abuse desk}, reject",
            _ => "permit_auth_destination, check_recipient_access static:{REJECT 5.7.1 Outbound mail for "
                + domain + " is held until its SPF, DKIM and DMARC records verify}, reject"
        };

    /// <summary>The script the Postfix container runs: build the maps, start, and reload on change.</summary>
    /// <param name="desired">The validated desired body, which carries the domain and the catch-all.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A LOOP, BECAUSE NEITHER MOUNT TELLS POSTFIX ANYTHING.</b> The kubelet swaps a
    ///         <c>ConfigMap</c> or <c>Secret</c> volume's contents in place when the object changes —
    ///         within its sync period, about a minute — and Postfix reads <c>main.cf</c> and its
    ///         <c>texthash</c> maps once per process. So every five seconds the script checksums both
    ///         mounts, and on a change it rebuilds the two maps and runs <c>postfix reload</c>. That is
    ///         how a new mailbox, a changed alias and the sending gate reach a running Postfix without a
    ///         pod restart, and it is why the gate's latency is the kubelet's rather than this loop's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>chroot</c> is turned off for every service.</b> Debian's <c>master.cf</c>
    ///         chroots most of them into <c>/var/spool/postfix</c>, which needs a copy of
    ///         <c>resolv.conf</c> and friends that only Debian's init script makes. The container is
    ///         the isolation boundary here, as it is for Dovecot's login services.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The domain and the catch-all are interpolated into shell, and both are safe by
    ///         the schema.</b> <see cref="HostnamePattern" /> and
    ///         <see cref="OptionalLocalPartPattern" /> admit neither a quote nor whitespace, and the
    ///         values are single-quoted besides.
    ///     </para>
    /// </remarks>
    public static string PostfixStartScript(JsonElement desired) {
        var domain = Domain(desired);
        var catchAll = CatchAll(desired);

        return new StringBuilder()
            .Append("#!/bin/sh\n")
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append("set -eu\n")
            .Append(CultureInfo.InvariantCulture, $"domain='{domain}'\n")
            .Append(CultureInfo.InvariantCulture, $"catch_all='{catchAll}'\n")
            .Append("fingerprint() {\n")
            .Append(CultureInfo.InvariantCulture, $"    cat {ConfigDirectory}/{PostfixMainCfKey} {UsersDirectory}/* 2>/dev/null | cksum\n")
            .Append("}\n")
            .Append("build() {\n")
            .Append(CultureInfo.InvariantCulture, $"    cp {ConfigDirectory}/{PostfixMainCfKey} /etc/postfix/main.cf\n")
            .Append("    : > /etc/postfix/mailboxes.next\n")
            .Append("    : > /etc/postfix/virtual.next\n")
            .Append(CultureInfo.InvariantCulture, $"    for file in {UsersDirectory}/*.passwd; do\n")
            .Append("        [ -e \"$file\" ] || continue\n")
            .Append("        printf '%s@%s OK\\n' \"$(basename \"$file\" .passwd)\" \"$domain\" >> /etc/postfix/mailboxes.next\n")
            .Append("    done\n")
            .Append(CultureInfo.InvariantCulture, $"    for file in {UsersDirectory}/*.virtual; do\n")
            .Append("        [ -e \"$file\" ] || continue\n")
            .Append("        cat \"$file\" >> /etc/postfix/virtual.next\n")
            .Append("    done\n")
            .Append("    if [ -n \"$catch_all\" ]; then\n")
            .Append("        printf '@%s %s@%s\\n' \"$domain\" \"$catch_all\" \"$domain\" >> /etc/postfix/virtual.next\n")
            .Append("    fi\n")
            .Append("    mv /etc/postfix/mailboxes.next /etc/postfix/mailboxes\n")
            .Append("    mv /etc/postfix/virtual.next /etc/postfix/virtual\n")
            .Append("}\n")
            .Append("build\n")
            .Append("postconf -F '*/*/chroot = n'\n")
            .Append(CultureInfo.InvariantCulture, $"postconf -M 'submission/inet=submission inet n - n - - smtpd'\n")
            .Append("postconf -P 'submission/inet/syslog_name=postfix/submission' \\\n")
            .Append("    'submission/inet/smtpd_sasl_auth_enable=yes' \\\n")
            .Append("    'submission/inet/smtpd_tls_security_level=none' \\\n")
            .Append("    'submission/inet/smtpd_client_restrictions=permit_sasl_authenticated,reject'\n")
            .Append("postfix check\n")
            .Append("trap 'postfix stop; exit 0' TERM INT\n")
            .Append("postfix start-fg &\n")
            .Append("master=$!\n")
            .Append("seen=$(fingerprint)\n")
            .Append("while kill -0 \"$master\" 2>/dev/null; do\n")
            .Append("    sleep 5 &\n")
            .Append("    wait $! || true\n")
            .Append("    now=$(fingerprint)\n")
            .Append("    if [ \"$now\" != \"$seen\" ]; then\n")
            .Append("        build\n")
            .Append("        postfix reload\n")
            .Append("        seen=$now\n")
            .Append("    fi\n")
            .Append("done\n")
            .Append("wait \"$master\"\n")
            .ToString();
    }

    /// <summary>Rspamd's proxy worker, in milter mode on <see cref="MilterPort" />, scanning in-process.</summary>
    public static string RspamdWorkerProxy() =>
        new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append(CultureInfo.InvariantCulture, $"bind_socket = \"127.0.0.1:{Text(MilterPort)}\";\n")
            .Append("milter = yes;\n")
            .Append("timeout = 120s;\n")
            .Append("upstream \"local\" {\n")
            .Append("  default = yes;\n")
            .Append("  self_scan = yes;\n")
            .Append("}\n")
            .ToString();

    /// <summary>Rspamd's DKIM signer: this domain, <see cref="DkimSelector" />, the key the vault holds.</summary>
    /// <param name="desired">The validated desired body, which carries the domain.</param>
    /// <remarks>
    ///     ⚠ <b>Authenticated senders only, and only as themselves.</b> <c>sign_local</c> is off, so a
    ///     message that reached Postfix unauthenticated is never signed with the tenant's key, and
    ///     <c>allow_username_mismatch</c> is off, so an authenticated <c>alice@</c> is signed for
    ///     only when the <c>From</c> domain is this domain.
    /// </remarks>
    public static string RspamdDkimSigning(JsonElement desired) =>
        new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append("enabled = true;\n")
            .Append("sign_authenticated = true;\n")
            .Append("sign_local = false;\n")
            .Append("allow_username_mismatch = false;\n")
            .Append("use_domain = \"header\";\n")
            .Append("domain {\n")
            .Append(CultureInfo.InvariantCulture, $"  {Domain(desired)} {{\n")
            .Append(CultureInfo.InvariantCulture, $"    selector = \"{DkimSelector}\";\n")
            .Append(CultureInfo.InvariantCulture, $"    path = \"{DkimDirectory}/{DkimKeyFile}\";\n")
            .Append("  }\n")
            .Append("}\n")
            .ToString();

    /// <summary>Rspamd's action thresholds.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Antivirus is not rendered, and the first cut rendered it at a socket nothing
    ///     serves.</b> <c>/run/clamav/clamd.sock</c> had no ClamAV container behind it, and Rspamd's
    ///     antivirus module fails a message whose scanner is unreachable — which, with
    ///     <c>milter_default_action = tempfail</c>, would have deferred every message with an
    ///     attachment. <c>filtering.antivirus</c> is accepted and does nothing until a scanner
    ///     exists: <c>charts/managed/mail/conformance.yaml § owed</c>, <c>antivirus-has-no-scanner</c>.
    /// </remarks>
    public static string RspamdActions(JsonElement desired) =>
        new StringBuilder()
            .Append("# Generated by CyberCloud.Mail/domains. Do not edit in place.\n")
            .Append(CultureInfo.InvariantCulture, $"reject = {Text(RejectThreshold(desired))};\n")
            .Append(CultureInfo.InvariantCulture, $"add_header = {Text(Math.Max(1, RejectThreshold(desired) / 3))};\n")
            .ToString();

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

        var ports = new JsonArray {
            Port("lmtp", LmtpPort, LmtpContainerPort),
            Port("imap", ImapPort, ImapContainerPort),
            Port("submission", SubmissionPort, SubmissionPort)
        };

        if (SieveEnabled(desired)) {
            ports.Add(Port("sieve", SievePort, SieveContainerPort));
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ServiceName(name) },
            ["spec"] = new JsonObject { ["type"] = "ClusterIP", ["selector"] = Selector(name), ["ports"] = ports }
        }.ToJsonString();
    }

    /// <summary>How many ports <see cref="ServiceJson" /> renders for a body.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int ServicePortCount(JsonElement desired) => SieveEnabled(desired) ? 4 : 3;

    /// <summary>The <c>StatefulSet</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One replica, always, and it is not a property.</b> See <see cref="StatefulSetKind" />
    ///         for why: two pods on one mail store corrupt the index. Scaling a mail back end is a
    ///         Dovecot director and a shared filesystem, which is a different design rather than a
    ///         larger number here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The DKIM key is mounted into Rspamd and nowhere else.</b> The first cut mounted the
    ///         credentials into all three containers at <c>0400</c> — which Rspamd, running as uid
    ///         11333, could not read at all, and Dovecot and Postfix had no use for. Now it is one
    ///         <c>items</c> projection, <c>0440</c>, readable through <see cref="MailGroupId" />.
    ///     </para>
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

        var dovecot = new JsonObject {
            ["name"] = ImapComponent,
            ["image"] = DovecotImage(Version(desired)),
            // ⚠ `args`, not `command`: both images start through tini, and replacing the entrypoint
            // would make Dovecot PID 1 with nobody reaping its children.
            ["args"] = new JsonArray { "dovecot", "-F", "-c", ConfigDirectory + "/" + DovecotConfKey },
            ["ports"] = new JsonArray {
                ContainerPort("lmtp", LmtpContainerPort),
                ContainerPort("imap", ImapContainerPort),
                ContainerPort("sieve", SieveContainerPort)
            },
            ["volumeMounts"] = new JsonArray {
                Mount("config", ConfigDirectory),
                Mount("users", UsersDirectory),
                Mount("dovecot-run", "/run/dovecot"),
                Mount(MailVolumeName, "/srv/mail")
            }
        };

        if (resources.Count > 0) {
            dovecot["resources"] = resources;
        }

        var postfix = new JsonObject {
            ["name"] = MtaComponent,
            ["image"] = PostfixImage,
            ["command"] = new JsonArray { "sh", ConfigDirectory + "/" + PostfixStartKey },
            ["ports"] = new JsonArray { ContainerPort("submission", SubmissionPort) },
            ["volumeMounts"] = new JsonArray { Mount("config", ConfigDirectory), Mount("users", UsersDirectory) }
        };

        var rspamd = new JsonObject {
            ["name"] = FilterComponent,
            ["image"] = RspamdImage,
            ["ports"] = new JsonArray { ContainerPort("metrics", MetricsPort) },
            ["volumeMounts"] = new JsonArray { Mount("rspamd-config", "/etc/rspamd/local.d"), Mount("dkim", DkimDirectory) }
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
                        ["securityContext"] = new JsonObject { ["fsGroup"] = MailGroupId },
                        ["containers"] = new JsonArray { dovecot, postfix, rspamd },
                        ["volumes"] = new JsonArray {
                            new JsonObject {
                                ["name"] = "config",
                                ["configMap"] = new JsonObject { ["name"] = ConfigMapName(name) }
                            },
                            new JsonObject {
                                ["name"] = "rspamd-config",
                                ["configMap"] = new JsonObject {
                                    ["name"] = ConfigMapName(name),
                                    ["items"] = new JsonArray {
                                        Item(RspamdProxyKey, "worker-proxy.inc"),
                                        Item(RspamdDkimKey, "dkim_signing.conf"),
                                        Item(RspamdActionsKey, "actions.conf")
                                    }
                                }
                            },
                            new JsonObject {
                                ["name"] = "users",
                                ["secret"] = new JsonObject {
                                    ["secretName"] = UsersSecretName(name),
                                    // 0440, readable by the vmail group — MailGroupId.
                                    ["defaultMode"] = 288
                                }
                            },
                            new JsonObject {
                                ["name"] = "dkim",
                                ["secret"] = new JsonObject {
                                    ["secretName"] = CredentialsSecretName(name),
                                    ["items"] = new JsonArray { Item(DkimPrivateKeyField, DkimKeyFile) },
                                    ["defaultMode"] = 288
                                }
                            },
                            new JsonObject { ["name"] = "dovecot-run", ["emptyDir"] = new JsonObject() }
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
            // A ConfigMap, the two Secrets and a PodMonitor are matched by the reconciler applying
            // them and reading them back at all: none carries a field a controller rewrites, so an
            // apply that landed is an object that matches. ⚠ The mailbox Secret's DATA is its
            // co-writers', and nothing the domain asks for is in it.
            _ => true
        };
    }

    static bool MatchesSet(JsonObject document, JsonElement desired) {
        var spec = document["spec"] as JsonObject;
        var claims = spec?["volumeClaimTemplates"] as JsonArray;
        var size = (claims?.FirstOrDefault() as JsonObject)?["spec"]?["resources"]?["requests"]?["storage"];
        var images = (spec?["template"]?["spec"]?["containers"] as JsonArray)?
            .Select(static x => x?["image"]?.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        return spec?["replicas"]?.GetValue<int>() == 1
            && size?.GetValue<string>() == StorageSize(desired)
            // ⚠ The Dovecot image, because it is the one `version` moves; a set still running the
            // old major after an update is exactly the drift worth reporting.
            && images is not null
            && images.Contains(DovecotImage(Version(desired)));
    }

    static bool MatchesService(JsonObject document, JsonElement desired) =>
        document["spec"]?["ports"] is JsonArray ports && ports.Count == ServicePortCount(desired);

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

    static JsonObject Port(string portName, int port, int targetPort) =>
        new() { ["name"] = portName, ["port"] = port, ["targetPort"] = targetPort };

    static JsonObject ContainerPort(string portName, int port) =>
        new() { ["name"] = portName, ["containerPort"] = port };

    static JsonObject Mount(string volume, string path) => new() { ["name"] = volume, ["mountPath"] = path };

    static JsonObject Item(string key, string path) => new() { ["key"] = key, ["path"] = path };

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
