using CyberCloud.Core.Time;
using CyberCloud.Identity.Host.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     <see cref="PasskeyChallengeCookie" /> is the reason a passkey assertion is verified against
///     options <b>this process</b> issued rather than options the caller posted.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The attack it prevents is a complete sign-in bypass, not a hardening nicety.</b> An
///         endpoint that took <c>OriginalOptions</c> from the request body would let an attacker
///         generate a challenge, sign it with an authenticator they hold, and post both; the library
///         verifies the signature correctly and the sign-in succeeds, because nothing in that
///         exchange was ever chosen by this server. Every assertion below is about one of the
///         properties that closes it.
///     </para>
///     <para>
///         ⚠ Real <see cref="IDataProtector" /> and a real <see cref="HttpContext" />, not doubles.
///         Three of these tests — tamper, foreign key ring, wrong purpose — are assertions about what
///         data protection actually does, and a substitute that returned what it was given would pass
///         all three while proving nothing.
///     </para>
/// </remarks>
public sealed class PasskeyChallengeCookieTests {
    sealed class FrozenClock(DateTimeOffset now) : IClock {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    static PasskeyChallengeTicket Ticket(DateTimeOffset? expiresAt = null) =>
        new("{\"challenge\":\"abc\",\"allowCredentials\":[{\"id\":\"k1\"}]}",
            "someone@example.com",
            expiresAt ?? Now.AddMinutes(5));

    static (PasskeyChallengeCookie Cookie, FrozenClock Clock) Subject(
        IDataProtectionProvider? protection = null
    ) {
        var clock = new FrozenClock(Now);
        return (new(protection ?? new EphemeralDataProtectionProvider(), clock), clock);
    }

    /// <summary>Moves the cookie an <c>Issue</c> wrote onto the next request, as a browser would.</summary>
    static DefaultHttpContext NextRequestCarrying(HttpContext issued) {
        var setCookie = issued.Response.Headers.SetCookie.ToString();
        var value = setCookie
            .Split(';')[0]
            .Split('=', 2)[1];

        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = $"{PasskeyChallengeCookie.CookieName}={value}";
        return next;
    }

    [Fact]
    public void AnIssuedChallengeComesBackOnTheNextRequest() {
        var (cookie, _) = Subject();
        var issued = new DefaultHttpContext();
        var ticket = Ticket();

        cookie.Issue(issued, ticket);

        var taken = cookie.Take(NextRequestCarrying(issued));

        taken.ShouldNotBeNull();
        taken.OptionsJson.ShouldBe(ticket.OptionsJson);
        taken.Email.ShouldBe(ticket.Email, "completion must not be answerable for a different account");
        taken.ExpiresAt.ShouldBe(ticket.ExpiresAt);
    }

    [Fact]
    public void TheCookieIsHostPrefixedHttpOnlyAndSecure() {
        var (cookie, _) = Subject();
        var context = new DefaultHttpContext();

        cookie.Issue(context, Ticket());

        var setCookie = context.Response.Headers.SetCookie.ToString();

        // ⚠ The `__Host-` prefix is browser-enforced: it only accepts the cookie when it is Secure,
        // has Path=/ and carries no Domain. A subdomain the tenant does not control therefore cannot
        // set a challenge for this origin, which is the half of the defence the server cannot do.
        PasskeyChallengeCookie.CookieName.ShouldStartWith("__Host-");
        setCookie.ShouldStartWith(PasskeyChallengeCookie.CookieName + "=");
        setCookie.ShouldContain("path=/", Case.Insensitive);
        setCookie.ShouldContain("secure", Case.Insensitive);
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldNotContain("domain=", Case.Insensitive);
    }

    [Fact]
    public void TheChallengeIsNotReadableFromTheCookieValue() {
        var (cookie, _) = Subject();
        var context = new DefaultHttpContext();

        cookie.Issue(context, Ticket());

        var setCookie = context.Response.Headers.SetCookie.ToString();

        // ⚠ Protected, not merely signed. A signed-only payload is readable, and reading it
        // enumerates the credential ids the account was offered — which is a list of the user's
        // authenticators handed to anyone who can start a sign-in with their address.
        setCookie.ShouldNotContain("allowCredentials");
        setCookie.ShouldNotContain("someone@example.com");
        setCookie.ShouldNotContain("challenge");
    }

    [Fact]
    public void AChallengeIsTakenOnceAndTheCookieIsDeletedWhateverTheAnswer() {
        var (cookie, _) = Subject();
        var issued = new DefaultHttpContext();
        cookie.Issue(issued, Ticket());

        var second = NextRequestCarrying(issued);
        cookie.Take(second).ShouldNotBeNull();

        // ⚠ A nonce that survives a failed attempt is not a nonce. The delete goes out on the
        // response of the request that read it, so the browser cannot present it twice.
        second.Response.Headers.SetCookie
            .ToString()
            .ShouldContain(PasskeyChallengeCookie.CookieName + "=;");

        // And on the path where there was nothing to read, so a caller cannot leave a stale one.
        var empty = new DefaultHttpContext();
        cookie.Take(empty).ShouldBeNull();
        empty.Response.Headers.SetCookie
            .ToString()
            .ShouldContain(PasskeyChallengeCookie.CookieName + "=;");
    }

    [Fact]
    public void AnExpiredChallengeIsRefusedEvenThoughTheBrowserSentIt() {
        var (cookie, clock) = Subject();
        var issued = new DefaultHttpContext();
        cookie.Issue(issued, Ticket(Now.AddMinutes(5)));

        var next = NextRequestCarrying(issued);
        clock.UtcNow = Now.AddMinutes(5);

        // ⚠ The expiry that counts is the one inside the protected payload. The `Expires` attribute
        // on the cookie is the browser's copy and the browser is the caller's — a caller who keeps
        // sending an expired cookie must be refused by this server, not trusted to drop it.
        cookie.Take(next).ShouldBeNull("the ticket expires at exactly this instant, not after it");
    }

    [Fact]
    public void ATamperedChallengeIsIndistinguishableFromNoChallenge() {
        var (cookie, _) = Subject();
        var issued = new DefaultHttpContext();
        cookie.Issue(issued, Ticket());

        var value = issued.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];

        foreach (var mangled in new[] {
            value[..^4],
            value + "AAAA",
            "not-a-protected-payload",
            string.Concat(value.AsSpan(0, value.Length - 1), value[^1] == 'A' ? "B" : "A")
        }) {
            var next = new DefaultHttpContext();
            next.Request.Headers.Cookie = $"{PasskeyChallengeCookie.CookieName}={mangled}";

            cookie.Take(next).ShouldBeNull(
                "an unprotect failure and an absent cookie must answer identically — a caller who "
                + "could tell them apart would have an oracle over the key ring"
            );
        }
    }

    [Fact]
    public void AChallengeFromAnotherKeyRingIsRefused() {
        var (issuer, _) = Subject();
        var (reader, _) = Subject();

        var issued = new DefaultHttpContext();
        issuer.Issue(issued, Ticket());

        // ⚠ Two replicas with unshared data-protection keys, which is the default. This is the shape
        // of the deployment failure the type's remarks name: `begin` on one replica and `complete`
        // on another silently refuses every sign-in. It has to be a refusal rather than a crash.
        reader.Take(NextRequestCarrying(issued)).ShouldBeNull();
    }

    [Fact]
    public void APayloadProtectedForAnotherPurposeIsRefused() {
        var provider = new EphemeralDataProtectionProvider();
        var (cookie, _) = Subject(provider);

        var elsewhere = provider
            .CreateProtector("CyberCloud.Identity.Host.SomethingElse.v1")
            .Protect(JsonSerializer.Serialize(Ticket()));

        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = $"{PasskeyChallengeCookie.CookieName}={elsewhere}";

        // ⚠ The purpose string is what stops one protected payload being presented as another. Same
        // key ring, same serialized ticket, and it still must not unprotect here.
        cookie.Take(next).ShouldBeNull();
    }

    [Fact]
    public void AProtectedValueThatIsNotATicketIsRefused() {
        var provider = new EphemeralDataProtectionProvider();
        var (cookie, _) = Subject(provider);

        var notATicket = provider
            .CreateProtector(PasskeyChallengeCookie.Purpose)
            .Protect("[1, 2, 3]");

        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = $"{PasskeyChallengeCookie.CookieName}={notATicket}";

        // Genuine key ring, genuine purpose, and the plaintext is still not a ticket. A JsonException
        // escaping here would be a 500 on an unauthenticated endpoint.
        cookie.Take(next).ShouldBeNull();
    }
}
