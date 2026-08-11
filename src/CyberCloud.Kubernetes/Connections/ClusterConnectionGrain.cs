using System.Globalization;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Health;
using CyberCloud.Kubernetes.Informers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CyberCloud.Kubernetes.Connections;

/// <summary>
///     One activation per cluster, platform-wide. Holds the client, the informers, and the tenancy
///     check that the null-tenant key cannot make.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Grain keys: <i>"<c>IClusterConnectionGrain</c> is a null-tenant grain and
///         that is a deliberate exception … The grain therefore carries the owning tenant as
///         <b>state</b> and checks it on every call … <b>This is the single place tenancy is enforced
///         by code rather than by key</b>, and it is called out here so nobody has to discover
///         it."</i> <see cref="EnsureCallerMayReach" /> is that check. Every public method's first
///         statement is a call to it, without exception, and
///         <c>ClusterConnectionTenancyTests.EveryGrainMethodChecksTheOwningTenant</c> enumerates the
///         interface reflectively and fails if a method is added that does not.
///     </para>
/// </remarks>
public sealed class ClusterConnectionGrain : Grain, IClusterConnectionGrain
{
    readonly IPersistentState<ClusterConnectionState> state;
    readonly IKubeApiClientFactory clients;
    readonly IClusterOperatorAuthority operators;
    readonly IClock clock;
    readonly ILogger<ClusterConnectionGrain> logger;
    readonly KubernetesOptions options;
    readonly Dictionary<string, SharedInformer> informers = new(StringComparer.Ordinal);

    ClusterHealthTracker health = null!;
    IKubeApiClient? api;
    Guid clusterId;
    IDisposable? healthTimer;

    /// <summary>Creates the grain.</summary>
    /// <param name="state">Durable state — the owning tenant and the informer cursors.</param>
    /// <param name="clients">How to reach a cluster.</param>
    /// <param name="operators">The platform → connection edge of docs/plan/06 § Platform administration.</param>
    /// <param name="clock">The clock the health window is measured against.</param>
    /// <param name="options">Ping interval, staleness window, stagger window.</param>
    /// <param name="logger">Where the allowed and denied edges are logged.</param>
    public ClusterConnectionGrain(
        [PersistentState("clusterConnection", StorageTiers.Durable)]
        IPersistentState<ClusterConnectionState> state,
        IKubeApiClientFactory clients,
        IClusterOperatorAuthority operators,
        IClock clock,
        IOptions<KubernetesOptions> options,
        ILogger<ClusterConnectionGrain> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.state = state;
        this.clients = clients;
        this.operators = operators;
        this.clock = clock;
        this.logger = logger;
        this.options = options.Value;
    }

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var key = this.GetPrimaryKeyString();

        // ⚠ The key is parsed, not trusted. GrainKeys is the only type allowed to decode one
        // (ADR-002), and a cluster connection reached under any other key string would be a second
        // activation of a thing there is exactly one of.
        var parsed = GrainKeys.Parse(key);
        if (parsed.TryGetError(out var error) || parsed.GetValueOrThrow().Kind != GrainKeyKind.ClusterConnection)
        {
            throw new InvalidOperationException(
                $"'{key}' is not a cluster-connection grain key. The shape is "
                + $"'{GrainKeys.ClusterConnectionPrefix}{{clusterId:N}}' — docs/plan/06 § Grain keys. "
                + (error is null ? string.Empty : error.Message));
        }

        clusterId = parsed.GetValueOrThrow().Id;
        health = new ClusterHealthTracker(clusterId, clock, options.HealthStalenessWindow);

        // Continuity across activations: a rehomed grain that forgot its last success would report
        // Unknown and, worse, would not report Degraded — the transition is measured from the last
        // success, so losing it loses the transition.
        if (state.State.LastSuccessAt != default)
        {
            health.SeedLastSuccess(state.State.LastSuccessAt);
        }

        foreach (var (kindKey, cursor) in state.State.InformerCursors)
        {
            // The cursors are restored here but the informers are not re-established until somebody
            // asks for the kind again — establishing every previously-watched kind on activation
            // would be exactly the stampede the stagger exists to spread.
            logger.LogDebug(
                "Cluster {Cluster} restored informer cursor {Cursor} for {Kind}.",
                clusterId,
                cursor,
                kindKey);
        }

        if (options.RunHealthTimer && state.State.Descriptor is not null)
        {
            StartHealthTimer();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        healthTimer?.Dispose();
        api?.Dispose();
        api = null;
        return Task.CompletedTask;
    }

    // ── The tenancy check ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The one check docs/plan/06 § Grain keys says this grain exists to make.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The policy, and why each arm is what it is.</b>
    ///     </para>
    ///     <list type="table">
    ///         <item>
    ///             <term>The owning tenant</term>
    ///             <description>Allowed, silently. This is the normal path.</description>
    ///         </item>
    ///         <item>
    ///             <term>The platform tenant, with an operator relation</term>
    ///             <description>
    ///                 Allowed and <b>logged, always, with the operator's user id</b> — docs/plan/06
    ///                 § Platform administration, row 1. Also the edge docs/plan/06 § Grain keys
    ///                 names explicitly for this grain.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>Another null-tenant platform grain</term>
    ///             <description>
    ///                 Allowed. A platform grain has no tenant to be wrong about, and the reverse
    ///                 edge (null-tenant → a tenant) is already denied by
    ///                 <c>PlatformCrossTenantAuthorizer</c>, so nothing reaches here in a tenant's
    ///                 name without having been checked.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>Any other tenant, a client, or an unidentified caller</term>
    ///             <description>Refused — see the ⚠ below on which error code.</description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠ <b>The refusal is <see cref="ErrorCode.ResourceNotFound" /> and not
    ///         <see cref="ErrorCode.AuthorizationFailed" />.</b> docs/plan/00 § Non-negotiables
    ///         requires <c>404</c> rather than <c>403</c> for a caller who may not read a resource,
    ///         because <c>403</c> discloses that it exists. Tenant B probing cluster ids must not be
    ///         able to tell "not yours" from "no such cluster" — and the connection grain is
    ///         null-tenant, so B can address A's cluster grain directly by key and would otherwise
    ///         have a platform-wide existence oracle for every cluster id it can guess.
    ///         <see cref="ErrorCode.AuthorizationFailed" />'s own remarks say exactly this.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <see cref="CallerKind.Client" /> is refused too</b>, and that is not an
    ///         oversight. Orleans' tenant separation returns early for a non-grain caller, so a
    ///         cluster client is the one caller nothing else checks. Since every legitimate use of
    ///         this grain is from a reconciler — which is a grain — a client call is either a test or
    ///         a mistake, and the safe reading of both is "no". A test opts in with
    ///         <see cref="CallerTenant.Enter" />, which is explicit and local to the test.
    ///     </para>
    /// </remarks>
    Result EnsureCallerMayReach(string operation)
    {
        var descriptor = state.State.Descriptor;
        if (descriptor is null)
        {
            return Result.Failure(
                ErrorCode.ResourceNotFound,
                $"Cluster {clusterId:D} has no connection. Call AttachAsync first.");
        }

        var caller = CallerTenant.Current;

        if (caller.Kind == CallerKind.Tenant && caller.TenantId == descriptor.OwningTenantId)
        {
            return Result.Success;
        }

        if (caller.Kind == CallerKind.NullTenant)
        {
            return Result.Success;
        }

        if (caller.Kind == CallerKind.Tenant && caller.TenantId == PlatformTenantId)
        {
            if (operators.OperatorFor(clusterId, descriptor.OwningTenantId) is { } operatorId)
            {
                // docs/plan/06 § Platform administration: "Always, with the operator's user id."
                // LogWarning rather than LogInformation on purpose — this is a privileged edge and
                // it should be visible at the level an operator actually filters on.
                logger.LogWarning(
                    "Platform operator {Operator} reached cluster {Cluster} (owned by tenant "
                    + "{Owner}) to {Operation}. docs/plan/06 § Platform administration requires "
                    + "this edge to be logged, always.",
                    operatorId,
                    clusterId,
                    descriptor.OwningTenantId,
                    operation);

                return Result.Success;
            }

            logger.LogError(
                "DENIED: the platform tenant tried to {Operation} on cluster {Cluster} without an "
                + "active platform:root#operator relation. docs/plan/06 § Platform administration.",
                operation,
                clusterId);

            return Refused();
        }

        logger.LogError(
            "DENIED: {Caller} tried to {Operation} on cluster {Cluster}, which is owned by tenant "
            + "{Owner}. This grain is null-tenant, so this check is the only thing between the two "
            + "— docs/plan/06 § Grain keys.",
            caller,
            operation,
            clusterId,
            descriptor.OwningTenantId);

        return Refused();
    }

    /// <summary>
    ///     The platform tenant — <c>Guid.Empty</c>, docs/plan/06 § Platform administration.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not "the null tenant". docs/plan/06 § Platform administration's own table separates
    ///     them: the platform tenant is an ordinary tenant id that happens to be all zeroes, and the
    ///     null tenant is the <i>absence</i> of qualification. Both reach this grain, by different
    ///     arms of <see cref="EnsureCallerMayReach" />.
    /// </remarks>
    public static readonly Guid PlatformTenantId = Guid.Empty;

    Result Refused() => Result.Failure(
        ErrorCode.ResourceNotFound,
        $"Cluster {clusterId:D} does not exist, or you may not reach it.");

    static Result<T> Refused<T>(Result refusal)
        where T : notnull =>
        Result<T>.Failure(refusal.Error!);

    // ── The grain surface ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result> AttachAsync(ClusterConnectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.ClusterId != clusterId)
        {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"The descriptor names cluster {descriptor.ClusterId:D} but this grain is "
                + $"{clusterId:D}. The key and the state must agree.");
        }

        var existing = state.State.Descriptor;

        if (existing is null)
        {
            // ⚠ The FIRST attach establishes the owner and is therefore the one call that cannot
            // check it. It is guarded by cardinality instead: it can only ever happen once per
            // cluster id, and the cluster id is minted by the resource manager when the cluster
            // resource is created inside a tenant that has already been authorized. Every
            // subsequent call, including a re-attach, goes through EnsureCallerMayReach.
            logger.LogInformation(
                "Cluster {Cluster} attached: {Descriptor}.", clusterId, descriptor);
        }
        else
        {
            var allowed = EnsureCallerMayReach("re-attach");
            if (allowed.TryGetError(out var error))
            {
                return Result.Failure(error);
            }

            if (existing.OwningTenantId != descriptor.OwningTenantId)
            {
                // ⚠ Changing the owner is how "check the owner on every call" would be defeated by
                // one call. The current owner may not hand the cluster to another tenant; only a
                // platform operator may, and they have already been checked and logged above.
                var caller = CallerTenant.Current;
                if (!(caller.Kind == CallerKind.Tenant && caller.TenantId == PlatformTenantId)
                    && caller.Kind != CallerKind.NullTenant)
                {
                    return Result.Failure(
                        ErrorCode.AuthorizationFailed,
                        $"Cluster {clusterId:D} is owned by tenant {existing.OwningTenantId:D} and "
                        + "only a platform operator may transfer it. A tenant that could rewrite "
                        + "the owning tenant could defeat the check docs/plan/06 § Grain keys puts "
                        + "on every other call in one move.");
                }

                logger.LogWarning(
                    "Cluster {Cluster} transferred from tenant {From} to tenant {To}.",
                    clusterId,
                    existing.OwningTenantId,
                    descriptor.OwningTenantId);
            }
        }

        state.State.Descriptor = descriptor;
        state.State.AttachedAt = state.State.AttachedAt == default
            ? clock.UtcNow
            : state.State.AttachedAt;

        await state.WriteStateAsync();

        // The client is rebuilt on the next use; the credentials may have changed.
        api?.Dispose();
        api = null;

        if (options.RunHealthTimer)
        {
            StartHealthTimer();
        }

        return Result.Success;
    }

    /// <inheritdoc />
    public async Task<Result<ClusterHealth>> PingAsync()
    {
        var allowed = EnsureCallerMayReach(nameof(PingAsync));
        if (allowed.IsFailure)
        {
            return Refused<ClusterHealth>(allowed);
        }

        await PingCoreAsync(CancellationToken.None);
        return Result<ClusterHealth>.Success(health.Current);
    }

    /// <inheritdoc />
    public Task<Result<ClusterHealth>> GetHealthAsync()
    {
        var allowed = EnsureCallerMayReach(nameof(GetHealthAsync));
        return Task.FromResult(allowed.IsFailure
            ? Refused<ClusterHealth>(allowed)
            : Result<ClusterHealth>.Success(health.Current));
    }

    /// <inheritdoc />
    public async Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var allowed = EnsureCallerMayReach(nameof(ApplyAsync));
        if (allowed.IsFailure)
        {
            return Refused<ApplyOutcome>(allowed);
        }

        // ⚠ A second, independent check: the COMMAND's tenant must be the owner's too.
        //
        // The ambient check above says "who is calling"; this says "whose object is this". They come
        // apart in exactly one case that matters — a platform operator, legitimately reaching in,
        // applying a command whose labels say tenant A into tenant B's cluster. The seven labels are
        // what billing and orphan detection join on (ADR-013), so an object landing in the wrong
        // cluster with the right labels is worse than one that fails to land.
        var descriptor = state.State.Descriptor!;
        if (command.TenantId != descriptor.OwningTenantId)
        {
            return Result<ApplyOutcome>.Failure(
                ErrorCode.AuthorizationFailed,
                $"The command is labelled for tenant {command.TenantId:D} but cluster "
                + $"{clusterId:D} is owned by tenant {descriptor.OwningTenantId:D}. "
                + "cybercloud.io/tenant-id is what billing and orphan detection join on, so an "
                + "object written into the wrong cluster with the right labels is a worse outcome "
                + "than a refused write.");
        }

        // ⚠ SUSPENDED, NOT FAILED. docs/plan/09 § Cluster connections: a Degraded cluster's
        // reconciles are "suspended (not failed) and the portal says 'cannot reach your cluster'
        // instead of 'provisioning failed'". A failed Result here would become a failed operation
        // and page somebody about a tenant's network.
        var current = health.Current;
        if (current.State == ClusterHealthState.Degraded)
        {
            return Result<ApplyOutcome>.Success(new ApplyOutcome
            {
                Result = ApplyResult.Suspended,
                Target = command.Target,
                ReconcileHash = command.ReconcileHash,
                Message = current.Message,
            });
        }

        var client = await ClientAsync(CancellationToken.None);
        if (client.TryGetError(out var connectError))
        {
            return Result<ApplyOutcome>.Failure(connectError);
        }

        var outcome = await client.GetValueOrThrow()
            .ApplyAsync(command, CancellationToken.None);

        await RecordReachabilityAsync(outcome.IsSuccess);

        if (outcome.IsSuccess && outcome.GetValueOrThrow() is { Result: ApplyResult.Conflict } conflicted)
        {
            logger.LogWarning(
                "Drift on cluster {Cluster}: {Drift}",
                clusterId,
                conflicted.Drift?.Describe() ?? conflicted.Message);
        }

        return outcome;
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(KubeCommand command, CascadePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(command);

        var allowed = EnsureCallerMayReach(nameof(DeleteAsync));
        if (allowed.TryGetError(out var refusal))
        {
            return Result.Failure(refusal);
        }

        var descriptor = state.State.Descriptor!;
        if (command.TenantId != descriptor.OwningTenantId)
        {
            return Result.Failure(
                ErrorCode.AuthorizationFailed,
                $"The command is labelled for tenant {command.TenantId:D} but cluster "
                + $"{clusterId:D} is owned by tenant {descriptor.OwningTenantId:D}.");
        }

        if (health.Current.State == ClusterHealthState.Degraded)
        {
            // Same rule as apply, and it matters more here: docs/plan/06 § Two-phase create leaves a
            // resource whose teardown fails in `Deleting` with a retry reminder, visible in
            // listings. A hard failure would take that resource out of the retry loop while its
            // pods still run and its meter still ticks.
            return Result.Failure(
                ErrorCode.OperationInProgress,
                health.Current.Message);
        }

        var client = await ClientAsync(CancellationToken.None);
        if (client.TryGetError(out var connectError))
        {
            return Result.Failure(connectError);
        }

        var outcome = await client.GetValueOrThrow()
            .DeleteAsync(command.Target, policy, CancellationToken.None);

        await RecordReachabilityAsync(outcome.IsSuccess);
        return outcome;
    }

    /// <inheritdoc />
    public async Task<Result<KubeObject>> GetAsync(ObjectRef target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var allowed = EnsureCallerMayReach(nameof(GetAsync));
        if (allowed.IsFailure)
        {
            return Refused<KubeObject>(allowed);
        }

        var client = await ClientAsync(CancellationToken.None);
        if (client.TryGetError(out var connectError))
        {
            return Result<KubeObject>.Failure(connectError);
        }

        var outcome = await client.GetValueOrThrow()
            .GetAsync(target, CancellationToken.None);

        // A 404 is the cluster answering, so it counts as reachable. Only a transport failure does
        // not — see KubeApiClient.IsTransport.
        await RecordReachabilityAsync(
            outcome.IsSuccess || outcome.Error!.Code == ErrorCode.ResourceNotFound);

        return outcome;
    }

    /// <inheritdoc />
    public async Task<Result<InformerLease>> WatchAsync(GroupVersionKind kind, string labelSelector)
    {
        ArgumentNullException.ThrowIfNull(kind);

        var allowed = EnsureCallerMayReach(nameof(WatchAsync));
        if (allowed.IsFailure)
        {
            return Refused<InformerLease>(allowed);
        }

        var client = await ClientAsync(CancellationToken.None);
        if (client.TryGetError(out var connectError))
        {
            return Result<InformerLease>.Failure(connectError);
        }

        var key = InformerKey(kind);

        if (informers.TryGetValue(key, out var existing))
        {
            // Shared: one informer per kind, however many callers. docs/plan/09 § Observing —
            // "one watch per kind is O(kinds)".
            existing.Subscribe();
            return Result<InformerLease>.Success(existing.Lease);
        }

        var informer = new SharedInformer(
            clusterId,
            kind,
            string.Empty,
            labelSelector,
            client.GetValueOrThrow(),
            logger,
            options.InformerStaggerWindow);

        if (state.State.InformerCursors.TryGetValue(key, out var cursor))
        {
            informer.ResumeFrom(cursor);
        }

        var established = await informer.EstablishAsync(cancellationToken: CancellationToken.None);
        if (established.TryGetError(out var establishError))
        {
            await RecordReachabilityAsync(false);
            return Result<InformerLease>.Failure(establishError);
        }

        informer.Subscribe();
        informers[key] = informer;

        state.State.InformerCursors[key] = informer.ResourceVersion;
        await state.WriteStateAsync();
        await RecordReachabilityAsync(true);

        var outcome = established.GetValueOrThrow();
        logger.LogInformation(
            "Cluster {Cluster} established an informer for {Kind}: resumed={Resumed}, "
            + "fullListFallback={Fallback}, stagger={Stagger} ms, items={Items}, cursor={Cursor}.",
            clusterId,
            kind,
            outcome.Resumed,
            outcome.FellBackToFullList,
            outcome.StaggerDelay.TotalMilliseconds,
            outcome.ItemCount,
            outcome.ResourceVersion);

        return Result<InformerLease>.Success(informer.Lease);
    }

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<InformerLease>>> ListInformersAsync()
    {
        var allowed = EnsureCallerMayReach(nameof(ListInformersAsync));
        if (allowed.IsFailure)
        {
            return Task.FromResult(Refused<IReadOnlyList<InformerLease>>(allowed));
        }

        IReadOnlyList<InformerLease> leases = [.. informers.Values.Select(x => x.Lease)];
        return Task.FromResult(Result<IReadOnlyList<InformerLease>>.Success(leases));
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    static string InformerKey(GroupVersionKind kind) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind.Group}/{kind.Version}/{kind.Plural}");

    async Task<Result<IKubeApiClient>> ClientAsync(CancellationToken cancellationToken)
    {
        if (api is not null)
        {
            return Result<IKubeApiClient>.Success(api);
        }

        var connected = await clients
            .ConnectAsync(state.State.Descriptor!, cancellationToken);

        if (connected.TryGetError(out var error))
        {
            return Result<IKubeApiClient>.Failure(error);
        }

        api = connected.GetValueOrThrow();
        return connected;
    }

    async Task PingCoreAsync(CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        if (client.TryGetError(out var connectError))
        {
            health.RecordFailure(connectError.Message);
            return;
        }

        var pinged = await client.GetValueOrThrow().PingAsync(cancellationToken);
        if (pinged.TryGetError(out var pingError))
        {
            health.RecordFailure(pingError.Message);
            return;
        }

        health.RecordSuccess();
        await PersistLastSuccessAsync();
    }

    /// <summary>
    ///     Folds the outcome of a real API call into the health window.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every successful call is a ping. A cluster that is answering applies does not also need
    ///     to be probed, and treating only the timer's ping as evidence would let a busy, healthy
    ///     cluster be called <c>Degraded</c> because one probe was late.
    /// </remarks>
    async Task RecordReachabilityAsync(bool reachable)
    {
        if (reachable)
        {
            health.RecordSuccess();
            await PersistLastSuccessAsync();
        }
        else
        {
            health.RecordFailure("The cluster did not answer.");
        }
    }

    async Task PersistLastSuccessAsync()
    {
        var now = health.Current.LastSuccessAt;

        // ⚠ Written at most once a minute, not on every call. This is a durable write on the hot
        // path of every apply; persisting it each time would put a PostgreSQL round trip in front of
        // every reconcile to buy a field that only has to be roughly right across an activation.
        if (now - state.State.LastSuccessAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        state.State.LastSuccessAt = now;
        await state.WriteStateAsync();
    }

    void StartHealthTimer()
    {
        healthTimer?.Dispose();

        // ⚠ A TIMER, AND docs/plan/09 § Cluster connections SAYS "pinned by a reminder".
        //
        // The difference is real and the gap is named rather than papered over: a timer stops when
        // the activation is collected, so this keeps health fresh while the grain is alive but does
        // NOT keep it alive. docs/plan/09 wants the activation pinned so that a cluster's informers
        // and health survive an idle period.
        //
        // A reminder cannot be registered yet: nothing in this repository configures a reminder
        // service. Microsoft.Orleans.Reminders.Redis is pinned in Directory.Packages.props, but no
        // silo calls UseRedisReminderService, so RegisterOrUpdateReminder throws at runtime.
        // Registering one here would move that failure into every silo that hosts this grain.
        // When the reminder service lands, this becomes RegisterOrUpdateReminder and the grain
        // becomes IRemindable; the ping logic (PingCoreAsync) does not change.
        healthTimer = this.RegisterGrainTimer(
            async cancellationToken => await PingCoreAsync(cancellationToken),
            new GrainTimerCreationOptions
            {
                DueTime = TimeSpan.Zero,
                Period = options.PingInterval,
                Interleave = true,
            });
    }
}
