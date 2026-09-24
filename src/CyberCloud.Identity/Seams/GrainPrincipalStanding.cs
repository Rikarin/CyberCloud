using CyberCloud.Authorization.Contracts;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Seams;

/// <summary>
///     The <see cref="IPrincipalStanding" /> that reads the principal's own grain: a user may act while
///     <see cref="UserStatus.Active" />, a service principal while enabled, and a managed identity while
///     it's bound to a workload.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Stricter than <c>GrainPrincipalDirectory</c>, because it answers a different question.
///         </b> That one asks whether a principal can be <i>granted</i> to, and a suspended user or
///         a disabled service principal can: a grant is about what it may do once it's allowed to
///         act again. This asks whether it may act <i>now</i>, which is what authenticating would
///         have established had there been a token — so each type is held to the rule its sign-in
///         path applies: <see cref="UserStatus" /> <c>Active</c> (an invited user can't sign in
///         either), <see cref="ServicePrincipalDescriptor.Enabled" />, and
///         <see cref="ManagedIdentityDescriptor.IsExchangeable" />.
///     </para>
///     <para>
///         ⚠ <b>Cross-tenant is closed by <c>ForTenant</c> alone</b>, as in the directory: every
///         principal grain is tenant-qualified, so a principal of another tenant is an activation
///         nothing created, and "not found" here.
///     </para>
///     <para>
///         ⚠ <b>A group is refused, not looked up.</b> A group is a subject a tuple can name and never
///         the subject of a request, so a recorded caller that is one has been forged or corrupted.
///     </para>
/// </remarks>
public sealed class GrainPrincipalStanding(IGrainFactory grains) : IPrincipalStanding {
    /// <inheritdoc />
    public async Task<Result> EnsureMayActAsync(
        Guid tenantId,
        string principalType,
        string principalId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(principalType);
        ArgumentNullException.ThrowIfNull(principalId);

        var who = $"{principalType}:{principalId}";

        if (!GuidFormat.TryParseN(principalId, out var id)) {
            return Refused($"{who} is not an id any principal of tenant {tenantId:D} can have.");
        }

        var tenant = grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

        switch (principalType) {
            case SubjectTypes.User: {
                var user = await tenant.GetGrain<IUserGrain>(GrainKeys.User(id)).GetAsync();

                if (user.TryGetError(out var error)) {
                    return Absent(who, tenantId, error);
                }

                var status = user.GetValueOrThrow().Status;

                return status == UserStatus.Active
                    ? Result.Success
                    : Refused($"{who} is {status}, and only an active user may act.");
            }
            case SubjectTypes.ServicePrincipal: {
                var principal = await tenant
                    .GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(id))
                    .GetAsync();

                if (principal.TryGetError(out var error)) {
                    return Absent(who, tenantId, error);
                }

                return principal.GetValueOrThrow().Enabled
                    ? Result.Success
                    : Refused($"{who} is disabled.");
            }
            case SubjectTypes.ManagedIdentity: {
                var identity = await tenant
                    .GetGrain<IManagedIdentityGrain>(GrainKeys.ManagedIdentity(id))
                    .GetAsync();

                if (identity.TryGetError(out var error)) {
                    return Absent(who, tenantId, error);
                }

                return identity.GetValueOrThrow().IsExchangeable
                    ? Result.Success
                    : Refused($"{who} is not bound to a workload, so it can't obtain a token.");
            }
            default:
                return Refused($"{who} is not a kind of principal that makes requests.");
        }
    }

    static Result Refused(string reason) => Result.Failure(ErrorCode.AuthorizationFailed, reason);

    /// <summary>
    ///     A grain's "not found" is a refusal naming the principal; anything else is passed on as the
    ///     failure it is, which the caller refuses on too.
    /// </summary>
    static Result Absent(string who, Guid tenantId, Error error) =>
        error.Code == ErrorCode.ResourceNotFound
            ? Refused($"{who} no longer exists in tenant {tenantId:D}.")
            : Result.Failure(error);
}
