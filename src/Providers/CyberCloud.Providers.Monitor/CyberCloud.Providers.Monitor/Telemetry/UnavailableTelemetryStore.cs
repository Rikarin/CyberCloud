using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Telemetry;

/// <summary>
///     The <see cref="ITelemetryStore" /> a host with no telemetry ClickHouse configured gets: it
///     refuses every view, naming the section to set.
/// </summary>
/// <remarks>
///     A refusal and not an empty result, for the reason <c>UnavailableAlertQuerySeam</c> gives: an
///     empty view says "no requests", which is a claim about the tenant's application the platform
///     cannot make.
/// </remarks>
public sealed class UnavailableTelemetryStore : ITelemetryStore {
    /// <inheritdoc />
    public Task<Result<ImmutableArray<JsonObject>>> QueryAsync(
        TelemetryQuery query,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ImmutableArray<JsonObject>>.Failure(
                ErrorCode.InternalError,
                "No telemetry store is configured on this host, so the view cannot be read. Set "
                + $"{MonitorTelemetryOptions.SectionName}:ClickHouseEndpoint, ClickHouseUser and "
                + "ClickHousePassword."
            )
        );
}
