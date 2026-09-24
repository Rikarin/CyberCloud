using CyberCloud.ResourceManager.Contracts.Registry;
using Orleans.Concurrency;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Actions;

/// <summary>
///     Where <see cref="ActionDispatcher" /> sends an action it can't run in this process because the
///     resource's cluster is out of reach from here.
/// </summary>
/// <remarks>
///     Registered by the gateway and by nothing else: a silo reaches clusters itself, and a silo that
///     relayed would be relaying to itself. <see cref="IClusterActionGrain" />'s remarks say why the
///     gateway can't reach one.
/// </remarks>
public interface IClusterActionRelay {
    /// <summary>Runs the action where the cluster is reachable and returns what its handler answered.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="action">The declared action's name.</param>
    /// <param name="input">The resource as stored.</param>
    /// <param name="body">The validated <c>POST</c> body.</param>
    /// <param name="caller">Who asked. Empty when the manager had nobody to hand over.</param>
    /// <param name="parent">The resource's parent with its GUID resolved, or <see langword="null" />.</param>
    /// <param name="createsAsCaller">
    ///     Whether the manager handed the action a creator bound to <paramref name="caller" />, which the
    ///     far side rebuilds — see <see cref="IClusterActionGrain" />.
    /// </param>
    /// <param name="cancellationToken">Stops the call before it's sent; a sent call runs to its own budget.</param>
    Task<Result<string>> InvokeAsync(
        ResourceId id,
        string action,
        ReconcileInput input,
        JsonElement body,
        CallerContext caller,
        ResourceId? parent,
        bool createsAsCaller,
        CancellationToken cancellationToken = default
    );
}

/// <summary><see cref="IClusterActionRelay" /> over <see cref="IClusterActionGrain" />.</summary>
/// <param name="grains">The gateway's Orleans client.</param>
public sealed class GrainClusterActionRelay(IGrainFactory grains) : IClusterActionRelay {
    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        ResourceId id,
        string action,
        ReconcileInput input,
        JsonElement body,
        CallerContext caller,
        ResourceId? parent,
        bool createsAsCaller,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested();

        // ⚠ Qualified by the RESOURCE's tenant, which the manager resolved from the path it
        // authorized. The grain refuses any other, so the key and the argument can't disagree.
        return grains
            .ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IClusterActionGrain>(ClusterActionKeys.Worker)
            .InvokeAsync(
                id,
                action,
                input,
                body.ValueKind == JsonValueKind.Undefined ? "{}" : body.GetRawText(),
                caller,
                parent,
                createsAsCaller
            );
    }
}

/// <summary>
///     Runs a relayed action's handler on the silo that received it — <see cref="IClusterActionGrain" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A stateless worker, because an action holds nothing between calls.</b> Each call reads
///         everything it needs from its arguments and the silo's registry, so one activation per
///         silo per tenant is enough, and Orleans adds local activations when calls queue up rather
///         than serializing a tenant's actions behind one another.
///     </para>
///     <para>
///         ⚠ <b>It never relays again.</b> It calls <see cref="ActionDispatcher.InvokeHereAsync" />, so a
///         silo that can't reach the cluster either refuses by name, as the dispatcher always has,
///         rather than bouncing the call back through the relay.
///     </para>
/// </remarks>
/// <param name="registry">The silo's provider registry — the same providers the gateway routed with.</param>
/// <param name="actions">The silo's dispatcher, holding the silo's cluster connections and handlers.</param>
/// <param name="manager">
///     The silo's resource manager, whose write path a relayed action's creator writes through. A
///     manager that isn't <see cref="ResourceManagerService" /> (a test double) leaves the handler the
///     refusing creator.
/// </param>
[StatelessWorker]
public sealed class ClusterActionGrain(IProviderRegistry registry, ActionDispatcher actions, IResourceManager manager)
    : Grain, IClusterActionGrain {
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        var tenant = this.GetTenantId()
            ?? throw new InvalidOperationException(
                $"{nameof(ClusterActionGrain)} runs one tenant's actions and was activated with no tenant "
                + "qualification. Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<IClusterActionGrain>(…) "
                + "— ADR-002."
            );

        if (!Guid.TryParseExact(tenant, "D", out tenantId)) {
            throw new InvalidOperationException(
                $"{nameof(ClusterActionGrain)} was activated for tenant '{tenant}', which is not a tenant GUID."
            );
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ResourceId id,
        string action,
        ReconcileInput input,
        string body,
        CallerContext caller,
        ResourceId? parent,
        bool createsAsCaller
    ) {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(caller);

        // ⚠ The connection grain trusts this activation's tenant, so a resource from another tenant run
        // here would reach that tenant's cluster as this one. The caller's own tenant isn't compared:
        // who may act on the resource was the manager's question, and it has been answered.
        if (id.TenantId != tenantId) {
            return Result<string>.Failure(
                ErrorCode.AuthorizationFailed,
                $"An action on '{id.Path}' was relayed to the action worker of tenant {tenantId:D}. The "
                + "worker only runs actions on its own tenant's resources."
            );
        }

        if (!registry.TryGetType(id.Type, out var registration)
            || !registration.TryGetAction(action, out var declared)) {
            // The gateway routed this from a registry built from the same providers, so a miss here is
            // two hosts running different provider sets — HostCompositionTests' territory.
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{id.Type}/{action}' was relayed to a silo whose provider registry doesn't declare it. "
                + "The gateway and the silo have to load the same provider modules."
            );
        }

        // ⚠ Rebuilt here for the caller the gateway's manager bound it to, never for anybody else. Its
        // writes go through this silo's manager, so every step of the write path is checked against the
        // same person the gateway's would have been. CallerResourceCreator's remarks say why no
        // container may hand one out.
        var creator = createsAsCaller && manager is ResourceManagerService here
            ? new CallerResourceCreator(here, id, caller)
            : null;

        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

        return await actions.InvokeHereAsync(id, registration, declared, input, parsed.RootElement, creator, caller, parent);
    }
}
