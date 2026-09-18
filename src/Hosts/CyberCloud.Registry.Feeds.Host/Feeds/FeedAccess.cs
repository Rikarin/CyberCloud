using CyberCloud.Identity.Validation;
using CyberCloud.Registry.Feeds.Host.Authentication;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Registry.Feeds.Host.Feeds;

/// <summary>
///     One feed, resolved and authorised for one request: who is calling, which resource they
///     named, which protocol it speaks, its catalogue, and where its bytes live.
/// </summary>
/// <param name="Caller">The validated token.</param>
/// <param name="CallerContext">The same caller in the resource manager's shape.</param>
/// <param name="Feed">The feed's address, with its GUID resolved.</param>
/// <param name="Kind">The protocol the feed was created for.</param>
/// <param name="Catalogue">The feed's grain, reached through <c>ForTenant</c>.</param>
/// <param name="StoragePrefix">Where the feed's bytes live — <see cref="ArtifactFeeds.StoragePrefix" />.</param>
public sealed record FeedContext(
    TokenClaims Caller,
    CallerContext CallerContext,
    ResourceId Feed,
    FeedKind Kind,
    IFeedGrain Catalogue,
    string StoragePrefix
) {
    /// <summary>The subject that is calling — <c>{subjectType}:{subjectId}</c>, for a catalogue entry's <c>PublishedBy</c>.</summary>
    public string Subject => Caller.SubjectType + ":" + Caller.SubjectId;
}

/// <summary>Which of a feed's two permissions a request needs.</summary>
public enum FeedIntent {
    /// <summary>A pull — the type's <c>read</c> permission.</summary>
    Read = 0,

    /// <summary>A push, unlist or tag — the type's <c>write</c> permission.</summary>
    Write = 1
}

/// <summary>
///     Turns a request into a <see cref="FeedContext" />, or refuses it — the one path every protocol
///     endpoint goes through before it touches a catalogue or an object.
/// </summary>
/// <remarks>
///     <para>
///         <b>Four steps, in an order that is a security argument.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Authenticate</b> — <see cref="FeedCredentialResolver" />, so an anonymous request
///             is a <c>401</c> before any resource path is looked at.
///         </item>
///         <item>
///             <b>Resolve the feed through the resource manager's front door</b> —
///             <see cref="IResourceManager.ReadAsync" /> at the address the URL names in the
///             caller's own tenant. That read is the enforcement seam: a feed the caller cannot
///             read answers the canonical <c>404</c>, exactly as it would through the gateway, and
///             a feed in another tenant is unreachable because the tenant comes from the token and
///             not from the URL. docs/plan/07 § The enforcement seam.
///         </item>
///         <item>
///             <b>Check the kind</b> — a NuGet URL against an npm feed is a <c>404</c>, because the
///             feed the caller named does not speak that protocol and no package in it could.
///         </item>
///         <item>
///             <b>Authorise a write</b> — <see cref="IResourceAuthorizer" /> with the type's own
///             <c>write</c> and <c>read</c> permission names, which is the same call the resource
///             manager makes for a <c>PUT</c> on the resource. A caller who can read but not write
///             is <c>403</c>; one who cannot read was already <c>404</c>.
///         </item>
///     </list>
///     <para>
///         ⚠
///         <b>
///             This host names <see cref="IResourceAuthorizer" /> and never the engine, and the
///             difference is where docs/plan/07 puts the seam.
///         </b> The authorizer is the resource manager's own abstraction, registered by
///         <c>AddCyberCloudResourceManager</c> and implemented inside that assembly over
///         <c>ICheckGrain</c>; asking it the question the manager asks, with the names the type
///         declared, is asking the manager. <c>FeedsIsolationTests</c> reads this assembly's
///         <c>AssemblyRef</c> table for <c>CyberCloud.Authorization.Contracts</c> and finds no row.
///     </para>
///     <para>
///         ⚠ <b>A feed that is not <c>Succeeded</c> is not served.</b> <c>Creating</c> means the
///         catalogue may not be open yet; <c>Deleting</c> means it is being emptied; either answers
///         <c>409</c> with the state, because a push into a feed mid-teardown is a push the teardown
///         then deletes.
///     </para>
/// </remarks>
/// <param name="credentials">Step 1.</param>
/// <param name="manager">Step 2.</param>
/// <param name="authorizer">Step 4.</param>
/// <param name="grains">The catalogue, through <c>ForTenant</c>.</param>
/// <param name="registry">The feeds type's permission names, so this host spells none itself.</param>
public sealed class FeedAccess(
    FeedCredentialResolver credentials,
    IResourceManager manager,
    IResourceAuthorizer authorizer,
    IGrainFactory grains,
    IProviderRegistry registry
) {
    /// <summary>Resolves and authorises the feed a request names.</summary>
    /// <param name="http">The request.</param>
    /// <param name="kind">The protocol the endpoint speaks. A feed of another kind is <c>404</c>.</param>
    /// <param name="subscription">The subscription segment of the URL.</param>
    /// <param name="resourceGroup">The resource-group segment.</param>
    /// <param name="feed">The feed's name.</param>
    /// <param name="intent">Whether the request pulls or pushes.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>
    ///     The context, or a refusal: <see cref="ErrorCode.AuthorizationFailed" /> for no credential
    ///     or no write, <see cref="ErrorCode.ResourceNotFound" /> for a feed the caller cannot see,
    ///     <see cref="ErrorCode.Conflict" /> for a feed that is not <c>Succeeded</c>.
    /// </returns>
    public async Task<Result<FeedContext>> ResolveAsync(
        HttpContext http,
        FeedKind kind,
        Guid subscription,
        string resourceGroup,
        string feed,
        FeedIntent intent,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(http);

        var authenticated = await credentials.ResolveAsync(http, cancellationToken);

        if (authenticated.TryGetError(out var unauthenticated)) {
            return Result<FeedContext>.Failure(unauthenticated);
        }

        var claims = authenticated.GetValueOrThrow();
        http.Items[FeedResponses.AuthenticatedItem] = true;

        var caller = new CallerContext {
            TenantId = claims.TenantId,
            SubjectType = claims.SubjectType,
            SubjectId = claims.SubjectId,
            ImpersonatedBy = claims.ImpersonatedBy,
            CorrelationId = http.TraceIdentifier
        };

        // ⚠ The tenant is the TOKEN's. The URL names a subscription, a group and a feed inside
        // whatever tenant the caller proved they are in; there is no tenant segment for a caller to
        // spell, which is the same design as the gateway's stage 3.
        if (ResourceNaming.Validate(resourceGroup, "resource group name").TryGetError(out var badGroup)) {
            return Result<FeedContext>.Failure(ErrorCode.ResourceNotFound, badGroup.Message);
        }

        if (ResourceNaming.Validate(feed, "feed name").TryGetError(out var badFeed)) {
            return Result<FeedContext>.Failure(ErrorCode.ResourceNotFound, badFeed.Message);
        }

        var address = new ResourceId(
            claims.TenantId,
            subscription,
            resourceGroup,
            ArtifactFeeds.Type,
            feed,
            Guid.Empty
        );

        var read = await manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = ArtifactFeeds.V2026, Caller = caller },
            cancellationToken
        );

        if (read.TryGetError(out var unreadable)) {
            return Result<FeedContext>.Failure(unreadable);
        }

        var snapshot = read.GetValueOrThrow();
        var resolved = address.WithId(snapshot.Id);

        using var body = JsonDocument.Parse(snapshot.Body);
        var actual = ArtifactFeeds.KindOf(body.RootElement);

        if (actual != kind) {
            // The canonical 404 and not a 400: the feed exists and is readable, but it has no
            // packages of this protocol and never will, so "no such thing here" is the true answer.
            return Result<FeedContext>.Failure(
                ErrorCode.ResourceNotFound,
                $"'{address.Path}' is a {ArtifactFeeds.NameOf(actual)} feed and serves no {ArtifactFeeds.NameOf(kind)} packages."
            );
        }

        if (snapshot.ProvisioningState != ProvisioningState.Succeeded) {
            return Result<FeedContext>.Failure(
                ErrorCode.Conflict,
                $"'{address.Path}' is {snapshot.ProvisioningState} and is not served until it is Succeeded."
            );
        }

        if (intent == FeedIntent.Write) {
            registry.TryGetType(ArtifactFeeds.Type, out var registration);

            var authorized = await authorizer.AuthorizeAsync(
                resolved,
                registration.WritePermission,
                registration.ReadPermission,
                caller,
                false,
                cancellationToken
            );

            if (authorized.TryGetError(out var refused)) {
                return Result<FeedContext>.Failure(refused);
            }
        }

        var catalogue = grains
            .ForTenant(claims.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IFeedGrain>(GrainKeys.Resource(snapshot.Id));

        return Result<FeedContext>.Success(
            new(claims, caller, resolved, kind, catalogue, ArtifactFeeds.StoragePrefix(claims.TenantId, snapshot.Id))
        );
    }
}
