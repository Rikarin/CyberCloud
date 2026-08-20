using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IApplicationGrain" /> — Entity, Durable, key <c>app/{applicationId:N}</c>.
/// </summary>
/// <remarks>
///     This is what OpenIddict's application store reads through. ADR-015: "OpenIddict is a library:
///     it handles the protocol, we own the stores, and the stores are grains."
/// </remarks>
public sealed class ApplicationGrain(
    [PersistentState("application", StorageTiers.Durable)] IPersistentState<ApplicationGrainState> state,
    IClock clock
)
    : Grain, IApplicationGrain {
    Guid applicationId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        applicationId = IdentityGrainKeys.Decode(this, GrainKeyKind.Application).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistration>> CreateAsync(ApplicationRegistration registration) {
        ArgumentNullException.ThrowIfNull(registration);

        if (state.State.Registration is not null) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.Conflict,
                $"Application {applicationId:D} is already registered."
            );
        }

        var validated = Validate(registration);
        if (validated.TryGetError(out var error)) {
            return Result<ApplicationRegistration>.Failure(error);
        }

        // ⚠ The ids come from the KEY, not from the body. A registration whose body named a different
        // application would otherwise write one grain's state under another's identity — and the body
        // is caller-supplied on a control-plane endpoint.
        state.State.Registration = validated.GetValueOrThrow() with {
            ApplicationId = applicationId,
            TenantId = tenantId,
            CreatedAt = clock.UtcNow
        };

        await state.WriteStateAsync();
        return Result<ApplicationRegistration>.Success(state.State.Registration);
    }

    /// <inheritdoc />
    public Task<Result<ApplicationRegistration>> GetAsync() =>
        Task.FromResult(
            state.State.Registration is { } registration
                ? Result<ApplicationRegistration>.Success(registration)
                : NotFound<ApplicationRegistration>()
        );

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistration>> UpdateAsync(ApplicationRegistration registration) {
        ArgumentNullException.ThrowIfNull(registration);

        if (state.State.Registration is not { } existing) {
            return NotFound<ApplicationRegistration>();
        }

        var validated = Validate(registration);
        if (validated.TryGetError(out var error)) {
            return Result<ApplicationRegistration>.Failure(error);
        }

        state.State.Registration = validated.GetValueOrThrow() with {
            ApplicationId = applicationId,
            TenantId = tenantId,
            ClientId = existing.ClientId,
            CreatedAt = existing.CreatedAt
        };

        await state.WriteStateAsync();
        return Result<ApplicationRegistration>.Success(state.State.Registration);
    }

    /// <inheritdoc />
    public Task<Result<bool>> AllowsGrantAsync(GrantType grant) =>
        Task.FromResult(
            state.State.Registration is { } registration
                ? Result<bool>.Success(registration.AllowedGrants.Contains(grant))
                : NotFound<bool>()
        );

    /// <inheritdoc />
    public Task<Result<bool>> IsRegisteredRedirectUriAsync(string redirectUri) {
        if (state.State.Registration is not { } registration) {
            return Task.FromResult(NotFound<bool>());
        }

        // ⚠ WHOLE-STRING, ORDINAL. Never StartsWith, never a wildcard, never a case-insensitive
        // compare on the path. A prefix match turns `https://app.example.com/cb` into a match for
        // `https://app.example.com/cb.attacker.test`, and the authorization code goes to whoever
        // asked. This one line is the difference between an authorization server and an open
        // redirect with extra steps.
        return Task.FromResult(
            Result<bool>.Success(
                registration.RedirectUris.Exists(x => string.Equals(x, redirectUri, StringComparison.Ordinal))
            )
        );
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync() {
        if (state.State.Registration is null) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"Application {applicationId:D} does not exist.");
        }

        state.State.Registration = null;
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    static Result<ApplicationRegistration> Validate(ApplicationRegistration registration) {
        if (string.IsNullOrWhiteSpace(registration.ClientId)) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.InvalidRequestBody,
                "An application registration needs a client id."
            );
        }

        foreach (var uri in registration.RedirectUris) {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) {
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{uri}' is not an absolute redirect URI. Relative redirect URIs cannot be "
                    + "compared safely, because what they resolve against is the attacker's choice."
                );
            }

            // ⚠ AND `UriKind.Absolute` DOES NOT MEAN WHAT THE LINE ABOVE NEEDS IT TO MEAN ON UNIX.
            // Measured on .NET 10, macOS and Linux: `/callback` parses as `file:///callback` and
            // `//evil.example/x` parses as `file://evil.example/x`, both with TryCreate returning
            // true. On Windows both are refused. So the guard above held on the one platform nobody
            // runs this on and let the second string — the protocol-relative open-redirect payload
            // that ReturnUrl.Sanitize exists to refuse elsewhere in this tree — straight through.
            //
            // A `file:` redirect URI is never a legitimate one: the authorization response has
            // nowhere to go and the browser resolves `//host/path` against the page's own scheme,
            // which is exactly the "resolves against the attacker's choice" the message names. A
            // custom scheme is left alone — `com.example.app:/oauth` is how OAuth 2.1 says a native
            // client registers, and it parses correctly on every platform.
            if (parsed.IsFile) {
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{uri}' has no scheme of its own, so it is a relative or protocol-relative "
                    + "reference that this platform's URI parser turned into a 'file:' URI. What it "
                    + "resolves against is the browser's context and therefore the attacker's choice."
                );
            }

            if (parsed.Fragment.Length > 0) {
                // ⚠ OAuth 2.1 forbids a fragment in a redirect URI, and the reason is mechanical: the
                // authorization response appends its own query or fragment, so a registered fragment
                // makes the comparison and the actual navigation disagree.
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{uri}' carries a fragment. A redirect URI must not — the authorization "
                    + "response appends its own, so the registered value and the value the browser "
                    + "is sent to would differ."
                );
            }
        }

        // ⚠ A public client with a stored secret is a contradiction that is worth failing on rather
        // than resolving: whichever way it is resolved, somebody's threat model is wrong.
        if (registration.IsPublicClient && !registration.ClientSecretRef.IsEmpty) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.InvalidRequestBody,
                "A public client cannot hold a client secret — a secret shipped in a browser or a "
                + "CLI is public. Use PKCE, which is what OAuth 2.1 requires here anyway."
            );
        }

        return Result<ApplicationRegistration>.Success(registration);
    }

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"Application {applicationId:D} does not exist.");
}
