namespace CyberCloud.Providers.Compute;

/// <summary>
///     The one cross-resource read a machine and a scale set both make before rendering: is the image
///     their root disks clone still importing?
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Absent proceeds and importing waits</b> — <c>VirtualMachineReconciler</c>'s comment at
///         the call carries the whole argument. CDI's admission refuses a clone whose source claim is not
///         there and KubeVirt retries it, so a machine created before its image recovers by itself; a
///         clone of a claim that exists and is mid-import is admitted and sits <c>Provisioning</c> with
///         its reason on a third object, which is the case this turns into a sentence naming the image.
///     </para>
///     <para>
///         ⚠ <b>One copy for both types</b>, because a scale set's template is a machine's and the two
///         would otherwise answer the same image's failure with two messages — and the terminal branch
///         is a policy: an image that failed to import cannot be replaced by a PUT on either, because
///         <c>image</c> is immutable on both.
///     </para>
/// </remarks>
static class ImageGate {
    /// <summary>The outcome that ends the pass while the image is not ready, or <see langword="null" /> to render.</summary>
    /// <param name="context">The pass.</param>
    /// <param name="cluster">The pass's cluster.</param>
    /// <param name="image">The image resource's name, from the body.</param>
    /// <param name="what">What waits for it — <c>machine</c> or <c>set</c> — for the sentences.</param>
    /// <param name="cancellationToken">The pass's token.</param>
    public static async Task<ReconcileOutcome?> WaitForAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        string image,
        string what,
        CancellationToken cancellationToken
    ) {
        var read = await cluster.GetAsync(Images.DataVolumeRef(context.Namespace, image), cancellationToken);

        if (read.TryGetError(out var readError) && readError.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(readError);
        }

        var phase = read.IsSuccess ? Cdi.Phase(read.GetValueOrThrow().Json) : Cdi.Succeeded;

        if (Cdi.IsPopulated(phase)) {
            return null;
        }

        if (phase == Cdi.Failed) {
            // ⚠ Terminal, because `image` is immutable: the body cannot be pointed at another image by
            // a PUT, so a retry would spin forever on a body the tenant cannot mend.
            return ReconcileOutcome.Failed(
                ErrorCode.ProvisioningFailed,
                $"the image '{image}' failed to import"
                + Detail(Cdi.Detail(read.GetValueOrThrow().Json))
                + $", so there is nothing for the root disk to clone. Replace the image and create the {what} "
                + "again; its image cannot be changed in place."
            );
        }

        context.Log.Report("waiting-for-image", $"the image '{image}' is {Phase(phase)}", 15);

        return ReconcileOutcome.InProgress(
            $"the image '{image}' is {Phase(phase)} and the root disk clones it once it has imported",
            TimeSpan.FromSeconds(15)
        );
    }

    static string Phase(string phase) => phase.Length == 0 ? "not yet reported on by CDI" : phase;

    static string Detail(string detail) => detail.Length == 0 ? string.Empty : $" ({detail})";
}
