using Orleans;
using System.Reflection;

namespace CyberCloud.Communication.Contracts.Tests;

/// <summary>
///     docs/plan/00 § Non-negotiables, the "Secrets never reach grain state" row: <i>"secrets are
///     <c>SecretRef</c> handles resolved at the data plane"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>CC1005 already fails the build for a member <i>named</i> like a secret, and these
///         assert the two things a name rule cannot.</b> The first is that a suppression, when one is
///         used, carries a real argument rather than a shrug. The second is that no wire type in this
///         module has a member whose <i>type</i> could hold a credential value — which is the shape
///         that gets past a name rule, because <c>public string Twilio { get; init; }</c> is not
///         called anything suspicious.
///     </para>
///     <para>
///         This module is where the rule bites hardest. docs/plan/17 § The channel abstraction offers
///         BYO from day one, so the steady state is that a durable, replicated, backed-up grain sits
///         next to a paying customer's Twilio contract. A leak here is not our bill and not our
///         incident to close — it is theirs, and we caused it.
///     </para>
/// </remarks>
public sealed class NoCredentialsInStateTests {
    static readonly Assembly Contracts = typeof(SendRequest).Assembly;

    /// <summary>
    ///     The member names CC1005 bans outright, from docs/plan/00 § Non-negotiables.
    /// </summary>
    static readonly string[] BannedSuffixes = ["Password", "Secret", "Token", "Key"];

    /// <summary>
    ///     The two members in this assembly that end in a banned suffix and are not credentials.
    /// </summary>
    /// <remarks>
    ///     ⚠ An allow-list rather than a rule, so adding a third is a visible edit here rather than
    ///     a build that quietly stayed green. Both are the client-supplied idempotency key of
    ///     docs/plan/17 § The parts that are actually the work.
    /// </remarks>
    static readonly string[] Sanctioned = ["SendRequest.IdempotencyKey"];

    [Fact]
    public void NoWireTypeCarriesAMemberNamedLikeASecretExceptTheSanctionedOnes() {
        var offending = new List<string>();

        foreach (var type in Contracts.GetTypes().Where(x => x.GetCustomAttribute<GenerateSerializerAttribute>() is not null)) {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (property.GetCustomAttribute<IdAttribute>() is null) {
                    continue;
                }

                if (!BannedSuffixes.Any(x => property.Name.EndsWith(x, StringComparison.Ordinal))) {
                    continue;
                }

                var qualified = $"{type.Name}.{property.Name}";
                if (!Sanctioned.Contains(qualified, StringComparer.Ordinal)) {
                    offending.Add(qualified);
                }
            }
        }

        offending.ShouldBeEmpty(
            "an [Id] member is written to the durable tier and travels between silos, so a secret in "
            + "one is a secret in every backup — docs/plan/00 § Non-negotiables. Store a "
            + "CarrierSecretRef handle and resolve it at the data plane."
        );
    }

    [Fact]
    public void ACarrierSecretRefHasNoMemberAValueCouldRideIn() {
        var members = typeof(CarrierSecretRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.GetCustomAttribute<IdAttribute>() is not null)
            .Select(x => x.Name)
            .ToList();

        members.ShouldBe(
            ["Path", "Field", "Version"],
            ignoreOrder: true,
            "every member is an address. A nullable Value \"for convenience\" would be populated by "
            + "the first caller who found resolving inconvenient, and from then on every backup of "
            + "the durable tier would contain a customer's carrier credential"
        );
    }

    [Fact]
    public void CarrierCredentialsReachTheCarrierOnlyAsHandles() {
        foreach (var property in typeof(CarrierCredentials).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.Name == nameof(CarrierCredentials.Mode)) {
                continue;
            }

            property.PropertyType.ShouldBe(
                typeof(CarrierSecretRef),
                $"CarrierCredentials.{property.Name} is how a BYO credential reaches a provider, and "
                + "a string there would be the credential itself"
            );
        }
    }

    [Fact]
    public void AMessageSnapshotCarriesNoBody() {
        var members = typeof(MessageSnapshot)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(x => x.Name)
            .ToList();

        members.ShouldNotContain(
            "Body",
            "a status object answers \"did it arrive\". The body of an OTP message IS the one-time "
            + "code and the body of a password-reset message is a bearer token, so a snapshot "
            + "carrying one would put a live credential in front of anyone who can read a status"
        );

        members.ShouldNotContain("Arguments");
    }

    [Fact]
    public void EveryWireTypeCarriesAStableAlias() {
        var missing = Contracts.GetTypes()
            .Where(x => x.GetCustomAttribute<GenerateSerializerAttribute>() is not null)
            .Where(x => x.GetCustomAttribute<AliasAttribute>() is null)
            .Select(x => x.Name)
            .ToList();

        missing.ShouldBeEmpty(
            "a rolling silo upgrade resolves types by alias — CC1003, docs/plan/04 § Failure and "
            + "upgrade. This asserts the same thing the analyzer does, from the other side, so a "
            + "type reaching here without one fails a test rather than a build somebody suppressed."
        );
    }
}

/// <summary>The derived grain keys — <see cref="CommunicationGrainKeys" />.</summary>
public sealed class GrainKeyDerivationTests {
    static readonly Guid Service = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1");
    static readonly Guid Other = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb1");

    // ── FAILURE CLASS: the same idempotency key addresses the same grain, a different one does not ─

    [Fact]
    public void TheSameServiceAndKeyAlwaysDeriveTheSameGrainKey() =>
        CommunicationGrainKeys.Message(Service, "otp-42")
            .ShouldBe(
                CommunicationGrainKeys.Message(Service, "otp-42"),
                "this IS the idempotency mechanism — a retry has to reach the activation that already "
                + "recorded the send, with no index in between"
            );

    [Fact]
    public void SurroundingWhiteSpaceDoesNotMakeADifferentMessage() =>
        CommunicationGrainKeys.Message(Service, "  otp-42 ")
            .ShouldBe(CommunicationGrainKeys.Message(Service, "otp-42"));

    [Fact]
    public void ADifferentKeyDerivesADifferentGrain() =>
        CommunicationGrainKeys.Message(Service, "otp-42")
            .ShouldNotBe(
                CommunicationGrainKeys.Message(Service, "otp-43"),
                "an idempotency check that swallows genuine sends is worse than a duplicate"
            );

    [Fact]
    public void CaseIsNotFolded() =>
        CommunicationGrainKeys.Message(Service, "Otp-42")
            .ShouldNotBe(
                CommunicationGrainKeys.Message(Service, "otp-42"),
                "a caller who sent one and retried with the other meant two things or made a mistake, "
                + "and folding them would silently pick one reading"
            );

    [Fact]
    public void TwoServicesNeverShareAMessageGrain() =>
        CommunicationGrainKeys.Message(Service, "otp-42")
            .ShouldNotBe(CommunicationGrainKeys.Message(Other, "otp-42"));

    [Fact]
    public void AMessageKeyAndAProviderIndexKeyNeverCollide() =>
        CommunicationGrainKeys.Message(Service, "SM123")
            .ShouldNotBe(
                CommunicationGrainKeys.ProviderMessage(Service, "SM123"),
                "the domain string is what keeps the two derivations apart; without it a service "
                + "whose idempotency key happened to equal a carrier's message id would produce one "
                + "guid for two different grains"
            );

    [Fact]
    public void TwoTenantsCarriersNeverShareAnIndexEntry() =>
        CommunicationGrainKeys.ProviderMessage(Service, "SM123")
            .ShouldNotBe(
                CommunicationGrainKeys.ProviderMessage(Other, "SM123"),
                "carrier ids are unique per account, not per planet"
            );

    [Fact]
    public void ASendWithNoIdempotencyKeyCannotEvenBeAddressed() =>
        Should.Throw<ArgumentException>(() => CommunicationGrainKeys.Message(Service, "  "))
            .Message.ShouldContain("retry");

    [Fact]
    public void EveryDerivedKeyIsAParseableGrainKey() {
        foreach (var key in new[] {
            CommunicationGrainKeys.Message(Service, "otp-42"),
            CommunicationGrainKeys.ProviderMessage(Service, "SM123"),
            CommunicationGrainKeys.Service(Service)
        }) {
            // ADR-002: GrainKeys is the only type allowed to build the within-tenant part, and the
            // parser's canonicity guard is what makes a key that round-trips to a different string
            // impossible. A derived guid that failed this would be a second activation of one entity.
            CyberCloud.Core.Resources.GrainKeys.Parse(key).GetValueOrThrow().ToString().ShouldBe(key);
        }
    }
}
