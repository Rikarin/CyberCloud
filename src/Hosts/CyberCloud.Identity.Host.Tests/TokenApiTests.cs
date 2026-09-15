using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.Seams;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The token endpoint's decisions — <see cref="TokenApi" /> — without a cluster, a vault or a
///     <c>TestServer</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two properties, asserted from opposite sides.</b> Everything that can be refused
///         before a grain is reached is refused before a grain is reached, which
///         <see cref="RefusingGrainFactory" /> turns into a thrown exception if it stops being true;
///         and everything that is refused after one is reached answers the same sentence, which is
///         what keeps <c>/token</c> from enumerating a tenant's service principals.
///     </para>
///     <para>
///         What is not here: the signed token. Minting needs OpenIddict's server pipeline and a key,
///         and the test that reads a real one is <c>CyberCloud.AppHost.Tests</c>'
///         <c>TenantOverHttpTests</c>, which takes it from the running host and hands it to the
///         gateway.
///     </para>
/// </remarks>
public sealed class TokenApiTests {
    static readonly Guid Tenant = SignInApiHarness.Tenant;
    static readonly Guid PrincipalId = Guid.Parse("7c0e3b52-1d4f-4a8b-9e6c-2f1a0b3c4d5e");
    static readonly SecretRef Credential = new() { Path = "tenants/t/sp/ci", Field = "secret" };

    static readonly ServicePrincipalDescriptor Principal = new() {
        ServicePrincipalId = PrincipalId,
        TenantId = Tenant,
        DisplayName = "ci",
        Enabled = true,
        CredentialSecretRef = Credential
    };

    static string ClientId => PrincipalId.ToString("N");

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "a-secret")]
    [InlineData("7c0e3b521d4f4a8b9e6c2f1a0b3c4d5e", null)]
    [InlineData("7c0e3b521d4f4a8b9e6c2f1a0b3c4d5e", "")]
    public async Task MissingCredentialsAreRefusedBeforeAnyGrain(string? clientId, string? clientSecret) {
        var api = Build(new RefusingGrainFactory(), new UnavailableClientSecrets());

        var refused = await api.AuthenticateClientAsync(clientId, clientSecret, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldBe(TokenApi.InvalidClientDescription);
    }

    [Theory]
    [InlineData("portal")]
    [InlineData("7c0e3b52-1d4f-4a8b-9e6c-2f1a0b3c4d5e")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("sp/7c0e3b521d4f4a8b9e6c2f1a0b3c4d5e")]
    public async Task AClientIdThatIsNotAServicePrincipalIdIsRefusedBeforeAnyGrain(string clientId) {
        // ⚠ The D form, the platform GUID and a grain-key-shaped string are all refused without a
        // grain call. A key built from caller input is a grain activated by caller input, and the
        // endpoint is unauthenticated — see TokenApi's remarks on why the id is parsed strictly.
        var api = Build(new RefusingGrainFactory(), new UnavailableClientSecrets());

        var refused = await api.AuthenticateClientAsync(clientId, "a-secret", TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldBe(TokenApi.InvalidClientDescription);
    }

    [Fact]
    public async Task AnUnknownPrincipalADisabledOneAWrongSecretAndAnUnwiredVaultAllAnswerTheSameSentence() {
        // ⚠ FOUR REASONS, ONE SENTENCE. Anything that told them apart would let an unauthenticated
        // caller learn which GUIDs are service principals in this tenant, which of those are
        // enabled, and which have a credential — from the token endpoint, at volume.
        var unknown = Build(new OneServicePrincipal(null), new OneSecret(Credential, "right"));
        var disabled = Build(new OneServicePrincipal(Principal with { Enabled = false }), new OneSecret(Credential, "right"));
        var wrong = Build(new OneServicePrincipal(Principal), new OneSecret(Credential, "right"));
        var unwired = Build(new OneServicePrincipal(Principal), new UnavailableClientSecrets());

        foreach (var (api, secret) in new[] { (unknown, "right"), (disabled, "right"), (wrong, "wrong"), (unwired, "right") }) {
            var refused = await api.AuthenticateClientAsync(ClientId, secret, TestContext.Current.CancellationToken);

            refused.IsFailure.ShouldBeTrue();
            refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
            refused.Error.Message.ShouldBe(TokenApi.InvalidClientDescription);
        }
    }

    [Fact]
    public async Task TheUnwiredVaultsSentenceReachesTheLogAndNotTheCaller() {
        // ⚠ The one line an operator whose host has no verifier will see. It names the seam to
        // register; the caller sees invalid_client and nothing else.
        var logger = new CapturingLogger();
        var api = Build(new OneServicePrincipal(Principal), new UnavailableClientSecrets(), logger);

        var refused = await api.AuthenticateClientAsync(ClientId, "right", TestContext.Current.CancellationToken);

        refused.Error!.Message.ShouldNotContain("IClientSecretSeam");
        logger.Messages.ShouldContain(x => x.Contains("IClientSecretSeam", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARightSecretAuthenticatesThePrincipal() {
        var api = Build(new OneServicePrincipal(Principal), new OneSecret(Credential, "right"));

        var authenticated = await api.AuthenticateClientAsync(ClientId, "right", TestContext.Current.CancellationToken);

        authenticated.IsSuccess.ShouldBeTrue(authenticated.Error?.Message);
        authenticated.GetValueOrThrow().ServicePrincipalId.ShouldBe(PrincipalId);
    }

    [Fact]
    public void MintingCarriesTheContractsAudienceAsBothClaimAndRegisteredAudience() {
        var api = Build(new RefusingGrainFactory(), new UnavailableClientSecrets());

        var principal = api.Mint(Principal, ClientId, ["cyc.api"]);

        // ⚠ Both, from one value. OpenIddict writes `aud` from the registered audiences and the
        // gateway pins `aud` to AccessTokenPolicy.Audience; the factory's claim is the contract's
        // half and the registration is the library's, and a principal with only one of them mints a
        // token the gateway refuses.
        principal.FindFirst(AccessTokenClaims.Audience)!.Value.ShouldBe(AccessTokenPolicy.Audience);
        principal.GetAudiences().ShouldBe([AccessTokenPolicy.Audience]);
        principal.GetPresenters().ShouldBe([ClientId]);
        principal.FindFirst(AccessTokenClaims.SubjectType)!.Value.ShouldBe(SubjectTypes.ServicePrincipal);
        principal.FindFirst(AccessTokenClaims.Scope)!.Value.ShouldBe("cyc.api");
    }

    static TokenApi Build(IGrainFactory grains, IClientSecretSeam secrets, ILogger<TokenApi>? logger = null) =>
        new(
            grains,
            secrets,
            Options.Create(new IdentityHostOptions { TenantId = Tenant }),
            new SystemClock(),
            logger ?? new CapturingLogger()
        );

    /// <summary>A vault holding exactly one secret behind one handle, compared in constant time.</summary>
    sealed class OneSecret(SecretRef known, string secret) : IClientSecretSeam {
        public Task<Result<bool>> VerifyAsync(SecretRef reference, string presented, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<bool>.Success(
                    reference == known
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret))
                )
            );
    }

    /// <summary>
    ///     A cluster holding at most one service principal, and refusing every other reference.
    /// </summary>
    /// <remarks>
    ///     ⚠ Only the string-keyed overload answers, because that is the one <c>ForTenant</c>'s
    ///     factory calls with the tenant-qualified key; every other member is
    ///     <see cref="RefusingGrainFactory" />'s, so a path that reached for anything but the
    ///     service principal grain still fails loudly.
    /// </remarks>
    sealed class OneServicePrincipal(ServicePrincipalDescriptor? descriptor) : IGrainFactory {
        readonly RefusingGrainFactory refusing = new();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey =>
            typeof(TGrainInterface) == typeof(IServicePrincipalGrain)
                ? (TGrainInterface)(object)new Grain(descriptor)
                : refusing.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => refusing.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => refusing.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey =>
            refusing.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey =>
            refusing.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => refusing.GetGrain(grainInterfaceType, grainPrimaryKey);

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => refusing.GetGrain(grainInterfaceType, grainPrimaryKey);

        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => refusing.GetGrain(grainInterfaceType, grainPrimaryKey);

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) =>
            refusing.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) =>
            refusing.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable => refusing.GetGrain<TGrainInterface>(grainId);

        public IAddressable GetGrain(GrainId grainId) => refusing.GetGrain(grainId);

        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => refusing.GetGrain(interfaceType, grainKey);

        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) =>
            refusing.GetGrain(interfaceType, grainKey, grainClassNamePrefix);

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => refusing.GetGrain(grainId, interfaceType);

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => default!;

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver { }

        sealed class Grain(ServicePrincipalDescriptor? descriptor) : IServicePrincipalGrain {
            public Task<Result<ServicePrincipalDescriptor>> GetAsync() =>
                Task.FromResult(
                    descriptor is { } found
                        ? Result<ServicePrincipalDescriptor>.Success(found)
                        : Result<ServicePrincipalDescriptor>.Failure(ErrorCode.ResourceNotFound, "no such principal")
                );

            public Task<Result<ServicePrincipalDescriptor>> CreateAsync(ServicePrincipalDescriptor descriptor) =>
                throw new NotSupportedException();

            public Task<Result<ServicePrincipalDescriptor>> SetEnabledAsync(bool enabled) => throw new NotSupportedException();

            public Task<Result<ServicePrincipalDescriptor>> RotateCredentialAsync(SecretRef credentialSecretRef) =>
                throw new NotSupportedException();

            public Task<Result> DeleteAsync() => throw new NotSupportedException();

            public Task DeactivateAsync() => throw new NotSupportedException();
        }
    }

    /// <summary>Every rendered message, so the log can be asserted on.</summary>
    sealed class CapturingLogger : ILogger<TokenApi> {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Enqueue(formatter(state, exception));
        }
    }
}
