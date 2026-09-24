using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Terminals;

/// <summary><see cref="ITerminalSessions" /> over <see cref="ITerminalSessionGrain" />.</summary>
/// <param name="grains">The grain factory — a silo's, or the gateway's Orleans client.</param>
public sealed class GrainTerminalSessions(IGrainFactory grains) : ITerminalSessions {
    /// <inheritdoc />
    public Task<Result> OpenAsync(
        TerminalSessionSpec spec,
        CallerContext owner,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        // ⚠ Qualified by the RESOURCE's tenant, which the manager resolved from the request path it
        // authorized — and the grain refuses an owner whose tenant differs, so neither half can be
        // steered by the other.
        return grains
            .ForTenant(spec.Resource.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITerminalSessionGrain>(TerminalSessionKeys.Session(spec.PodUid))
            .OpenAsync(spec, owner);
    }
}
