using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.SignUp;
using CyberCloud.Identity.SignIn;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     A <see cref="SignUpApi" /> over in-memory grains and a recording scope manager — the seam
///     the sign-up orchestrator drives, with every call it makes visible.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Doubles at the grain seam, deliberately, and the doubles are as thin as they can be.</b>
///         <c>SignUpGrainTests</c> in <c>CyberCloud.Identity.Tests</c> holds the real
///         <c>SignUpGrain</c> to its properties against a real silo; what is under test here is what
///         the <i>host</i> does with what the grains answer — which steps it runs, in which order,
///         as whom, and what it tells the person. Every fake here answers the way its production
///         grain would on the path the suite drives and throws <see cref="NotSupportedException" />
///         everywhere else, so a handler that started calling something new fails loudly rather
///         than passing against a stub.
///     </para>
///     <para>
///         ⚠ <b>The scope manager is a double because the real one needs a cluster</b>, and because
///         the claim about it — who each call is made as — is a claim about the
///         <c>CallerContext</c> the orchestrator hands over, which is exactly what
///         <see cref="RecordingScopeManager" /> keeps.
///     </para>
/// </remarks>
public sealed class SignUpApiHarness {
    /// <summary>The region the harness's host is configured with.</summary>
    public const string Region = "local";

    /// <summary>The grain factory, with every fake reachable through it.</summary>
    public FakeGrainFactory Grains { get; } = new();

    /// <summary>The scope manager, recording every call.</summary>
    public RecordingScopeManager Scopes { get; } = new();

    /// <summary>The WebAuthn double.</summary>
    public ScriptedPasskeyService Passkeys { get; } = new();

    /// <summary>The clock.</summary>
    public SystemClock Clock { get; } = new();

    /// <summary>
    ///     The clock the caller ladder reads. ⚠ Frozen rather than the real one, so a held caller
    ///     stays held for exactly as long as a test says and no test races a one-second lock.
    /// </summary>
    public FrozenClock LadderClock { get; } = new();

    /// <summary>The address every begin comes from unless a test says otherwise.</summary>
    public const string Caller = "203.0.113.7";

    /// <summary>The API, over the harness.</summary>
    public SignUpApi Api { get; }

    /// <summary>Builds the harness.</summary>
    /// <param name="selfServe">Whether sign-up is open.</param>
    public SignUpApiHarness(bool selfServe = true) {
        var options = Options.Create(new IdentityHostOptions { SelfServeSignUp = selfServe, DefaultRegion = Region });

        var signIn = new SignInService(
            Grains,
            new InMemoryLockoutCounter(Clock),
            new Argon2idPasswordHasher(Argon2idOptions.Default),
            NullLogger<SignInService>.Instance,
            SignInOptions.Default with { MinimumDuration = TimeSpan.Zero }
        );

        var orchestrator = new SignUpOrchestrator(
            Scopes,
            Grains,
            signIn,
            options,
            NullLogger<SignUpOrchestrator>.Instance
        );

        Api = new(
            Grains,
            Passkeys,
            orchestrator,
            new InMemoryLockoutCounter(LadderClock),
            options,
            SignInOptions.Default with { MinimumDuration = TimeSpan.Zero },
            Clock,
            NullLogger<SignUpApi>.Instance
        );
    }

    /// <summary>
    ///     Begins and verifies a sign-up the way the page does, and hands back the ticket
    ///     <c>complete</c> needs.
    /// </summary>
    /// <param name="email">The address.</param>
    public async Task<SignUpTicket> VerifiedSignUpAsync(string email = "rene@example.com") {
        var begun = await Api.BeginAsync(new(email, "/"), null, Caller, TestContext.Current.CancellationToken);
        var ticket = begun.Ticket.ShouldNotBeNull();

        var code = Grains.SignUps[ticket.SignupId].Code.ShouldNotBeNull();
        var verified = await Api.VerifyAsync(new(code), ticket);
        verified.Body.ShouldBeOfType<SignUpVerifyResponse>().Verified.ShouldBeTrue();

        return ticket;
    }

    /// <summary>The user grain the sign-up created, if any.</summary>
    /// <param name="ticket">The sign-up.</param>
    public FakeUserGrain? UserOf(SignUpTicket ticket) {
        var signup = Grains.SignUps[ticket.SignupId];
        return Grains.Users.GetValueOrDefault((signup.TenantId, signup.UserId));
    }
}

/// <summary>A clock that moves only when a test moves it.</summary>
public sealed class FrozenClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 18, 9, 30, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
///     An <see cref="IGrainFactory" /> that hands out the harness's fakes, keyed the way
///     <c>Orleans.Multitenant</c> keys them, and counts every reference.
/// </summary>
public sealed class FakeGrainFactory : IGrainFactory {
    /// <summary>Every sign-up grain reached, by id.</summary>
    public Dictionary<Guid, FakeSignUpGrain> SignUps { get; } = [];

    /// <summary>Every user grain reached, by (tenant, user).</summary>
    public Dictionary<(Guid Tenant, Guid User), FakeUserGrain> Users { get; } = [];

    /// <summary>Every email index reached, by its tenant-qualified key.</summary>
    public Dictionary<string, FakeEmailIndexGrain> EmailIndexes { get; } = new(StringComparer.Ordinal);

    /// <summary>Every session grain reached, by its tenant-qualified key.</summary>
    public Dictionary<string, FakeSessionGrain> Sessions { get; } = new(StringComparer.Ordinal);

    /// <summary>The one directory. Slugs put here read as taken.</summary>
    public FakeTenantDirectoryGrain Directory { get; } = new();

    /// <summary>How many grain references have been taken.</summary>
    public int References { get; private set; }

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey {
        References++;

        var (tenant, within) = Split(primaryKey);

        object grain = typeof(TGrainInterface) switch {
            var t when t == typeof(ISignUpGrain) => SignUps.GetOrAdd(
                Id(within),
                static id => new FakeSignUpGrain().WithId(id)
            ),
            var t when t == typeof(IUserGrain) => Users.GetOrAdd((tenant, Id(within)), static _ => new()),
            var t when t == typeof(IEmailIndexGrain) => EmailIndexes.GetOrAdd(primaryKey, static _ => new()),
            var t when t == typeof(ISessionGrain) => Sessions.GetOrAdd(primaryKey, static _ => new()),
            var t when t == typeof(ITenantDirectoryGrain) => Directory,
            var t => throw new NotSupportedException(
                $"The sign-up path reached for {t.Name} ('{primaryKey}'), which the harness does not fake. "
                + "Either the orchestrator grew a step or a fake is missing — see SignUpApiHarness."
            )
        };

        return (TGrainInterface)grain;
    }

    static (Guid Tenant, string Within) Split(string primaryKey) {
        var separator = primaryKey.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0) {
            return (Guid.Empty, primaryKey);
        }

        return (Guid.Parse(primaryKey[..separator]), primaryKey[(separator + 1)..]);
    }

    static Guid Id(string within) => GrainKeys.Parse(within).GetValueOrThrow().Id;

    static NotSupportedException Unkeyed() =>
        new("Every grain on the sign-up path is IGrainWithStringKey; anything else is a mistake.");

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidKey =>
        throw Unkeyed();

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerKey =>
        throw Unkeyed();

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(
        Guid primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null
    )
        where TGrainInterface : IGrainWithGuidCompoundKey =>
        throw Unkeyed();

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(
        long primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null
    )
        where TGrainInterface : IGrainWithIntegerCompoundKey =>
        throw Unkeyed();

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw Unkeyed();

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw Unkeyed();

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw Unkeyed();

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw Unkeyed();

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw Unkeyed();

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
        where TGrainInterface : IAddressable =>
        throw Unkeyed();

    /// <inheritdoc />
    public IAddressable GetGrain(GrainId grainId) => throw Unkeyed();

    /// <inheritdoc />
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw Unkeyed();

    /// <inheritdoc />
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw Unkeyed();

    /// <inheritdoc />
    public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw Unkeyed();

    /// <inheritdoc />
    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver =>
        throw Unkeyed();

    /// <inheritdoc />
    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver =>
        throw Unkeyed();
}

/// <summary>A small helper so a fake is created once per key.</summary>
static class FakeDictionaries {
    public static TValue GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary,
        TKey key,
        Func<TKey, TValue> create
    )
        where TKey : notnull {
        if (!dictionary.TryGetValue(key, out var value)) {
            value = create(key);
            dictionary[key] = value;
        }

        return value;
    }
}

/// <summary>
///     <see cref="ISignUpGrain" /> in memory — the same state machine, with the code readable so
///     the harness can type it.
/// </summary>
public sealed class FakeSignUpGrain : ISignUpGrain {
    /// <summary>The outstanding code, or <see langword="null" />.</summary>
    public string? Code { get; private set; }

    /// <summary>The address.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>The pre-allocated tenant.</summary>
    public Guid TenantId { get; } = Guid.NewGuid();

    /// <summary>The pre-allocated user.</summary>
    public Guid UserId { get; } = Guid.NewGuid();

    /// <summary>The pre-allocated subscription.</summary>
    public Guid SubscriptionId { get; } = Guid.NewGuid();

    /// <summary>Whether the address is proven.</summary>
    public bool Verified { get; private set; }

    /// <summary>The recorded steps.</summary>
    public List<SignUpStep> Steps { get; } = [];

    /// <summary>How many times a code was issued.</summary>
    public int Issues { get; private set; }

    Guid signupId;

    /// <inheritdoc />
    public Task<Result> BeginAsync(string email) {
        if (Steps.Contains(SignUpStep.Completed)) {
            return Task.FromResult(Result.Failure(ErrorCode.Conflict, "completed"));
        }

        Email = GrainKeys.NormalizeEmail(email).GetValueOrThrow();
        Code = "482913";
        Issues++;
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<bool>> VerifyAsync(string candidate) {
        var matched = Code is not null && string.Equals(Code, candidate, StringComparison.Ordinal);
        if (matched) {
            Verified = true;
            Code = null;
        }

        return Task.FromResult(Result<bool>.Success(matched));
    }

    /// <inheritdoc />
    public Task<Result<SignUpDescriptor>> GetAsync() =>
        Task.FromResult(
            Email.Length == 0
                ? Result<SignUpDescriptor>.Failure(ErrorCode.ResourceNotFound, "no such sign-up")
                : Result<SignUpDescriptor>.Success(
                    new() {
                        SignupId = signupId,
                        Email = Email,
                        TenantId = TenantId,
                        UserId = UserId,
                        SubscriptionId = SubscriptionId,
                        Verified = Verified,
                        CompletedSteps = [.. Steps],
                        ExpiresAt = DateTimeOffset.MaxValue
                    }
                )
        );

    /// <inheritdoc />
    public Task<Result> RecordStepAsync(SignUpStep completed) {
        if (!Verified) {
            return Task.FromResult(Result.Failure(ErrorCode.AuthorizationFailed, "not verified"));
        }

        if (Steps.Contains(SignUpStep.Completed)) {
            return Task.FromResult(Result.Failure(ErrorCode.Conflict, "completed"));
        }

        if (!Steps.Contains(completed)) {
            Steps.Add(completed);
        }

        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result> CompleteAsync() => RecordStepAsync(SignUpStep.Completed);

    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>Stamps the id the factory keyed this fake by.</summary>
    public FakeSignUpGrain WithId(Guid id) {
        signupId = id;
        return this;
    }
}

/// <summary>
///     <see cref="IUserGrain" /> in memory — create, one credential, and session tracking; nothing
///     else is on the sign-up path.
/// </summary>
public sealed class FakeUserGrain : IUserGrain {
    /// <summary>How many times <see cref="CreateAsync" /> ran.</summary>
    public int Creates { get; private set; }

    /// <summary>The address it was created with.</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>The display name it was created with.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>The password set, or <see langword="null" />.</summary>
    public string? Password { get; private set; }

    /// <summary>The passkey enrolled, or <see langword="null" />.</summary>
    public PasskeyCredential? Passkey { get; private set; }

    /// <summary>The sessions tracked.</summary>
    public List<Guid> Sessions { get; } = [];

    /// <inheritdoc />
    public Task<Result<UserProfile>> CreateAsync(string email, string displayName, UserStatus status) {
        Creates++;
        Email = email;
        DisplayName = displayName;
        return Task.FromResult(
            Result<UserProfile>.Success(new() { Email = email, DisplayName = displayName, Status = status })
        );
    }

    /// <inheritdoc />
    public Task<Result> SetPasswordAsync(string candidate) {
        if (string.IsNullOrEmpty(candidate)) {
            // The real grain's sentence, verbatim in shape: a refusal the person reads.
            return Task.FromResult(Result.Failure(ErrorCode.InvalidRequestBody, "A password is required."));
        }

        Password = candidate;
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<UserProfile>> AddPasskeyAsync(PasskeyCredential credential) {
        Passkey = credential;
        return Task.FromResult(Result<UserProfile>.Success(new() { Email = Email, DisplayName = DisplayName }));
    }

    /// <inheritdoc />
    public Task<Result> TrackSessionAsync(Guid sessionId) {
        Sessions.Add(sessionId);
        return Task.FromResult(Result.Success);
    }

    static NotSupportedException OffPath(string member) =>
        new($"IUserGrain.{member} is not on the sign-up path. See SignUpApiHarness.");

    /// <inheritdoc />
    public Task<Result<UserProfile>> GetAsync() => throw OffPath(nameof(GetAsync));

    /// <inheritdoc />
    public Task<Result<UserProfile>> SetStatusAsync(UserStatus status) => throw OffPath(nameof(SetStatusAsync));

    /// <inheritdoc />
    public Task<Result<UserProfile>> ChangeEmailAsync(string email) => throw OffPath(nameof(ChangeEmailAsync));

    /// <inheritdoc />
    public Task<Result<UserProfile>> SetDisplayNameAsync(string displayName) =>
        throw OffPath(nameof(SetDisplayNameAsync));

    /// <inheritdoc />
    public Task<Result<bool>> VerifyPasswordAsync(string candidate) => throw OffPath(nameof(VerifyPasswordAsync));

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<PasskeyCredential>>> ListPasskeysAsync() =>
        throw OffPath(nameof(ListPasskeysAsync));

    /// <inheritdoc />
    public Task<Result<bool>> RecordPasskeyAssertionAsync(string credentialId, uint signCount) =>
        throw OffPath(nameof(RecordPasskeyAssertionAsync));

    /// <inheritdoc />
    public Task<Result<UserProfile>> RemovePasskeyAsync(string credentialId) =>
        throw OffPath(nameof(RemovePasskeyAsync));

    /// <inheritdoc />
    public Task<Result<UserProfile>> EnrollTotpAsync(TotpEnrollment enrollment) =>
        throw OffPath(nameof(EnrollTotpAsync));

    /// <inheritdoc />
    public Task<Result<TotpEnrollment>> GetTotpAsync() => throw OffPath(nameof(GetTotpAsync));

    /// <inheritdoc />
    public Task<Result<bool>> ClaimTotpCounterAsync(long counter) => throw OffPath(nameof(ClaimTotpCounterAsync));

    /// <inheritdoc />
    public Task<Result> IssueOtpAsync(OtpPurpose purpose, CredentialKind kind) => throw OffPath(nameof(IssueOtpAsync));

    /// <inheritdoc />
    public Task<Result<bool>> RedeemOtpAsync(OtpPurpose purpose, string candidate) =>
        throw OffPath(nameof(RedeemOtpAsync));

    /// <inheritdoc />
    public Task<Result<RecoveryCodeBatch>> GenerateRecoveryCodesAsync() =>
        throw OffPath(nameof(GenerateRecoveryCodesAsync));

    /// <inheritdoc />
    public Task<Result<bool>> RedeemRecoveryCodeAsync(string candidate) =>
        throw OffPath(nameof(RedeemRecoveryCodeAsync));

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<Guid>>> ListSessionsAsync() => throw OffPath(nameof(ListSessionsAsync));

    /// <inheritdoc />
    public Task<Result> ForgetSessionAsync(Guid sessionId) => throw OffPath(nameof(ForgetSessionAsync));

    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
}

/// <summary><see cref="IEmailIndexGrain" /> in memory — claim and confirm.</summary>
public sealed class FakeEmailIndexGrain : IEmailIndexGrain {
    /// <summary>The user bound, or <see cref="Guid.Empty" />.</summary>
    public Guid BoundTo { get; private set; }

    /// <summary>Whether the claim was confirmed.</summary>
    public bool Confirmed { get; private set; }

    /// <inheritdoc />
    public Task<Result<IndexEntry>> TryClaimAsync(string email, Guid userId) {
        if (BoundTo != Guid.Empty && BoundTo != userId) {
            return Task.FromResult(Result<IndexEntry>.Failure(ErrorCode.Conflict, "taken"));
        }

        BoundTo = userId;
        return Task.FromResult(Result<IndexEntry>.Success(new() { BoundTo = userId, IndexedValue = email }));
    }

    /// <inheritdoc />
    public Task<Result<IndexEntry>> ConfirmAsync(Guid userId) {
        Confirmed = BoundTo == userId;
        return Task.FromResult(Result<IndexEntry>.Success(new() { BoundTo = userId }));
    }

    /// <inheritdoc />
    public Task<Result> ReleaseAsync(Guid userId) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<IndexEntry>> GetAsync() => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<Guid>> ResolveAsync() => throw new NotSupportedException();

    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
}

/// <summary><see cref="ISessionGrain" /> in memory — open, and nothing else.</summary>
public sealed class FakeSessionGrain : ISessionGrain {
    /// <summary>The methods the session was opened with.</summary>
    public IReadOnlyList<AuthenticationMethod> Methods { get; private set; } = [];

    /// <inheritdoc />
    public Task<Result<RefreshRotation>> OpenAsync(
        Guid userId,
        string clientId,
        string deviceLabel,
        string clientAddressDigest,
        IReadOnlyList<AuthenticationMethod> methods
    ) {
        Methods = methods;
        return Task.FromResult(Result<RefreshRotation>.Success(new() { Handle = "h", Generation = 1 }));
    }

    /// <inheritdoc />
    public Task<Result<RefreshRotation>> RefreshAsync(string presented) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<SessionDescriptor>> GetAsync() => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<bool>> IsLiveAsync() => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result> RevokeAsync(RevocationReason reason) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<int>> ChainLengthAsync() => throw new NotSupportedException();

    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
}

/// <summary><see cref="ITenantDirectoryGrain" /> in memory — a slug lookup over a list the test fills.</summary>
public sealed class FakeTenantDirectoryGrain : ITenantDirectoryGrain {
    /// <summary>Slugs that read as held, with the tenant holding them.</summary>
    public Dictionary<string, Guid> Held { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Result<TenantDirectoryEntry>> LookupBySlugAsync(string slug) =>
        Task.FromResult(
            Held.TryGetValue(slug, out var tenant)
                ? Result<TenantDirectoryEntry>.Success(
                    new() { TenantId = tenant, Slug = slug, Status = TenantStatus.Active }
                )
                : Result<TenantDirectoryEntry>.Failure(ErrorCode.TenantNotFound, "no such slug")
        );

    /// <inheritdoc />
    public Task<Result<TenantDirectoryEntry>> RegisterAsync(TenantDirectoryEntry entry) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<TenantDirectoryEntry>> LookupAsync(Guid tenantId) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<TenantDirectoryEntry>> SetStatusAsync(Guid tenantId, TenantStatus status) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<TenantDirectoryDelta>> GetDeltaAsync(long knownVersion) => throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<int>> CountAsync() => throw new NotSupportedException();

    /// <inheritdoc />
    public Task DeactivateAsync() => Task.CompletedTask;
}

/// <summary>
///     An <see cref="IScopeManager" /> that records who asked for what and can be told to fail one
///     call — the instrument for "as whom" and for "the retry resumes at the next step".
/// </summary>
public sealed class RecordingScopeManager : IScopeManager {
    /// <summary>Every <see cref="CreateTenantAsync" /> call, with its caller.</summary>
    public List<(TenantCreateRequest Request, CallerContext Caller)> TenantCreates { get; } = [];

    /// <summary>Every <see cref="CreateAsync" /> call.</summary>
    public List<ScopeRequest> Creates { get; } = [];

    /// <summary>A path whose next <see cref="CreateAsync" /> fails, once, with <see cref="FailWith" />.</summary>
    public string? FailOnceAt { get; set; }

    /// <summary>The code the scripted failure carries.</summary>
    public ErrorCode FailWith { get; set; } = ErrorCode.InternalError;

    /// <summary>Whether <see cref="CreateTenantAsync" /> refuses with <see cref="ErrorCode.Conflict" />.</summary>
    public bool TenantSlugConflicts { get; set; }

    /// <inheritdoc />
    public Task<Result<ScopeSnapshot>> CreateTenantAsync(
        TenantCreateRequest request,
        CallerContext caller,
        CancellationToken cancellationToken = default
    ) {
        TenantCreates.Add((request, caller));

        return Task.FromResult(
            TenantSlugConflicts
                ? Result<ScopeSnapshot>.Failure(ErrorCode.Conflict, $"Slug '{request.Slug}' is already held.")
                : Result<ScopeSnapshot>.Success(
                    new() {
                        Path = ScopeId.Tenant(request.TenantId).Path,
                        Kind = ScopeKind.Tenant,
                        Name = request.Slug,
                        Created = true
                    }
                )
        );
    }

    /// <inheritdoc />
    public Task<Result<ScopeSnapshot>> CreateAsync(
        ScopeRequest request,
        CancellationToken cancellationToken = default
    ) {
        Creates.Add(request);

        if (string.Equals(FailOnceAt, request.Path, StringComparison.Ordinal)) {
            FailOnceAt = null;
            return Task.FromResult(Result<ScopeSnapshot>.Failure(FailWith, "scripted failure"));
        }

        return Task.FromResult(
            Result<ScopeSnapshot>.Success(new() { Path = request.Path, Created = true, Name = request.Path })
        );
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(ScopeRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<ScopeSnapshot>> ReadAsync(ScopeRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<ScopeListPage>> ListAsync(
        ScopeListRequest request,
        CancellationToken cancellationToken = default
    ) =>
        throw new NotSupportedException();
}

/// <summary>An <see cref="IPasskeyService" /> that issues a registration challenge and accepts any attestation.</summary>
public sealed class ScriptedPasskeyService : IPasskeyService {
    /// <summary>The credential every completed registration produces.</summary>
    public PasskeyCredential Credential { get; } = new() { CredentialId = "cred-1", PublicKey = "pk" };

    /// <summary>What <see cref="BeginRegistrationAsync" /> was asked for.</summary>
    public PasskeyRegistrationRequest? Requested { get; private set; }

    /// <inheritdoc />
    public Task<Result<PasskeyRegistrationChallenge>> BeginRegistrationAsync(PasskeyRegistrationRequest request) {
        Requested = request;
        return Task.FromResult(
            Result<PasskeyRegistrationChallenge>.Success(
                new() {
                    OptionsJson = """{"challenge":"abc"}""",
                    UserId = request.UserId,
                    ExpiresAt = DateTimeOffset.MaxValue
                }
            )
        );
    }

    /// <inheritdoc />
    public Task<Result<PasskeyCredential>> CompleteRegistrationAsync(
        PasskeyRegistrationChallenge challenge,
        string attestationJson
    ) =>
        Task.FromResult(Result<PasskeyCredential>.Success(Credential));

    /// <inheritdoc />
    public Task<Result<PasskeyAssertionChallenge>> BeginAssertionAsync(IReadOnlyList<PasskeyCredential> credentials) =>
        throw new NotSupportedException();

    /// <inheritdoc />
    public Task<Result<uint>> CompleteAssertionAsync(
        PasskeyAssertionChallenge challenge,
        string assertionJson,
        PasskeyCredential credential
    ) =>
        throw new NotSupportedException();
}
