using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Globalization;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     The JetStream stream <c>resource-changed</c> lives on: its subjects, how it is declared, and
///     how a subject is read back.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Declared by whoever touches it first, publisher or projector, with the same
///         configuration — and <c>CreateOrUpdate</c> so the second arrival is a no-op rather than a
///         conflict.</b> The gateway may start before any silo has consumed, and a silo may restart
///         into a cluster whose gateway has been publishing for a week. Either order has to work,
///         and a declaration that lived on one side only would make the other side's start depend
///         on it. The alternative — a stream an operator declares by hand — is the ConfigMap the
///         Monitor workspace publishes and nobody reads.
///     </para>
///     <para>
///         The subject filter is <c>cc.*.res.&gt;</c>: every tenant, every provider, every type.
///         docs/plan/04 § Streams puts the other four namespaces (<c>op</c>, <c>k8s</c>,
///         <c>usage</c>, <c>platform</c>) beside this one under the same <c>cc.</c> root; each is its
///         own stream when it lands, because a stream's retention is one number and an operation's
///         progress and a resource's history do not want the same one.
///     </para>
/// </remarks>
public static class ResourceChangedLog {
    /// <summary>What the stream captures — every tenant's <c>res</c> namespace.</summary>
    public const string SubjectFilter = "cc.*.res.>";

    /// <summary>Which token of the subject is the tenant, zero-based.</summary>
    const int TenantToken = 1;

    /// <summary>
    ///     A NATS connection for one host, from the options. One per process, following
    ///     <c>CyberCloud.ObjectStorage</c>'s one <c>HttpClient</c> per store.
    /// </summary>
    /// <param name="options">The bound section, with <see cref="ResourceGraphOptions.NatsUrl" /> set.</param>
    /// <param name="name">The connection name the server shows, so a <c>nats server</c> listing says who is who.</param>
    public static NatsConnection Connect(ResourceGraphOptions options, string name) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(name);

        return new(
            NatsOpts.Default with {
                Url = options.NatsUrl,
                Name = name,
                // ⚠ A publish while disconnected is buffered rather than refused (the client's
                // default, left explicit here), so a gateway that loses NATS for a second does not
                // drop the events in that second. The sink's caller — the write path — does not fail
                // on a failed publish either, so this is the only place the event gets a second chance.
                PublishTimeoutOnDisconnected = false,
                RetryOnInitialConnect = true
            }
        );
    }

    /// <summary>Declares the stream, or confirms the declaration that is already there.</summary>
    /// <param name="jetStream">The JetStream context.</param>
    /// <param name="options">Names the stream and sets its retention.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<INatsJSStream> EnsureAsync(
        INatsJSContext jetStream,
        ResourceGraphOptions options,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(jetStream);
        ArgumentNullException.ThrowIfNull(options);

        return await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(options.Stream, [SubjectFilter]) {
                Description = "resource-changed — docs/plan/04 § Streams, docs/plan/08 § The resource-graph projection",
                Retention = StreamConfigRetention.Limits,
                Storage = StreamConfigStorage.File,
                Discard = StreamConfigDiscard.Old,
                MaxAge = options.Retention,
                // ⚠ The publisher's message id is `{resourceId:N}.{version}`, so a gateway that
                // retries a publish it did not see acknowledged is one message on the stream and
                // not two. Two minutes is JetStream's default and covers any retry the sink makes.
                DuplicateWindow = TimeSpan.FromMinutes(2)
            },
            cancellationToken
        );
    }

    /// <summary>
    ///     The message id JetStream de-duplicates on: the resource and the version, so one change is
    ///     one message however many times its publish is retried.
    /// </summary>
    /// <param name="change">The event.</param>
    public static string MessageId(ResourceChangedEvent change) {
        ArgumentNullException.ThrowIfNull(change);
        return string.Create(CultureInfo.InvariantCulture, $"{change.ResourceId:N}.{change.Version}");
    }

    /// <summary>
    ///     Reads the tenant out of a subject, so a consumer can route before it decodes the body.
    /// </summary>
    /// <param name="subject">A subject of the form <c>cc.{tenant:N}.res.…</c>.</param>
    /// <param name="tenantId">The tenant, when the subject carries one.</param>
    /// <returns><c>true</c> if the second token is a GUID in its 32-digit form.</returns>
    public static bool TryTenantOf(string subject, out Guid tenantId) {
        tenantId = Guid.Empty;

        if (string.IsNullOrEmpty(subject)) {
            return false;
        }

        var tokens = subject.Split('.');

        return tokens.Length > TenantToken + 1
            && string.Equals(tokens[0], "cc", StringComparison.Ordinal)
            && string.Equals(tokens[TenantToken + 1], "res", StringComparison.Ordinal)
            && Guid.TryParseExact(tokens[TenantToken], "N", out tenantId);
    }
}
