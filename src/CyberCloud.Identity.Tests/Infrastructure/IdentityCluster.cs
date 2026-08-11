using CyberCloud.Authorization;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.SignIn;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.Runtime;
using Orleans.TestingHost;
using System.Globalization;

namespace CyberCloud.Identity.Tests.Infrastructure;

/// <summary>A clock the tests drive. Every lease, window and lifetime in this module is minutes to days.</summary>
public sealed class TestClock : IClock {
    /// <summary>The one instance the silo resolves.</summary>
    public static TestClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;

    /// <summary>Puts time back to the start of a test.</summary>
    public void Reset() => UtcNow = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>
///     Argon2id at parameters a test suite can afford.
/// </summary>
/// <remarks>
///     ⚠ <b>Reducing the cost does not weaken the timing assertion, and it is worth being precise
///     about why.</b> The property under test is that the exists and does-not-exist branches take the
///     <i>same</i> time, not that either takes a particular time. Both branches run the same
///     <c>Verify</c> against the same parameters, so lowering them scales both identically. At
///     production parameters (<c>m=64MB, t=3, p=4</c>) one verification is tens of milliseconds and
///     the suite runs hundreds, which would be a minute of wall clock for one assertion.
///     <para>
///         What this does <i>not</i> prove is that the production parameters are affordable on the
///         request path. That is a load question and belongs to <c>CyberCloud.Load</c>.
///     </para>
/// </remarks>
public static class CheapArgon2 {
    /// <summary>8 MB, one pass, one lane. Fast enough for a suite, real enough to be the same code.</summary>
    public static Argon2idOptions Options { get; } = new() {
        MemoryKibibytes = 8_192,
        Iterations = 1,
        Parallelism = 1
    };

    /// <summary>A hasher at <see cref="Options" />, with no pepper.</summary>
    public static Argon2idPasswordHasher Hasher { get; } = new(Options);
}

/// <summary>
///     An <see cref="IGrainFactory" /> that records every grain reference taken through it.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the instrument for docs/plan/11 § Credentials' denial-of-service rule</b> — "an
///     authentication endpoint whose failure path costs a grain activation is a denial-of-service
///     amplifier". "Touches no grain" is a claim about the code path, and the only way to assert it
///     rather than read it is to count. Both <c>GetGrain</c> and <c>ForTenant</c> are counted,
///     because <c>ForTenant</c> is where a tenant-qualified reference begins and counting only the
///     former would miss a call that took a factory and did nothing with it — which is still an
///     object allocated per request on an unauthenticated path.
/// </remarks>
public sealed class RecordingGrainFactory(IGrainFactory inner) : IGrainFactory {
    /// <summary>How many grain references have been taken since the last <see cref="Reset" />.</summary>
    public int References { get; private set; }

    /// <summary>Every grain interface asked for, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Back to zero.</summary>
    public void Reset() {
        References = 0;
        Asked.Clear();
    }

    void Record<T>() {
        References++;
        Asked.Add(typeof(T).Name);
    }

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithGuidKey {
        Record<TGrainInterface>();
        return inner.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
    }

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithIntegerKey {
        Record<TGrainInterface>();
        return inner.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
    }

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
        where TGrainInterface : IGrainWithStringKey {
        Record<TGrainInterface>();
        return inner.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
    }

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(
        Guid primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null
    )
        where TGrainInterface : IGrainWithGuidCompoundKey {
        Record<TGrainInterface>();
        return inner.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);
    }

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(
        long primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null
    )
        where TGrainInterface : IGrainWithIntegerCompoundKey {
        Record<TGrainInterface>();
        return inner.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);
    }

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) {
        References++;
        Asked.Add(grainInterfaceType.Name);
        return inner.GetGrain(grainInterfaceType, grainPrimaryKey);
    }

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) {
        References++;
        Asked.Add(grainInterfaceType.Name);
        return inner.GetGrain(grainInterfaceType, grainPrimaryKey);
    }

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) {
        References++;
        Asked.Add(grainInterfaceType.Name);
        return inner.GetGrain(grainInterfaceType, grainPrimaryKey);
    }

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) {
        References++;
        Asked.Add(grainInterfaceType.Name);
        return inner.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);
    }

    /// <inheritdoc />
    public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) {
        References++;
        Asked.Add(grainInterfaceType.Name);
        return inner.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);
    }

    /// <inheritdoc />
    public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver =>
        inner.CreateObjectReference<TGrainObserverInterface>(obj);

    /// <inheritdoc />
    public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
        where TGrainObserverInterface : IGrainObserver =>
        inner.DeleteObjectReference<TGrainObserverInterface>(obj);

    /// <inheritdoc />
    public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
        where TGrainInterface : IAddressable {
        Record<TGrainInterface>();
        return inner.GetGrain<TGrainInterface>(grainId);
    }

    /// <inheritdoc />
    public IAddressable GetGrain(GrainId grainId) {
        References++;
        Asked.Add(grainId.Type.ToString() ?? string.Empty);
        return inner.GetGrain(grainId);
    }

    /// <inheritdoc />
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) {
        References++;
        Asked.Add(interfaceType.Name);
        return inner.GetGrain(interfaceType, grainKey);
    }

    /// <inheritdoc />
    public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix) {
        References++;
        Asked.Add(interfaceType.Name);
        return inner.GetGrain(interfaceType, grainKey, grainClassNamePrefix);
    }

    /// <inheritdoc />
    public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) {
        References++;
        Asked.Add(interfaceType.ToString());
        return inner.GetGrain(grainId, interfaceType);
    }
}

/// <summary>
///     An in-process Orleans cluster with the identity module wired as production wires it.
/// </summary>
/// <remarks>
///     ⚠ In-memory storage rather than Testcontainers — see this project's <c>.csproj</c> for exactly
///     what that costs and what is owed. Everything else — the grains, the tenancy index, the ReBAC
///     tuple store and check evaluator, the sign-in path — is the production wiring.
/// </remarks>
public sealed class IdentityCluster : IAsyncLifetime {
    TestCluster cluster = null!;

    /// <summary>The tenant most tests use.</summary>
    public static Guid Tenant { get; } = Guid.Parse("11111111-1111-4111-8111-111111111111");

    /// <summary>A second tenant, for the per-tenant email uniqueness assertions.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("22222222-2222-4222-8222-222222222222");

    /// <summary>The cluster's grain factory. ⚠ Tenant-unaware — a client, in the filter's terms.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>A tenant-qualified grain factory.</summary>
    /// <param name="tenant">The tenant.</param>
    public TenantGrainFactory For(Guid tenant) =>
        Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>A user grain.</summary>
    /// <param name="userId">Which user.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IUserGrain User(Guid userId, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<IUserGrain>(GrainKeys.User(userId));

    /// <summary>A session grain.</summary>
    /// <param name="sessionId">Which session.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public ISessionGrain Session(Guid sessionId, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(sessionId));

    /// <summary>A group grain.</summary>
    /// <param name="groupId">Which group.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IGroupGrain Group(Guid groupId, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<IGroupGrain>(GrainKeys.Group(groupId));

    /// <summary>An application grain.</summary>
    /// <param name="applicationId">Which application.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IApplicationGrain Application(Guid applicationId, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<IApplicationGrain>(GrainKeys.Application(applicationId));

    /// <summary>A service-principal grain.</summary>
    /// <param name="servicePrincipalId">Which principal.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IServicePrincipalGrain ServicePrincipal(Guid servicePrincipalId, Guid? tenant = null) =>
        For(tenant ?? Tenant).GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(servicePrincipalId));

    /// <summary>The real per-tenant email index.</summary>
    /// <param name="email">The address.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    public IEmailIndexGrain EmailIndex(string email, Guid? tenant = null) {
        var id = tenant ?? Tenant;
        return For(id).GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(id, email));
    }

    /// <summary>
    ///     Creates a user the way sign-up does: claim the index, create the user, confirm the claim.
    /// </summary>
    /// <param name="email">The address.</param>
    /// <param name="password">A password to enrol, or <see langword="null" /> for none.</param>
    /// <param name="tenant">The tenant, defaulting to <see cref="Tenant" />.</param>
    /// <remarks>
    ///     ⚠ The order is docs/plan/06 § Two-phase create's: the index claim comes first, so a crash
    ///     between the steps leaves a leased name rather than a user nobody can find by address.
    /// </remarks>
    public async Task<Guid> CreateUserAsync(string email, string? password = null, Guid? tenant = null) {
        var id = tenant ?? Tenant;
        var userId = Guid.NewGuid();

        var claimed = await EmailIndex(email, id).TryClaimAsync(email, userId);
        claimed.IsSuccess.ShouldBeTrue(claimed.Error?.Message);

        var user = User(userId, id);
        (await user.CreateAsync(email, "Test User", UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await EmailIndex(email, id).ConfirmAsync(userId)).IsSuccess.ShouldBeTrue();

        if (password is not null) {
            (await user.SetPasswordAsync(password)).IsSuccess.ShouldBeTrue();
        }

        return userId;
    }

    /// <summary>
    ///     A sign-in service built on the <b>client</b> side, over a recording grain factory.
    /// </summary>
    /// <param name="lockout">The counter to use.</param>
    /// <param name="recorder">The factory whose references the test counts.</param>
    /// <param name="options">Timing options — pass <c>MinimumDuration = Zero</c> to measure the real work.</param>
    /// <param name="logger">Where the log lines go.</param>
    /// <remarks>
    ///     ⚠ Built here rather than resolved from the silo, and that is the faithful shape rather than
    ///     a convenience: the sign-in path runs in the identity <i>host</i>, which is an Orleans
    ///     client, so the factory it holds is the client's.
    /// </remarks>
    public SignInService SignIn(
        ILockoutCounter lockout,
        RecordingGrainFactory? recorder = null,
        SignInOptions? options = null,
        ILogger<SignInService>? logger = null
    ) =>
        new(
            recorder ?? new RecordingGrainFactory(Grains),
            lockout,
            CheapArgon2.Hasher,
            logger ?? NullLogger<SignInService>.Instance,
            options ?? new() { MinimumDuration = TimeSpan.Zero }
        );

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            silo.ConfigureServices(services => {
                    // FIRST, so the module's TryAdd keeps them.
                    services.AddSingleton<IClock>(TestClock.Instance);
                    services.AddSingleton<IPasswordHasher>(CheapArgon2.Hasher);
                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudIdentity();
            silo.AddCyberCloudAuthorization();
        }
    }
}

/// <summary>Binds <see cref="IdentityCluster" /> to the classes that share it.</summary>
[CollectionDefinition(Name)]
public sealed class IdentitySuite : ICollectionFixture<IdentityCluster> {
    /// <summary>The collection name.</summary>
    public const string Name = "identity-cluster";
}
