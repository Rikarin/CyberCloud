using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Routing;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.Gateway.Host.WellKnown;
using NSubstitute;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     RFC 9116 <c>security.txt</c>, served from the gateway — docs/plan/18 § Disclosure. Three
///     things: the file is served as the RFC says, the pipeline's invariants survived the one
///     non-JSON body, and the committed file will not go stale without the build going red first.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="ExpiresIsAtLeastThirtyDaysOut" /> reads the real clock, on purpose, and is the
///     only test in this suite that does.</b> Every other test here drives a <c>FakeClock</c>; this
///     one asks whether the bytes that ship are good for another thirty days from <i>today</i>,
///     because that is the question. It will go red one day, by design, and the fix is in
///     <see cref="SecurityTxt" />'s remarks.
/// </remarks>
public sealed class SecurityTxtTests {
    const int MinimumDaysAhead = 30;

    // ── Served ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheFileIsServedAsPlainTextWithNoTokenAndNoApiVersion() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync("GET", SecurityTxt.Path, token: null, query: "");

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Header("Content-Type").ShouldBe("text/plain; charset=utf-8");
        response.Body.ShouldBe(SecurityTxt.Content);
        response.Trace.ShouldContain("Dispatch");
    }

    /// <summary>
    ///     docs/plan/10 § Request pipeline puts the ids on <i>every</i> response. The plain-text
    ///     body went through the same writer as every JSON one, so the ids are there without a
    ///     second act of care — this test is what says the invariant held.
    /// </summary>
    [Fact]
    public async Task TheFileCarriesBothCorrelationIdsLikeEveryOtherResponse() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            SecurityTxt.Path,
            token: null,
            query: "",
            headers: (GatewayHeaders.CorrelationRequestId, "scanner-run-7")
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Header(GatewayHeaders.RequestId).ShouldNotBeEmpty();
        response.Header(GatewayHeaders.CorrelationRequestId).ShouldBe("scanner-run-7");
    }

    /// <summary>
    ///     A token is not required, and a token is not refused either — a researcher with a
    ///     tenant credential in their client still gets the file. What must not happen is a
    ///     tenant reaching dispatch off an anonymous route, so the manager saw nothing.
    /// </summary>
    [Fact]
    public async Task ATokenIsNeitherRequiredNorRefusedAndNoManagerIsReached() {
        var gateway = new GatewayHarness();

        var withToken = await gateway.SendAsync("GET", SecurityTxt.Path, gateway.Token(GatewayHarness.TenantA), "");

        withToken.Status.ShouldBe(StatusCodes.Status200OK);
        withToken.Body.ShouldBe(SecurityTxt.Content);
        gateway.Manager.Paths.ShouldBeEmpty();
        gateway.Scopes.Paths.ShouldBeEmpty();
        gateway.Grains.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task AnyOtherMethodOnThePathIsTheCanonical404InJson(string method) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(method, SecurityTxt.Path, gateway.Token(GatewayHarness.TenantA), "");

        response.Status.ShouldBe(StatusCodes.Status404NotFound);
        response.Header("Content-Type").ShouldBe(ErrorBody.ContentType);
        response.Body.ShouldContain("\"code\":\"ResourceNotFound\"");
        response.Header(GatewayHeaders.RequestId).ShouldNotBeEmpty();
    }

    /// <summary>
    ///     The plain-text kind exists for one path. Everything else — a success, a <c>400</c>, a
    ///     <c>401</c>, a <c>404</c>, and the rest of <c>/.well-known</c> — still answers JSON with
    ///     <see cref="ErrorBody.ContentType" />, which is what "one more kind, not a content-type
    ///     member" has to mean to be worth saying.
    /// </summary>
    [Theory]
    [InlineData("GET", "/openapi", true, 400)]
    [InlineData("GET", "/.well-known/openid-configuration", false, 400)]
    [InlineData("GET", "/.well-known/security.txt.bak", false, 400)]
    [InlineData("GET", "/tenants/nonsense", false, 401)]
    [InlineData("GET", "/operations/00000000-0000-0000-0000-000000000000", true, 404)]
    public async Task EveryOtherPathStillAnswersJson(string method, string path, bool authenticated, int expected) {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            method,
            path,
            authenticated ? gateway.Token(GatewayHarness.TenantA) : null,
            path == "/openapi" ? "" : "api-version=" + OneTypeRegistry.TheVersion
        );

        response.Status.ShouldBe(expected);
        response.Header("Content-Type").ShouldBe(ErrorBody.ContentType);
        response.Header(GatewayHeaders.RequestId).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ASuccessfulResourceReadIsStillJson() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "GET",
            GatewayHarness.ResourcePath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA)
        );

        response.Status.ShouldBe(StatusCodes.Status200OK);
        response.Header("Content-Type").ShouldBe(ErrorBody.ContentType);
    }

    // ── The invariant ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRouterAdmitsExactlyOneWellKnownPathAndOnlyOnGet() {
        GatewayRouter.Resolve(SecurityTxt.Path, "GET", Guid.Empty).GetValueOrThrow().Kind.ShouldBe(RouteKind.SecurityTxt);

        GatewayRouter.Resolve(SecurityTxt.Path, "POST", Guid.Empty).TryGetError(out var post).ShouldBeTrue();
        post.Code.ShouldBe(ErrorCode.ResourceNotFound);

        GatewayRouter.Resolve("/.well-known/security.txt/", "GET", Guid.Empty).TryGetError(out _).ShouldBeTrue();
        GatewayRouter.Resolve("/.well-known/Security.txt", "GET", Guid.Empty).TryGetError(out _).ShouldBeTrue();
    }

    /// <summary>
    ///     The writer decides the media type by body kind and refuses an outcome that sets two.
    ///     A precedence rule would have hidden the mistake; this is the sabotage that proves there is
    ///     none.
    /// </summary>
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AnOutcomeWithTwoBodiesIsRefusedNotResolved(bool error, bool json, bool text) {
        var context = new GatewayRequestContext(new DefaultHttpContext());

        var outcome = new GatewayOutcome {
            Error = error ? new(ErrorCode.InternalError, "x") : null,
            Json = json ? "{}" : null,
            Text = text ? "Contact: mailto:x@example" : null
        };

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => ResponseWriter.WriteAsync(context, outcome));

        refused.Message.ShouldContain("body kinds");
        context.Http.Response.Headers.ContentType.ToString().ShouldBeEmpty();
    }

    // ── The committed file ─────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     RFC 9116 § 2.5: <c>Contact</c> and <c>Expires</c> are required; <c>Canonical</c>,
    ///     <c>Policy</c>, and <c>Preferred-Languages</c> are the recommended ones this platform
    ///     commits to. Each is checked for shape, not only presence, because a <c>Contact</c> that is
    ///     not a URI or an <c>Expires</c> that is a bare date is a file every validator rejects.
    /// </summary>
    [Fact]
    public void TheCommittedFileHasTheRequiredAndRecommendedFieldsInTheRfcsShape() {
        var fields = SecurityTxt.Fields;

        string One(string name) {
            var matches = fields.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            matches.Count.ShouldBe(1, $"security.txt must carry exactly one '{name}' line");
            return matches[0].Value;
        }

        // § 2.5.3 — a URI, and a mailto: one here. The address is the one the disclosure policy
        // names, and it is the same string in three places on purpose: this file, SECURITY.md, and
        // docs/security/disclosure-policy.md. A drift between them is a report sent nowhere.
        One("Contact").ShouldBe("mailto:security@cybercloud.io");

        // § 2.5.5 — an RFC 3339 date-time, so a scanner can compare it. A bare date is refused.
        SecurityTxt.Expires().ShouldNotBeNull();

        // § 2.5.8 — language tags, comma-separated. One is enough; it must not be empty.
        One("Preferred-Languages").ShouldBe("en");

        // § 2.5.7 — where the disclosure policy is published. The repository's URL, because no apex
        // ingress exists to serve one — docs/plan/18 § Disclosure.
        var policy = One("Policy");
        Uri.TryCreate(policy, UriKind.Absolute, out var policyUri).ShouldBeTrue();
        policyUri!.Scheme.ShouldBe("https");
        policy.ShouldEndWith("/docs/security/disclosure-policy.md");

        // § 2.5.2 — the URL this file is served at, so a copy found elsewhere can be told from the
        // original. The gateway's own origin and the route this suite drives.
        One("Canonical").ShouldBe("https://api.cybercloud.io" + SecurityTxt.Path);

        // § 4 — UTF-8, no byte-order mark, LF or CRLF only.
        SecurityTxt.Content.ShouldNotStartWith("﻿");
        SecurityTxt.Content.ShouldNotContain("\r\r");
    }

    /// <summary>
    ///     The build gate docs/plan/18 § Disclosure asks for: <i>"a <c>security.txt</c> with a stale
    ///     <c>Expires</c> is worse than none. Shipping one means shipping the thing that fails the
    ///     build before it expires, in the same commit."</i> Thirty days is the notice period.
    /// </summary>
    /// <remarks>
    ///     ⚠ Real clock. When this goes red, the file has not lapsed yet — it has thirty days
    ///     left, which is the point. Move <c>Expires</c> forward about eleven months after rereading
    ///     the policy; <see cref="SecurityTxt" />'s remarks carry the checklist.
    /// </remarks>
    [Fact]
    public void ExpiresIsAtLeastThirtyDaysOut() {
        var expires = SecurityTxt.Expires();
        expires.ShouldNotBeNull();

        var daysLeft = (expires.Value - DateTimeOffset.UtcNow).TotalDays;

        daysLeft.ShouldBeGreaterThan(
            MinimumDaysAhead,
            $"security.txt's Expires ({expires.Value:O}) is {daysLeft:F0} days away and the gate is "
            + $"{MinimumDaysAhead}. Reread docs/security/disclosure-policy.md, confirm the contact is "
            + "monitored, and move Expires in src/Hosts/CyberCloud.Gateway.Host/WellKnown/security.txt "
            + "forward by about eleven months. docs/plan/18 § Disclosure."
        );
    }

    /// <summary>
    ///     RFC 9116 § 2.5.5: <i>"It is RECOMMENDED that the value of this field be less than a year
    ///     into the future"</i>. An <c>Expires</c> set far out is a file nobody rereads, which is the
    ///     failure the field exists to prevent — so the recommendation is enforced.
    /// </summary>
    [Fact]
    public void ExpiresIsLessThanAYearOut() {
        var expires = SecurityTxt.Expires();
        expires.ShouldNotBeNull();

        (expires.Value - DateTimeOffset.UtcNow).TotalDays.ShouldBeLessThan(366);
    }
}
