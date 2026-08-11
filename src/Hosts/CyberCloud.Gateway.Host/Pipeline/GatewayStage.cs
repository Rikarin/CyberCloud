namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     The nine stages of docs/plan/10 § Request pipeline, numbered as the document numbers them.
/// </summary>
/// <remarks>
///     ⚠ <b>The numbers are the contract, not decoration.</b> docs/plan/10 § Request pipeline:
///     <i>"Order matters and each step is here for a named reason … Steps 5 and 8 are the
///     load-bearing ones."</i> <see cref="GatewayTrace" /> compares a request's actual sequence
///     against <see cref="GatewayTrace.Canonical" />, so moving a stage fails a test rather than a
///     review — the same instrument <c>WriteTrace</c> uses for the write path.
/// </remarks>
enum GatewayStage {
    /// <summary>No stage. The value a trace reports before anything ran.</summary>
    None = 0,

    /// <summary><c>x-ms-correlation-request-id</c> in, <c>x-cybercloud-request-id</c> out.</summary>
    Correlation = 1,

    /// <summary>The token becomes a caller. See <c>ICallerContextResolver</c>.</summary>
    Authenticate = 2,

    /// <summary>
    ///     The token's <c>tid</c> becomes the tenant, and a path that disagrees becomes a
    ///     <c>404</c>. ⚠ A security boundary, not a routing convenience.
    /// </summary>
    ResolveTenant = 3,

    /// <summary>Home region, or one proxy hop to it. Never two.</summary>
    RegionRouting = 4,

    /// <summary>Redis counters. ⚠ Never touches a grain.</summary>
    RateLimit = 5,

    /// <summary>Path plus <c>api-version</c> to a provider and a type, from the registry.</summary>
    Route = 6,

    /// <summary>What the gateway can decide about a body without a grain call.</summary>
    Validate = 7,

    /// <summary>To the resource manager, which owns authorization, quota and locks.</summary>
    Dispatch = 8,

    /// <summary><see cref="Result" /> to a status code and the one error shape.</summary>
    ShapeResponse = 9
}
