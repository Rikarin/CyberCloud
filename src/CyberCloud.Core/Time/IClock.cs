namespace CyberCloud.Core.Time;

/// <summary>
///     The one source of "now". docs/plan/03:60 lists the clock under <c>CyberCloud.Core</c>.
/// </summary>
/// <remarks>
///     <para>
///         Everything that stamps time — <c>createdAt</c>/<c>modifiedAt</c> (docs/plan/06:173), the
///         5-minute index claim lease (docs/plan/06:101), the reconcile backoff schedule
///         (docs/plan/08:77), the 60-minute operation timeout, the 30-day tenant tombstone, the
///         7-day soft delete — takes this rather than <see cref="DateTimeOffset.UtcNow" />. A lease
///         you cannot advance the clock past is a lease you cannot test, and every one of those
///         durations is measured in minutes to days.
///     </para>
///     <para>
///         ⚠ <b>UTC only.</b> There is no local-time member and there will not be one: a control
///         plane whose silos disagree about the time zone produces leases that expire early in one
///         region and late in another.
///     </para>
///     <para>
///         This wraps <see cref="TimeProvider" /> rather than replacing it — see
///         <see cref="SystemClock" />. The reason for the extra interface is that
///         <see cref="TimeProvider" /> also carries timers and time-zone conversion, and a grain
///         that can start a timer outside Orleans' scheduler is a grain that breaks its own
///         single-threaded contract. Narrowing the surface to one property is the point.
///     </para>
/// </remarks>
public interface IClock {
    /// <summary>The current instant, in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
