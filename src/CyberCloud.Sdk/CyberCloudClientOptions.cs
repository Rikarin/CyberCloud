namespace CyberCloud.Sdk;

/// <summary>Options for <see cref="CyberCloudClient" />.</summary>
public class CyberCloudClientOptions {
    /// <summary>
    ///     The api-versions this SDK build can speak. docs/plan/10 § API versioning: <i>"Versions are
    ///     dates and immutable"</i> — a member is never removed and never re-pointed.
    /// </summary>
    /// <remarks>
    ///     ⚠ The generator adds a member per published api-version and the emitted clients select their
    ///     route table from it. Nothing here may be renamed: the enum member name is part of the SDK's
    ///     binary contract in the same way the date is part of the API's.
    /// </remarks>
    public enum ServiceVersion {
        /// <summary>The first published api-version — <c>openapi/2026-08-01.json</c>.</summary>
        V2026_08_01 = 1,
    }

    /// <summary>The newest api-version this build knows. New clients default to it.</summary>
    internal const ServiceVersion Latest = ServiceVersion.V2026_08_01;

    /// <summary>Creates options pinned to an api-version.</summary>
    /// <param name="version">
    ///     The api-version. Defaults to the newest this build knows. ⚠ Pin it explicitly in anything
    ///     long-lived: an SDK upgrade otherwise moves the api-version underneath a deployed
    ///     application, which is what docs/plan/10 § API versioning's immutable dates exist to prevent.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version" /> is not a member.</exception>
    public CyberCloudClientOptions(ServiceVersion version = Latest) {
        ApiVersion = version switch {
            ServiceVersion.V2026_08_01 => "2026-08-01",
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown service version."),
        };

        Version = version;
    }

    /// <summary>The api-version this client sends.</summary>
    public ServiceVersion Version { get; }

    /// <summary>
    ///     The api-version as it appears on the wire — <c>2026-08-01</c>. Sent as a query parameter on
    ///     every request by <see cref="ApiVersionHandler" />.
    /// </summary>
    public string ApiVersion { get; }

    /// <summary>The scopes requested from the <see cref="TokenCredential" />.</summary>
    public IList<string> Scopes { get; } = [CyberCloudScopes.Default];

    /// <summary>How hard the pipeline retries.</summary>
    public RetryOptions Retry { get; } = new();

    /// <summary>
    ///     A transport to use instead of the default — the seam this SDK is tested through. Nothing in
    ///     the SDK's own test suite opens a socket.
    /// </summary>
    public HttpMessageHandler? Transport { get; set; }

    /// <summary>
    ///     Overrides the interval between long-running-operation polls. <see langword="null" />, the
    ///     default, honours the <c>Retry-After</c> the service sends with each poll.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Setting this ignores the service's own back-pressure signal, so it is a debugging and
    ///         testing control rather than a tuning knob.
    ///     </para>
    ///     <para>
    ///         <see cref="TimeSpan.Zero" /> polls as fast as the service answers. The SDK's own tests
    ///         use it so that a three-poll operation costs three round trips and no wall-clock time.
    ///     </para>
    /// </remarks>
    public TimeSpan? PollingInterval { get; set; }
}

/// <summary>The OAuth scopes the platform's resource API accepts.</summary>
public static class CyberCloudScopes {
    /// <summary>
    ///     The resource API's scope. docs/plan/11 § Protocol puts <c>aud</c> on the API and the
    ///     caller's permissions outside the token — <i>"Roles and permissions are not in the token"</i>
    ///     — so there is one scope rather than one per permission.
    /// </summary>
    public const string Default = "https://api.cybercloud.io/.default";
}
