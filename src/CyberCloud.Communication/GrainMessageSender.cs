using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Communication;

/// <summary>
///     The <see cref="IMessageSender" /> that talks to grains. What identity, monitor and billing
///     hold.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every <c>GetGrain</c> here is qualified with <c>Orleans.Multitenant</c>'s
///         <c>ForTenant</c>, and CC1006 is what makes that non-negotiable.</b> A gateway, a host or a
///         notification worker is not a grain, so <c>Orleans.Multitenant</c>'s call filter never sees
///         the caller and an unqualified reference would be outside tenant separation entirely. Doing
///         it in one class means the three consuming modules do not each have to get it right — the
///         same arrangement <c>GrainUsageEmitter</c> has, for the same reason.
///     </para>
///     <para>
///         There is deliberately no caching, no batching and no retry here. A send is one grain call
///         and the grain is where idempotency, limits and retry live; a second retry loop in front of
///         it would be a second place that can send twice.
///     </para>
/// </remarks>
public sealed class GrainMessageSender(IGrainFactory grains) : IMessageSender {
    /// <inheritdoc />
    public Task<Result<MessageSnapshot>> SendAsync(
        Guid tenantId,
        SendRequest request,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) {
            return Task.FromResult(
                Result<MessageSnapshot>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A send carries a client-supplied idempotency key — docs/plan/17 § The parts that "
                    + "are actually the work. Derive it from the thing being notified about, never "
                    + "from the attempt: Guid.NewGuid() per call means every timeout sends twice."
                )
            );
        }

        if (request.ServiceId == Guid.Empty) {
            return Task.FromResult(
                Result<MessageSnapshot>.Failure(
                    ErrorCode.InvalidResourceId,
                    "A send names the communication service resource it goes through."
                )
            );
        }

        return For(tenantId)
            .GetGrain<IMessageGrain>(CommunicationGrainKeys.Message(request.ServiceId, request.IdempotencyKey))
            .SendAsync(request);
    }

    /// <inheritdoc />
    public Task<Result<MessageSnapshot>> GetStatusAsync(
        Guid tenantId,
        Guid serviceId,
        string idempotencyKey,
        CancellationToken cancellationToken = default
    ) {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) {
            return Task.FromResult(
                Result<MessageSnapshot>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "A status read names the idempotency key the message was sent under."
                )
            );
        }

        return For(tenantId)
            .GetGrain<IMessageGrain>(CommunicationGrainKeys.Message(serviceId, idempotencyKey))
            .GetAsync();
    }

    /// <summary>
    ///     ⚠ <c>"D"</c> and <see cref="CultureInfo.InvariantCulture" />, matching every other
    ///     tenant-qualified call site in the tree. Two spellings of a tenant id are two tenants to
    ///     <c>Orleans.Multitenant</c>, which encodes the string it is given.
    /// </summary>
    TenantGrainFactory For(Guid tenantId) => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));
}
