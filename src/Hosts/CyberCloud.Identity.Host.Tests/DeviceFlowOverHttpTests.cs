using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.RateLimiting;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using System.Net;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     RFC 8628 over HTTP, against the real host: <c>/device</c>, the verification page's API, and
///     every answer <c>/token</c> gives a poll — <c>authorization_pending</c>, <c>slow_down</c>,
///     <c>access_denied</c>, <c>expired_token</c>, the tokens, and <c>invalid_grant</c> for a code
///     that is spent or never existed — then the refresh and <c>/revoke</c> that <c>cyc</c> runs on
///     them. docs/plan/11 § Protocol, issue #43 step 8.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The device is an <see cref="HttpClient" /> with no cookies and no <c>Origin</c>, and
///         the person is a <see cref="BrowserClient" /> on the identity app's origin.</b> Two
///         machines, as the flow means them: nothing the browser holds reaches the device except
///         through the grain the user code names.
///     </para>
///     <para>
///         ⚠ <b>The interval and the ten minutes are the silo's clock, moved by
///         <see cref="IdentityHostFixture.SiloClock" /></b>, because the grain measures both — a test
///         that waited five real seconds per poll would be a test nobody runs.
///     </para>
/// </remarks>
[Collection(IdentityHostSuite.Name)]
public sealed class DeviceFlowOverHttpTests(IdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    const string Scope = "openid profile offline_access cyc.api";

    [Fact]
    public async Task TheDiscoveryDocumentAdvertisesTheDeviceAndRevocationEndpoints() {
        using var http = Device();
        using var discovery = await http.GetAsync("/.well-known/openid-configuration", Ct);
        var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync(Ct)).RootElement;

        document.GetProperty("device_authorization_endpoint")
            .GetString()
            .ShouldEndWith(IdentityHostOpenIddict.DeviceAuthorizationPath);
        document.GetProperty("revocation_endpoint").GetString().ShouldEndWith(IdentityHostOpenIddict.RevocationPath);
        document.GetProperty("grant_types_supported")
            .EnumerateArray()
            .Select(static x => x.GetString())
            .ShouldContain("urn:ietf:params:oauth:grant-type:device_code");
        // ⚠ Still no introspection: /revoke is for refresh tokens, and nothing here makes the
        // gateway's "validate locally" contract optional.
        document.TryGetProperty("introspection_endpoint", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ADeviceIsPendingThenToldToSlowDownThenApprovedThenSpent() {
        using var device = Device();

        // ── 1. The device asks. RFC 8628 § 3.2's response, every member.
        var started = await StartAsync(device);

        started.UserCode.ShouldMatch("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$");
        started.VerificationUri.ShouldEndWith(IdentityHostOpenIddict.EndUserVerificationPath);
        started.VerificationUriComplete.ShouldEndWith("?user_code=" + started.UserCode);
        started.ExpiresIn.ShouldBeInRange((int)DeviceCodes.Lifetime.TotalSeconds - 2, (int)DeviceCodes.Lifetime.TotalSeconds);
        started.Interval.ShouldBe((int)DeviceCodes.PollingInterval.TotalSeconds);
        // ⚠ The device code carries its user code and 256 bits beside it — DeviceCodes' remarks.
        started.DeviceCode.ShouldStartWith(started.UserCode.Replace("-", string.Empty, StringComparison.Ordinal) + ".");

        // ── 2. Nobody has answered: authorization_pending. Polled again at once: slow_down.
        await ShouldBePollError(device, started.DeviceCode, "authorization_pending", "nobody has answered yet");
        await ShouldBePollError(device, started.DeviceCode, "slow_down", "the second poll came inside the interval");

        // ── 3. The person opens the printed link: the verification endpoint redirects to the page,
        //       carrying the code in its display form.
        var person = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);
        using var browser = person.Browser;

        using var link = await browser.GetAsync(new Uri(started.VerificationUriComplete).PathAndQuery, Ct);

        link.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        BrowserClient.Location(link)
            .ToString()
            .ShouldBe(IdentityHostFixture.SignInPageBaseUri + DeviceApi.PagePath + "?user_code=" + started.UserCode);

        // ── 4. The page looks the code up — typed in lower case with no dash, as a phone types it.
        var lookup = await LookupAsync(browser, started.UserCode.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant());

        lookup.GetProperty("found").GetBoolean().ShouldBeTrue(lookup.GetRawText());
        lookup.GetProperty("clientName").GetString().ShouldBe("cyc", "the registered name, never the request's");
        lookup.GetProperty("signedIn").GetBoolean().ShouldBeTrue();
        lookup.GetProperty("account").GetString().ShouldBe(person.Email);
        lookup.GetProperty("status").GetString().ShouldBe("pending");
        lookup.GetProperty("scopes").EnumerateArray().Select(static x => x.GetString()).ShouldBe(Scope.Split(' '));

        // ── 5. Allow.
        var decided = await DecideAsync(browser, started.UserCode, "allow");

        decided.GetProperty("status").GetString().ShouldBe("approved", decided.GetRawText());
        decided.GetProperty("message").GetString().ShouldBe(DeviceApi.Approved);

        // ⚠ One-time: the same code cannot be answered twice, whichever way.
        var again = await DecideAsync(browser, started.UserCode, "deny");

        again.GetProperty("found").GetBoolean().ShouldBeFalse();
        again.GetProperty("message").GetString().ShouldBe(DeviceApi.AlreadyUsed);

        // ── 6. The device polls after the interval — now ten seconds, after the slow_down — and
        //       collects its tokens: the access token in the person's tenant, a refresh token in the
        //       body because the CLI is not a browser, and an id_token for openid.
        fixture.SiloClock.Advance(TimeSpan.FromSeconds(11));

        using var collected = await Poll(device, started.DeviceCode);
        var tokens = await BrowserClient.JsonAsync(collected, Ct);

        collected.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());
        tokens.GetProperty("token_type").GetString().ShouldBe("Bearer");
        tokens.TryGetProperty("refresh_token", out var refreshToken).ShouldBeTrue();
        tokens.TryGetProperty("id_token", out _).ShouldBeTrue();

        var access = BrowserClient.Payload(tokens.GetProperty("access_token").GetString()!);

        access.GetProperty(AccessTokenClaims.TenantId).GetString().ShouldBe(IdentityHostFixture.Tenant.ToString("N"));
        access.GetProperty(AccessTokenClaims.Subject).GetString().ShouldBe(person.UserId.ToString("N"));
        access.GetProperty(AccessTokenClaims.SubjectType).GetString().ShouldBe("user");
        access.GetProperty("azp").GetString().ShouldBe(FirstPartyClients.Cli);
        access.EnumerateObject()
            .Select(static x => x.Name)
            .ShouldAllBe(x => AccessTokenClaims.Permitted.Contains(x), "the closed set holds for this grant too");
        // The sign-in's methods, not the approval's: a password and a delivered code.
        access.GetProperty(AccessTokenClaims.AuthenticationMethods)
            .EnumerateArray()
            .Select(static x => x.GetString())
            .ShouldBe(["pwd", "otp"]);

        // ── 7. The device code is spent. Presented again, it is refused — and the session its first
        //       redemption opened is revoked, as a replayed authorization code's is.
        await ShouldBePollError(device, started.DeviceCode, "invalid_grant", "a spent device code");

        using var afterReplay = await RefreshAsync(device, refreshToken.GetString()!);

        afterReplay.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            "the replay revoked the session the first redemption opened"
        );
    }

    [Fact]
    public async Task ACliSessionRefreshesRotatesSurvivesTheBrowsersSignOutAndEndsAtRevoke() {
        using var device = Device();
        var (browser, _, _) = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);

        using (browser) {
            var tokens = await ApprovedTokensAsync(device, browser);
            var refresh = tokens.GetProperty("refresh_token").GetString()!;

            // ── Refresh on use: #94's rotation — a new refresh token, and the old one is spent.
            using var rotated = await RefreshAsync(device, refresh);
            var next = await BrowserClient.JsonAsync(rotated, Ct);

            rotated.StatusCode.ShouldBe(HttpStatusCode.OK, next.GetRawText());

            var nextRefresh = next.GetProperty("refresh_token").GetString()!;

            nextRefresh.ShouldNotBe(refresh);

            // ── The browser signs out. ⚠ The CLI's chain is bound to itself, not to the browser
            //    session it was approved from — TokenApi.MintForDeviceCodeAsync — so it survives.
            using (await browser.GetAsync(
                       IdentityHostOpenIddict.EndSessionPath
                       + "?client_id="
                       + FirstPartyClients.Portal
                       + "&post_logout_redirect_uri="
                       + Uri.EscapeDataString(FirstPartyClients.DevelopmentPortalPostLogoutRedirectUri),
                       Ct
                   )) { }

            using var afterLogout = await RefreshAsync(device, nextRefresh);
            var survived = await BrowserClient.JsonAsync(afterLogout, Ct);

            afterLogout.StatusCode.ShouldBe(HttpStatusCode.OK, "the browser's sign-out ended the device's session: " + survived.GetRawText());

            var live = survived.GetProperty("refresh_token").GetString()!;

            // ── An access token at /revoke is refused out loud: RFC 7009 § 2.2.1.
            using var accessRevoke = await RevokeAsync(device, survived.GetProperty("access_token").GetString()!);
            var accessAnswer = await BrowserClient.JsonAsync(accessRevoke, Ct);

            accessRevoke.StatusCode.ShouldBe(HttpStatusCode.BadRequest, accessAnswer.GetRawText());
            accessAnswer.GetProperty("error").GetString().ShouldBe("unsupported_token_type");

            // ── cyc logout: the refresh token is revoked, and the chain with it.
            using var revoked = await RevokeAsync(device, live);

            revoked.StatusCode.ShouldBe(HttpStatusCode.OK, await revoked.Content.ReadAsStringAsync(Ct));

            using var afterRevoke = await RefreshAsync(device, live);
            var dead = await BrowserClient.JsonAsync(afterRevoke, Ct);

            afterRevoke.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            dead.GetProperty("error").GetString().ShouldBe("invalid_grant");
        }
    }

    [Fact]
    public async Task ADenialIsAccessDenied() {
        using var device = Device();
        var started = await StartAsync(device);
        var (browser, _, _) = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);

        using (browser) {
            (await DecideAsync(browser, started.UserCode, "deny")).GetProperty("status").GetString().ShouldBe("denied");
        }

        await ShouldBePollError(device, started.DeviceCode, "access_denied", "the person declined");
    }

    [Fact]
    public async Task ACodeNobodyAnsweredExpiresAndTheLookupForgetsIt() {
        using var device = Device();
        var started = await StartAsync(device);

        fixture.SiloClock.Advance(DeviceCodes.Lifetime + TimeSpan.FromSeconds(1));

        await ShouldBePollError(device, started.DeviceCode, "expired_token", "the ten minutes are over");

        using var anonymous = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        var lookup = await LookupAsync(anonymous, started.UserCode);

        lookup.GetProperty("found").GetBoolean().ShouldBeFalse();
        lookup.GetProperty("message").GetString().ShouldBe(DeviceApi.NotFound);
    }

    [Fact]
    public async Task ADeviceCodeThisServerNeverIssuedIsInvalidGrantAndAGuessedSecretMovesNothing() {
        using var device = Device();
        var started = await StartAsync(device);
        var userCode = started.UserCode.Replace("-", string.Empty, StringComparison.Ordinal);

        await ShouldBePollError(device, "not-a-device-code", "invalid_grant", "garbage");

        // ⚠ The user code is on the person's screen; a device code built around it with a made-up
        // secret is refused as if it did not exist, and does not slow the real device down.
        await ShouldBePollError(device, userCode + "." + new string('A', 43), "invalid_grant", "a guessed secret");
        await ShouldBePollError(device, started.DeviceCode, "authorization_pending", "the guess did not touch the interval");
    }

    [Fact]
    public async Task TheLookupIsAnonymousAndOneSentenceForEveryWrongCode() {
        using var anonymous = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        using var device = Device();
        var started = await StartAsync(device);

        var found = await LookupAsync(anonymous, started.UserCode);

        found.GetProperty("found").GetBoolean().ShouldBeTrue();
        found.GetProperty("signedIn").GetBoolean().ShouldBeFalse("the page must send this person to sign in first");

        foreach (var wrong in new[] { "BCDF-GHJ", "AEIO-UAEI", "BCDF-GHJK-L", "", "<script>" }) {
            var refused = await LookupAsync(anonymous, wrong);

            refused.GetProperty("found").GetBoolean().ShouldBeFalse(wrong);
            refused.GetProperty("message").GetString().ShouldBe(DeviceApi.NotFound, wrong);
        }

        // And an answer with no cookie is "sign in first", not an approval.
        var unsigned = await DecideAsync(anonymous, started.UserCode, "allow");

        unsigned.GetProperty("message").GetString().ShouldBe(DeviceApi.SignInFirst);
    }

    [Fact]
    public async Task AnAnswerFromAForeignOriginChangesNothing() {
        using var device = Device();
        var started = await StartAsync(device);
        var (browser, _, _) = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);

        using (browser) {
            // ⚠ The person's own cookie, from somebody else's page — IdentityEndpoints.MapDevicePage.
            browser.Origin = "https://attacker.example";

            var forged = await DecideAsync(browser, started.UserCode, "allow");

            forged.GetProperty("message").GetString().ShouldBe(DeviceApi.SignInFirst);

            browser.Origin = IdentityHostFixture.SignInPageBaseUri;
            (await LookupAsync(browser, started.UserCode)).GetProperty("status").GetString().ShouldBe("pending");
        }

        await ShouldBePollError(device, started.DeviceCode, "authorization_pending", "the forged answer approved nothing");
    }

    [Fact]
    public async Task OnlyAClientRegisteredForTheGrantMayStartOne() {
        using var device = Device();

        foreach (var (clientId, error) in new[] {
                     (FirstPartyClients.Portal, "unauthorized_client"),
                     (IdentityHostFixture.TenantPublicClient, "invalid_client"),
                     ("nobody", "invalid_client")
                 }) {
            using var refused = await device.PostAsync(
                IdentityHostOpenIddict.DeviceAuthorizationPath,
                new FormUrlEncodedContent(
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = clientId, ["scope"] = Scope }
                ),
                Ct
            );

            var body = await BrowserClient.JsonAsync(refused, Ct);

            body.GetProperty("error").GetString().ShouldBe(error, clientId + ": " + body.GetRawText());
        }
    }

    [Fact]
    public async Task ThePerIpLimitOnStartingADeviceSignInTripsAndRecovers() {
        using var device = Device();
        var bucket = IdentityRateLimits.DeviceAuthorization;

        fixture.Clock.Advance(bucket.Window + TimeSpan.FromSeconds(1));

        for (var i = 0; i < bucket.Limit; i++) {
            using var admitted = await Start(device);

            admitted.StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1} of {bucket.Limit} was refused inside the window");
        }

        using var refused = await Start(device);

        refused.StatusCode.ShouldBe((HttpStatusCode)429);
        refused.Headers.RetryAfter.ShouldNotBeNull();
        (await BrowserClient.JsonAsync(refused, Ct)).GetProperty("message").GetString().ShouldBe(IdentityRateLimits.RefusedMessage);

        fixture.Clock.Advance(bucket.Window + TimeSpan.FromSeconds(1));

        using var recovered = await Start(device);

        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, "the limit did not recover once the window passed");
    }

    // ── The device ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The device: no cookies, no <c>Origin</c>, as <c>cyc</c> on a build agent is.</summary>
    HttpClient Device() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
        BaseAddress = fixture.BaseAddress
    };

    sealed record Started(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string VerificationUriComplete,
        int ExpiresIn,
        int Interval
    );

    static Task<HttpResponseMessage> Start(HttpClient device) =>
        device.PostAsync(
            IdentityHostOpenIddict.DeviceAuthorizationPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = FirstPartyClients.Cli, ["scope"] = Scope }
            ),
            Ct
        );

    static async Task<Started> StartAsync(HttpClient device) {
        using var response = await Start(device);
        var body = await BrowserClient.JsonAsync(response, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.GetRawText());

        return new(
            body.GetProperty("device_code").GetString()!,
            body.GetProperty("user_code").GetString()!,
            body.GetProperty("verification_uri").GetString()!,
            body.GetProperty("verification_uri_complete").GetString()!,
            body.GetProperty("expires_in").GetInt32(),
            body.GetProperty("interval").GetInt32()
        );
    }

    static Task<HttpResponseMessage> Poll(HttpClient device, string deviceCode) =>
        device.PostAsync(
            IdentityHostOpenIddict.TokenPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = FirstPartyClients.Cli,
                    ["device_code"] = deviceCode
                }
            ),
            Ct
        );

    /// <summary>RFC 8628 § 3.5: every one of these is a <c>400</c> with the error in the body.</summary>
    static async Task ShouldBePollError(HttpClient device, string deviceCode, string error, string because) {
        using var response = await Poll(device, deviceCode);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, because + ": " + body);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().ShouldBe(error, because + ": " + body);
    }

    static Task<HttpResponseMessage> RefreshAsync(HttpClient device, string refreshToken) =>
        device.PostAsync(
            IdentityHostOpenIddict.TokenPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = FirstPartyClients.Cli,
                    ["refresh_token"] = refreshToken
                }
            ),
            Ct
        );

    static Task<HttpResponseMessage> RevokeAsync(HttpClient device, string token) =>
        device.PostAsync(
            IdentityHostOpenIddict.RevocationPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["client_id"] = FirstPartyClients.Cli,
                    ["token"] = token
                }
            ),
            Ct
        );

    /// <summary>A device started, approved by <paramref name="browser" />'s person, and redeemed.</summary>
    async Task<JsonElement> ApprovedTokensAsync(HttpClient device, BrowserClient browser) {
        var started = await StartAsync(device);

        (await DecideAsync(browser, started.UserCode, "allow")).GetProperty("status").GetString().ShouldBe("approved");

        using var collected = await Poll(device, started.DeviceCode);
        var tokens = await BrowserClient.JsonAsync(collected, Ct);

        collected.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        return tokens;
    }

    // ── The page ───────────────────────────────────────────────────────────────────────────────

    static async Task<JsonElement> LookupAsync(BrowserClient browser, string userCode) {
        using var response = await browser.PostJsonAsync("/api/device/lookup", new { userCode }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await BrowserClient.JsonAsync(response, Ct);
    }

    static async Task<JsonElement> DecideAsync(BrowserClient browser, string userCode, string decision) {
        using var response = await browser.PostJsonAsync("/api/device/decision", new { userCode, decision }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await BrowserClient.JsonAsync(response, Ct);
    }
}
