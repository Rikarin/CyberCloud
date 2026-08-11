using CyberCloud.Core.Contracts;
using CyberCloud.Core.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using System.CodeDom.Compiler;
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

    /// <summary>
    ///     The handle identity uses is an address, and there is nowhere in it to put a value.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/00 § Non-negotiables: <i>"secrets are <c>SecretRef</c> handles resolved at
    ///         the data plane"</i>. ⚠ <b>The absence is what makes the rule structural rather than a
    ///         convention</b>: a nullable <c>Value</c> "for convenience" would be populated by the
    ///         first caller who found resolving inconvenient, and from then on every backup of the
    ///         durable tier would contain it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Asserted from this assembly even though the type belongs to another one.</b>
    ///         <c>CyberCloud.ResourceManager.Contracts.Tests</c> makes the same assertion, and the
    ///         duplication is deliberate: identity gave up its own <c>VaultSecretRef</c> on the
    ///         strength of this property holding, so identity is entitled to a test that fails if it
    ///         stops holding — rather than to a comment saying somebody else checks.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSharedSecretRefIsAnAddressAndHasNowhereToPutAValue() {
        var value = new SecretRef {
            Path = "tenants/x/users/y/totp",
            Field = "secret",
            Version = "3"
        };

        RoundTrip(value).ShouldBe(value);

        typeof(SecretRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .ShouldBe(["Path", "Field", "Version", "IsEmpty"], ignoreOrder: true);
    }

    /// <summary>
    ///     ⚠ The three properties hold the <i>shared</i> record, and re-declaring a local one would
    ///     be the regression this test exists to catch.
    /// </summary>
    /// <remarks>
    ///     Identity carried its own <c>VaultSecretRef</c> — same three fields, its own
    ///     <c>[Alias]</c> — until 2026-08-11. A platform-wide rule with two incompatible spellings of
    ///     its own vocabulary is the rule eroding, and the second spelling reappears by somebody
    ///     wanting one extra field on it rather than by anybody deciding to fork it.
    /// </remarks>
    [Fact]
    public void TheSecretHandlePropertiesHoldTheSharedRecordAndNotALocalCopy() {
        typeof(ApplicationRegistration).GetProperty(nameof(ApplicationRegistration.ClientSecretRef))!
            .PropertyType.ShouldBe(typeof(SecretRef));

        typeof(ServicePrincipalDescriptor)
            .GetProperty(nameof(ServicePrincipalDescriptor.CredentialSecretRef))!
            .PropertyType.ShouldBe(typeof(SecretRef));

        typeof(TotpEnrollment).GetProperty(nameof(TotpEnrollment.SecretRef))!
            .PropertyType.ShouldBe(typeof(SecretRef));

        // And it really is the one in CyberCloud.Core.Contracts, published under the alias it has
        // carried since it lived in the resource manager — docs/plan/04 § Failure and upgrade.
        typeof(SecretRef).Assembly.GetName().Name.ShouldBe("CyberCloud.Core.Contracts");
        typeof(SecretRef).GetCustomAttribute<AliasAttribute>()!.Alias
            .ShouldBe("CyberCloud.ResourceManager.SecretRef");
    }

    /// <summary>
    ///     The <c>[Id(n)]</c> baseline for the three wire types that carry a secret handle.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This list is append-only, and it is the reason the handle type could be swapped
    ///         at all.</b> Orleans serialization is positional: the number is the contract and the
    ///         member's <i>type</i> is looked up from it. Replacing
    ///         <c>CyberCloud.Identity.VaultSecretRef</c> with
    ///         <see cref="CyberCloud.Core.Contracts.SecretRef" /> was safe on the wire only because
    ///         the two records had identical numbered members — <c>0 Path</c>, <c>1 Field</c>,
    ///         <c>2 Version</c> — and because not one of the numbers below moved. A renumber here is
    ///         indistinguishable on the wire from a rewrite.
    ///     </para>
    ///     <para>
    ///         The assembly-wide equivalent for <c>CyberCloud.Core.Contracts</c> is
    ///         <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c>. It does not reach this
    ///         assembly — it reflects over the assembly that holds <c>ResultSurrogate</c> — so these
    ///         three types had no manifest at all until this test.
    ///     </para>
    /// </remarks>
    static readonly (string Type, int Id, string Member)[] SecretBearingBaseline = [
        ("ApplicationRegistration", 0, "ApplicationId"),
        ("ApplicationRegistration", 1, "TenantId"),
        ("ApplicationRegistration", 2, "ClientId"),
        ("ApplicationRegistration", 3, "DisplayName"),
        ("ApplicationRegistration", 4, "RedirectUris"),
        ("ApplicationRegistration", 5, "PostLogoutRedirectUris"),
        ("ApplicationRegistration", 6, "AllowedGrants"),
        ("ApplicationRegistration", 7, "AllowedScopes"),
        ("ApplicationRegistration", 8, "IsPublicClient"),
        ("ApplicationRegistration", 9, "ClientSecretRef"),
        ("ApplicationRegistration", 10, "CreatedAt"),

        ("ServicePrincipalDescriptor", 0, "ServicePrincipalId"),
        ("ServicePrincipalDescriptor", 1, "TenantId"),
        ("ServicePrincipalDescriptor", 2, "ApplicationId"),
        ("ServicePrincipalDescriptor", 3, "DisplayName"),
        ("ServicePrincipalDescriptor", 4, "Enabled"),
        ("ServicePrincipalDescriptor", 5, "CredentialSecretRef"),
        ("ServicePrincipalDescriptor", 6, "CertificateThumbprints"),
        ("ServicePrincipalDescriptor", 7, "CreatedAt"),

        ("TotpEnrollment", 0, "SecretRef"),
        ("TotpEnrollment", 1, "Digits"),
        ("TotpEnrollment", 2, "PeriodSeconds"),
        ("TotpEnrollment", 3, "ConfirmedAt")
    ];

    [Fact]
    public void TheSecretBearingWireTypesKeepTheIdNumbersTheyPublished() {
        var types = SecretBearingBaseline.Select(x => x.Type).ToHashSet(StringComparer.Ordinal);

        var actual = typeof(UserProfile).Assembly
            .GetTypes()
            .Where(t => types.Contains(t.Name))
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Select(member => (member, id: member.GetCustomAttribute<IdAttribute>()))
                .Where(x => x.id is not null)
                .Select(x => (Type: type.Name, Id: (int)x.id!.Id, Member: x.member.Name))
            )
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Id)
            .ToList();

        actual.ShouldBe(
            SecretBearingBaseline.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Id).ToList(),
            "docs/plan/05 § Serialization and schema evolution: [Id(n)] numbers are never reused and "
            + "never reordered. If this fails because a member was added, append it with the next "
            + "unused number. If it fails for any other reason, the wire contract just broke."
        );
    }

    /// <summary>
    ///     A populated secret handle through the bytes, inside each of the three types that carry one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Populated, because <c>new()</c> proves nothing.</b> The pre-existing coverage passed
    ///     an empty handle, which round-trips identically whether or not the nested type has a codec
    ///     at all — every member is already its default. The three fields below are distinct
    ///     non-defaults, so a handle that does not survive comes back visibly empty.
    /// </remarks>
    [Fact]
    public void ASecretHandleSurvivesInsideEveryWireTypeThatCarriesOne() {
        var application = RoundTrip(
            new ApplicationRegistration {
                ClientId = "portal",
                IsPublicClient = false,
                ClientSecretRef = new() { Path = "tenants/x/apps/portal", Field = "clientSecret", Version = "7" }
            }
        );

        application.ClientSecretRef.Path.ShouldBe("tenants/x/apps/portal");
        application.ClientSecretRef.Field.ShouldBe("clientSecret");
        application.ClientSecretRef.Version.ShouldBe("7");
        application.ClientSecretRef.IsEmpty.ShouldBeFalse();

        var principal = RoundTrip(
            new ServicePrincipalDescriptor {
                DisplayName = "CI",
                CredentialSecretRef = new() { Path = "tenants/x/sp/ci", Field = "secret", Version = "2" }
            }
        );

        principal.CredentialSecretRef.Path.ShouldBe("tenants/x/sp/ci");
        principal.CredentialSecretRef.Field.ShouldBe("secret");
        principal.CredentialSecretRef.Version.ShouldBe("2");

        var enrollment = RoundTrip(
            new TotpEnrollment {
                SecretRef = new() { Path = "tenants/x/users/y/totp", Field = "secret", Version = "3" }
            }
        );

        enrollment.SecretRef.Path.ShouldBe("tenants/x/users/y/totp");
        enrollment.SecretRef.Field.ShouldBe("secret");
        enrollment.SecretRef.Version.ShouldBe("3");
        enrollment.SecretRef.ToString().ShouldBe("tenants/x/users/y/totp#secret@3");
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

    /// <summary>
    ///     Every alias string this assembly publishes, recorded. Changing one is a wire break.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The strings, not the shape.</b> Until this list existed, the only thing asserted
    ///         about these aliases was that each starts with <c>CyberCloud.Identity.</c> — so
    ///         re-spelling <c>CyberCloud.Identity.VaultSecretRef</c> to
    ///         <c>CyberCloud.Identity.VaultSecretReference</c> passed every test in the repository,
    ///         and would have silently failed to deserialize every payload written before it.
    ///         <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c> has had the equivalent list
    ///         since that assembly existed; this one did not, and the gap was found by making
    ///         exactly that edit and watching nothing go red.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Grain interfaces are in the list too, and they are not decoration.</b> An
    ///         <c>[Alias]</c> on a grain interface is how a caller's request is routed to an
    ///         implementation — docs/plan/04 § Failure and upgrade — so renaming
    ///         <c>IUserGrain</c>'s alias breaks calls rather than payloads. Both failure modes are
    ///         invisible at compile time, which is why one list covers both.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Adding a line is a wire-contract decision.</b> Removing one retires an alias:
    ///         safe only while <c>git tag</c> is empty and nothing has ever been deserialized under
    ///         it, which is the argument recorded at the top of <c>IdentityWireTypes.cs</c> for the
    ///         one line that has been removed. After the first release tag the correct move is a
    ///         burned entry with a comment, never a deletion.
    ///     </para>
    /// </remarks>
    static readonly string[] PublishedAliases = [
        "CyberCloud.Identity.ApplicationRegistration",
        "CyberCloud.Identity.AuthenticationMethod",
        "CyberCloud.Identity.ClusterOidcIssuer",
        "CyberCloud.Identity.CredentialKind",
        "CyberCloud.Identity.ExchangedSubject",
        "CyberCloud.Identity.GrantType",
        "CyberCloud.Identity.GroupDescriptor",
        "CyberCloud.Identity.IApplicationGrain",
        "CyberCloud.Identity.IGroupGrain",
        "CyberCloud.Identity.IManagedIdentityGrain",
        "CyberCloud.Identity.IServicePrincipalGrain",
        "CyberCloud.Identity.ISessionGrain",
        "CyberCloud.Identity.IUserGrain",
        "CyberCloud.Identity.Invitation",
        "CyberCloud.Identity.ManagedIdentityDescriptor",
        "CyberCloud.Identity.PasskeyAssertionChallenge",
        "CyberCloud.Identity.PasskeyCredential",
        "CyberCloud.Identity.PasskeyRegistrationChallenge",
        "CyberCloud.Identity.PasskeyRegistrationRequest",
        "CyberCloud.Identity.RecoveryCodeBatch",
        "CyberCloud.Identity.RefreshRotation",
        "CyberCloud.Identity.RevocationReason",
        "CyberCloud.Identity.ServicePrincipalDescriptor",
        "CyberCloud.Identity.SessionDescriptor",
        "CyberCloud.Identity.SignInOutcome",
        "CyberCloud.Identity.TotpEnrollment",
        "CyberCloud.Identity.UserProfile",
        "CyberCloud.Identity.UserStatus",
        "CyberCloud.Identity.WorkloadBinding"

        // ⚠ "CyberCloud.Identity.VaultSecretRef" was REMOVED, not burned, and that is the one
        // exception this list's own rule allows. The append-only rule protects aliases a deployed
        // peer might send; `git tag` is empty, nothing has ever run, and the type it named is now
        // CyberCloud.Core.Contracts.SecretRef under the alias that type was published with. If any
        // silo had ever run, this would instead be a burned entry with a comment. The full argument,
        // including when it stops being available, is at the top of IdentityWireTypes.cs.
    ];

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() {
        // ⚠ Hand-written types only. Orleans' generator emits a proxy per grain interface and gives
        // every one of them [Alias("GrainRef")] — six identical strings that are not this assembly's
        // contract to keep, are not unique, and would drown the list they were recorded in.
        var actual = typeof(UserProfile).Assembly
            .GetTypes()
            .Where(x => x.GetCustomAttribute<GeneratedCodeAttribute>() is null)
            .Select(x => x.GetCustomAttribute<AliasAttribute>()?.Alias)
            .Where(x => x is not null)
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        actual.ShouldBe(
            PublishedAliases.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            "an alias string changed, or an [Alias] type was added or removed without recording it. "
            + "All three are wire-contract changes — docs/plan/04 § Failure and upgrade makes the "
            + "alias, not the CLR name, what the far side looks up."
        );
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
