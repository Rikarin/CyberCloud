using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The request validation OpenIddict hands back to us in degraded mode, and where each answer
///     comes from. ADR-015, docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Degraded mode is what "we own the stores, and the stores are grains" costs at the
///             protocol layer, and this file is the bill.
///         </b> OpenIddict's own request validation resolves clients, redirect URIs and codes through
///         its core managers, which want a store implementation per object; without one the server
///         throws <i>"The core services must be registered"</i> on the first token request — which
///         is what this host did for as long as it mapped no token endpoint. Enabling the degraded
///         mode turns those built-in checks off and makes the server refuse any request for which
///         no custom validator is registered: <i>"No custom token request validation handler was
///         found. When enabling the degraded mode, a custom
///         'IOpenIddictServerHandler&lt;ValidateTokenRequestContext&gt;' must be implemented"</i>.
///         Every handler here exists to answer one of those sentences.
///     </para>
///     <para>
///         ⚠ <b>All but the first answer "not yet", and they answer it as an OAuth error rather than
///         as an exception.</b> A client that tries the authorization-code, device or end-session
///         endpoint today gets <c>temporarily_unavailable</c> with a description naming what the flow
///         is waiting on; without these handlers it would get a <c>500</c> and a stack trace naming
///         an OpenIddict interface. <see cref="TokenApi" />'s remarks carry the reasons per flow.
///         ⚠ Rejecting <i>before</i> reading a <c>redirect_uri</c> is what keeps the authorization
///         handler from being an open redirect: OpenIddict redirects an error to the client only once
///         the URI has been validated, and this handler validates nothing.
///     </para>
/// </remarks>
static class DegradedModeHandlers {
    /// <summary>
    ///     Where <see cref="ValidateClientCredentials" /> leaves the authenticated service principal
    ///     for the endpoint that mints — <see cref="OpenIddictServerTransaction.Properties" />.
    /// </summary>
    /// <remarks>
    ///     A transaction property rather than a second grain call: the endpoint runs later in the
    ///     same request, and re-authenticating there would be a second vault read for one grant.
    /// </remarks>
    public const string ServicePrincipalProperty = "cybercloud.token.service-principal";

    /// <summary>Every handler this server registers, in one list so a missing one is a visible gap.</summary>
    public static IReadOnlyList<OpenIddictServerHandlerDescriptor> All { get; } = [
        ValidateClientCredentials.Descriptor,
        RefuseAuthorizationRequests.Descriptor,
        RefuseDeviceAuthorizationRequests.Descriptor,
        RefuseEndUserVerificationRequests.Descriptor,
        RefuseEndSessionRequests.Descriptor,
        RefuseDeviceCodeStorage.GenerateDescriptor,
        RefuseDeviceCodeStorage.ValidateDescriptor
    ];

    /// <summary>
    ///     Answers the token request's validation: client credentials through
    ///     <see cref="TokenApi.AuthenticateClientAsync" />, everything else refused.
    /// </summary>
    /// <param name="api">The decision.</param>
    public sealed class ValidateClientCredentials(TokenApi api) : IOpenIddictServerHandler<ValidateTokenRequestContext> {
        /// <summary>The registration.</summary>
        /// <remarks>
        ///     ⚠ Ordered after OpenIddict's own parameter checks, so a request with no
        ///     <c>client_secret</c> at all gets the library's <c>invalid_request</c> naming the
        ///     parameter rather than this host's uniform <c>invalid_client</c> — the former is a
        ///     malformed request and says so; the latter is reserved for a well-formed one that
        ///     failed authentication, which is the one that must not explain itself.
        /// </remarks>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
                .UseSingletonHandler<ValidateClientCredentials>()
                .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAuthentication.Descriptor.Order + 1_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public async ValueTask HandleAsync(ValidateTokenRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (!context.Request.IsClientCredentialsGrantType()) {
                context.Reject(
                    OpenIddictConstants.Errors.UnsupportedGrantType,
                    "Only the client_credentials grant is served today. The authorization-code, "
                    + "refresh-token, device and token-exchange grants are owed — docs/plan/11 "
                    + "§ Protocol, and TokenApi's remarks say what each is waiting on."
                );

                return;
            }

            var authenticated = await api.AuthenticateClientAsync(
                context.Request.ClientId,
                context.Request.ClientSecret,
                context.CancellationToken
            );

            if (authenticated.TryGetError(out var refused)) {
                context.Reject(OpenIddictConstants.Errors.InvalidClient, refused.Message);

                return;
            }

            context.Transaction.Properties[ServicePrincipalProperty] = authenticated.GetValueOrThrow();
        }
    }

    /// <summary>The authorization-code flow, until <c>/authorize</c> can validate a client.</summary>
    public sealed class RefuseAuthorizationRequests : IOpenIddictServerHandler<ValidateAuthorizationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
                .UseSingletonHandler<RefuseAuthorizationRequests>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateAuthorizationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            context.Reject(
                OpenIddictConstants.Errors.TemporarilyUnavailable,
                "The authorization-code flow is not served yet: nothing maps a client_id to its "
                + "application registration, so neither the client nor its redirect_uri can be "
                + "validated. docs/plan/11 § Protocol; IdentityHostOptions says where the index goes."
            );

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The device flow, until there is a verification page and a code store.</summary>
    public sealed class RefuseDeviceAuthorizationRequests
        : IOpenIddictServerHandler<ValidateDeviceAuthorizationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateDeviceAuthorizationRequestContext>()
                .UseSingletonHandler<RefuseDeviceAuthorizationRequests>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateDeviceAuthorizationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            context.Reject(
                OpenIddictConstants.Errors.TemporarilyUnavailable,
                "The device-authorization flow is not served yet: it needs the verification page and "
                + "a store for device and user codes. docs/plan/11 § Protocol."
            );

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The device flow's other half, refused for the same reason.</summary>
    public sealed class RefuseEndUserVerificationRequests
        : IOpenIddictServerHandler<ValidateEndUserVerificationRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateEndUserVerificationRequestContext>()
                .UseSingletonHandler<RefuseEndUserVerificationRequests>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateEndUserVerificationRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            context.Reject(
                OpenIddictConstants.Errors.TemporarilyUnavailable,
                "The device-authorization flow is not served yet, so there is no user code to verify. "
                + "docs/plan/11 § Protocol."
            );

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Sign-out, until a <c>post_logout_redirect_uri</c> can be validated against a registration.</summary>
    public sealed class RefuseEndSessionRequests : IOpenIddictServerHandler<ValidateEndSessionRequestContext> {
        /// <summary>The registration.</summary>
        public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateEndSessionRequestContext>()
                .UseSingletonHandler<RefuseEndSessionRequests>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateEndSessionRequestContext context) {
            ArgumentNullException.ThrowIfNull(context);

            context.Reject(
                OpenIddictConstants.Errors.TemporarilyUnavailable,
                "The end-session endpoint is not served yet: a post_logout_redirect_uri cannot be "
                + "validated without the client index the authorization-code flow is also waiting on."
            );

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     The device flow's codes, which in degraded mode the server cannot store or look up
    ///     without help — refused at generation and at validation.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Required at start-up, not at request time, and that is the one place the pattern on
    ///         this file breaks.
    ///     </b> A device code and a user code are the two tokens OpenIddict cannot make
    ///     self-contained — a user types the user code into a page, so something has to map it back
    ///     — and its post-configuration refuses to build the server options at all when the device
    ///     flow is allowed and no custom <c>ValidateTokenContext</c> and <c>GenerateTokenContext</c>
    ///     handler exists: <i>"No custom token validation handler was found. When enabling the
    ///     degraded mode, a custom 'IOpenIddictServerHandler&lt;ValidateTokenContext&gt;' must be
    ///     implemented to handle device and user codes"</i>.
    ///     <c>OpenIddictServerOptionsTests.TheOptionsCanBeMaterialisedAtAll</c> found it. ⚠ Both
    ///     handlers act on those two token types and no other — an access token passes through
    ///     untouched, which is what makes them safe to register beside the grant that is served.
    /// </remarks>
    public sealed class RefuseDeviceCodeStorage
        : IOpenIddictServerHandler<GenerateTokenContext>, IOpenIddictServerHandler<ValidateTokenContext> {
        const string Reason =
            "The device-authorization flow is not served yet: its device and user codes need a store "
            + "this host does not have. docs/plan/11 § Protocol.";

        /// <summary>The generation half.</summary>
        public static OpenIddictServerHandlerDescriptor GenerateDescriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<GenerateTokenContext>()
                .UseSingletonHandler<RefuseDeviceCodeStorage>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <summary>The validation half.</summary>
        public static OpenIddictServerHandlerDescriptor ValidateDescriptor { get; } =
            OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenContext>()
                .UseSingletonHandler<RefuseDeviceCodeStorage>()
                .SetOrder(int.MinValue + 100_000)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        /// <inheritdoc />
        public ValueTask HandleAsync(GenerateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            if (context.TokenType is OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode
                or OpenIddictConstants.TokenTypeIdentifiers.Private.UserCode) {
                context.Reject(OpenIddictConstants.Errors.TemporarilyUnavailable, Reason);
            }

            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask HandleAsync(ValidateTokenContext context) {
            ArgumentNullException.ThrowIfNull(context);

            // Only when the caller could ONLY be presenting a device or user code. A validation that
            // would also accept an access token or a refresh token is somebody else's to answer.
            if (context.ValidTokenTypes.Count > 0
                && context.ValidTokenTypes.All(x =>
                    x is OpenIddictConstants.TokenTypeIdentifiers.Private.DeviceCode
                        or OpenIddictConstants.TokenTypeIdentifiers.Private.UserCode
                )) {
                context.Reject(OpenIddictConstants.Errors.TemporarilyUnavailable, Reason);
            }

            return ValueTask.CompletedTask;
        }
    }
}
