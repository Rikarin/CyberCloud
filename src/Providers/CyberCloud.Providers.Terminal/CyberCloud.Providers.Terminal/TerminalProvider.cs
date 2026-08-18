// ⚠ For `Result<decimal>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.Terminal;

/// <summary>
///     The cloud terminal — one resource type, one api-version, one reconciler, two actions.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/19 § <c>CyberCloud.Terminal/consoles</c>, M1 · 1.5 EM, and step 6 of
///         docs/plan/24's M1 exit story: <i>"open the cloud terminal and <c>psql</c> into it using a
///         managed identity"</i>.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST TYPE IN THE CATALOGUE WHOSE PRODUCT IS NOT A CONVERGED OBJECT, AND THE
///         DECLARATION BELOW IS WHERE THAT COSTS SOMETHING.</b> What a tenant buys is an interactive
///         session; what the registry can describe is a resource. The split is drawn on
///         <see cref="CloudConsoles" /> and shows up here as two synchronous actions with handlers
///         where every other family has at most one, and as a meter set that deliberately omits the
///         two meters a reader would expect. Both are argued below.
///     </para>
///     <para>
///         ⚠ <b>NO <c>SupportsSoftDelete</c>, AND THIS ROW HAD TO ARGUE IT RATHER THAN INHERIT
///         IT.</b> The two precedents do not transfer. <c>ContainerService</c> declined because
///         <i>"a soft-deleted cluster whose worker VMs are gone is not a cluster anybody can be handed
///         back"</i>, and <c>Cache</c> declined because a cache's data is reconstructible. A console
///         is neither: the session really is unrecoverable — a dead shell is dead, and no window
///         changes that — but the home volume is <b>data</b>, and docs/plan/06 § Tags, locks asks for
///         seven days on <i>"resources carrying data"</i>. So the honest position is:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>For it:</b> <c>cyc terminal shell delete</c> on the wrong name destroys a home
///             directory with no undo, which is exactly the mistake a recovery window exists for.
///         </item>
///         <item>
///             <b>Against it, and this is the half that decides it:</b> the volume already has a
///             retention policy of its own — <c>home.retentionDays</c>, docs/plan/19's ninety days
///             after last use — and it is a <b>better</b> one, because it is visible in the body, it
///             is chosen by the tenant, and it starts from the last time the console was useful rather
///             than from the moment somebody typed a delete. Two retention mechanisms over one volume
///             is two things that can disagree about when the bytes go, and the disagreement would be
///             discovered by whoever needed the bytes.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>What that leaves open is recorded rather than argued away</b>: today neither
///         mechanism runs. <c>SoftDeleteDays</c> is not declared and nothing sweeps on
///         <c>retentionDays</c> either, so a console's delete takes the home directory immediately
///         and permanently. <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>delete-takes-the-home-directory</c>, and the reason a claim cannot ask for its bytes to
///         outlive it is on <see cref="CloudConsoles.HomeClaimJson" />.
///     </para>
/// </remarks>
public sealed class TerminalProvider : IResourceProvider {
    /// <summary>The CLI alias, and the one string this row had to check against sixteen others.</summary>
    /// <remarks>
    ///     ⚠ <b><c>terminal</c> IS ALREADY TAKEN BY THIS PROVIDER'S OWN GROUP.</b> <c>CliEmitter</c>
    ///     derives the CLI group key from the provider namespace's last segment, lower-cased, so
    ///     <c>CyberCloud.Terminal</c> is the group <c>terminal</c> before any alias is declared — the
    ///     same trap <c>CyberCloud.Storage/accounts</c> hit and ships as <c>objectstore</c> to avoid.
    ///     System.CommandLine's <c>ValidTokens</c> builds <b>one</b> dictionary over every command
    ///     token and every alias in the whole tree, so a group and an alias sharing a string throw
    ///     <c>ArgumentException: An item with the same key has already been added</c> on the first
    ///     parse of <i>any</i> command line, naming neither the provider nor the string.
    ///     <c>ConsoleDeclarationTests</c> checks this one against the eleven group keys, the sixteen
    ///     declared aliases and the nine reserved groups, as literals, because
    ///     <c>ProviderRegistry.Build</c> compares none of them.
    /// </remarks>
    public const string ShortName = "shell";

    /// <inheritdoc />
    public string ProviderNamespace => CloudConsoles.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(CloudConsoles.TypePath)
            .ApiVersion(CloudConsoles.V2026, CloudConsoles.Schema2026)
            .Reconciler<CloudConsoleReconciler>()
            // ── The meters, and the two that are missing are the finding ──────────────────────
            //
            // ⚠ StorageGb IS RESERVED AND vcpu/memoryGb ARE NOT, AND THAT ASYMMETRY IS THE WHOLE
            // ANSWER TO docs/plan/19's "idle cost is the design constraint".
            //
            // A quota meter is reserved at WRITE time from a pure function of the resource body —
            // MeterDerivation.Amount — and released at delete. That shape fits a home volume exactly:
            // the PersistentVolumeClaim is allocated from the moment the console exists and stays
            // allocated whether anybody is attached or not, so a tenant with a hundred consoles has a
            // hundred volumes and should be held to an allowance for them.
            //
            // ⚠ IT FITS CPU AND MEMORY EXACTLY BACKWARDS. A console's pod exists only while somebody
            // is typing into it — CloudConsoles' remarks, § What is a resource and what is a session
            // — so a state-based reservation from the body would hold 2 vCPU and 4 GiB against a
            // subscription's allowance for a terminal that was closed a week ago. That is not a
            // conservative approximation of the truth; it is the precise failure docs/plan/19 says the
            // idle reclaim exists to prevent, moved from the cluster into the quota grain where
            // nobody would see it. The CPU-hours a console actually burns are ATTACHED-session hours,
            // which is BillingMeter.VCpuHours driven from a usage event the session grain emits —
            // docs/plan/22's usage pipeline, MeterKind.EventBased — rather than anything derivable
            // from a body.
            //
            // ⚠ THIS IS THE FOURTH TYPE TO REPORT AN UNDECLARABLE METER AND THE FIRST FOR WHICH
            // DECLARING IT WOULD BE WRONG RATHER THAN MERELY IMPOSSIBLE. charts/managed/kafka's was a
            // string where a number was wanted, charts/managed/nats' was a conditional deriving zero,
            // and charts/managed/seaweedfs' egress-is-not-derivable is a counter that only exists once
            // traffic has flowed. Those three are gaps in the registry. This one is a case where the
            // registry's shape is right and the resource's is not, and no seam would fix it.
            // conformance.yaml § owed, `session-hours-are-not-a-state-meter`.
            .Meter(QuotaMeter.StorageGb, HomeVolumeDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                CloudConsoles.ConnectAction,
                ActionKind.Post,
                CloudConsoles.ConnectPermission,
                response: CloudConsoles.ConnectResponse,
                handler: typeof(CloudConsoleSessionHandler)
            )
            .Action(
                CloudConsoles.TerminateAction,
                ActionKind.Post,
                CloudConsoles.TerminatePermission,
                response: CloudConsoles.TerminateResponse,
                handler: typeof(CloudConsoleSessionHandler)
            )
            .Display(
                "Cloud terminal",
                "Cloud terminals",
                shortName: ShortName,
                summary: "A browser shell running in this subscription's own cluster, holding a "
                + "managed identity, with a persistent home directory and no stored credential."
            )
            .Chart(CloudConsoles.ChartName)
            .SupportsTags()
            .RequiresCluster(CloudConsoles.ClusterIdPointer);
    }

    /// <summary>What a console reserves against <see cref="QuotaMeter.StorageGb" />.</summary>
    /// <remarks>
    ///     ⚠ The home volume and nothing else. The container's <c>ephemeral-storage</c> limit is node
    ///     disk rather than provisioned storage — it is reclaimed when the pod ends and no volume is
    ///     ever cut for it — so reserving against it would charge a tenant twice for one number and
    ///     would make a subscription's storage allowance depend on how many terminals happened to be
    ///     open.
    /// </remarks>
    static MeterDerivation HomeVolumeDrawn { get; } =
        MeterDerivation.Of(
            "home.size, in GiB",
            ["/properties/home/size"],
            body => KubeQuantity.TryGibibytes(CloudConsoles.HomeSize(body), out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "The storage a cloud terminal draws could not be read from home.size. A meter that "
                    + "cannot resolve refuses rather than reserving zero, because a zero reservation is "
                    + "a volume a subscription is never held to and a delete that returns nothing."
                )
        );
}
