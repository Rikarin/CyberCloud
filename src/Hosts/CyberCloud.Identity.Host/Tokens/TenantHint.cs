using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     Turns the <c>tenant</c> a request names into a tenant id, through the platform directory and
///     before any per-tenant grain is touched. docs/plan/11 § Sign-up and tenant creation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The directory check is what makes a caller-chosen tenant safe to accept.</b>
///         <see cref="IdentityHostOptions" /> used to refuse the idea because an unauthenticated
///         caller could "choose which tenant's lockout counters and email index it probes" — and
///         that is exactly what a per-tenant grain keyed from a request string would be: an
///         activation table filled by whoever is probing. So nothing here builds a tenant-qualified
///         key from the hint. A GUID or a slug goes to <see cref="ITenantDirectoryGrain" /> — one
///         platform grain, one activation, whatever is asked — and only a tenant the directory knows
///         and has not retired comes back. A made-up value activates nothing.
///     </para>
///     <para>
///         ⚠ <b>Absent means the fallback, and the fallback is environment-gated.</b>
///         <see cref="IdentityHostOptions.TenantId" /> when a deployment set it; the platform tenant
///         only when the environment is Development, because that is what lets a fresh
///         <c>dotnet run</c> sign in before any tenant exists; and "no tenant" anywhere else, so a
///         production request that names nothing is refused rather than quietly filed under the one
///         tenant whose members reach across all the others.
///         <c>TokenApiTests.TheDevelopmentDefaultTenantAppliesOnlyInDevelopment</c> pins the gate.
///     </para>
///     <para>
///         A hint that names the fallback tenant itself resolves without a lookup, because the
///         platform tenant is never in the directory and a portal that remembered it would otherwise
///         be refused for naming the tenant this host would have chosen anyway.
///     </para>
/// </remarks>
public sealed class TenantHint(
    IGrainFactory grains,
    IOptions<IdentityHostOptions> options,
    IHostEnvironment environment
) {
    /// <summary>The request parameter and body field the hint travels in.</summary>
    public const string ParameterName = "tenant";

    readonly IdentityHostOptions options = options.Value;

    /// <summary>
    ///     The tenant a request that names none gets, or <see langword="null" /> when there is none.
    /// </summary>
    public Guid? Default => options.TenantId ?? (environment.IsDevelopment() ? Guid.Empty : null);

    /// <summary>
    ///     Resolves a hint.
    /// </summary>
    /// <param name="value">
    ///     The <c>tenant</c> value as it arrived: a GUID in <c>D</c> or <c>N</c> form, a slug, empty,
    ///     or <see langword="null" />.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>
    ///     The tenant id, or <see langword="null" /> for "no tenant": an unknown value, a retired
    ///     tenant, a value that is neither a GUID nor a slug, or nothing named and no fallback.
    /// </returns>
    public async Task<Guid?> ResolveAsync(string? value, CancellationToken cancellationToken = default) {
        var hint = value?.Trim() ?? string.Empty;

        if (hint.Length == 0) {
            return Default;
        }

        if (Guid.TryParseExact(hint, "D", out var id) || Guid.TryParseExact(hint, "N", out id)) {
            if (id == Default) {
                return id;
            }

            cancellationToken.ThrowIfCancellationRequested();

            return Accept(await Directory.LookupAsync(id));
        }

        // ⚠ Refused before the grain call rather than by it. A slug is DNS-1123 (docs/plan/06
        // § Identifiers), and a string that is not one cannot name a tenant — so it is answered here
        // without waking the directory for it.
        if (!ResourceNaming.IsValid(hint)) {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Accept(await Directory.LookupBySlugAsync(hint));
    }

    /// <summary>The <c>D</c>-form string <c>ForTenant</c> wants for a resolved tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static string Qualifier(Guid tenantId) => tenantId.ToString("D", CultureInfo.InvariantCulture);

    // ⚠ A platform grain reached with a null-tenant key and a plain GetGrain, which is the one shape
    // CC1006 exempts: ITenantDirectoryGrain lives in no tenant, and ForTenant would put it in one.
    ITenantDirectoryGrain Directory => grains.GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory());

    static Guid? Accept(Result<TenantDirectoryEntry> looked) {
        if (looked.TryGetError(out _)) {
            return null;
        }

        var entry = looked.GetValueOrThrow();

        // A tombstoned tenant is not one anybody may sign into, and PendingDeletion is the 30-day
        // tombstone that is still restorable — docs/plan/06 § Tenant lifecycle. Both answer as if
        // the tenant were unknown, which over HTTP they are.
        return entry.Status is TenantStatus.Purged or TenantStatus.PendingDeletion ? null : entry.TenantId;
    }
}
