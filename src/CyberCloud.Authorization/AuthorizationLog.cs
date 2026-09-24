using Microsoft.Extensions.Logging;

namespace CyberCloud.Authorization;

/// <summary>
///     The authorization engine's audit events — the start and the end of a time-bounded grant, and
///     the sweep that records the end. docs/plan/07 § Time-bounded relations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An audit event here is a structured log event, and that's the platform's audit
///         trail rather than a stand-in for one.</b> docs/plan/11 § Auditing sends every audit
///         event to the telemetry pipeline as structured fields, to ClickHouse and not to a SQL
///         table, because the question people ask is "everything for this principal in this window
///         across every host". <c>IdentityLog</c> is the same mechanism for sign-in; these are its
///         authorization counterparts, in the 1700 range.
///     </para>
///     <para>
///         ⚠ <b>Every placeholder is a GUID, a tuple, an instant, or a version</b>, so docs/plan/11
///         § Auditing's PII rule holds by construction: a tuple names object and subject ids —
///         GUIDs in <c>N</c> form, or a resource group's name — and never an email, a display name,
///         or an address.
///     </para>
/// </remarks>
public static partial class AuthorizationLog {
    /// <summary>A tuple with an expiry was written: a time-bounded grant started or changed.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="tuple">The tuple, in <c>object#relation@subject</c> notation.</param>
    /// <param name="expiresOn">When it stops granting.</param>
    /// <param name="version">The relation version the write produced — the token that covers it.</param>
    [LoggerMessage(
        EventId = 1700,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId} wrote {Tuple}, granting until {ExpiresOn:O}, at relation version {Version}."
    )]
    public static partial void ExpiringTupleWritten(
        ILogger logger,
        Guid tenantId,
        string tuple,
        DateTimeOffset expiresOn,
        long version
    );

    /// <summary>The sweep deleted a tuple whose expiry had passed — the end of a time-bounded grant.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="tuple">The tuple, in <c>object#relation@subject</c> notation.</param>
    /// <param name="expiresOn">When it stopped granting — the instant every check started denying.</param>
    /// <param name="sweptAt">When the sweep removed it, which is later and is only housekeeping.</param>
    /// <param name="version">The relation version the delete produced.</param>
    [LoggerMessage(
        EventId = 1701,
        Level = LogLevel.Information,
        Message = "Tenant {TenantId}'s {Tuple} expired at {ExpiresOn:O} and was removed at {SweptAt:O}, at relation version {Version}."
    )]
    public static partial void ExpiredTupleRemoved(
        ILogger logger,
        Guid tenantId,
        string tuple,
        DateTimeOffset? expiresOn,
        DateTimeOffset sweptAt,
        long version
    );

    /// <summary>The sweep couldn't delete one expired tuple. It stays registered for the next pass.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="tuple">The tuple.</param>
    /// <param name="expiresOn">When it stopped granting.</param>
    /// <param name="reason">The store's own failure message.</param>
    [LoggerMessage(
        EventId = 1702,
        Level = LogLevel.Warning,
        Message = "Tenant {TenantId}'s {Tuple} expired at {ExpiresOn:O} and could not be removed yet: {Reason}"
    )]
    public static partial void ExpiredTupleSweepFailed(
        ILogger logger,
        Guid tenantId,
        string tuple,
        DateTimeOffset? expiresOn,
        string reason
    );

    /// <summary>A whole sweep pass failed before it reached the register. The reminder stays armed.</summary>
    /// <param name="logger">The sink.</param>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="reason">The failure message.</param>
    [LoggerMessage(
        EventId = 1703,
        Level = LogLevel.Warning,
        Message = "Tenant {TenantId}'s expiry sweep failed and will run again on the next tick: {Reason}"
    )]
    public static partial void ExpirySweepFailed(ILogger logger, Guid tenantId, string reason);
}
