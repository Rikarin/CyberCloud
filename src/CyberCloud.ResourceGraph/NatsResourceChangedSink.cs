using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The <see cref="IResourceChangedSink" /> that puts an event on NATS JetStream, under
///     <c>cc.{tenant}.res.{provider}.{type}.{id}</c>, with the resource's version as the message id.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Never throws out of <see cref="PublishAsync" />, because the write path treats a
///         failure as a warning and an exception as a 500.</b> <c>IResourceChangedSink</c>'s contract
///         is a <see cref="Result" /> — docs/plan/08 § The resource-graph projection makes the
///         projection eventually consistent, so a create is not refused because a list will lag — and
///         a NATS client that cannot reach its server throws. Every path here turns that into a
///         failure the caller logs, with the subject in the message so the operator knows which event
///         is missing from the projection — and the server's address without its credential, since
///         the message is headed for a log line.
///     </para>
///     <para>
///         ⚠ <b>The stream is declared lazily and once.</b> <see cref="ResourceChangedLog.EnsureAsync" />
///         runs on the first publish rather than at construction, because construction happens in a
///         composition root that must not do I/O, and because the gateway may compose before the NATS
///         container has answered its first ping. A failed declaration is retried on the next
///         publish; a successful one is remembered.
///     </para>
///     <para>
///         The message id is <c>{resourceId:N}.{version}</c>, and the stream's duplicate window makes
///         a retried publish one message. That is the publisher's half of idempotency; the projector's
///         half is dropping any version at or below the one it holds, and the two together are what
///         docs/plan/04 § Streams asks of "every stream consumer".
///     </para>
/// </remarks>
public sealed class NatsResourceChangedSink : IResourceChangedSink, IAsyncDisposable {
    readonly NatsConnection connection;
    readonly NatsJSContext jetStream;
    readonly ResourceGraphOptions options;
    readonly ILogger<NatsResourceChangedSink> logger;
    readonly SemaphoreSlim declaring = new(1, 1);
    bool declared;

    /// <summary>Creates a sink over one connection it owns.</summary>
    /// <param name="options">The bound section, with the NATS URL set.</param>
    /// <param name="logger">Where a refused publish is recorded.</param>
    public NatsResourceChangedSink(ResourceGraphOptions options, ILogger<NatsResourceChangedSink> logger) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.logger = logger;
        connection = ResourceChangedLog.Connect(options, "cybercloud-resource-changed-publisher");
        jetStream = new NatsJSContext(connection);
    }

    /// <inheritdoc />
    public async Task<Result> PublishAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(change);

        var subject = change.Subject;

        // ⚠ BOUNDED, BECAUSE THE CALLER IS A REQUEST. The connection retries its initial connect
        // and buffers a publish while disconnected, both of which are right for the event and wrong
        // for the PUT waiting on it: a NATS that is down would otherwise hold every write open. The
        // caller's token is the request's; this one adds the sink's own ceiling on top.
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(options.PublishTimeout);

        try {
            await EnsureDeclaredAsync(bounded.Token);

            var acknowledged = await jetStream.PublishAsync(
                subject,
                ResourceChangedJson.Encode(change),
                opts: new NatsJSPubOpts { MsgId = ResourceChangedLog.MessageId(change) },
                cancellationToken: bounded.Token
            );

            // ⚠ A DUPLICATE IS A SUCCESS, AND EnsureSuccess DISAGREES. The client's EnsureSuccess
            // throws NatsJSDuplicateMessageException for an ack with Duplicate set, treating the
            // stream's own de-duplication as a refusal; here it is the outcome the message id exists
            // for — the event is on the stream already, which is exactly what the caller wanted. So
            // Duplicate is checked first, and EnsureSuccess is left with the genuine refusals (a
            // subject the stream does not capture, a full stream under Discard.New), which the catch
            // below turns into the Result the caller expects. ProjectionRoundTripTests
            // .ARetriedPublishIsOneMessageOnTheStream is what found the order mattered.
            if (acknowledged.Duplicate) {
                logger.LogDebug("resource-changed {Subject} at version {Version} was already on the stream", subject, change.Version);
                return Result.Success;
            }

            acknowledged.EnsureSuccess();

            return Result.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (OperationCanceledException) {
            return Result.Failure(
                ErrorCode.InternalError,
                $"Publishing resource-changed on '{subject}' to NATS at '{ResourceChangedLog.RedactedUrl(options.NatsUrl)}' did not complete "
                + $"within {options.PublishTimeout.TotalSeconds:0}s. The write stands; the resource-graph "
                + "projection is behind by this event until the resource changes again."
            );
        }
        catch (Exception exception) when (exception is NatsException or TimeoutException or InvalidOperationException) {
            return Result.Failure(
                ErrorCode.InternalError,
                $"Publishing resource-changed on '{subject}' to NATS at '{ResourceChangedLog.RedactedUrl(options.NatsUrl)}' failed: "
                + $"{exception.GetType().Name}: {exception.Message}. The write stands; the resource-graph "
                + "projection is behind by this event until the resource changes again."
            );
        }
    }

    async Task EnsureDeclaredAsync(CancellationToken cancellationToken) {
        if (declared) {
            return;
        }

        await declaring.WaitAsync(cancellationToken);

        try {
            if (declared) {
                return;
            }

            await ResourceChangedLog.EnsureAsync(jetStream, options, cancellationToken);
            declared = true;
        }
        finally {
            declaring.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        declaring.Dispose();
        await connection.DisposeAsync();
    }
}
