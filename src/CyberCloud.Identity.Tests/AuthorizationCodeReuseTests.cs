using CyberCloud.Core;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Grains;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     RFC 6749 § 4.1.2, at the grain: an authorization code is consumed exactly once, the second
///     presenter learns which token session the first one opened, and a session revoked before it
///     opened stays revoked. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     ⚠ <b>The property is "the first exchange's session can be killed by the second", not "the
///     second exchange fails".</b> The host's part of the story — refusing the replay and calling
///     <c>RevokeAsync</c> on what this grain hands back — is <c>GrantsOverHttpTests</c>'; what the
///     grain owes is that the record names the right session and that the race between the two
///     exchanges cannot lose the revocation. The last fact lives in <c>SessionGrain.OpenAsync</c>
///     and is asserted here beside the record it exists for.
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class AuthorizationCodeReuseTests(IdentityCluster cluster) {
    static DateTimeOffset InFiveMinutes => TestClock.Instance.UtcNow + AccessTokenPolicy.AuthorizationCodeLifetime;

    [Fact]
    public async Task ACodeIsConsumedOnceAndTheReplayLearnsTheFirstSession() {
        var code = cluster.AuthorizationCode(Guid.NewGuid());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await code.IsConsumedAsync()).GetValueOrThrow().ShouldBeFalse();

        var consumed = await code.ConsumeAsync(first, InFiveMinutes);

        consumed.IsSuccess.ShouldBeTrue(consumed.Error?.Message);
        consumed.GetValueOrThrow().FirstUse.ShouldBeTrue();
        consumed.GetValueOrThrow().TokenSessionId.ShouldBe(first);

        // ⚠ THE ASSERTION THAT MATTERS: the replay is told the FIRST session's id, not its own, because
        // that is the session RFC 6749 § 4.1.2 says to revoke.
        var replayed = await code.ConsumeAsync(second, InFiveMinutes);

        replayed.IsSuccess.ShouldBeTrue(replayed.Error?.Message);
        replayed.GetValueOrThrow().FirstUse.ShouldBeFalse();
        replayed.GetValueOrThrow().TokenSessionId.ShouldBe(first);
        replayed.GetValueOrThrow().ConsumedAt.ShouldBe(consumed.GetValueOrThrow().ConsumedAt);

        (await code.IsConsumedAsync()).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task TwoCodesAreTwoRecords() {
        var a = cluster.AuthorizationCode(Guid.NewGuid());
        var b = cluster.AuthorizationCode(Guid.NewGuid());
        var session = Guid.NewGuid();

        (await a.ConsumeAsync(session, InFiveMinutes)).GetValueOrThrow().FirstUse.ShouldBeTrue();
        (await b.ConsumeAsync(session, InFiveMinutes)).GetValueOrThrow().FirstUse.ShouldBeTrue("a second code was read as a replay of the first");
    }

    [Fact]
    public async Task AnEmptySessionIdCannotConsume() {
        // The record exists to name the session a replay revokes; an empty id would name none and
        // the replay would have nothing to kill.
        var code = cluster.AuthorizationCode(Guid.NewGuid());

        var refused = await code.ConsumeAsync(Guid.Empty, InFiveMinutes);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        (await code.IsConsumedAsync()).GetValueOrThrow().ShouldBeFalse();
    }

    [Fact]
    public async Task ARecordPastTheCodesExpiryIsForgotten() {
        // ⚠ Past the expiry PLUS the grace, so the last seconds of a code's life are still guarded:
        // a record forgotten at exactly `exp` would let a replay through against a code OpenIddict
        // still accepts, if the two clocks disagree by a second.
        var code = cluster.AuthorizationCode(Guid.NewGuid());
        var expiresAt = InFiveMinutes;

        (await code.ConsumeAsync(Guid.NewGuid(), expiresAt)).GetValueOrThrow().FirstUse.ShouldBeTrue();

        TestClock.Instance.Advance(AccessTokenPolicy.AuthorizationCodeLifetime);
        (await code.IsConsumedAsync()).GetValueOrThrow().ShouldBeTrue("the record was forgotten at the code's own expiry, before the skew grace");

        TestClock.Instance.Advance(AuthorizationCodeGrain.Grace);
        (await code.IsConsumedAsync()).GetValueOrThrow().ShouldBeFalse();

        // A stale record is absent: the same id consumes again. OpenIddict refuses the expired code
        // before the host asks, so this branch is the tier's tidiness rather than a hole.
        var again = await code.ConsumeAsync(Guid.NewGuid(), TestClock.Instance.UtcNow + AccessTokenPolicy.AuthorizationCodeLifetime);

        again.GetValueOrThrow().FirstUse.ShouldBeTrue();
    }

    [Fact]
    public async Task ASessionRevokedBeforeItOpenedDoesNotOpen() {
        // ⚠ THE RACE. Exchange A mints a session id and consumes the code; exchange B, replaying the
        // same code a moment later, reads that id and revokes it; exchange A then opens the session.
        // If the open succeeded, B's revocation would have revoked nothing and A would hold a live
        // chain minted from a code that was provably presented twice.
        var sessionId = Guid.NewGuid();
        var session = cluster.Session(sessionId);

        (await session.RevokeAsync(RevocationReason.AuthorizationCodeReuseDetected)).IsSuccess.ShouldBeTrue();

        var opened = await session.OpenAsync(Guid.NewGuid(), "cyc-portal", "device", "digest", [AuthenticationMethod.Password]);

        opened.IsFailure.ShouldBeTrue("a session revoked before it opened was opened over the revocation");
        opened.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        (await session.IsLiveAsync()).GetValueOrThrow().ShouldBeFalse();
        (await session.GetAsync()).IsFailure.ShouldBeTrue("a session that never opened has nothing to describe");
    }
}
