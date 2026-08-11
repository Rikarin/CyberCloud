using CyberCloud.Core.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using System.Reflection;

namespace CyberCloud.Identity.Contracts.Tests;

/// <summary>
///     Every identity wire type through a real Orleans <see cref="Serializer" />, as bytes.
/// </summary>
/// <remarks>
///     ⚠ <b>Not a hand-rolled round trip.</b> <c>CyberCloud.Identity.Tests</c> runs against in-memory
///     grain storage, which keeps the object graph rather than serializing it — so a type with a
///     missing codec, or a get-only collection System.Text.Json will not populate, passes every test
///     there and fails on the first real silo. The bytes are what catches it.
/// </remarks>
public sealed class IdentitySerializationTests : IDisposable {
    readonly ServiceProvider provider;
    readonly Serializer serializer;

    /// <summary>Builds a serializer over every contract assembly a silo would load.</summary>
    public IdentitySerializationTests() {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder
            .AddAssembly(typeof(UserProfile).Assembly)
            .AddAssembly(typeof(ResultSurrogate).Assembly)
        );

        provider = services.BuildServiceProvider();
        serializer = provider.GetRequiredService<Serializer>();
    }

    /// <inheritdoc />
    public void Dispose() => provider.Dispose();

    [Fact]
    public void AUserProfileRoundTrips() {
        var value = new UserProfile {
            UserId = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
            TenantId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
            Email = "alice@example.com",
            DisplayName = "Alice",
            Status = UserStatus.Active,
            CreatedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            EnrolledCredentials = [CredentialKind.Passkey, CredentialKind.Totp],
            RemainingRecoveryCodes = 7
        };

        var back = RoundTrip(value);

        // ⚠ Compared member by member rather than with ShouldBe(value), and the reason is a trap
        // worth naming: a `record` with a `List<T>` member does NOT have value equality. The compiler
        // generates an EqualityContract-plus-per-member comparison, and for a reference type that
        // member comparison is List<T>'s, which is reference equality. So `back == value` is false
        // for a perfectly correct round trip, and a test written the obvious way fails for a reason
        // that has nothing to do with serialization.
        back.UserId.ShouldBe(value.UserId);
        back.TenantId.ShouldBe(value.TenantId);
        back.Email.ShouldBe(value.Email);
        back.DisplayName.ShouldBe(value.DisplayName);
        back.Status.ShouldBe(value.Status);
        back.CreatedAt.ShouldBe(value.CreatedAt);
        back.RemainingRecoveryCodes.ShouldBe(value.RemainingRecoveryCodes);

        // ⚠ The collection specifically. A `{ get; }` here would come back EMPTY rather than wrong,
        // which is the failure mode that looks like "somebody deleted my credentials".
        back.EnrolledCredentials.ShouldBe([CredentialKind.Passkey, CredentialKind.Totp]);
    }

    [Fact]
    public void APasskeyCredentialRoundTrips() {
        var value = new PasskeyCredential {
            CredentialId = "Y3JlZC1pZA",
            PublicKey = "cHVibGljLWtleQ",
            AaGuid = Guid.Parse("08987058-cadc-4b81-b6e1-30de50dcbe96"),
            SignCount = 42,
            Label = "YubiKey 5",
            CreatedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            LastUsedAt = new(2026, 8, 12, 9, 30, 0, TimeSpan.Zero)
        };

        RoundTrip(value).ShouldBe(value);
    }

    [Fact]
    public void ASessionDescriptorRoundTripsIncludingItsMethods() {
        var value = new SessionDescriptor {
            SessionId = Guid.Parse("5f3d1a90-8c2b-4a6e-9d1f-7b0c4e2a6d38"),
            UserId = Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
            TenantId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
            ClientId = "portal",
            DeviceLabel = "Firefox on macOS",
            ClientAddressDigest = "0123456789abcdef",
            CreatedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            LastRefreshedAt = new(2026, 8, 11, 12, 30, 0, TimeSpan.Zero),
            AuthenticatedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero),
            Generation = 4,
            IsLive = true,
            RevokedBecause = RevocationReason.None,
            Methods = [AuthenticationMethod.Passkey, AuthenticationMethod.Totp]
        };

        var back = RoundTrip(value);

        // Member by member — see AUserProfileRoundTrips on why a record with a List member is not
        // value-equal to its own round trip.
        back.SessionId.ShouldBe(value.SessionId);
        back.UserId.ShouldBe(value.UserId);
        back.TenantId.ShouldBe(value.TenantId);
        back.ClientId.ShouldBe(value.ClientId);
        back.DeviceLabel.ShouldBe(value.DeviceLabel);
        back.ClientAddressDigest.ShouldBe(value.ClientAddressDigest);
        back.CreatedAt.ShouldBe(value.CreatedAt);
        back.LastRefreshedAt.ShouldBe(value.LastRefreshedAt);
        back.AuthenticatedAt.ShouldBe(value.AuthenticatedAt);
        back.Generation.ShouldBe(value.Generation);
        back.IsLive.ShouldBe(value.IsLive);
        back.RevokedBecause.ShouldBe(value.RevokedBecause);
        back.Methods.ShouldBe([AuthenticationMethod.Passkey, AuthenticationMethod.Totp]);
    }

    [Fact]
    public void AnApplicationRegistrationRoundTripsWithEveryCollectionPopulated() {
        var value = new ApplicationRegistration {
            ApplicationId = Guid.Parse("6d0b2c14-3e58-4f7a-8b9c-1d2e3f405162"),
            TenantId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
            ClientId = "portal",
            DisplayName = "Cyber Cloud Portal",
            RedirectUris = ["https://portal.example/callback"],
            PostLogoutRedirectUris = ["https://portal.example/"],
            AllowedGrants = [GrantType.AuthorizationCode, GrantType.RefreshToken],
            AllowedScopes = ["openid", "profile", "cyc.api"],
            IsPublicClient = true,
            ClientSecretRef = new(),
            CreatedAt = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)
        };

        var back = RoundTrip(value);

        back.RedirectUris.ShouldBe(["https://portal.example/callback"]);
        back.AllowedGrants.ShouldBe([GrantType.AuthorizationCode, GrantType.RefreshToken]);
        back.AllowedScopes.ShouldBe(["openid", "profile", "cyc.api"]);
    }

    [Fact]
    public void AVaultSecretRefRoundTripsAndCarriesNoValue() {
        var value = new VaultSecretRef {
            Path = "tenants/x/users/y/totp",
            Field = "secret",
            Version = "3"
        };

        RoundTrip(value).ShouldBe(value);

        // docs/plan/00 § Non-negotiables: "secrets are SecretRef handles resolved at the data plane".
        // ⚠ There is no member a value could ride in, and that is what makes the rule structural.
        typeof(VaultSecretRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .ShouldBe(["Path", "Field", "Version", "IsEmpty"], ignoreOrder: true);
    }

    [Fact]
    public void EveryRemainingWireTypeRoundTrips() {
        RoundTrip(new GroupDescriptor { GroupId = Guid.NewGuid(), Name = "Eng" }).Name.ShouldBe("Eng");

        RoundTrip(
                new ServicePrincipalDescriptor {
                    ServicePrincipalId = Guid.NewGuid(),
                    DisplayName = "CI",
                    CertificateThumbprints = ["aa", "bb"]
                }
            )
            .CertificateThumbprints.ShouldBe(["aa", "bb"]);

        RoundTrip(new RefreshRotation { Handle = "h", Generation = 2 }).Generation.ShouldBe(2);
        RoundTrip(new RecoveryCodeBatch { Codes = ["AAAAA-BBBBB"] }).Codes.ShouldBe(["AAAAA-BBBBB"]);
        RoundTrip(new TotpEnrollment { SecretRef = new() { Path = "p", Field = "f" } }).Digits.ShouldBe(6);
        RoundTrip(SignInOutcome.Success(Guid.NewGuid(), Guid.NewGuid(), AuthenticationMethod.Passkey))
            .Succeeded.ShouldBeTrue();
        RoundTrip(new Invitation { Email = "b@example.com", Relation = "owner" }).Relation.ShouldBe("owner");
        RoundTrip(new PasskeyRegistrationChallenge { OptionsJson = "{}" }).OptionsJson.ShouldBe("{}");
        RoundTrip(new PasskeyAssertionChallenge { OptionsJson = "{}" }).OptionsJson.ShouldBe("{}");
        RoundTrip(new PasskeyRegistrationRequest { Email = "c@example.com", Existing = [] }).Email
            .ShouldBe("c@example.com");
    }

    [Fact]
    public void EveryGenerateSerializerTypeCarriesAStableAlias() {
        // CC1003 enforces this at compile time for the assembly; this is the cross-check that the
        // aliases are actually distinct, which the analyzer cannot see.
        var aliased = typeof(UserProfile).Assembly
            .GetTypes()
            .Where(x => x.GetCustomAttribute<GenerateSerializerAttribute>() is not null)
            .Select(x => (x.Name, Alias: x.GetCustomAttribute<AliasAttribute>()?.Alias))
            .ToList();

        aliased.ShouldNotBeEmpty();
        aliased.ShouldAllBe(x => x.Alias != null);

        aliased.Select(x => x.Alias).Distinct(StringComparer.Ordinal).Count().ShouldBe(aliased.Count);
        aliased.ShouldAllBe(x => x.Alias!.StartsWith("CyberCloud.Identity.", StringComparison.Ordinal));
    }

    T RoundTrip<T>(T value) => serializer.Deserialize<T>(serializer.SerializeToArray(value));
}
