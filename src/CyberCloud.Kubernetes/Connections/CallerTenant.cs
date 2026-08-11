using System.Globalization;

namespace CyberCloud.Kubernetes.Connections;

/// <summary>Which kind of caller made the current grain call.</summary>
public enum CallerKind
{
    /// <summary>
    ///     Nothing established the caller. ⚠ Treated as <b>refused</b>, never as trusted — see
    ///     <see cref="CallerTenant" />.
    /// </summary>
    Unknown = 0,

    /// <summary>A grain qualified to a tenant.</summary>
    Tenant = 1,

    /// <summary>A grain with no tenant qualification — another platform grain.</summary>
    NullTenant = 2,

    /// <summary>
    ///     Not a grain: a cluster client, the gateway, a test. Orleans' own separation filter returns
    ///     early for these, so they carry no tenant at all.
    /// </summary>
    Client = 3,
}

/// <summary>
///     The tenant on whose behalf the current grain call is running.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is ambient, not a parameter, and that is the entire security argument.</b>
///         <c>IClusterConnectionGrain</c> is null-tenant (docs/plan/06 § Grain keys), so its key
///         carries no tenant and Orleans' separation cannot decide anything for it. The grain must
///         therefore ask "who is calling" — and if the answer were a method parameter, tenant B would
///         simply pass tenant A's GUID. A parameter is a claim; this is a fact, derived from
///         <c>IGrainCallContext.SourceId</c>, which is the calling <i>activation's</i> identity and
///         is set by the runtime.
///     </para>
///     <para>
///         ⚠ <b><see cref="AsyncLocal{T}" /> and deliberately not <c>RequestContext</c>.</b>
///         <c>RequestContext</c> is serialized and sent with the message, so a caller can put
///         whatever it likes in it; we would be overwriting an attacker-controlled value and relying
///         on ordering. An <see cref="AsyncLocal{T}" /> does not cross a network boundary at all, so
///         there is no value to overwrite and no ordering to get wrong. It is set by
///         <see cref="ClusterConnectionTenantFilter" /> immediately before the invocation and flows
///         into it with the execution context.
///     </para>
///     <para>
///         <b>It fails closed.</b> If the filter is not registered, every read returns
///         <see cref="CallerKind.Unknown" /> and the grain refuses every call. A missing registration
///         is therefore a loud, total outage of one grain type rather than a silent removal of its
///         only tenancy check — which is the direction a security control must fail in.
///         <c>ClusterConnectionTenancyTests</c> asserts exactly that.
///     </para>
/// </remarks>
public readonly record struct CallerTenant
{
    static readonly AsyncLocal<CallerTenant> Ambient = new();

    CallerTenant(CallerKind kind, Guid tenantId)
    {
        Kind = kind;
        TenantId = tenantId;
    }

    /// <summary>What kind of caller.</summary>
    public CallerKind Kind { get; }

    /// <summary>The tenant, when <see cref="Kind" /> is <see cref="CallerKind.Tenant" />.</summary>
    public Guid TenantId { get; }

    /// <summary>Nothing is known about the caller. The default, and refused.</summary>
    public static CallerTenant Unknown => default;

    /// <summary>A caller that is not a grain.</summary>
    public static CallerTenant Client { get; } = new(CallerKind.Client, Guid.Empty);

    /// <summary>A caller that is a grain with no tenant qualification.</summary>
    public static CallerTenant NullTenant { get; } = new(CallerKind.NullTenant, Guid.Empty);

    /// <summary>A caller that is a grain in <paramref name="tenantId" />.</summary>
    /// <param name="tenantId">The calling grain's tenant.</param>
    public static CallerTenant Of(Guid tenantId) => new(CallerKind.Tenant, tenantId);

    /// <summary>
    ///     Interprets <c>Orleans.Multitenant</c>'s tenant-id string, which may be
    ///     <see langword="null" /> or the literal <c>"Null"</c>.
    /// </summary>
    /// <param name="tenantId">The value from <c>GrainId.GetTenantId()</c>.</param>
    /// <remarks>
    ///     ⚠ <c>Guid.Parse</c> is not enough. <c>Orleans.Multitenant</c> uses the literal string
    ///     <c>"Null"</c> for the null tenant (<c>MultitenantStorageOptions.TenantIdForNullTenant</c>)
    ///     and <c>Guid.Parse("Null")</c> throws — the same live trap docs/plan/05 § Storage provider
    ///     wiring documents and <c>NullTenantGrainTests</c> pins. A non-GUID, non-<c>"Null"</c> value
    ///     is not guessed at: it becomes <see cref="Unknown" /> and is refused.
    /// </remarks>
    public static CallerTenant FromTenantId(string? tenantId)
    {
        if (tenantId is null)
        {
            return NullTenant;
        }

        if (string.Equals(tenantId, NullTenantSentinel, StringComparison.Ordinal))
        {
            return NullTenant;
        }

        return Guid.TryParseExact(tenantId, "D", out var parsed) ? Of(parsed) : Unknown;
    }

    /// <summary>
    ///     <c>Orleans.Multitenant</c>'s literal for "no tenant" —
    ///     <c>MultitenantStorageOptions.TenantIdForNullTenant</c>'s default.
    /// </summary>
    public const string NullTenantSentinel = "Null";

    /// <summary>The caller of the grain call currently executing.</summary>
    public static CallerTenant Current => Ambient.Value;

    /// <summary>
    ///     Sets the ambient caller for the duration of the current execution context. Called by
    ///     <see cref="ClusterConnectionTenantFilter" /> and by tests.
    /// </summary>
    /// <param name="caller">Who is calling.</param>
    /// <returns>A scope that restores the previous value.</returns>
    public static IDisposable Enter(CallerTenant caller) => new Scope(caller);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        CallerKind.Tenant => "tenant " + TenantId.ToString("D", CultureInfo.InvariantCulture),
        CallerKind.NullTenant => "a null-tenant platform grain",
        CallerKind.Client => "a client (not a grain)",
        _ => "an unidentified caller",
    };

    sealed class Scope : IDisposable
    {
        readonly CallerTenant previous;
        bool disposed;

        internal Scope(CallerTenant caller)
        {
            previous = Ambient.Value;
            Ambient.Value = caller;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Ambient.Value = previous;
        }
    }
}
