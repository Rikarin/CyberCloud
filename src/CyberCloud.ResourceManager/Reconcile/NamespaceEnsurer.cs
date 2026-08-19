using CyberCloud.Core.Time;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Reconcile;

/// <summary>What one call to <see cref="NamespaceEnsurer.EnsureAsync" /> did.</summary>
/// <param name="Written">
///     Whether the cluster was actually written to. <see langword="false" /> means the memo answered
///     — see <see cref="NamespaceEnsurer.RecheckAfter" /> — and <paramref name="Result" /> says
///     nothing.
/// </param>
/// <param name="Result">The apply's own verdict, when there was one.</param>
/// <param name="Message">The API server's words, for the conflict case. Usually empty.</param>
public readonly record struct NamespaceEnsured(bool Written, ApplyResult Result, string Message) {
    /// <summary>The memo answered and no round trip was made.</summary>
    public static NamespaceEnsured Remembered { get; } = new(false, ApplyResult.Unknown, string.Empty);
}

/// <summary>
///     Creates and labels the Kubernetes namespace a resource group's objects live in, before the
///     first apply that needs it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Who owns namespace creation, and why it is this and not the resource-group grain.</b>
///         <see cref="ReconcileDriver.NamespaceFor" /> derives
///         <c>{subscriptionId:N}-{resourceGroup}</c> and every reconciler in the catalogue applies
///         into it. Three components could have created it, and two of them cannot:
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>IResourceGroupGrain.CreateAsync</c> — refused, because a resource group has no
///             cluster.</b> A group is a control-plane lifecycle unit; a namespace is a data-plane
///             object <i>in one cluster</i>. Which cluster a group's objects land in is decided
///             per-resource — the body carries a <c>clusterId</c> and
///             <c>ReconcileInput.ClusterId</c> is what the driver connects to — so a group with
///             resources on two clusters needs a namespace on <b>each</b>, and a group created before
///             its first resource has no cluster to create one on at all. The grain would have to
///             guess, and <c>CyberCloud.Tenancy</c> holds no reference to
///             <c>CyberCloud.Kubernetes.Contracts</c> to guess with — see its <c>.csproj</c>.
///         </item>
///         <item>
///             <b>A platform-level controller — refused for now, because it has nothing to
///             enumerate.</b> The set of (group × cluster) pairs that need a namespace is not
///             knowable from the control plane's own state: it is the set of clusters the group's
///             resources <i>name</i>, which is a scan of every resource in every group on every
///             tick. That is a watch and a work queue to solve a problem the pass that needs the
///             namespace already knows the answer to.
///         </item>
///         <item>
///             <b>The reconcile driver, immediately before the pass — chosen.</b> It is the one place
///             that holds the resource's address <i>and</i> a live connection to the cluster the
///             objects are about to land in, which is exactly the pair a namespace is keyed by. It
///             also has the correct failure behaviour for free: a namespace that cannot be created is
///             a pass that fails with a retryable error naming the namespace, rather than twenty
///             reconcilers each discovering a <c>404</c> in their own words.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Idempotent and race-safe because it is a server-side apply, not a create.</b> Two
///         reconcilers converging two resources in one group, on two silos, at the same instant both
///         call this. An apply patch on an absent object creates it and on a present one is a no-op
///         over identical fields, so the second caller does not see <c>409 AlreadyExists</c> — which
///         is what a <c>POST</c> would give, and what would have to be caught and swallowed, and
///         swallowing <i>that</i> conflict is indistinguishable from swallowing a real one.
///     </para>
///     <para>
///         ⚠ <b>The seven labels are part of the same write and are not applied afterwards.</b> A
///         namespace that exists and is unlabelled is worse than one that does not exist:
///         <c>CloudConsoles.NetworkPolicyJson</c>'s tenant-wide egress rule selects namespaces
///         carrying <c>cybercloud.io/tenant-id</c>, so an unlabelled namespace makes a shell's reach
///         silently narrower than the isolation model says it is, while an absent namespace fails the
///         reconcile loudly. Going through <c>KubeCommand</c> is what makes the two inseparable —
///         ADR-013's builder injects all seven or refuses to build.
///     </para>
///     <para>
///         ⚠ <b>Nothing deletes the namespace, deliberately.</b> See
///         <see cref="ReconcileDriver" />'s remarks and <c>src/Providers/README.md</c> § Namespaces.
///     </para>
/// </remarks>
/// <param name="clock">The clock the re-check interval is measured on.</param>
public sealed class NamespaceEnsurer(IClock clock) {
    /// <summary>
    ///     How long a successful ensure is trusted before the namespace is applied again.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what keeps the promise that a driver-side create does not cost a round trip
    ///     per apply.</b> Without the memo every pass of every resource would read and patch a
    ///     namespace that has existed for months. With a memo that never expired, a namespace deleted
    ///     out of band would never come back and every later apply into it would <c>404</c> until the
    ///     silo restarted. An hour is short enough that an operator who deletes a namespace by hand
    ///     sees it return within one, and long enough that the amortised cost is nil.
    /// </remarks>
    public static TimeSpan RecheckAfter { get; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     How many (cluster, namespace) pairs the memo holds before it is emptied.
    /// </summary>
    /// <remarks>
    ///     ⚠ The memo is keyed by data a tenant controls the cardinality of — a subscription may hold
    ///     many resource groups — so it needs a ceiling or it is a slow leak in a process that runs
    ///     for weeks. Clearing the whole map rather than evicting the oldest entry is deliberate: the
    ///     cost of being wrong is one extra apply per namespace, an LRU here would be a second cache
    ///     to get right, and a silo holding ten thousand live namespaces has a placement problem
    ///     rather than a caching one.
    /// </remarks>
    public const int MaxEntries = 10_000;

    /// <summary>
    ///     The resource type the namespace's <c>cybercloud.io/resource-type</c> label carries —
    ///     <see cref="KubeLabels.ResourceGroupType" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A namespace belongs to a resource <i>group</i>, and a group is not a resource.</b>
    ///         ADR-013's seven include <c>resource-id</c> and <c>resource-type</c>, and neither has an
    ///         honest value here: the group has no GUID — <c>ResourceGroupDescriptor</c> carries a
    ///         name, a subscription and a tenant, and no id — and a resource group is a structural
    ///         segment of a resource path rather than a registered provider type.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Stamping the first resource's id was the tempting mistake.</b> Whichever resource
    ///         happened to reconcile first would own the namespace's label, the label would name a
    ///         resource that may be deleted while the namespace and everything else in it lives on,
    ///         and the drift scan's orphan join — <c>DriftScanner</c>, on <c>resource-id</c> — would
    ///         report the namespace as an orphan of a resource that was legitimately removed. So the
    ///         id is derived from the group instead (<see cref="IdFor" />) and the type says plainly
    ///         what the object is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The type itself lives in <see cref="KubeLabels" /> and not here, and the reason is
    ///         a defect this decision caused.</b> Three components have to <i>recognise</i> a
    ///         group-attributed object — the drift scan, the conformance harness's labels assertion,
    ///         and any future cluster inventory — and none of them should have to reference the
    ///         component that <i>writes</i> one. Keeping the vocabulary in the label assembly is what
    ///         makes the recognition one rule rather than three spellings of it.
    ///     </para>
    /// </remarks>
    public static ResourceTypeName GroupType => KubeLabels.ResourceGroupType;

    /// <summary>
    ///     The <c>api-version</c> label a namespace carries. It is the platform's, not a tenant
    ///     request's — nothing versions a namespace.
    /// </summary>
    public const string GroupApiVersion = "2026-08-01";

    /// <summary>The field manager every namespace write uses.</summary>
    /// <remarks>
    ///     ⚠ <b>One manager for the whole platform, and not <c>cybercloud/{provider}</c>.</b> Twenty
    ///     providers write this one object; under server-side apply, per-provider managers would make
    ///     the namespace's labels co-owned by whichever providers happened to have run, and the day
    ///     one of those values changed the other managers would be told they had a conflict over a
    ///     field they never meant to own. A single manager makes every write the same writer.
    /// </remarks>
    public const string FieldManager = "cybercloud/resource-manager";

    /// <summary>The kind. Cluster-scoped, so its <c>ObjectRef</c> carries no namespace.</summary>
    static GroupVersionKind Kind { get; } =
        new() { Group = "", Version = "v1", Kind = "Namespace", Plural = "namespaces" };

    readonly ConcurrentDictionary<(Guid Cluster, string Namespace), DateTimeOffset> ensured = new();

    /// <summary>
    ///     Applies the namespace <paramref name="ns" /> to <paramref name="connection" />'s cluster,
    ///     with ADR-013's seven labels, unless a recent pass already did.
    /// </summary>
    /// <param name="id">The resource whose pass needs the namespace. Supplies tenant, subscription and group.</param>
    /// <param name="ns">The namespace name — <see cref="ReconcileDriver.NamespaceFor" />'s output.</param>
    /// <param name="connection">The cluster the objects are about to land in.</param>
    /// <param name="cancellationToken">The pass's budget.</param>
    /// <returns>
    ///     What the write did, or <see cref="NamespaceEnsured.Remembered" /> when the memo answered
    ///     and no call was made. A failed <see cref="Result" /> means the namespace is not there and
    ///     the pass must not proceed.
    /// </returns>
    public async Task<Result<NamespaceEnsured>> EnsureAsync(
        ResourceId id,
        string ns,
        IKubeClusterConnection connection,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(ns);

        var key = (connection.ClusterId, ns);
        var now = clock.UtcNow;

        // ⚠ THE LOWER BOUND IS NOT REDUNDANT. `now - checkedAt` is negative whenever the clock has
        // gone backwards — an NTP correction in production, a fixture resetting its clock in a test —
        // and a negative TimeSpan is less than RecheckAfter, so a bare upper bound would trust the
        // memo for as long as the jump was large. Re-applying is cheap and idempotent; trusting a
        // stale entry is a 404 on every apply into a namespace that is not there.
        if (ensured.TryGetValue(key, out var checkedAt)
            && now - checkedAt is { Ticks: >= 0 } age
            && age < RecheckAfter) {
            return Result<NamespaceEnsured>.Success(NamespaceEnsured.Remembered);
        }

        var applied = await KubeCommand.For(connection)
            .WithTenantId(id.TenantId)
            .WithResourceId(GroupAddress(id))
            .WithKind(Kind)
            .WithApiVersion(GroupApiVersion)
            .WithFieldManager(FieldManager)
            .ObjectJson(Body(ns))
            .ApplyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (applied.TryGetError(out var error)) {
            // ⚠ The caller turns this into a RETRYABLE failure. A namespace the platform may not
            // create is an RBAC problem on the connection's credential, which an operator fixes
            // without the tenant re-issuing anything — so the next pass may well find the API server
            // willing, and a terminal failure here would strand a create that was never wrong.
            return Result<NamespaceEnsured>.Failure(
                new(
                    error.Code,
                    $"The namespace '{ns}' could not be created on cluster {connection.ClusterId:D}, "
                    + $"so nothing can be applied into it: {error.Message} Every resource in "
                    + $"'{id.ResourceGroup}' lands in this namespace — ReconcileDriver.NamespaceFor "
                    + "derives it — and an apply into a namespace that does not exist answers 404 "
                    + "naming it.",
                    error.Target
                )
            );
        }

        var outcome = applied.GetValueOrThrow();

        // ⚠ Only a write that took effect is memoised. Suspended means the cluster is degraded and
        // the write was never attempted, and remembering it would skip the namespace for an hour
        // after the cluster came back. Conflict means another field manager holds one of our labels —
        // the namespace exists, so the pass may proceed, but the label set is not ours and the next
        // pass should look again rather than trust it.
        if (outcome.Result is ApplyResult.Created or ApplyResult.Updated or ApplyResult.Unchanged) {
            if (ensured.Count >= MaxEntries) {
                ensured.Clear();
            }

            ensured[key] = now;
        }

        return Result<NamespaceEnsured>.Success(new(true, outcome.Result, outcome.Message));
    }

    /// <summary>
    ///     Forgets every memoised namespace, so the next pass applies again.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists because the memo is a cache of a fact about a cluster, and something
    ///         that empties the cluster invalidates it.</b> In production the only such event is an
    ///         operator deleting a namespace by hand, which <see cref="RecheckAfter" /> covers within
    ///         the hour. In a test harness it happens between every test — <c>FakeKubeCluster.Reset</c>
    ///         wipes the world in microseconds — and no interval is short enough for that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bug it closes is not a test-tidiness one.</b> Without it the harness's labels
    ///         assertion saw the namespace command or not <i>depending on which test ran first</i>:
    ///         cold memo, the namespace is applied and lands in the assertion's loop; warm memo, it
    ///         does not. That is a check whose answer depends on its neighbours, which is worth more
    ///         to close than the failure that revealed it.
    ///     </para>
    /// </remarks>
    public void Forget() => ensured.Clear();

    /// <summary>
    ///     The address the namespace's seven labels are derived from: the resource group itself, as
    ///     if it were a resource.
    /// </summary>
    /// <param name="id">Any resource in the group.</param>
    /// <remarks>
    ///     The tenant, subscription and group come straight from <paramref name="id" /> — they are
    ///     what the namespace is <i>about</i>. The name is the group's own, and the id is
    ///     <see cref="IdFor" />.
    /// </remarks>
    public static ResourceId GroupAddress(ResourceId id) =>
        new(
            id.TenantId,
            id.SubscriptionId,
            id.ResourceGroup,
            GroupType,
            id.ResourceGroup,
            IdFor(id.SubscriptionId, id.ResourceGroup)
        );

    /// <summary>
    ///     The GUID a resource group's namespace carries as <c>cybercloud.io/resource-id</c>, derived
    ///     from the group's identity so that it is the same on every silo and across restarts.
    /// </summary>
    /// <param name="subscriptionId">The owning subscription.</param>
    /// <param name="resourceGroup">The group's name, which is unique within that subscription.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="Guid.Empty" /> was the alternative and is worse.</b> Every namespace on
    ///         a cluster would then carry the same <c>resource-id</c>, so the drift scan's hash join
    ///         would fold every group's namespace into one finding, and
    ///         <c>ProviderConformanceTests</c> already names an empty GUID here as "the failure that
    ///         produces objects the drift scan cannot attribute".
    ///     </para>
    ///     <para>
    ///         The derivation is SHA-256 over a fixed prefix and the group's identity, with RFC 9562's
    ///         version-8 (custom) and variant bits set, read big-endian so the value does not depend
    ///         on the machine. It is not drawn from the same pool as a resource GUID —
    ///         <c>Guid.NewGuid</c> — so a collision with a real resource is not a case that has to be
    ///         reasoned about beyond the birthday bound.
    ///     </para>
    /// </remarks>
    public static Guid IdFor(Guid subscriptionId, string resourceGroup) {
        ArgumentException.ThrowIfNullOrEmpty(resourceGroup);

        var seed = "cybercloud.io/resource-group\n"
            + subscriptionId.ToString("N", CultureInfo.InvariantCulture)
            + "/"
            + resourceGroup;

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new(bytes, bigEndian: true);
    }

    /// <summary>The namespace document, before the builder injects the labels.</summary>
    /// <param name="ns">The namespace name.</param>
    /// <remarks>
    ///     ⚠ <b><c>metadata.name</c> and nothing else.</b> A <c>Namespace</c> has a <c>spec</c>, and
    ///     the only thing in it is <c>finalizers</c> — a field the API server and the namespace
    ///     controller own. Sending <c>"spec": {}</c> or <c>"spec": null</c> in an apply patch would
    ///     contest a field this platform has no business holding, and an explicit null under
    ///     server-side apply is a <i>deletion</i> instruction. The key is absent because there is no
    ///     value for it.
    ///     <para>
    ///         The name is spelled here rather than left to the builder's fallback, which would use
    ///         the resource's own name — the resource <i>group</i>'s name, not
    ///         <c>{subscriptionId:N}-{resourceGroup}</c>.
    ///     </para>
    /// </remarks>
    static string Body(string ns) =>
        new JsonObject { ["metadata"] = new JsonObject { ["name"] = ns } }.ToJsonString();
}
