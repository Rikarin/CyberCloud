// ⚠ For `Result<T>` on the retained-volume seam. Safe beside the GlobalUsings ErrorCode alias, which
// is what disambiguates the one name this namespace collides with.

using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail;

/// <summary>
///     Converges one mail domain onto the five objects it is: a <c>Secret</c>, a <c>ConfigMap</c>, a
///     <c>Service</c>, a <c>StatefulSet</c> and a <c>PodMonitor</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             NO OPERATOR EXISTS FOR ANY OF THE THREE COMPONENTS, AND THAT IS THE FIRST FACT ABOUT
///             THIS RECONCILER.
///         </b> Postfix, Dovecot and Rspamd are three of the oldest and most widely
///         run daemons on the internet and not one has a Kubernetes controller worth depending on —
///         so every object here is a core kind this provider names itself, the shape
///         <see cref="MailDomains" /> shares with <c>charts/managed/nats</c> after
///         <c>nats-operator</c> was archived. The cost is visible in this file: five applies and
///         five reads where the RabbitMQ row has one of each.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop, and where each is satisfied:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> ⚠
///             <b>
///                 This is the clause this type is most able to break, and the
///                 mint is where.
///             </b> <see cref="MailDomains.GenerateCredentials" /> returns a NEW DKIM
///             keypair on every call, and what reaches the <c>Secret</c> is never that keypair: it is
///             what <c>ISecretWriter.MintAsync</c>'s <c>cas=0</c> left in the vault on the first pass
///             and <c>ISecretResolver</c> read back on this one. A pass that rendered the freshly
///             generated key would apply a different <c>Secret</c> every time <i>and</i> invalidate
///             the public key the tenant has already published in DNS — mail that fails DKIM at every
///             receiver while this loop reports the domain converged. The rest of the clause is
///             ordinary: the apply is server-side, and
///             <see cref="MailDomains.RelayHosts" /> sorts so that two bodies naming the same relays
///             in different orders render the same Postfix configuration.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's
///             <see cref="IClock" />, which is a dependency rather than a memory. ⚠ A reconciler is
///             registered <b>as a singleton, by concrete type</b>, so one instance serves every
///             tenant in the process — a field caching a resolved DKIM key would hand tenant B
///             tenant A's signing identity, which is the worst instance of this failure class the
///             catalogue has.
///         </item>
///         <item>
///             <b>Bounded.</b> One mint, two resolves, five applies and five reads, all on the
///             caller's token. ⚠ There is no wait for Dovecot to be <i>serving</i> — the mail store
///             is opened and indexed on first start and clause 3's budget is thirty seconds, so
///             readiness is <see cref="ReconcileOutcome.InProgress" /> and the reminder comes back.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of every object applied, never an apply's own result.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>THE APPLY ORDER IS LOAD-BEARING AND THE FIRST TWO ENTRIES ARE THE REASON.</b> The
///         <c>Secret</c> and the <c>ConfigMap</c> come first because the pod mounts both and a
///         container whose mount is missing sits in <c>CreateContainerConfigError</c> rather than
///         failing in a way this loop would see. The <c>Service</c> precedes the
///         <c>StatefulSet</c> because the set names it as its <c>serviceName</c>. The
///         <c>PodMonitor</c> is last because nothing depends on it — and because it is the one
///         object whose CRD may be absent, so putting it last means a cluster without Prometheus
///         Operator fails <i>after</i> the mail is running rather than instead of it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Converged here means "the five objects are applied and read back", not "mail is
///             flowing".
///         </b> The honest stronger check is an LMTP conversation with the back end, and it
///         is not made because nothing in this repository can hold one: the Docker-free harness is a
///         dictionary and the cluster-backed harness runs a bare k3s with no mail images. That is
///         written down as owed in <c>charts/managed/mail/conformance.yaml</c> rather than left for
///         somebody to discover.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An <see cref="ApplyResult.Conflict" /> is reported and retried rather than failed and
///             never forced.
///         </b> ADR-013 makes a conflict <i>"a drift event with a name"</i>; forcing
///         would let the platform silently overwrite a tenant's own controller.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class MailDomainReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MailDomains.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // Unreachable through the driver, which refuses a RequiresCluster type with no connection
            // and names the type. Kept because a reconciler is also callable directly.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a mail domain is a StatefulSet "
                + "in a cluster. CyberCloud.Mail/domains declares RequiresCluster, so the driver "
                + "should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        // ── The mint, before anything is applied ───────────────────────────────────────────────
        //
        // ⚠ THE ORDER OF THE TWO FAILURE WINDOWS, AND WHY THIS ONE IS FIRST:
        //
        //   • Mint, then the cluster fails → one orphaned KV document. INERT: nothing runs, nothing
        //     is reachable, nothing is billed, and the next pass finds it and uses it, because
        //     mint-once is cas=0 rather than an overwrite.
        //   • The cluster, then the mint fails → a StatefulSet referencing a Secret that does not
        //     exist, which is three containers stuck in CreateContainerConfigError.
        context.Log.Report("minting", $"ensuring the mail identity of '{name}' is in the vault", 5);

        var credentials = await EnsureCredentialsAsync(context, cancellationToken);

        if (credentials.TryGetError(out var credentialError)) {
            // ⚠ Retryable, and nothing has been applied. A vault that is sealed, unreachable or not
            // wired is a resource that has not started rather than one that failed.
            return ReconcileOutcome.FromFailure(credentialError);
        }

        var secrets = credentials.GetValueOrThrow();

        // ── The five applies, in dependency order ──────────────────────────────────────────────
        var percent = 20;

        foreach (var (target, body) in Documents(context.Namespace, name, context.Desired, secrets)) {
            context.Log.Report(
                "applying",
                $"applying {target.Kind.Kind} '{target.Name}' to {context.Namespace}",
                percent
            );

            percent = Math.Min(percent + 12, 85);

            var applied = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                // ⚠ Not the seven, and not the object's own labels. This puts the six
                // lifetime-stable labels into the claim template so that the PersistentVolumeClaim
                // the StatefulSet controller makes is findable by selector — see
                // MailDomains.ClaimTemplatePath. It is a no-op on the four objects that have no
                // template at that path.
                    .WithTemplateLabels(MailDomains.ClaimTemplatePath)
                    .ObjectJson(body)
                    .ApplyAsync(cancellationToken);

            if (applied.TryGetError(out var applyError)) {
                // ⚠ The code decides, not this call site. An apply that could not reach the cluster
                // is a request that can be made again; one the API server refused — an admission
                // policy, a PodMonitor CRD the bundle never installed, our own credentials — will be
                // refused identically for the next hour.
                return ReconcileOutcome.FromFailure(applyError);
            }

            if (Unfinished(context, applied.GetValueOrThrow()) is { } waiting) {
                return waiting;
            }
        }

        // ── Clause 4. Everything above this line is a claim; these are the readings. ───────────
        //
        // ⚠ EVERY OBJECT, BECAUSE EVERY OBJECT WAS APPLIED. An object applied and never read back is
        // one this loop reports Converged without having observed, and four right ones make the
        // fifth invisible.
        foreach (var target in Targets(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        $"'{target}' was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(readError);
            }

            if (!MailDomains.Matches(read.GetValueOrThrow().Json, context.Desired)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        context.Log.Report("ready", $"the mail domain '{name}' reads back as desired", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            // ⚠ Converged, not Failed, and the asymmetry with ReconcileAsync is deliberate — a
            // teardown with no cluster to reach has nothing left to remove, and failing would park
            // the resource in Deleting: visible, billed and permanent, for a wiring reason.
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the mail objects of '{name}'");

        // ⚠ REVERSE ORDER, SO THE SECRET GOES LAST. Taking the DKIM key away from a running signer
        // is the one removal that would make the domain send UNSIGNED mail rather than no mail —
        // which is worse, because unsigned mail from a domain publishing DKIM is what a receiver
        // scores as a forgery. A teardown interrupted before the last step leaves a Secret nobody
        // mounts, which is inert.
        foreach (var target in Targets(context.Namespace, name).Reverse()) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(context.Namespace)
                .WithKind(target.Kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(Placeholder(target.Name))
                // ⚠ Background, not Foreground. A Foreground cascade blocks the delete on the garbage
                // collector removing every dependent, and a converge loop with a bounded pass budget
                // would run out of passes waiting for a controller it does not drive. The read-back
                // below is what makes Background safe: this returns Converged when the objects are
                // GONE, not when the deletes were issued.
                    .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError)
                && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in Targets(context.Namespace, name)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        // ⚠ THE MAIL STORE SURVIVES THIS. A claim created from a volumeClaimTemplate has no owner
        // reference to the set, which is Kubernetes' own behaviour; RetainedVolumesAsync below is
        // what makes that deliberate here rather than incidental.
        context.Log.Report("deleted", $"the mail objects of '{name}' are gone", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>One claim, and unconditionally</b> — see <see cref="MailDomains.RetainedClaims" />,
    ///     which carries the argument. This runs on the convergence of a hard delete and of a purge
    ///     and on nothing else, so nothing here can reach a resource that is coming back.
    /// </remarks>
    public Task<Result<ImmutableArray<RetainedVolume>>> RetainedVolumesAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<RetainedVolume>>.Success(
                MailDomains.RetainedClaims(context.Namespace, context.Id.Name)
            )
        );

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        // ⚠ The StatefulSet alone, and not the five. A mail domain IS its back end: the ConfigMap
        // and the Secret are how it is configured and the Service is how it is reached, and any of
        // them present without the StatefulSet is configuration for a domain that does not exist.
        var read = await cluster.GetAsync(
            MailDomains.SetRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the mail back end is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = MailDomains.Matches(found.Json, context.Desired);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches
                ? "the mail back end carries the desired spec"
                : "the mail back end has drifted"
        };
    }

    /// <summary>
    ///     Puts a DKIM keypair and a master password in the vault if there is not one there, and
    ///     reads back whichever pair is now authoritative.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         THE MINT AND THE READ ARE BOTH HERE, AND THE READ IS WHAT MAKES THE PASS
    ///         IDEMPOTENT.
    ///     </b> See this type's clause-1 note: a rendered fresh key would break DKIM for
    ///     the domain silently. <c>MintAsync</c>'s <c>cas=0</c> writes only when the path is empty,
    ///     so the value read back afterwards is the first pass's on every pass.
    /// </remarks>
    static async Task<Result<Dictionary<string, string>>> EnsureCredentialsAsync(
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        var minted = await context.SecretWriter.MintAsync(
            MailDomains.SecretPath(context.Id),
            MailDomains.GenerateCredentials(),
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            return Result<Dictionary<string, string>>.Failure(mintError);
        }

        if (minted.GetValueOrThrow().Minted) {
            context.Log.Report(
                "minting",
                $"a DKIM keypair was written to the vault for '{context.Id.Name}'. The domain's "
                + "public record must be published before the platform will send for it."
            );
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in MailDomains.CredentialFields) {
            var value = await context.Secrets.ResolveAsync(
                MailDomains.CredentialRef(context.Id, field),
                cancellationToken
            );

            if (value.TryGetError(out var resolveError)) {
                return Result<Dictionary<string, string>>.Failure(resolveError);
            }

            resolved[field] = value.GetValueOrThrow();
        }

        return Result<Dictionary<string, string>>.Success(resolved);
    }

    /// <summary>
    ///     The five objects a domain is, in dependency order.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="MailDomains.Objects" /> and not a second list.</b> The conformance case
    ///     declares the objects it expects from that same method, so a reconciler with its own copy
    ///     could apply a sixth object — or miss one — and the suite that exists to catch exactly
    ///     that would be reading the list it was told to expect rather than the list applied.
    /// </remarks>
    static ImmutableArray<ObjectRef> Targets(string ns, string name) => MailDomains.Objects(ns, name);

    /// <summary>The five documents, paired with where each goes, in the same order.</summary>
    static ImmutableArray<(ObjectRef Target, string Body)> Documents(
        string ns,
        string name,
        JsonElement desired,
        IReadOnlyDictionary<string, string> secrets
    ) =>
        [
            (MailDomains.CredentialsSecretRef(ns, name), MailDomains.CredentialsSecretJson(name, secrets)),
            (MailDomains.ConfigMapRef(ns, name), MailDomains.ConfigMapJson(name, desired)),
            (MailDomains.ServiceRef(ns, name), MailDomains.ServiceJson(name, desired)),
            (MailDomains.SetRef(ns, name), MailDomains.StatefulSetJson(name, desired)),
            (MailDomains.PodMonitorRef(ns, name), MailDomains.PodMonitorJson(name))
        ];

    /// <summary>The smallest object a delete command will accept.</summary>
    /// <remarks>
    ///     ⚠ A name and nothing else, because a delete addresses rather than describes. Rendering the
    ///     real body here would mean resolving the DKIM key out of the vault in order to throw it
    ///     away.
    /// </remarks>
    static string Placeholder(string name) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = name } }.ToJsonString();

    /// <summary>
    ///     Turns an apply that did not land into the outcome that comes back for it, or
    ///     <see langword="null" /> when it landed.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         A static method rather than an instance one with a cached builder, and that is the
    ///         clause-2 rule rather than a style choice.
    ///     </b> A reconciler is a singleton serving every
    ///     tenant, so any field is shared state.
    /// </remarks>
    static ReconcileOutcome? Unfinished(ReconcileContext context, ApplyOutcome outcome) {
        switch (outcome.Result) {
            case ApplyResult.Suspended:
                // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles
                // rather than failing them. A tenant whose cluster is down has a resource that is
                // still coming, not one that broke.
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? "another field manager owns part of the mail domain and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }
}
