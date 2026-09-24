using CyberCloud.Providers.Monitor.Contracts;
using System.Collections.Concurrent;

namespace CyberCloud.Providers.Monitor.Conformance;

/// <summary>
///     An <see cref="IMonitorAccounts" /> in a dictionary, for a workspace reconciler built outside a
///     silo: first claim wins, and a claim is never released.
/// </summary>
/// <remarks>
///     ⚠ <b>Only for <see cref="MonitorCase" />'s <c>CreateReconciler</c> and the Gateway suite's own
///     checks.</b> A reconciler the harness's silo resolves claims through the real
///     <c>MonitorAccountGrain</c>; this one stands in where no grain factory reaches, which is the
///     direct drives of the conformance suites and the k3s lane. Its claims aren't the silo's, so a
///     workspace the silo already claimed claims again here and gets the same "yes".
/// </remarks>
public sealed class InMemoryMonitorAccounts : IMonitorAccounts {
    readonly ConcurrentDictionary<uint, Guid> holders = new();

    /// <inheritdoc />
    public Task<bool> ClaimAsync(uint accountId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(holders.GetOrAdd(accountId, workspaceId) == workspaceId);

    /// <inheritdoc />
    public Task<bool> IsHeldByAsync(uint accountId, Guid workspaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(holders.TryGetValue(accountId, out var holder) && holder == workspaceId);
}
