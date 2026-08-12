using CyberCloud.Core;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Seams;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     A <see cref="SignInApi" /> that can answer without a cluster behind it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="RefusingGrainFactory" /> throws on every member, and that is the point
///         rather than a shortcut.</b> Every path exercised by the suites in this project is one that
///         must answer <i>before</i> touching a grain — a malformed address, a passkey completion
///         with no issued challenge, a second factor with no session. Giving the harness a factory
///         that works would let one of those quietly acquire a grain reference and still pass; a
///         factory that throws turns the same regression into a failing test with a stack trace
///         pointing at the line that reached for it.
///     </para>
///     <para>
///         This is the same property <c>LockoutIsGrainFreeTests</c> asserts by counting in
///         <c>CyberCloud.Identity.Tests</c>, arrived at from the other side. That suite has a real
///         cluster and counts references; this project has neither a cluster nor a
///         <c>TestServer</c> — see the <c>.csproj</c> — so it asserts the same thing by making a
///         reference impossible.
///     </para>
///     <para>
///         ⚠ <b><see cref="SignInOptions.MinimumDuration" /> is zero here.</b> The 250 ms timing
///         floor is a real defence and it is <c>EnumerationAndTimingTests</c>'s to assert; paying it
///         on every row of a table-driven suite would add minutes and prove nothing these tests are
///         about.
///     </para>
/// </remarks>
public static class SignInApiHarness {
    /// <summary>The tenant these tests sign into. Arbitrary, and never the platform tenant.</summary>
    public static Guid Tenant { get; } = Guid.Parse("6f2b7c14-9a3d-4e58-b061-7c2d5e8f9a10");

    /// <summary>Builds the API over refusing infrastructure.</summary>
    public static SignInApi Build() {
        var grains = new RefusingGrainFactory();

        var signIn = new SignInService(
            grains,
            new InMemoryLockoutCounter(new SystemClock()),
            new Argon2idPasswordHasher(Argon2idOptions.Default),
            NullLogger<SignInService>.Instance,
            SignInOptions.Default with { MinimumDuration = TimeSpan.Zero }
        );

        return new(
            signIn,
            grains,
            new RefusingPasskeyService(),
            new UnavailableTotpSecrets(),
            Options.Create(new IdentityHostOptions { TenantId = Tenant }),
            new SystemClock(),
            NullLogger<SignInApi>.Instance
        );
    }
}

/// <summary>
///     An <see cref="IGrainFactory" /> that refuses to hand out a reference.
/// </summary>
/// <remarks>
///     ⚠ Reaching any member here means a code path that must be grain-free took a grain reference.
///     The message says so, because the stack trace alone would read as a missing test double.
/// </remarks>
public sealed class RefusingGrainFactory : IGrainFactory {
    static InvalidOperationException Refuse(string what) =>
        new(
            $"A grain reference ({what}) was taken on a path this suite drives, and every one of them "
            + "must answer before touching a grain. docs/plan/11 § Credentials: an authentication "
            + "endpoint whose failure path costs a grain activation is a denial-of-service "
            + "amplifier, and the attacker chooses the address. See SignInApiHarness."
        );

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidKey =>
        throw Refuse(typeof(TGrainInterface).Name);

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerKey =>
        throw Refuse(typeof(TGrainInterface).Name);

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey =>
        throw Refuse(typeof(TGrainInterface).Name);

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(
        Guid primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null
    )
        where TGrainInterface : IGrainWithGuidCompoundKey =>
        throw Refuse(typeof(TGrainInterface).Name);

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(
        long primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null
    )
        where TGrainInterface : IGrainWithIntegerCompoundKey =>
        throw Refuse(typeof(TGrainInterface).Name);

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) =>
        throw Refuse(grainInterfaceType.Name);

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) =>
        throw Refuse(grainInterfaceType.Name);

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) =>
        throw Refuse(grainInterfaceType.Name);

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) =>
        throw Refuse(grainInterfaceType.Name);

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) =>
        throw Refuse(grainInterfaceType.Name);

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
        where TGrainInterface : IAddressable =>
        throw Refuse(typeof(TGrainInterface).Name);

    /// <inheritdoc />
    public IAddressable GetGrain(GrainId grainId) =>
        throw Refuse(grainId.Type.ToString() ?? nameof(GrainId));

    /// <inheritdoc />
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw Refuse(interfaceType.Name);

    /// <inheritdoc />
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) =>
        throw Refuse(interfaceType.Name);

    /// <inheritdoc />
    public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) =>
        throw Refuse(interfaceType.ToString());

    // ⚠ The two observer members do NOT throw. They take no grain reference — an observer reference
    // is the reverse direction, a callback the cluster holds onto us — so refusing them would be
    // asserting something this harness is not about. Nothing in these suites calls them; returning
    // the default keeps that true without making the type lie about what it guards.
    /// <inheritdoc />
    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver =>
        default!;

    /// <inheritdoc />
    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver { }
}

/// <summary>
///     An <see cref="IPasskeyService" /> that refuses. ⚠ No path in this project should reach it —
///     the WebAuthn library's own behaviour is not what these suites are about.
/// </summary>
public sealed class RefusingPasskeyService : IPasskeyService {
    static Result<T> Refuse<T>()
        where T : notnull =>
        Result<T>.Failure(
            ErrorCode.InternalError,
            "This suite drives no path that reaches the WebAuthn library. See SignInApiHarness."
        );

    /// <inheritdoc />
    public Task<Result<PasskeyRegistrationChallenge>> BeginRegistrationAsync(PasskeyRegistrationRequest request) =>
        Task.FromResult(Refuse<PasskeyRegistrationChallenge>());

    /// <inheritdoc />
    public Task<Result<PasskeyCredential>> CompleteRegistrationAsync(
        PasskeyRegistrationChallenge challenge,
        string attestationJson
    ) =>
        Task.FromResult(Refuse<PasskeyCredential>());

    /// <inheritdoc />
    public Task<Result<PasskeyAssertionChallenge>> BeginAssertionAsync(
        IReadOnlyList<PasskeyCredential> credentials
    ) =>
        Task.FromResult(Refuse<PasskeyAssertionChallenge>());

    /// <inheritdoc />
    public Task<Result<uint>> CompleteAssertionAsync(
        PasskeyAssertionChallenge challenge,
        string assertionJson,
        PasskeyCredential credential
    ) =>
        Task.FromResult(Refuse<uint>());
}
