using CyberCloud.Core.Time;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     Converges one virtual machine onto its KubeVirt <c>VirtualMachine</c> and, when the body names
///     cloud-init, the Secret that carries it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             IT READS THE OBJECT BEFORE IT RENDERS, AND THAT IS THE MOST IMPORTANT LINE IN THIS
///             FILE.
///         </b> The power state lives on the object as <c>spec.runStrategy</c>, written by the
///         power handler and never by a body — <see cref="VirtualMachines" />' class remarks carry the
///         whole argument. So every pass begins with a <c>GetAsync</c> of the <c>VirtualMachine</c>:
///         found, the render carries the run strategy it found; absent, the render says
///         <see cref="VirtualMachines.RunAlways" />, because a machine somebody just created should
///         boot. A pass that rendered <c>Always</c> unconditionally would turn every stopped machine
///         back on the first time its tenant changed a tag.
///     </para>
///     <para>
///         ⚠
///         <b>
///             IT WAITS FOR AN IMAGE THAT IS STILL IMPORTING, AND THIS IS THE ONE CROSS-RESOURCE READ
///             IN THE FAMILY.
///         </b> The root disk is a CDI clone of the image's claim. A clone of a claim
///         that is mid-import is admitted and sits <c>Provisioning</c> with its reason on a third
///         object nobody shows the tenant, so the pass reads the image's <c>DataVolume</c> in the same
///         namespace — a cluster read and not an engine call, docs/plan/07 § The enforcement seam is
///         untouched — and answers <c>InProgress</c> naming the image and its phase. An image that
///         is <i>absent</i> is left to KubeVirt: CDI's admission refuses the clone, KubeVirt puts the
///         refusal on the machine's <c>Failure</c> condition and retries, and the machine recovers by
///         itself when the image lands. ⚠ The disks are not checked at all: a claim named in
///         <c>volumes[]</c> is resolved when the launcher pod starts, and KubeVirt reports
///         <c>ErrorPvcNotFound</c>, which <see cref="VirtualMachines.ReadinessOf" /> carries back.
///     </para>
///     <para>
///         ⚠ <b><c>Converged</c> is KubeVirt's verdict, not the apply's.</b> After the read-back matches,
///         <see cref="VirtualMachines.ReadinessOf" /> decides: <c>Running</c> converges, <c>Stopped</c>
///         converges on a halted machine, no status at all converges the way
///         <c>ManagedClusterReconciler</c> converges without a Cluster API controller
///         (<c>conformance.yaml § owed</c>, <c>converged-is-not-ready</c>), and every other word
///         KubeVirt uses — <c>Provisioning</c>, <c>WaitingForVolumeBinding</c>, <c>Starting</c>,
///         <c>ErrorUnschedulable</c> — is <c>InProgress</c> with that word and, when the scheduler
///         refused the launcher, its sentence: on a node with no KVM device that is
///         <c>Insufficient devices.kubevirt.io/kvm</c>, and on the k3s-in-Docker lane — which has the
///         device, measured on 2026-09-17 — a machine passes through <c>Provisioning</c> and
///         <c>Starting</c> to <c>Running</c> in under a minute.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Both documents are pure functions of the address, the body, the vault
///             value and the run strategy read at the top of the pass; a second pass with the same four
///             applies the same bytes.
///         </item>
///         <item>
///             <b>No hidden state.</b> The only field is the primary constructor's <see cref="IClock" />.
///             <c>VirtualMachineReconcilerTests</c> asserts the structural check and the cross-tenant
///             behaviour both.
///         </item>
///         <item>
///             <b>Bounded.</b> At most one vault read, two cluster reads before the applies, two applies
///             and two reads after, on the caller's token.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> <see cref="ReconcileOutcome.Converged" /> follows a
///             <c>GetAsync</c> of the objects and a reading of KubeVirt's status, never any apply's own
///             result.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class VirtualMachineReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => VirtualMachines.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a virtual machine is a KubeVirt "
                + "object in a cluster. CyberCloud.Compute/virtualMachines declares RequiresCluster, so "
                + "the driver should have refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        // ── A data disk the body cannot render is refused before anything is read ──────────────
        //
        // What the schema cannot say about `dataDisks`: an element must be a resource name (the chart
        // surface refuses a per-element pattern, so the registry declares none) and must not be the
        // root or cloud-init volume's own name. KubeVirt's webhook refuses the duplicate and a real API
        // server the bad name; a derived stub admits both; this refuses either before any of them is
        // asked, terminally, because a PUT is what changes it. VirtualMachines.DataDiskProblem.
        if (VirtualMachines.DataDiskProblem(context.Desired) is { Length: > 0 } diskProblem) {
            return ReconcileOutcome.Failed(
                new Error(ErrorCode.InvalidRequestBody, diskProblem, "/properties/dataDisks")
            );
        }

        // ── An image that is still importing is waited for; one that is absent is KubeVirt's ──
        //
        // ⚠ ABSENT PROCEEDS AND IMPORTING WAITS, AND THE ASYMMETRY IS DELIBERATE. CDI's admission
        // refuses a clone whose source claim is not there, KubeVirt records the refusal on the
        // machine's Failure condition and retries — so a machine created before its image recovers
        // by itself the moment the image lands, and ReadinessOf carries CDI's own sentence back
        // meanwhile. An image that EXISTS and is mid-import is the case KubeVirt handles worse: the
        // clone is admitted and sits Provisioning with its reason on a third object, so the wait
        // here is what turns that into a message naming the image and its phase. It is also what
        // keeps the conformance suites honest rather than what they need: a harness with no image
        // object takes the absent branch and lands where every other type does.
        if (await ImageGate.WaitForAsync(context, cluster, VirtualMachines.Image(context.Desired), "machine", cancellationToken)
            is { } waiting) {
            return waiting;
        }

        // ── The power state, read off the object before anything is rendered ───────────────────
        var existing = await cluster.GetAsync(VirtualMachines.VirtualMachineRef(ns, name), cancellationToken);

        if (existing.TryGetError(out var existingError) && existingError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(existingError);
        }

        var runStrategy = existing.IsSuccess
            ? VirtualMachines.RunStrategyOf(existing.GetValueOrThrow().Json)
            : string.Empty;

        // ⚠ THE VERSION THE RUN STRATEGY WAS READ AT, AND THE APPLY BELOW IS CONDITIONAL ON IT. A
        // start or stop that lands between this read and that apply moves the object, the apply is
        // refused as Stale with nothing written, and the pass ends InProgress to read again — so the
        // pass can no longer write back a power state the action just replaced. Empty for a machine
        // that is not there yet, because the API server does not hold the lock against an absent
        // object and there is no action to race: an action never creates.
        var readVersion = existing.IsSuccess ? existing.GetValueOrThrow().ResourceVersion : string.Empty;

        if (runStrategy.Length == 0) {
            runStrategy = VirtualMachines.RunAlways;
        }

        // ── Cloud-init: resolved once, written to a Secret, never to a body ────────────────────
        //
        // ⚠ THE TENANT'S OWN PATHS AND NOBODY ELSE'S, CHECKED BEFORE THE RESOLVER IS ASKED. This is
        // the one place in the tree where a path a TENANT spelled reaches ISecretResolver and its
        // value reaches something the tenant can read — the guest mounts the Secret — and the
        // resolver holds one platform-wide token, so the path is the only thing that scopes the read.
        // VirtualMachines.TenantVaultPrefix carries the argument; the parse refuses a path outside
        // it with AuthorizationFailed, which ends the pass with nothing applied and nothing read.
        string? userData = null;

        if (VirtualMachines.HasCloudInit(context.Desired)) {
            var handle = VirtualMachines.ParseCloudInitRef(
                VirtualMachines.CloudInitRef(context.Desired),
                context.Id.TenantId
            );

            if (handle.TryGetError(out var handleError)) {
                return ReconcileOutcome.FromFailure(handleError);
            }

            var resolved = await context.Secrets.ResolveAsync(handle.GetValueOrThrow(), cancellationToken);

            if (resolved.TryGetError(out var resolveError)) {
                return ReconcileOutcome.FromFailure(resolveError);
            }

            userData = resolved.GetValueOrThrow();

            context.Log.Report(
                "applying-cloud-init",
                $"writing the cloud-init user data of '{name}' into its Secret",
                30
            );

            if (await Apply(
                    context,
                    cluster,
                    KubeSecret.Kind,
                    VirtualMachines.CloudInitSecretJson(name, userData),
                    cancellationToken
                ) is { } secretProblem) {
                return secretProblem;
            }
        }

        context.Log.Report(
            "applying-virtual-machine",
            $"applying the VirtualMachine of '{name}' with run strategy {runStrategy}",
            50
        );

        if (await Apply(
                context,
                cluster,
                VirtualMachines.VirtualMachineKind,
                VirtualMachines.VirtualMachineJson(ns, name, context.Desired, runStrategy),
                cancellationToken,
                true,
                readVersion
            ) is { } problem) {
            return problem;
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        if (userData is not null) {
            var secret = await cluster.GetAsync(VirtualMachines.CloudInitSecretRef(ns, name), cancellationToken);

            if (secret.TryGetError(out var secretReadError)) {
                return secretReadError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress(
                        "the cloud-init Secret was applied and is not readable back yet",
                        TimeSpan.FromSeconds(5)
                    )
                    : ReconcileOutcome.FromFailure(secretReadError);
            }

            var carried = KubeSecret.Value(secret.GetValueOrThrow(), VirtualMachines.CloudInitKey);

            if (!carried.IsSuccess || carried.GetValueOrThrow() != userData) {
                return ReconcileOutcome.InProgress(
                    "the cloud-init Secret is readable and does not yet carry the vault's value",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        var read = await cluster.GetAsync(VirtualMachines.VirtualMachineRef(ns, name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress(
                    "the VirtualMachine was applied and is not readable back yet",
                    TimeSpan.FromSeconds(5)
                )
                : ReconcileOutcome.FromFailure(readError);
        }

        var json = read.GetValueOrThrow().Json;

        if (!VirtualMachines.Matches(json, ns, context.Desired)) {
            return ReconcileOutcome.InProgress(
                "the VirtualMachine is readable and does not yet carry the desired spec",
                TimeSpan.FromSeconds(5)
            );
        }

        var readiness = VirtualMachines.ReadinessOf(json);

        if (readiness.Kind == VirtualMachines.ReadinessKind.NotReady) {
            context.Log.Report("waiting-for-kubevirt", readiness.Detail, 80);

            return ReconcileOutcome.InProgress(readiness.Detail, TimeSpan.FromSeconds(15));
        }

        context.Log.Report(
            "ready",
            readiness.Kind == VirtualMachines.ReadinessKind.Ready
                ? readiness.Detail
                // ⚠ Said in the tenant's own progress log, because this branch converges without a
                // controller's evidence — conformance.yaml § owed, `converged-is-not-ready`.
                : $"the VirtualMachine of '{name}' reads back as desired; KubeVirt has not reported on it yet",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;
        var ns = context.Namespace;

        context.Log.Report("deleting", $"deleting the VirtualMachine of '{name}'");

        // ⚠ THE MACHINE FIRST AND THE SECRET SECOND, which is the reverse of the apply order: KubeVirt
        // owns the teardown of the instance and the root DataVolume its template created — both are
        // garbage-collected with the VirtualMachine — and a Secret removed first is a guest that
        // cannot re-read its cloud-init while it shuts down. The Secret is deleted whether or not the
        // current body names cloud-init, because an earlier body may have.
        //
        // ⚠ THE DISKS AND THE IMAGE ARE NOT TOUCHED. Both are other resources' objects; a managed disk
        // outlives the machine by definition, and deleting a machine that leaves its data behind is the
        // point of the word "managed".
        foreach (var (kind, json) in new[] {
                     (VirtualMachines.VirtualMachineKind,
                         VirtualMachines.VirtualMachineJson(ns, name, context.Desired, VirtualMachines.RunHalted)),
                     (KubeSecret.Kind, VirtualMachines.CloudInitSecretJson(name, string.Empty))
                 }) {
            var deleted = await KubeCommand.For(cluster)
                .WithTenantId(context.Id.TenantId)
                .WithResourceId(context.Id)
                .InNamespace(ns)
                .WithKind(kind)
                .WithApiVersion(context.ApiVersion)
                .ObjectJson(json)
                .DeleteAsync(CascadePolicy.Background, cancellationToken);

            if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(deleteError);
            }
        }

        foreach (var target in new[] {
                     VirtualMachines.VirtualMachineRef(ns, name), VirtualMachines.CloudInitSecretRef(ns, name)
                 }) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", $"the VirtualMachine of '{name}' is gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Two reads: the machine and its instance.</b> The instance's <c>status.phase</c> —
    ///     <c>Pending</c>, <c>Scheduling</c>, <c>Running</c>, <c>Succeeded</c> — is the fact docs/plan/13
    ///     calls the VM's state, and it is on a second object KubeVirt creates and this platform never
    ///     applies. An absent instance on a halted machine is a stopped machine, not a missing one.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var read = await cluster.GetAsync(
            VirtualMachines.VirtualMachineRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the virtual machine is absent" };
        }

        var found = read.GetValueOrThrow();
        var matches = VirtualMachines.Matches(found.Json, context.Namespace, context.Desired);
        var runStrategy = VirtualMachines.RunStrategyOf(found.Json);

        var instance = await cluster.GetAsync(
            VirtualMachines.InstanceRef(context.Namespace, context.Id.Name),
            cancellationToken
        );

        var phase = instance.IsSuccess
            ? VirtualMachines.InstancePhase(instance.GetValueOrThrow().Json)
            : runStrategy == VirtualMachines.RunHalted ? "stopped" : "no instance";

        var printable = VirtualMachines.PrintableStatus(found.Json);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = (matches ? "the virtual machine carries the desired spec" : "the virtual machine has drifted")
                + $"; run strategy {runStrategy}, instance {phase}"
                + (printable.Length > 0 ? $", KubeVirt reports {printable}" : string.Empty)
        };
    }

    /// <summary>Applies one object, or ends the pass.</summary>
    /// <remarks>
    ///     <paramref name="templates" /> stamps the platform's lifetime-stable labels into the VM's pod
    ///     template and its root-disk template, so the launcher pod and the root claim are attributable
    ///     — ADR-013 on the objects KubeVirt derives from ours.
    /// </remarks>
    static Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        CancellationToken cancellationToken,
        bool templates = false,
        string readVersion = ""
    ) =>
        ApplyAsync(
            context,
            cluster,
            kind,
            objectJson,
            templates ? [PodTemplatePath, RootDiskTemplatePath] : [],
            readVersion,
            cancellationToken
        );

    /// <summary>Applies one object for a Compute reconciler, or answers the outcome that ends the pass.</summary>
    /// <param name="context">The pass.</param>
    /// <param name="cluster">The pass's cluster.</param>
    /// <param name="kind">The object's kind.</param>
    /// <param name="objectJson">The rendered object.</param>
    /// <param name="templatePaths">Nested templates the platform's labels are stamped into.</param>
    /// <param name="readVersion">
    ///     The version a value in the render was read at — <see cref="IKubeCommandBuilder.IfResourceVersion" />
    ///     — or empty for an unconditional apply.
    /// </param>
    /// <param name="cancellationToken">The pass's token.</param>
    /// <remarks>
    ///     Shared by the machine's and the scale set's reconciler, because the branches are a policy —
    ///     suspended, owned by somebody else, moved under the read — and both types read a value off
    ///     their object before rendering it.
    /// </remarks>
    internal static async Task<ReconcileOutcome?> ApplyAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        string[] templatePaths,
        string readVersion,
        CancellationToken cancellationToken
    ) {
        var command = KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .IfResourceVersion(readVersion)
            .ObjectJson(objectJson);

        if (templatePaths.Length > 0) {
            command = command.WithTemplateLabels(templatePaths);
        }

        var applied = await command.ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ `spec.runStrategy` is the plausible field here, and the plausible other manager
                // is virt-api acting for `virtctl`. Forcing would take a power decision an operator
                // made away from them; the pass reports it and the next one reads the object again.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe()
                    ?? $"another field manager owns part of the {kind.Kind} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Stale:
                // ⚠ AN ACTION LANDED BETWEEN THIS PASS'S READ AND ITS APPLY — a start, a stop, a
                // scale — and nothing was written. The next pass reads what the action left and
                // renders that, which is the whole of power-state-can-lose-a-race's fix; retrying
                // soon rather than in thirty seconds because nothing here is waiting on anybody.
                context.Log.Report("moved", outcome.Message);

                return ReconcileOutcome.InProgress(
                    $"the {kind.Kind} moved between this pass's read and its apply — an action changed it — "
                    + "and nothing was written; the next pass reads it again",
                    TimeSpan.FromSeconds(2)
                );
        }

        return null;
    }

    /// <summary>Where the VM's pod template sits, for <c>WithTemplateLabels</c>.</summary>
    public const string PodTemplatePath = "spec/template";

    /// <summary>Where the root-disk template sits, for <c>WithTemplateLabels</c>.</summary>
    public const string RootDiskTemplatePath = "spec/dataVolumeTemplates";
}
