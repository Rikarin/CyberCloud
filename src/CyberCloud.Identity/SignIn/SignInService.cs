using CyberCloud.Identity.Credentials;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;

namespace CyberCloud.Identity.SignIn;

/// <summary>
///     How long the sign-in path is allowed to look like it is doing something.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="MinimumDuration" /> is a floor, not the defence.</b> The defence is that both
///     branches do the same Argon2id work; the floor is a second layer that also absorbs the
///     differences the work equalization cannot reach — a warm grain activation against a cold one,
///     a user with fifty recovery codes against one with none, a hot durable-tier page against a
///     cold one. Setting it to <see cref="TimeSpan.Zero" /> disables it, which is what the timing
///     test does, because a floor makes any two measurements equal and would turn a real assertion
///     into a tautology.
/// </remarks>
public sealed record SignInOptions {
    /// <summary>The defaults.</summary>
    public static SignInOptions Default { get; } = new();

    /// <summary>
    ///     The shortest a sign-in attempt may take, successful or not. 250 ms comfortably exceeds
    ///     the Argon2id verification at the production parameters, so the pad is small in the common
    ///     case and large exactly where a branch was cheap.
    /// </summary>
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>What an <c>otpauth</c> URI calls this deployment.</summary>
    public string Issuer { get; init; } = "Cyber Cloud";
}

/// <summary>
///     What the caller knows about the request, beyond the credential.
/// </summary>
/// <remarks>
///     ⚠ <see cref="ClientAddress" /> is taken as the address and immediately digested — it is
///     accepted here so the module can hash it, and it is never stored or logged. docs/plan/11
///     § Auditing sends the address itself to the telemetry pipeline as a structured field, which is
///     the host's job because the host is what has an <c>HttpContext</c>.
/// </remarks>
public sealed record SignInContext {
    /// <summary>The client the user is signing in to.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Something the user will recognise in a device list.</summary>
    public string DeviceLabel { get; init; } = string.Empty;

    /// <summary>The originating address. Hashed on the way in; never kept.</summary>
    public string ClientAddress { get; init; } = string.Empty;
}

/// <summary>
///     The sign-in path, with the enumeration and timing hardening docs/plan/11 § Credentials asks
///     for.
/// </summary>
/// <remarks>
///     <para>
///         <b>The order of operations is the design, and it is not the obvious one.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Check the lockout counter, before anything else.</b> The key is derived from the
///             tenant and the address by hashing — no grain, no lookup. A locked identifier is
///             refused here, and <b>this is the only path an unauthenticated attacker can drive at
///             volume</b>. docs/plan/11 § Credentials: "an authentication endpoint whose failure path
///             costs a grain activation is a denial-of-service amplifier."
///         </item>
///         <item>
///             <b>Resolve the address to a user</b> through <c>IEmailIndexGrain</c> — the first grain
///             call, and it happens only for a caller who has passed the counter.
///         </item>
///         <item>
///             <b>Verify, on both branches.</b> When there is no user, the verification runs against
///             <see cref="IPasswordHasher.DummyHash" /> instead of returning early. This is what
///             makes "the same time whether or not the account exists" true rather than claimed.
///         </item>
///         <item>
///             <b>Answer identically.</b> One failure shape,
///             <see cref="SignInOutcome.Rejected" />, whatever happened. The real reason goes to the
///             log as a structured field.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Every grain reference goes through <c>ForTenant</c>.</b> This is a plain service, not
///         a grain, so <c>Orleans.Multitenant</c>'s tenant-separating call filter does not cover it —
///         CC1006 is what keeps that true after the next edit.
///     </para>
/// </remarks>
public sealed class SignInService(
    IGrainFactory grains,
    ILockoutCounter lockout,
    IPasswordHasher hasher,
    ILogger<SignInService> logger,
    SignInOptions? options = null
) {
    readonly SignInOptions options = options ?? SignInOptions.Default;

    /// <summary>
    ///     Signs in with an email address and a password.
    /// </summary>
    /// <param name="tenantId">Which tenant. A user belongs to exactly one.</param>
    /// <param name="email">The address typed.</param>
    /// <param name="password">The password typed.</param>
    /// <param name="context">What else is known about the request.</param>
    /// <param name="cancellationToken">Cancels the attempt, including its timing pad.</param>
    /// <returns>
    ///     A session, or <see cref="UniformFailures.SignIn" />. ⚠ The failure carries no information
    ///     about which of the six things that can go wrong did.
    /// </returns>
    public async Task<Result<SignInOutcome>> SignInWithPasswordAsync(
        Guid tenantId,
        string email,
        string password,
        SignInContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var started = Stopwatch.GetTimestamp();

        try {
            return await AttemptAsync(tenantId, email, password, context, cancellationToken);
        } finally {
            await PadAsync(started, cancellationToken);
        }
    }

    /// <summary>
    ///     Starts a password reset. Returns the same thing for every address.
    /// </summary>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="email">The address typed.</param>
    /// <param name="cancellationToken">Cancels the lookup, including its timing pad.</param>
    /// <returns>
    ///     Always success, carrying <see cref="UniformFailures.PasswordReset" />. docs/plan/11
    ///     § Credentials: "the reset email is the only signal, and it goes to the address typed
    ///     regardless."
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Delivery is <see cref="IOtpDeliverySeam" />'s job and it is not built</b>, so what
    ///     this currently does is resolve the address, record the probe, and return the uniform
    ///     answer. The shape is the part that matters and it is right: nothing about the return
    ///     value or its timing depends on whether the account exists.
    /// </remarks>
    public async Task<Result<string>> RequestPasswordResetAsync(
        Guid tenantId,
        string email,
        CancellationToken cancellationToken = default
    ) {
        var started = Stopwatch.GetTimestamp();

        try {
            var normalized = GrainKeys.NormalizeEmail(email);
            if (normalized.TryGetError(out _)) {
                // ⚠ Even a malformed address gets the uniform answer. Rejecting it with a validation
                // error would let a probe distinguish "no account" from "not an address", which is a
                // small leak on its own and a reliable oracle when combined with a list.
                return Result<string>.Success(UniformFailures.PasswordReset);
            }

            var address = normalized.GetValueOrThrow();
            var resolved = await ResolveAsync(tenantId, address, cancellationToken);

            if (resolved is null) {
                IdentityLog.UnknownAddressProbed(logger, tenantId, Digest(tenantId, address));
            }

            return Result<string>.Success(UniformFailures.PasswordReset);
        } finally {
            await PadAsync(started, cancellationToken);
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    async Task<Result<SignInOutcome>> AttemptAsync(
        Guid tenantId,
        string email,
        string password,
        SignInContext context,
        CancellationToken cancellationToken
    ) {
        var normalized = GrainKeys.NormalizeEmail(email);
        if (normalized.TryGetError(out _)) {
            // No grain touched, and the same answer as every other failure.
            return UniformFailures.RejectSignIn();
        }

        var address = normalized.GetValueOrThrow();
        var identifier = LockoutKey.ForIdentifier(tenantId, address);

        // ── 1. The gate. NOTHING ABOVE THIS LINE TOUCHES A GRAIN, AND NOTHING BELOW IT RUNS FOR A
        //       LOCKED IDENTIFIER. This is the property LockoutIsGrainFreeTests asserts. ─────────
        if (await lockout.IsLockedAsync(identifier, cancellationToken)) {
            IdentityLog.LockedOut(logger, tenantId, Digest(tenantId, address));
            return UniformFailures.RejectSignIn();
        }

        // ── 2. Resolve. The first grain call. ──────────────────────────────────────────────────
        var userId = await ResolveAsync(tenantId, address, cancellationToken);

        // ── 3. Verify, on both branches. ───────────────────────────────────────────────────────
        bool verified;

        if (userId is null) {
            // ⚠ THE ENUMERATION DEFENCE. Not `return Reject()` — that would make a missing account
            // answer in microseconds while a wrong password takes an Argon2id verification, and the
            // difference is trivially measurable over a handful of requests. The dummy hash is a
            // real hash of the configured shape over a value nobody knows, so verifying against it
            // costs exactly what verifying a real one costs.
            verified = hasher.Verify(password ?? string.Empty, hasher.DummyHash);
            IdentityLog.UnknownAddressProbed(logger, tenantId, Digest(tenantId, address));
        } else {
            var check = await Tenant(tenantId)
                .GetGrain<IUserGrain>(GrainKeys.User(userId.Value))
                .VerifyPasswordAsync(password ?? string.Empty);

            verified = check.IsSuccess && check.GetValueOrThrow();
        }

        if (!verified) {
            await RecordFailureAsync(identifier, userId, cancellationToken);
            IdentityLog.SignInRefused(logger, tenantId, "credential-rejected", Digest(tenantId, address));

            return UniformFailures.RejectSignIn();
        }

        // ── 4. Success. ────────────────────────────────────────────────────────────────────────
        await lockout.ResetAsync(identifier, cancellationToken);
        await lockout.ResetAsync(LockoutKey.ForUser(userId!.Value), cancellationToken);

        return await OpenSessionAsync(tenantId, userId.Value, AuthenticationMethod.Password, context);
    }

    /// <summary>
    ///     Opens a session for a subject who has already proven who they are.
    /// </summary>
    /// <param name="tenantId">Which tenant.</param>
    /// <param name="userId">Who.</param>
    /// <param name="method">How they proved it.</param>
    /// <param name="context">What else is known about the request.</param>
    /// <remarks>
    ///     ⚠ Public because a passkey assertion, a TOTP step-up and a recovery-code redemption all
    ///     end here, and each of those is verified by the host (which holds the WebAuthn challenge)
    ///     rather than by this service. Splitting session creation out means there is one place a
    ///     session is minted whatever authenticated it, so the <c>amr</c> claim and the device
    ///     record cannot diverge between flows.
    /// </remarks>
    public async Task<Result<SignInOutcome>> OpenSessionAsync(
        Guid tenantId,
        Guid userId,
        AuthenticationMethod method,
        SignInContext context
    ) {
        ArgumentNullException.ThrowIfNull(context);

        var sessionId = Guid.NewGuid();
        var tenant = Tenant(tenantId);

        var opened = await tenant
            .GetGrain<ISessionGrain>(GrainKeys.Session(sessionId))
            .OpenAsync(
                userId,
                context.ClientId,
                context.DeviceLabel,
                CredentialDigest.AddressDigest(context.ClientAddress),
                [method]
            );

        if (opened.TryGetError(out _)) {
            return UniformFailures.RejectSignIn();
        }

        await tenant.GetGrain<IUserGrain>(GrainKeys.User(userId)).TrackSessionAsync(sessionId);

        IdentityLog.SignInSucceeded(logger, tenantId, userId, sessionId, method);

        // ⚠ A passkey assertion with user verification is already two factors. Asking for a TOTP
        // afterwards is theatre that teaches users to reach for the weaker credential — docs/plan/11
        // § Credentials makes passkeys the default rather than an upsell for exactly this reason.
        return Result<SignInOutcome>.Success(
            SignInOutcome.Success(userId, sessionId, method, method != AuthenticationMethod.Passkey)
        );
    }

    async Task<Guid?> ResolveAsync(Guid tenantId, string address, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = await Tenant(tenantId)
            .GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(tenantId, address))
            .ResolveAsync();

        return resolved.IsSuccess ? resolved.GetValueOrThrow() : null;
    }

    async Task RecordFailureAsync(LockoutKey identifier, Guid? userId, CancellationToken cancellationToken) {
        await lockout.RecordFailureAsync(identifier, cancellationToken);

        // ⚠ The per-user counter is what makes "this account is locked" true across every address
        // that resolves to it, which matters after an email change. It is incremented only when the
        // user is known, which is fine — the identifier counter is the one that has to work for an
        // address with no account, and it does.
        if (userId is { } id) {
            await lockout.RecordFailureAsync(LockoutKey.ForUser(id), cancellationToken);
        }
    }

    async Task PadAsync(long startedAt, CancellationToken cancellationToken) {
        if (options.MinimumDuration <= TimeSpan.Zero) {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (elapsed >= options.MinimumDuration) {
            return;
        }

        // ⚠ Padding to a FIXED floor, not adding a random delay. A random delay averages out over
        // enough samples and leaves the underlying difference measurable; a fixed floor removes the
        // signal entirely for anything that finishes under it. The cost is that every sign-in takes
        // at least this long, which is a deliberate trade docs/plan/11 § Credentials is asking for.
        try {
            await Task.Delay(options.MinimumDuration - elapsed, cancellationToken);
        } catch (OperationCanceledException) {
            // A cancelled pad is a cancelled request. Rethrowing here would replace whatever the
            // caller was about to receive with a cancellation, including a successful sign-in.
        }
    }

    // ⚠ The return type is TenantGrainFactory and NOT IGrainFactory, deliberately. CC1006's
    // discriminator is the TYPE rather than the syntax — ForTenant returns a distinct factory with
    // its own GetGrain — so widening this helper to IGrainFactory would erase exactly the thing the
    // analyzer reads, and every call below would become an unqualified reference again.
    TenantGrainFactory Tenant(Guid tenantId) =>
        grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    static string Digest(Guid tenantId, string address) =>
        LockoutKey.ForIdentifier(tenantId, address).Value;
}
