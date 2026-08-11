using CyberCloud.ResourceManager.Contracts.Registry;

namespace CyberCloud.Gateway.Host;

/// <summary>
///     What a gateway pod needs to know about itself.
/// </summary>
/// <remarks>
///     ⚠ <b>Every value here is <i>configuration</i>, never a request input.</b>
///     <see cref="PublicBaseUri" /> in particular is not <c>Request.Host</c>: behind Envoy the Host
///     header is whatever the client sent, and a caller who can set it can make the platform hand
///     every <c>Azure-AsyncOperation</c> URL out pointing at a host they control.
/// </remarks>
sealed class GatewayOptions {
    /// <summary>The configuration section, <c>CyberCloud:Gateway</c>.</summary>
    public const string SectionName = "CyberCloud:Gateway";

    /// <summary>
    ///     The current api-version, named in the <c>400</c> a missing <c>?api-version=</c> gets.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a default. docs/plan/10 § API versioning: the parameter is required and there is no
    ///     "latest". This value is only ever put into an error message.
    /// </remarks>
    public ApiVersion CurrentApiVersion { get; init; } = ApiVersion.Parse("2026-08-01");

    /// <summary>The region this pod runs in. Empty means "serve everything here".</summary>
    public string Region { get; init; } = "";

    /// <summary>The public origin, for <c>Azure-AsyncOperation</c>.</summary>
    public string PublicBaseUri { get; init; } = "https://api.cybercloud.io";

    /// <summary>The first <c>Retry-After</c> on an operation poll. docs/plan/10 gives 10 seconds.</summary>
    public int OperationRetryAfterSeconds { get; init; } = 10;

    /// <summary>
    ///     The largest request body accepted, in bytes. Default 1 MiB.
    /// </summary>
    /// <remarks>
    ///     A resource's desired state is a description, not a payload. The cap is here rather than
    ///     only in Kestrel because the tenant check at stage 3 buffers the body, and a stage that
    ///     runs before rate limiting must have a bound that does not depend on the server's.
    /// </remarks>
    public int MaxBodyBytes { get; init; } = 1024 * 1024;
}
