// ⚠ For `Result<ApplyOutcome>`, which the co-writer answers. The `ErrorCode` alias in GlobalUsings
// still wins over Orleans' own.

using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Text.Json;

namespace CyberCloud.Providers.Mail;

/// <summary>
///     Converges one mailbox onto the four keys of its domain's mailbox <c>Secret</c> that are its
///     own: a password file, alias-map lines, and a claim on every address it answers for.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE SECOND RECONCILER IN THE TREE THAT OWNS NO OBJECT</b>, after
///         <c>VirtualNetworkPeeringReconciler</c>, and it takes that one's shape:
///         <see cref="ReconcileContext.CoWriter" /> reads the domain's object, merges this mailbox's
///         fragment with every other mailbox's, and applies the union under the domain's shared
///         co-writer manager with the live <c>resourceVersion</c>. <see cref="MailMailboxes" /> has
///         the argument for why a mailbox is a slice and not an object.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> The fragment is a pure function of the body, the vault value and the
///             mailbox's GUID — the hash's salt is derived from the GUID
///             (<see cref="Sha512Crypt.SaltFor" />) for exactly this clause — and the co-owned apply
///             reports <c>Unchanged</c> when the stored fragment's hash is this one's.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's <see cref="IClock" />.
///             ⚠ The resolved password lives in a local for the length of one pass.
///         </item>
///         <item>
///             <b>Bounded.</b> Two reads, one resolve, one hash of <see cref="Sha512Crypt.MailboxRounds" />
///             rounds, and one co-owned apply of at most <see cref="KubeCoWriter.MaxAttempts" />
///             read-then-apply rounds.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a read
///             of the <c>Secret</c> that carries every key of the fragment, as written.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>CONVERGED IS "THE SECRET SAYS SO", AND DOVECOT SEES IT UP TO A MINUTE LATER.</b> The
///         kubelet refreshes a mounted <c>Secret</c> on its sync period, not on the write. A tenant
///         who creates a mailbox and signs in the same second is refused, and the refusal is the
///         kubelet's latency rather than a failed resource; the cluster-backed suite polls for exactly
///         that reason.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class MailMailboxReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => MailMailboxes.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a mailbox is four keys of a Secret in "
                + "its domain's namespace. CyberCloud.Mail/domains/mailboxes declares RequiresCluster, so the "
                + "driver should have refused this pass — see ReconcileDriver."
            );
        }

        if (MailMailboxes.Problem(context.Desired) is { } problem) {
            // ⚠ TERMINAL. A bad alias or forwarding address is wrong on every pass, and it is checked
            // before the password is resolved so a refused body never costs a vault read.
            context.Log.Report("refused", problem.Message);
            return ReconcileOutcome.Failed(new Error(ErrorCode.InvalidRequestBody, problem.Message, problem.Target));
        }

        var password = MailMailboxes.ParsePasswordRef(MailMailboxes.PasswordRef(context.Desired), context.Id.TenantId);

        if (password.TryGetError(out var handleError)) {
            context.Log.Report("refused", handleError.Message);
            return ReconcileOutcome.Failed(handleError);
        }

        var target = UsersSecret(context);

        // ── The domain, read off the object this mailbox is about to write onto ───────────────
        var live = await cluster.GetAsync(target, cancellationToken);

        if (live.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? WaitingForDomain(context, target)
                : ReconcileOutcome.FromFailure(readError);
        }

        var domain = MailMailboxes.DomainOf(live.GetValueOrThrow().Json);

        if (domain.Length == 0) {
            return WaitingForDomain(context, target);
        }

        // ── The password, resolved and hashed in this pass and nowhere else ────────────────────
        string? hash = null;
        var handle = password.GetValueOrThrow();

        if (!handle.IsEmpty) {
            var resolved = await context.Secrets.ResolveAsync(handle, cancellationToken);

            if (resolved.TryGetError(out var resolveError)) {
                // ⚠ Retryable or not by its code, and NOTHING is written first: a mailbox whose
                // handle cannot be read yet must not appear with no password, which would be a
                // mailbox that silently cannot be signed in to.
                return ReconcileOutcome.FromFailure(resolveError);
            }

            hash = Sha512Crypt.Hash(
                resolved.GetValueOrThrow(),
                Sha512Crypt.SaltFor(context.Id.Id),
                Sha512Crypt.MailboxRounds
            );
        }

        var local = MailMailboxes.LocalPart(context.Desired);
        var aliases = MailMailboxes.Aliases(context.Desired);
        var fragment = MailMailboxes.FragmentJson(
            context.Id.Id,
            local,
            MailMailboxes.PasswdLine(local + "@" + domain, hash, MailMailboxes.Quota(context.Desired)),
            MailMailboxes.VirtualLines(
                domain,
                local,
                aliases,
                MailMailboxes.ForwardTo(context.Desired),
                MailMailboxes.KeepCopy(context.Desired)
            ),
            aliases
        );

        context.Log.Report("applying", $"writing {local}@{domain} into '{target.Name}'", 40);

        var applied = await context.CoWriter.ApplyFragmentAsync(context.Id, target, fragment, cancellationToken);

        if (Interrupted(context, applied, target, local, domain) is { } interrupted) {
            return interrupted;
        }

        // ── Clause 4 ───────────────────────────────────────────────────────────────────────────
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var afterError)) {
            return afterError.Code == ErrorCode.ResourceNotFound
                ? WaitingForDomain(context, target)
                : ReconcileOutcome.FromFailure(afterError);
        }

        if (!MailMailboxes.Carries(read.GetValueOrThrow().Json, fragment)) {
            return ReconcileOutcome.InProgress(
                $"'{target.Name}' is readable and does not yet carry {local}@{domain} as written",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"{local}@{domain} is in '{target.Name}'", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The mail is not deleted, and that is the domain's retention rule reaching down.</b>
    ///     Withdrawing the fragment removes the password file and the alias lines, so the address
    ///     stops accepting mail and nobody can sign in. The messages stay in the domain's mail store —
    ///     the claim <see cref="MailDomains.RetainedClaims" /> keeps unconditionally — and a mailbox
    ///     recreated with the same local part finds them. ⚠ That is also the privacy edge: a
    ///     different person given the same local part later reads the previous owner's mail.
    ///     <c>charts/managed/mail-mailbox/conformance.yaml § owed</c>, <c>a-deleted-mailbox-keeps-its-mail</c>.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var target = UsersSecret(context);

        context.Log.Report("withdrawing", $"withdrawing the mailbox from '{target.Name}'");

        var withdrawn = await context.CoWriter.WithdrawFragmentAsync(context.Id, target, cancellationToken);

        if (withdrawn.TryGetError(out var error)) {
            return error.Code == ErrorCode.ResourceNotFound ? ReconcileOutcome.Converged : ReconcileOutcome.FromFailure(error);
        }

        if (withdrawn.GetValueOrThrow().Result is ApplyResult.Stale or ApplyResult.Suspended or ApplyResult.Conflict) {
            return ReconcileOutcome.InProgress(withdrawn.GetValueOrThrow().Message is { Length: > 0 } message
                ? message
                : $"'{target.Name}' could not be written yet", TimeSpan.FromSeconds(5));
        }

        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.Converged
                : ReconcileOutcome.FromFailure(readError);
        }

        if (CarriesFragmentOf(read.GetValueOrThrow().Json, context.Id.Id)) {
            return ReconcileOutcome.InProgress(
                $"'{target.Name}' still carries this mailbox's fragment after the withdrawal",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("withdrawn", $"'{target.Name}' no longer carries the mailbox", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A mailbox exists when its domain's mailbox <c>Secret</c> carries its fragment — the same
    ///     test a drift scan joins a co-writer on.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster || context.Id.Parent is not { } parent) {
            return ObservedState.Absent;
        }

        var read = await cluster.GetAsync(MailDomains.UsersSecretRef(context.Namespace, parent.Name), cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the domain's mailbox Secret is absent" };
        }

        var found = read.GetValueOrThrow();
        var present = CarriesFragmentOf(found.Json, context.Id.Id);

        return new() {
            Exists = present,
            Json = present ? found.Json : string.Empty,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = present
                ? "the domain's mailbox Secret carries this mailbox"
                : "the domain's mailbox Secret does not carry this mailbox"
        };
    }

    static ObjectRef UsersSecret(ReconcileContext context) =>
        // ⚠ The PARENT's name, off the address — a mailbox's own name is not the domain's, and the
        // object is the domain's. A mailbox always has a parent: the type is nested.
        MailDomains.UsersSecretRef(context.Namespace, context.Id.Parent?.Name ?? string.Empty);

    static ReconcileOutcome WaitingForDomain(ReconcileContext context, ObjectRef target) {
        context.Log.Report("waiting-for-domain", $"'{target.Name}' is not there yet");

        // ⚠ InProgress and not Failed, the peering's argument: a domain still Creating and a domain
        // that does not exist look the same from the cluster, and a mailbox never creates the domain's
        // object — a co-writer that did would create it without the seven labels.
        return ReconcileOutcome.InProgress(
            $"the domain's mailbox Secret '{target.Name}' is not in the cluster yet, or carries no mail domain. "
            + "A mailbox is written into its domain's back end, so the domain has to converge first.",
            TimeSpan.FromSeconds(10)
        );
    }

    static ReconcileOutcome? Interrupted(
        ReconcileContext context,
        Result<ApplyOutcome> applied,
        ObjectRef target,
        string local,
        string domain
    ) {
        if (applied.TryGetError(out var error)) {
            if (error.Code == ErrorCode.ResourceNotFound) {
                return WaitingForDomain(context, target);
            }

            if (error.Code == ErrorCode.InvalidRequestBody) {
                // ⚠ TERMINAL, AND THIS IS THE CLAIM WORKING. The co-writer's merge refuses two
                // fragments that set one key to two values, and the only key two mailboxes can
                // share is a `.claim` — so this is another mailbox already answering for an address
                // this one names. The merge's message names both resource ids.
                var message = $"{local}@{domain} or one of its aliases is already an address of another mailbox "
                    + "in this domain. " + error.Message;

                context.Log.Report("refused", message);

                return ReconcileOutcome.Failed(new Error(ErrorCode.Conflict, message, "/properties/localPart"));
            }

            return ReconcileOutcome.FromFailure(error);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);
                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Stale:
                // Another mailbox of the same domain wrote in between, three times over. Nothing
                // is forced; the next pass reads again.
                context.Log.Report("stale", outcome.Message);
                return ReconcileOutcome.InProgress(outcome.Message, TimeSpan.FromSeconds(5));

            case ApplyResult.Conflict:
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);
                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of '{target.Name}' and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    /// <summary>Whether an object carries this mailbox's fragment annotation.</summary>
    static bool CarriesFragmentOf(string objectJson, Guid writer) {
        try {
            using var document = JsonDocument.Parse(objectJson);

            return document.RootElement.TryGetProperty("metadata", out var metadata)
                && metadata.TryGetProperty("annotations", out var annotations)
                && annotations.ValueKind == JsonValueKind.Object
                && annotations.TryGetProperty(KubeLabels.FragmentAnnotation(writer), out _);
        } catch (JsonException) {
            return false;
        }
    }
}
