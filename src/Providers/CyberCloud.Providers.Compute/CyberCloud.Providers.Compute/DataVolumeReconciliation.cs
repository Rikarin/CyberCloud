using CyberCloud.Core.Time;
using System.Text.Json;

namespace CyberCloud.Providers.Compute;

/// <summary>
///     The one pass an image and a disk share: apply a <c>DataVolume</c>, read it back, ask CDI.
/// </summary>
/// <remarks>
///     ⚠ <b>One pass, two readiness rules, and the rule is the only parameter.</b> An image is ready
///     when CDI says <c>Succeeded</c> — the bytes are there; a disk is ready when CDI says the claim
///     is provisioned, which on a <c>WaitForFirstConsumer</c> class is a phase short of that. Two
///     reconcilers with two copies of the pass would be two places a CDI phase is spelled, so the pass
///     is here and each reconciler contributes its render, its reference and its rule.
/// </remarks>
static class DataVolumeReconciliation {
    /// <summary>What a type contributes to the shared pass.</summary>
    /// <param name="What">The noun for messages: <c>image</c> or <c>disk</c>.</param>
    /// <param name="Render">The <c>DataVolume</c> a body becomes.</param>
    /// <param name="Target">Where it is read back from.</param>
    /// <param name="Matches">Whether a read-back carries the body.</param>
    /// <param name="IsReady">Whether a CDI phase is as far as this type needs to go.</param>
    public readonly record struct Shape(
        string What,
        Func<string, JsonElement, string> Render,
        Func<string, string, ObjectRef> Target,
        Func<string, JsonElement, bool> Matches,
        Func<string, bool> IsReady
    );

    public static async Task<ReconcileOutcome> ReconcileAsync(
        Shape shape,
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a{An(shape.What)} {shape.What} is a CDI "
                + "DataVolume in a cluster. The type declares RequiresCluster, so the driver should have "
                + "refused this pass — see ReconcileDriver."
            );
        }

        var name = context.Id.Name;

        context.Log.Report("applying", $"applying the DataVolume of the {shape.What} '{name}'", 30);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Cdi.DataVolumeKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(shape.Render(name, context.Desired))
            .ApplyAsync(cancellationToken);

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
                // ⚠ CDI adopts fields of a DataVolume it fills — the read-back carries an access
                // mode and a volume mode this platform never wrote — and a conflict here means it
                // also took one this platform did write. Forcing would fight the operator that owns
                // the bytes; the pass reports the field and lets a person look.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe() ?? "another field manager owns part of the DataVolume and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );
        }

        // ── Clause 4. Everything above this line is a claim; this is the reading. ──────────────
        var read = await cluster.GetAsync(shape.Target(context.Namespace, name), cancellationToken);

        if (read.TryGetError(out var readError)) {
            return readError.Code == ErrorCode.ResourceNotFound
                ? ReconcileOutcome.InProgress("the DataVolume was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                : ReconcileOutcome.FromFailure(readError);
        }

        var json = read.GetValueOrThrow().Json;

        if (!shape.Matches(json, context.Desired)) {
            return ReconcileOutcome.InProgress("the DataVolume is readable and does not yet carry the desired spec", TimeSpan.FromSeconds(5));
        }

        var phase = Cdi.Phase(json);

        if (phase.Length == 0) {
            // ⚠ Converges without evidence, said out loud — the same branch ManagedClusterReconciler
            // takes when no Cluster API controller has reported, and the same owed row:
            // conformance.yaml § owed, `converged-is-not-ready`. A harness with a derived CRD stub and
            // no CDI behind it lands here; a real cluster writes a phase within seconds.
            context.Log.Report("ready", $"the DataVolume of '{name}' reads back as desired; CDI has not reported on it yet", 100);

            return ReconcileOutcome.Converged;
        }

        if (phase == Cdi.Failed) {
            return ReconcileOutcome.Failed(
                ErrorCode.ProvisioningFailed,
                $"CDI could not fill the {shape.What} '{name}'{Detail(Cdi.Detail(json))}. Its source and "
                + "size cannot be changed in place; delete it and create it again with the cause fixed."
            );
        }

        if (!shape.IsReady(phase)) {
            var detail = Cdi.Detail(json);
            var reason = detail.Length > 0 ? $"CDI reports {phase}: {detail}" : $"CDI reports {phase}";

            context.Log.Report("waiting-for-cdi", reason, 70);

            return ReconcileOutcome.InProgress(reason, TimeSpan.FromSeconds(15));
        }

        context.Log.Report("ready", $"the {shape.What} '{name}' is {phase}", 100);

        return ReconcileOutcome.Converged;
    }

    public static async Task<ReconcileOutcome> DeleteAsync(
        Shape shape,
        ReconcileContext context,
        CancellationToken cancellationToken
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var name = context.Id.Name;

        context.Log.Report("deleting", $"deleting the DataVolume of the {shape.What} '{name}'");

        // ⚠ THE CLAIM GOES WITH THE DataVolume — CDI owns it — AND THAT IS THE BYTES. There is no
        // recovery window on either type (ComputeProvider says why), so this is the moment an image
        // or a disk stops existing. Nothing here checks whether a machine still names the disk: that
        // is another resource's body, and a machine whose claim vanished reports ErrorPvcNotFound
        // through its own reconciler.
        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Cdi.DataVolumeKind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(shape.Render(name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(deleteError);
        }

        var read = await cluster.GetAsync(shape.Target(context.Namespace, name), cancellationToken);

        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress($"'{shape.Target(context.Namespace, name)}' is still readable", TimeSpan.FromSeconds(5));
        }

        if (read.Error!.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(read.Error);
        }

        context.Log.Report("deleted", $"the DataVolume of '{name}' is gone", 100);
        return ReconcileOutcome.Converged;
    }

    public static async Task<ObservedState> ObserveAsync(
        Shape shape,
        IClock clock,
        ObserveContext context,
        CancellationToken cancellationToken
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var read = await cluster.GetAsync(shape.Target(context.Namespace, context.Id.Name), cancellationToken);

        if (read.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = $"the {shape.What} is absent" };
        }

        var found = read.GetValueOrThrow();
        var phase = Cdi.Phase(found.Json);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = (shape.Matches(found.Json, context.Desired)
                    ? $"the {shape.What} carries the desired spec"
                    : $"the {shape.What} has drifted")
                + (phase.Length > 0 ? $"; CDI reports {phase}" : string.Empty)
        };
    }

    static string An(string what) => what.StartsWith('i') ? "n" : string.Empty;

    static string Detail(string detail) => detail.Length == 0 ? string.Empty : $" ({detail})";
}
