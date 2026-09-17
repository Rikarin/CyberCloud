using CyberCloud.Core.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Collections.Immutable;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The silo-side consumer of <c>resource-changed</c>: pulls the stream through one durable
///     JetStream consumer and writes each event into its tenant's <c>resource_graph</c> table.
///     docs/plan/08 § The resource-graph projection.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One durable consumer for the fleet, not one per tenant — and "per tenant" is where
///         the row lands, not how it is pulled.</b> A JetStream consumer per tenant needs a list of
///         tenants to create them from, and nothing in this platform holds one (the store's memo says
///         the same about databases). One durable consumer with <c>cc.*.res.&gt;</c> as its filter,
///         pulled by every silo, delivers each message to exactly one silo; the tenant is the second
///         subject token, so the message is routed to its database before its body is decoded, and
///         the access column is read through <c>IGrainFactory.ForTenant</c> under that tenant. A
///         reader that wants one tenant's changes filters the subject; that is what the grammar's
///         token positions are for.
///     </para>
///     <para>
///         ⚠ <b>At-least-once, and the two halves of idempotency it leans on.</b> A message is
///         acknowledged only after the row is written or dropped; a silo that dies in between sees
///         the message again after <c>AckWait</c>. That redelivery, a replay after a restore, and a
///         gateway event that lands after the silo's own all reach
///         <see cref="ClickHouseResourceGraphStore.ApplyAsync" /> with a version the table already
///         holds, and are dropped there — docs/plan/04 § Streams, "a consumer can drop what it has
///         already seen". A payload that is not an event at all is <b>terminated</b>, not
///         re-delivered: redelivering bytes that will never decode is a poison message that blocks
///         the consumer's window forever, and the subject and the reason are logged so the publisher
///         that produced it can be found.
///     </para>
///     <para>
///         ⚠ <b>A failure to reach ClickHouse or the tenant's check grain is a NAK with a delay,
///         and the loop does not die on it.</b> The projection is eventually consistent by design;
///         a ClickHouse that is restarting is a projection that is late, and the message waits on
///         the stream. What would be wrong is a projector that acknowledged what it could not write —
///         that row would be gone from the stream and absent from the table, with nothing left to
///         say so.
///     </para>
/// </remarks>
public sealed class ResourceGraphProjector : BackgroundService {
    /// <summary>How long a silo may hold a message before JetStream offers it to another.</summary>
    public static TimeSpan AckWait { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a message that could not be projected waits before redelivery.</summary>
    public static TimeSpan RetryDelay { get; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the loop waits after the connection or the consumer fails, before it starts over.</summary>
    public static TimeSpan ReconnectDelay { get; } = TimeSpan.FromSeconds(5);

    readonly ResourceGraphOptions options;
    readonly ClickHouseResourceGraphStore store;
    readonly IResourceAccessResolver access;
    readonly IClock clock;
    readonly ILogger<ResourceGraphProjector> logger;

    /// <summary>Creates the projector.</summary>
    /// <param name="options">The bound section, with both halves configured.</param>
    /// <param name="store">Where rows go.</param>
    /// <param name="access">Who may read each resource.</param>
    /// <param name="clock">Stamps <c>projected_at</c>.</param>
    /// <param name="logger">Where a dropped, terminated or failed message is recorded.</param>
    public ResourceGraphProjector(
        ResourceGraphOptions options,
        ClickHouseResourceGraphStore store,
        IResourceAccessResolver access,
        IClock clock,
        ILogger<ResourceGraphProjector> logger
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options;
        this.store = store;
        this.access = access;
        this.clock = clock;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        await using var connection = ResourceChangedLog.Connect(options, "cybercloud-resource-graph-projector");
        var jetStream = new NatsJSContext(connection);

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await ResourceChangedLog.EnsureAsync(jetStream, options, stoppingToken);

                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    options.Stream,
                    new ConsumerConfig(options.Consumer) {
                        FilterSubject = ResourceChangedLog.SubjectFilter,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                        DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                        AckWait = AckWait,
                        // -1 is JetStream's "unlimited": a message that cannot be projected is
                        // redelivered until it can, for the reason the remarks give.
                        MaxDeliver = -1
                    },
                    stoppingToken
                );

                logger.LogInformation(
                    "resource-graph projector consuming {Stream}/{Consumer} on {Filter}",
                    options.Stream,
                    options.Consumer,
                    ResourceChangedLog.SubjectFilter
                );

                await foreach (var message in consumer.ConsumeAsync<byte[]>(cancellationToken: stoppingToken)) {
                    await HandleAsync(message, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) when (exception is NatsException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException) {
                logger.LogWarning(
                    exception,
                    "resource-graph projector lost {Stream} at {Url}; retrying in {Delay}s",
                    options.Stream,
                    options.NatsUrl,
                    ReconnectDelay.TotalSeconds
                );

                try {
                    await Task.Delay(ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException) {
                    return;
                }
            }
        }
    }

    /// <summary>
    ///     Projects one event: resolves who may read it, builds the row, and writes it unless the
    ///     table already holds this version or a later one.
    /// </summary>
    /// <param name="change">The event.</param>
    /// <param name="cancellationToken">Cancels the grain read and the statements.</param>
    /// <returns>
    ///     <see cref="ProjectionOutcome.Applied" /> or <see cref="ProjectionOutcome.Dropped" />, or a
    ///     failure naming the store or the engine that did not answer.
    /// </returns>
    /// <remarks>
    ///     Public so a test can drive the projection without a stream between it and the assertion;
    ///     the consume loop calls exactly this.
    /// </remarks>
    public async Task<Result<ProjectionOutcome>> ProjectAsync(ResourceChangedEvent change, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(change);

        if (change.TenantId == Guid.Empty || change.ResourceId == Guid.Empty) {
            return Result<ProjectionOutcome>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A resource-changed event on '{change.Subject}' names no tenant or no resource, and the "
                + "projection is keyed on both."
            );
        }

        ImmutableArray<string> readers = [];

        // ⚠ A deleted resource has no tuples left to ask about — the operation grain unlinked its
        // parent edge in the same pass — and a row nobody can list is what a tombstone should be.
        if (change.Change != ResourceChangeKind.Deleted) {
            var resolved = await access.ReadersOfAsync(change.TenantId, change.ResourceId, cancellationToken);

            if (resolved.TryGetError(out var accessError)) {
                return Result<ProjectionOutcome>.Failure(
                    accessError.Code,
                    $"The access column for '{change.Subject}' could not be filled: {accessError.Message}"
                );
            }

            readers = resolved.GetValueOrThrow();
        }

        var row = new ResourceGraphRow {
            ResourceId = change.ResourceId,
            TenantId = change.TenantId,
            SubscriptionId = change.SubscriptionId,
            ResourceGroup = change.ResourceGroup,
            Provider = change.Provider,
            Type = change.Type,
            Name = change.Name,
            ApiVersion = change.ApiVersion,
            ProvisioningState = change.ProvisioningState.ToString(),
            Location = change.Location,
            ClusterId = change.ClusterId,
            Tags = change.Tags,
            CreatedAt = change.CreatedAt,
            ModifiedAt = change.ModifiedAt,
            DesiredHash = change.DesiredHash,
            Version = change.Version,
            Change = change.Change.ToString(),
            IsDeleted = change.Change == ResourceChangeKind.Deleted ? (byte)1 : (byte)0,
            Access = readers,
            ProjectedAt = clock.UtcNow
        };

        return await store.ApplyAsync(row, cancellationToken);
    }

    async Task HandleAsync(INatsJSMsg<byte[]> message, CancellationToken cancellationToken) {
        if (!ResourceChangedLog.TryTenantOf(message.Subject, out var subjectTenant)) {
            logger.LogError("resource-changed on '{Subject}' carries no tenant token and was terminated", message.Subject);
            await message.AckTerminateAsync(cancellationToken: cancellationToken);
            return;
        }

        var decoded = ResourceChangedJson.Decode(message.Data ?? []);

        if (decoded.TryGetError(out var decodeError)) {
            logger.LogError("resource-changed on '{Subject}' was terminated: {Message}", message.Subject, decodeError.Message);
            await message.AckTerminateAsync(cancellationToken: cancellationToken);
            return;
        }

        var change = decoded.GetValueOrThrow();

        // ⚠ THE SUBJECT AND THE BODY MUST AGREE ON THE TENANT. The row is keyed on the body's
        // tenant and the access column is read under it; a body that named another tenant than its
        // subject would be a publisher writing into a database its subject does not name. Nothing
        // in this tree can produce that — the sink derives the subject from the body — so it is a
        // forged or corrupted message, and it is terminated and logged rather than redelivered.
        if (change.TenantId != subjectTenant) {
            logger.LogError(
                "resource-changed on '{Subject}' names tenant {BodyTenant} in its body and was terminated",
                message.Subject,
                change.TenantId
            );
            await message.AckTerminateAsync(cancellationToken: cancellationToken);
            return;
        }

        var projected = await ProjectAsync(change, cancellationToken);

        if (projected.TryGetError(out var error)) {
            logger.LogWarning(
                "resource-changed on '{Subject}' at version {Version} was not projected and will be redelivered in {Delay}s: {Message}",
                message.Subject,
                change.Version,
                RetryDelay.TotalSeconds,
                error.Message
            );
            await message.NakAsync(new AckOpts { NakDelay = RetryDelay }, cancellationToken);
            return;
        }

        if (projected.GetValueOrThrow() == ProjectionOutcome.Dropped) {
            logger.LogDebug("resource-changed on '{Subject}' at version {Version} was already projected", message.Subject, change.Version);
        }

        await message.AckAsync(cancellationToken: cancellationToken);
    }
}
