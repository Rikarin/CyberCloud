using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.ResourceManager.Terminals;

/// <summary>One tenant's live terminal sessions, leased — <see cref="ITerminalSessionLimitGrain" />.</summary>
/// <remarks>
///     ⚠ <b>No storage, and losing the activation is the safe direction.</b> A lost count admits more
///     sessions until the live ones renew, at most one lease later; a stored count that outlived its
///     sessions would refuse a tenant shells it is not running. The first is a short over-admission
///     bounded by the pods' own hard caps, the second an outage — so the table is memory.
/// </remarks>
/// <param name="clock">What leases are measured against.</param>
public sealed class TerminalSessionLimitGrain(IClock clock) : Grain, ITerminalSessionLimitGrain {
    readonly Dictionary<string, DateTimeOffset> leases = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = this.GetTenantId()
            ?? throw new InvalidOperationException(
                $"{nameof(TerminalSessionLimitGrain)} counts one tenant's sessions and was activated with "
                + "no tenant qualification — ADR-002."
            );

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result> AdmitAsync(string sessionId) {
        Prune();

        if (!leases.ContainsKey(sessionId) && leases.Count >= TerminalSessionLimits.LiveSessionsPerTenant) {
            return Task.FromResult(
                Result.Failure(
                    ErrorCode.QuotaExceeded,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"This tenant already has {leases.Count} cloud shells running, which is the limit. "
                    )
                    + "Exit one, or terminate a console nobody is using; an idle shell is reclaimed on its own."
                )
            );
        }

        leases[sessionId] = clock.UtcNow + TerminalSessionLimits.Lease;
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string sessionId) {
        leases.Remove(sessionId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> LiveAsync() {
        Prune();
        return Task.FromResult(leases.Count);
    }

    void Prune() {
        var now = clock.UtcNow;

        foreach (var (session, expires) in leases.ToArray()) {
            if (expires <= now) {
                leases.Remove(session);
            }
        }
    }
}
