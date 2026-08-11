using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Protocol: refresh tokens are <i>"rotating, one-time-use, with reuse detection →
///     revoke the whole chain"</i>.
/// </summary>
/// <remarks>
///     ⚠ <b>"Revoke the whole chain" is the part that is easy to get wrong and hard to notice.</b> An
///     implementation that rejects the replayed request and leaves the session alive passes any test
///     that only checks the replay's return value — and leaves an attacker who stole a refresh token
///     holding a working session in exactly the case the detector fired. Every test here asserts on
///     the <i>chain</i> afterwards, not on the failed call.
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class RefreshReuseTests(IdentityCluster cluster) {
    [Fact]
    public async Task ARefreshRotatesAndTheOldHandleStopsWorking() {
        var session = cluster.Session(Guid.NewGuid());
        var first = await Open(session);

        var second = await session.RefreshAsync(first.Handle);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);

        second.GetValueOrThrow().Handle.ShouldNotBe(first.Handle);
        second.GetValueOrThrow().Generation.ShouldBe(first.Generation + 1);
    }

    [Fact]
    public async Task PresentingARefreshTokenTwiceRevokesTheWholeChain() {
        var sessionId = Guid.NewGuid();
        var session = cluster.Session(sessionId);

        var gen1 = await Open(session);
        var gen2 = (await session.RefreshAsync(gen1.Handle)).GetValueOrThrow();
        var gen3 = (await session.RefreshAsync(gen2.Handle)).GetValueOrThrow();

        // The session is healthy and three generations deep.
        (await session.IsLiveAsync()).GetValueOrThrow().ShouldBeTrue();
        (await session.ChainLengthAsync()).GetValueOrThrow().ShouldBe(3);

        // ── The replay. gen1 was retired two rotations ago. ────────────────────────────────────
        var replay = await session.RefreshAsync(gen1.Handle);

        replay.IsFailure.ShouldBeTrue();

        // ⚠ THE ASSERTION THAT MATTERS. Not "the replay failed" — that is the easy half — but that
        // gen3, the handle the LEGITIMATE client is holding right now, is dead too.
        var afterReplay = await session.RefreshAsync(gen3.Handle);

        afterReplay.IsFailure.ShouldBeTrue(
            "The live generation must die with the chain. A replayed handle and the live handle "
            + "cannot be told apart by who sent them, so revoking only the replayed one leaves the "
            + "thief holding a working session — docs/plan/11 § Protocol."
        );

        (await session.IsLiveAsync()).GetValueOrThrow().ShouldBeFalse();

        var descriptor = (await session.GetAsync()).GetValueOrThrow();
        descriptor.IsLive.ShouldBeFalse();
        descriptor.RevokedBecause.ShouldBe(RevocationReason.RefreshReuseDetected);
    }

    [Fact]
    public async Task EveryGenerationInTheChainIsDeadAfterReuseDetection() {
        var session = cluster.Session(Guid.NewGuid());

        var handles = new List<string>();
        var current = await Open(session);
        handles.Add(current.Handle);

        for (var i = 0; i < 5; i++) {
            current = (await session.RefreshAsync(current.Handle)).GetValueOrThrow();
            handles.Add(current.Handle);
        }

        // Replay the second-oldest.
        (await session.RefreshAsync(handles[1])).IsFailure.ShouldBeTrue();

        // Now nothing the chain ever issued works, in either direction from the replay point.
        foreach (var handle in handles) {
            (await session.RefreshAsync(handle)).IsFailure.ShouldBeTrue(
                "every generation of a chain with a detected replay must be dead"
            );
        }
    }

    [Fact]
    public async Task AHandleFromADifferentSessionIsRejectedAndChangesNothing() {
        var mine = cluster.Session(Guid.NewGuid());
        var theirs = cluster.Session(Guid.NewGuid());

        var myHandle = await Open(mine);
        var theirHandle = await Open(theirs);

        // ⚠ Case 3: a handle this chain never issued. It fails, and it must NOT revoke — otherwise
        // anyone who can reach the endpoint could kill any session by presenting garbage to it,
        // which turns the reuse detector into a denial-of-service primitive.
        (await mine.RefreshAsync(theirHandle.Handle)).IsFailure.ShouldBeTrue();
        (await mine.RefreshAsync("not-a-handle-at-all")).IsFailure.ShouldBeTrue();

        (await mine.IsLiveAsync()).GetValueOrThrow().ShouldBeTrue(
            "an unrecognised handle must not revoke the session — that would be a free denial of "
            + "service against any session id an attacker can guess or observe"
        );

        (await mine.RefreshAsync(myHandle.Handle)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RevokingASessionRetiresTheLiveHandleSoALaterReplayIsStillRecognised() {
        var session = cluster.Session(Guid.NewGuid());
        var handle = await Open(session);

        await session.RevokeAsync(RevocationReason.SignOut);

        var afterRevoke = await session.RefreshAsync(handle.Handle);
        afterRevoke.IsFailure.ShouldBeTrue();

        // ⚠ The reason changes from SignOut to RefreshReuseDetected, and that is deliberate rather
        // than sloppy. A handle presented after a sign-out is either a slow client or a thief, and
        // the audit trail should record that somebody tried — which is exactly where a compromise
        // gets noticed. Clearing the digest on revoke instead of retiring it would make this look
        // like an unknown token.
        var descriptor = (await session.GetAsync()).GetValueOrThrow();
        descriptor.RevokedBecause.ShouldBe(RevocationReason.RefreshReuseDetected);
    }

    [Fact]
    public async Task ChangingAPasswordKillsEverySessionTheUserHolds() {
        var userId = await cluster.CreateUserAsync("rotate-me@example.com", "first-password-value");

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await Open(cluster.Session(a), userId);
        await Open(cluster.Session(b), userId);

        await cluster.User(userId).TrackSessionAsync(a);
        await cluster.User(userId).TrackSessionAsync(b);

        (await cluster.User(userId).SetPasswordAsync("second-password-value")).IsSuccess.ShouldBeTrue();

        // docs/plan/11 § Sessions and revocation lists "password change" among the things that
        // invalidate a refresh chain. A password change that left a stolen session alive would be the
        // one recovery action a user takes after a compromise, doing nothing about it.
        (await cluster.Session(a).IsLiveAsync()).GetValueOrThrow().ShouldBeFalse();
        (await cluster.Session(b).IsLiveAsync()).GetValueOrThrow().ShouldBeFalse();

        (await cluster.Session(a).GetAsync()).GetValueOrThrow()
            .RevokedBecause.ShouldBe(RevocationReason.CredentialChange);
    }

    [Fact]
    public async Task SuspendingAUserKillsEverySessionBeforeTheCallReturns() {
        var userId = await cluster.CreateUserAsync("suspend-me@example.com", "a-password-value");

        var sessionId = Guid.NewGuid();
        await Open(cluster.Session(sessionId), userId);
        await cluster.User(userId).TrackSessionAsync(sessionId);

        await cluster.User(userId).SetStatusAsync(UserStatus.Suspended);

        // Synchronous, not eventual: an administrator who is told the account is suspended has been
        // told it cannot act.
        (await cluster.Session(sessionId).IsLiveAsync()).GetValueOrThrow().ShouldBeFalse();
    }

    [Fact]
    public async Task ASessionCannotBeOpenedTwice() {
        var session = cluster.Session(Guid.NewGuid());
        await Open(session);

        var again = await session.OpenAsync(
            Guid.NewGuid(),
            "portal",
            "another device",
            "digest",
            [AuthenticationMethod.Password]
        );

        // ⚠ A second Open would mint a second chain for one session, and the first chain would then
        // have nowhere to be revoked from.
        again.IsFailure.ShouldBeTrue();
        again.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    static async Task<RefreshRotation> Open(ISessionGrain session, Guid? userId = null) {
        var opened = await session.OpenAsync(
            userId ?? Guid.NewGuid(),
            "portal",
            "Firefox on macOS",
            "0123456789abcdef",
            [AuthenticationMethod.Password]
        );

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        return opened.GetValueOrThrow();
    }
}
