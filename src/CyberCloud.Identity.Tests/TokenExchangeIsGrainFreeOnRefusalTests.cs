using CyberCloud.Core.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.ManagedIdentity;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Credentials: <i>"an authentication endpoint whose failure path costs a grain
///     activation is a denial-of-service amplifier"</i>. It is written about the lockout counter and
///     applies verbatim to <c>/token</c>, which is unauthenticated and reaches the platform through
///     <see cref="GrainTokenExchange" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="ManagedIdentityTests" /> goes straight to
///         <see cref="IManagedIdentityGrain" /> and therefore never exercises this type at all.</b>
///         That is the right shape for assertions about what the grain decides, and it leaves the one
///         decision this type does make — refusing before a grain reference exists — unasserted.
///     </para>
///     <para>
///         ⚠ Counting references rather than reading the code: "touches no grain" is a claim about a
///         code path, and <see cref="RecordingGrainFactory" /> is the only way to hold it. A future
///         "look up the identity first so the log line can name it" would pass every other test in
///         this suite.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class TokenExchangeIsGrainFreeOnRefusalTests(IdentityCluster cluster) {
    public static TheoryData<string, Guid, Guid, string> Nonsense =>
        new() {
            { "no tenant", Guid.Empty, Guid.NewGuid(), "a.b.c" },
            { "no identity", Guid.NewGuid(), Guid.Empty, "a.b.c" },
            { "neither", Guid.Empty, Guid.Empty, "a.b.c" },
            { "no token", Guid.NewGuid(), Guid.NewGuid(), "" }
        };

    [Theory]
    [MemberData(nameof(Nonsense))]
    public async Task NonsenseIsRefusedBeforeAGrainReferenceExists(
        string why,
        Guid tenantId,
        Guid managedIdentityId,
        string subjectToken
    ) {
        var recorder = new RecordingGrainFactory(cluster.Grains);
        var exchange = new GrainTokenExchange(recorder);

        var result = await exchange.ExchangeAsync(
            tenantId,
            managedIdentityId,
            subjectToken,
            TokenExchange.JwtSubjectTokenType
        );

        result.IsSuccess.ShouldBeFalse(why);

        recorder.References.ShouldBe(
            0,
            $"'{why}' must cost nothing. /token is unauthenticated, so a caller who could provoke "
            + "one durable-tier grain activation per request by sending empty GUIDs would be "
            + "creating activations for free — docs/plan/11 § Credentials."
        );
    }

    [Theory]
    [MemberData(nameof(Nonsense))]
    public async Task TheRefusalIsTheSameOneEveryOtherFailureGives(
        string why,
        Guid tenantId,
        Guid managedIdentityId,
        string subjectToken
    ) {
        var exchange = new GrainTokenExchange(new RecordingGrainFactory(cluster.Grains));

        var result = await exchange.ExchangeAsync(
            tenantId,
            managedIdentityId,
            subjectToken,
            TokenExchange.JwtSubjectTokenType
        );

        result.TryGetError(out var error).ShouldBeTrue(why);

        // ⚠ Verbatim, not "contains". The cheap refusal above is the one place a distinguishable
        // message could creep in without anybody noticing, because it is the only branch that
        // answers without asking the grain — and a caller who could tell "malformed request" from
        // "no such identity" could enumerate a tenant's identities from the token endpoint.
        error.Message.ShouldBe(ManagedIdentityFailures.Exchange, why);
        error.Code.ShouldBe(ErrorCode.AuthorizationFailed, why);
    }

    [Fact]
    public async Task AWellFormedRequestDoesReachTheGrain() {
        // ⚠ The counterpart the other two need. A refusal that costs no grain reference is trivially
        // satisfiable by refusing everything, so the suite has to show that the guard is a guard and
        // not the whole method.
        var recorder = new RecordingGrainFactory(cluster.Grains);
        var exchange = new GrainTokenExchange(recorder);

        var result = await exchange.ExchangeAsync(
            IdentityCluster.Tenant,
            Guid.NewGuid(),
            "not.a.real.token",
            TokenExchange.JwtSubjectTokenType
        );

        recorder.References.ShouldBeGreaterThan(
            0,
            "a request with a tenant, an identity and a token is the grain's to judge"
        );

        // And the grain's answer to an unbound identity is the same sentence, which is what makes
        // the cheap branch above invisible from outside.
        result.TryGetError(out var error).ShouldBeTrue();
        error.Message.ShouldBe(ManagedIdentityFailures.Exchange);
    }
}
