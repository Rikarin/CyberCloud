namespace CyberCloud.Vault;

/// <summary>
///     Where OpenBao is, which role the silo logs in as, and where the projected token it logs in
///     with is mounted.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every member here is an address or a name, and none is a credential.</b> That is the
///         point of the whole design: the silo's credential is the projected service-account token
///         the kubelet writes into its own pod, so there is nothing to put in configuration, nothing
///         in a manifest's environment, and nothing in a chart's values file. docs/plan/18 §
///         Platform security, the Secrets row — <i>"never in env vars in a manifest"</i> — is a
///         requirement this type is shaped to make easy rather than one it has to be careful about.
///         <c>KubernetesLoginTests.NoOptionCanCarryACredential</c> asserts the absence.
///     </para>
///     <para>
///         ⚠ <b>There is deliberately no <c>Token</c> option, not even for local development.</b> A
///         static token field is the one member every "just for now" wiring reaches for, and once it
///         exists a deployment can set it. A developer who wants to run against
///         <c>bao server -dev</c> registers their own <see cref="IVaultTokenSource" /> in their own
///         host — see <see cref="VaultSiloBuilderExtensions" /> — which is a line of code somebody has
///         to write on purpose rather than a value somebody can paste into a secret manager.
///     </para>
/// </remarks>
public sealed class VaultOptions {
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "CyberCloud:Vault";

    /// <summary>
    ///     The default path the kubelet projects a service-account token to.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A default rather than a required value, and this is the one place a hard-coded path
    ///     is right.</b> It is fixed by Kubernetes, not by us — every pod with
    ///     <c>automountServiceAccountToken</c> gets it — so requiring a deployment to restate it
    ///     would be asking an operator to type a constant. A pod that projects an
    ///     <i>audience-bound</i> token puts it somewhere else, which is what
    ///     <see cref="TokenFilePath" /> is for.
    /// </remarks>
    public const string DefaultTokenFilePath = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>The OpenBao base address, for example <c>https://openbao.cc-vault.svc:8200</c>.</summary>
    /// <remarks>
    ///     ⚠ Refused unless it is absolute and <c>https</c>, with one exception —
    ///     <see cref="AllowInsecureTransport" />. docs/plan/18 § Platform security opens with
    ///     "TLS 1.3 everywhere", and a vault reached over plaintext hands every secret it serves to
    ///     anything on the path.
    /// </remarks>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    ///     The OpenBao role the silo logs in as, for example <c>cc-silo</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a credential — a role is a name OpenBao maps a <i>verified</i> service account onto,
    ///     and knowing it grants nothing. The role's own
    ///     <c>bound_service_account_names</c>/<c>bound_service_account_namespaces</c> are what decide
    ///     whether this pod may use it, and those live in OpenBao.
    /// </remarks>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    ///     Whether this host has been pointed at a vault at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two keys <c>AddOpenBaoSecretResolver</c> validates, and no more.</b> A host asks
    ///         this before opting in, so it has to distinguish "nobody configured a vault" — a
    ///         supported shape, which keeps the refusing seams — from "somebody configured one badly",
    ///         which is a start-up failure naming the key. Testing anything else here would turn a typo
    ///         in <c>KvMountPath</c> into a silo that silently has no vault.
    ///     </para>
    ///     <para>
    ///         ⚠ Shaped after <c>SiloIdentityOptions.IsConfigured</c>, which answers the same question
    ///         for the OTP delivery seam and is the precedent a host's conditional follows.
    ///     </para>
    /// </remarks>
    public bool IsConfigured => Address.Length > 0 && Role.Length > 0;

    /// <summary>Where the Kubernetes auth method is mounted. <c>kubernetes</c> unless somebody moved it.</summary>
    public string AuthMountPath { get; set; } = "kubernetes";

    /// <summary>Where the <c>kv-v2</c> engine is mounted. A <see cref="SecretRef.Path" /> is relative to it.</summary>
    /// <remarks>
    ///     ⚠ <b>The mount is configuration and the path is data, and conflating them is how a handle
    ///     stops being portable.</b> <c>SecretRef { Path = "tenants/x/postgres/main" }</c> means the
    ///     same secret in every region; if the mount name were baked into the handle, moving the
    ///     engine would rewrite every stored handle in the durable tier.
    /// </remarks>
    public string KvMountPath { get; set; } = "secret";

    /// <summary>
    ///     The OpenBao namespace to read within, or empty for the root namespace.
    /// </summary>
    /// <remarks>
    ///     docs/plan/18 § Shape puts one cluster per region with a <b>namespace per tenant</b>. This
    ///     is the namespace the <i>platform</i> operates in; a per-tenant read selects its namespace
    ///     per call rather than per silo, which this type has no member for because nothing resolves
    ///     per-tenant handles yet.
    ///     <para>
    ///         ⚠ Namespaces are open-source in OpenBao from 2.3 and were <b>not</b> in the fork's
    ///         first releases — they are Enterprise-only in HashiCorp Vault, which is what makes
    ///         docs/plan/18's topology affordable at all under ADR-011. Read in OpenBao's own
    ///         namespaces announcement, which also promises API compatibility with Vault Enterprise,
    ///         rather than assumed from Vault's documentation. It is why <c>OpenBaoFixture</c> pins a
    ///         2.x image.
    ///     </para>
    /// </remarks>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Where the projected service-account token is mounted.</summary>
    /// <remarks>
    ///     ⚠ Read fresh on every login and never cached, because the kubelet rewrites this file in
    ///     place as the token rotates. <c>CyberCloud.Sdk</c>'s <c>WorkloadIdentityCredential</c>
    ///     re-reads its own token file for the same reason and says so.
    /// </remarks>
    public string TokenFilePath { get; set; } = DefaultTokenFilePath;

    /// <summary>How long a single OpenBao request may take.</summary>
    /// <remarks>
    ///     ⚠ Short on purpose. A resolve happens inside a reconcile pass, and docs/plan/08 § The
    ///     reconcile loop bounds that pass at 30 seconds on a single-threaded grain turn. A vault
    ///     that has stopped answering must fail the pass long before it burns the whole budget.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     How far before a login's lease expires the cached token is thrown away.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The skew is what keeps a resolve from racing its own token's expiry.</b> Without it a
    ///     token that OpenBao considers valid when the request is built can be expired by the time it
    ///     arrives, and the read comes back <c>403</c> — indistinguishable, at the call site, from
    ///     the platform genuinely lacking permission. Bigger than any plausible request latency, far
    ///     smaller than any plausible lease.
    /// </remarks>
    public TimeSpan TokenExpirySkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Whether a plaintext <c>http</c> address is accepted. ⚠ For a test against a container, and
    ///     nothing else.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This exists because the alternative was worse, and it is worth saying which
    ///     alternative.</b> A vault suite needs a real OpenBao to be worth running, a container in
    ///     CI has no certificate anybody trusts, and the two ways to reach one are this flag or a
    ///     custom <c>HttpMessageHandler</c> that skips certificate validation. The flag is a single
    ///     boolean a reviewer can grep for and a deployment gate can refuse; a handler that ignores
    ///     certificates is invisible in configuration and would sit in the same code path production
    ///     runs on.
    ///     <para>
    ///         ⚠ Setting it does not merely permit <c>http</c> — it makes
    ///         <see cref="VaultSiloBuilderExtensions.AddOpenBaoSecretResolver(ISiloBuilder, VaultOptions)" />
    ///         log a warning naming the address, every time, so a production silo that has it set
    ///         says so in its first hundred log lines.
    ///     </para>
    /// </remarks>
    public bool AllowInsecureTransport { get; set; }
}
