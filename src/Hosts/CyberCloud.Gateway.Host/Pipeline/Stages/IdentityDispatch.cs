using CyberCloud.Gateway.Host.Http;
using CyberCloud.Identity.Validation;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 8 for the identity addresses — <c>/tenants/{t}/providers/CyberCloud.Identity/…</c>:
///     the verb decides the manager call, and the manager decides everything else. Issue #41, with
///     #43's invitation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No check here, for <see cref="DispatchStage" />'s reason.</b> A <c>POST</c> on the
///         invitations collection goes to <see cref="IInvitationManager" /> as it did under #43;
///         every other address goes to <see cref="IIdentityAdministration" />. Both own their checks,
///         and this class only maps a verb to a call and a result to a status.
///     </para>
///     <para>
///         ⚠ <b>A verb an address doesn't take is a <c>405</c> with <c>Allow</c>,</b> the resource
///         graph's and the role assignment's answer — never a <c>404</c>, which would say the address
///         doesn't exist when it does.
///     </para>
///     <para>
///         ⚠ <b>The two answers that carry a secret are <c>no-store</c>.</b> A registration and a
///         rotation return the client secret once, as a <c>secret: true</c> action's response does,
///         and a proxy that kept either would hand the next requester somebody's credential.
///     </para>
/// </remarks>
/// <param name="invitations">Sends an invitation — #43's manager.</param>
/// <param name="administration">Everything else under the namespace — #41's.</param>
sealed class IdentityDispatch(IInvitationManager invitations, IIdentityAdministration administration) {
    /// <summary>Serves one identity address.</summary>
    /// <param name="context">The request, with the route stage 6 parsed.</param>
    /// <param name="path">The request path, for the error body.</param>
    /// <param name="cancellationToken">Cancels the manager call.</param>
    public async Task<GatewayOutcome> DispatchAsync(
        GatewayRequestContext context,
        string path,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Route.Identity;
        var method = context.Http.Request.Method;
        var request = new IdentityAdministrationRequest {
            TenantId = address.TenantId,
            // ⚠ The caller as stages 2 and 3 established it, tenant included.
            Caller = context.Caller,
            CurrentSessionId = CurrentSession(context)
        };

        switch (address.Kind) {
            case IdentityAddressKind.Invitations when HttpMethods.IsPost(method):
                return await InviteAsync(context, path, cancellationToken);
            case IdentityAddressKind.Invitations when HttpMethods.IsGet(method):
                return Ok(
                    await administration.ListInvitationsAsync(request, cancellationToken),
                    IdentityBodies.Invitations,
                    path
                );
            case IdentityAddressKind.Invitations:
                return NotAllowed(method, "the invitations collection", "GET, POST");

            case IdentityAddressKind.Invitation when HttpMethods.IsDelete(method):
                return Ok(
                    await administration.RevokeInvitationAsync(request, address.Id, cancellationToken),
                    IdentityBodies.Invitation,
                    path
                );
            case IdentityAddressKind.Invitation:
                return NotAllowed(method, "an invitation", "DELETE");

            case IdentityAddressKind.InvitationResend when HttpMethods.IsPost(method):
                return Ok(
                    await administration.ResendInvitationAsync(request, address.Id, cancellationToken),
                    IdentityBodies.Invitation,
                    path
                );
            case IdentityAddressKind.InvitationResend:
                return NotAllowed(method, "an invitation's resend", "POST");

            case IdentityAddressKind.Members when HttpMethods.IsGet(method):
                return Ok(
                    await administration.ListMembersAsync(request, cancellationToken),
                    members => IdentityBodies.Members(address.TenantId, members),
                    path
                );
            case IdentityAddressKind.Members:
                return NotAllowed(
                    method,
                    "the members collection — a member joins by invitation, a POST on the invitations collection —",
                    "GET"
                );

            case IdentityAddressKind.Member when HttpMethods.IsDelete(method):
                return Ok(
                    await administration.RemoveMemberAsync(request, address.Id, cancellationToken),
                    member => IdentityBodies.Member(address.TenantId, member),
                    path
                );
            case IdentityAddressKind.Member:
                return NotAllowed(method, "a member", "DELETE");

            case IdentityAddressKind.Applications when HttpMethods.IsGet(method):
                return Ok(
                    await administration.ListApplicationsAsync(request, cancellationToken),
                    applications => IdentityBodies.Applications(address.TenantId, applications),
                    path
                );
            case IdentityAddressKind.Applications when HttpMethods.IsPost(method): {
                var draft = IdentityBodies.ApplicationDraft(context.Body);

                if (draft.TryGetError(out var bodyError)) {
                    return ResultShaper.Shape(bodyError, path);
                }

                var created = await administration.CreateApplicationAsync(request, draft.GetValueOrThrow(), cancellationToken);

                return created.TryGetError(out var refused)
                    ? ResultShaper.Shape(refused, path)
                    : Secret(StatusCodes.Status201Created, IdentityBodies.ApplicationRegistered(address.TenantId, created.GetValueOrThrow()));
            }
            case IdentityAddressKind.Applications:
                return NotAllowed(method, "the applications collection", "GET, POST");

            case IdentityAddressKind.Application when HttpMethods.IsGet(method):
                return Ok(
                    await administration.GetApplicationAsync(request, address.Id, cancellationToken),
                    application => IdentityBodies.Application(address.TenantId, application),
                    path
                );
            case IdentityAddressKind.Application when HttpMethods.IsDelete(method): {
                var deleted = await administration.DeleteApplicationAsync(request, address.Id, cancellationToken);

                return deleted.TryGetError(out var refused)
                    ? ResultShaper.Shape(refused, path)
                    : new() { StatusCode = StatusCodes.Status204NoContent };
            }
            case IdentityAddressKind.Application:
                return NotAllowed(method, "an application", "GET, DELETE");

            case IdentityAddressKind.ApplicationSecret when HttpMethods.IsPost(method): {
                var rotated = await administration.RotateApplicationSecretAsync(request, address.Id, cancellationToken);

                return rotated.TryGetError(out var refused)
                    ? ResultShaper.Shape(refused, path)
                    : Secret(StatusCodes.Status200OK, IdentityBodies.ApplicationRegistered(address.TenantId, rotated.GetValueOrThrow()));
            }
            case IdentityAddressKind.ApplicationSecret:
                return NotAllowed(method, "an application's secret rotation", "POST");

            case IdentityAddressKind.Sessions when HttpMethods.IsGet(method):
                return Ok(
                    await administration.ListOwnSessionsAsync(request, cancellationToken),
                    sessions => IdentityBodies.Sessions(address.TenantId, sessions),
                    path
                );
            case IdentityAddressKind.Sessions:
                return NotAllowed(method, "your sessions", "GET");

            case IdentityAddressKind.Session when HttpMethods.IsDelete(method): {
                var revoked = await administration.RevokeOwnSessionAsync(request, address.Id, cancellationToken);

                return revoked.TryGetError(out var refused)
                    ? ResultShaper.Shape(refused, path)
                    : new() { StatusCode = StatusCodes.Status204NoContent };
            }
            case IdentityAddressKind.Session:
                return NotAllowed(method, "a session", "DELETE");

            default:
                // Unreachable through the router, which parses nothing else under the namespace.
                return ResultShaper.Shape(
                    new(ErrorCode.InvalidResourceId, $"'{path}' is not an identity address."),
                    path
                );
        }
    }

    /// <summary>
    ///     An invitation — straight to <see cref="IInvitationManager" />, which owns the
    ///     <c>assignRole</c> check. Issue #43, step 7.
    /// </summary>
    /// <remarks>
    ///     ⚠ The body is parsed for its one field and nothing else; the tenant is the token's,
    ///     rebuilt into the address; the answer is <c>201</c> with the invitation — its id, the user
    ///     it created and when its link expires — and never the link, which went to the address and
    ///     nowhere else.
    /// </remarks>
    async Task<GatewayOutcome> InviteAsync(GatewayRequestContext context, string path, CancellationToken cancellationToken) {
        var email = InvitationBody.Email(context.Body);

        if (email.TryGetError(out var bodyError)) {
            return ResultShaper.Shape(bodyError, path);
        }

        var invited = await invitations.InviteAsync(
            new() { TenantId = context.Route.Identity.TenantId, Email = email.GetValueOrThrow(), Caller = context.Caller },
            cancellationToken
        );

        return invited.TryGetError(out var error)
            ? ResultShaper.Shape(error, path)
            : new() { StatusCode = StatusCodes.Status201Created, Json = IdentityBodies.Invitation(invited.GetValueOrThrow()) };
    }

    /// <summary>
    ///     The token's <c>sid</c>, which marks "this session" in the caller's own list and does
    ///     nothing else — <see cref="TokenClaims.SessionId" /> says why.
    /// </summary>
    static Guid CurrentSession(GatewayRequestContext context) =>
        context.Http.Items[AuthenticateStage.ClaimsItemKey] is TokenClaims claims
        && Guid.TryParseExact(claims.SessionId, "N", out var sid)
            ? sid
            : Guid.Empty;

    static GatewayOutcome Ok<T>(Result<T> result, Func<T, string> render, string path)
        where T : notnull =>
        result.TryGetError(out var error)
            ? ResultShaper.Shape(error, path)
            : new() { StatusCode = StatusCodes.Status200OK, Json = render(result.GetValueOrThrow()) };

    static GatewayOutcome Secret(int status, string json) =>
        new GatewayOutcome { StatusCode = status, Json = json }.WithHeader(GatewayHeaders.CacheControl, "no-store");

    static GatewayOutcome NotAllowed(string method, string what, string allow) =>
        new GatewayOutcome {
            StatusCode = StatusCodes.Status405MethodNotAllowed,
            Error = new(
                ErrorCode.InvalidRequestBody,
                $"{method} is not supported on {what}. It takes {allow} — docs/plan/11 § The object model."
            )
        }.WithHeader(GatewayHeaders.Allow, allow);
}
