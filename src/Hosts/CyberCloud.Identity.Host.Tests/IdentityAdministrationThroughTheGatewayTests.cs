using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     The identity administration API as the portal's pages use it: an owner signed in with a real
///     token, every call through the real gateway, the real managers and grains behind it, and the
///     consequences observed where a person meets them — the mail in Mailpit, the invitation page,
///     <c>/token</c>. Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The joins the other suites can't see.</b> <c>IdentityRoutingTests</c> substitutes the
///         managers and <c>CyberCloud.Isolation</c>'s <c>IdentityAdministrationTests</c> calls the
///         service in process. What only this suite shows: a resend reaches a real mailbox with a
///         second link while the first stops opening the page; a client registered through the
///         gateway authenticates at this identity host with the secret the gateway showed once, and
///         not with it after a rotation — so the digest the application grain keeps and the
///         <c>ClientSecretVerifier</c> the host reads it through agree; and "sign this session out"
///         ends the refresh chain of the very token that asked.
///     </para>
///     <para>
///         ⚠ <b>The gateway joins this fixture's cluster over TCP</b>, as
///         <see cref="InvitationThroughTheGatewayTests" />' does, so every new grain call here
///         crosses from an Orleans client into the silo — the directory index, the invitation's
///         resend and revoke, the application's issue and verify, the session list.
///     </para>
/// </remarks>
[Collection(MailpitIdentityHostSuite.Name)]
public sealed class IdentityAdministrationThroughTheGatewayTests(MailpitIdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Guid Tenant => IdentityHostFixture.Tenant;

    const string Scope = "openid profile offline_access cyc.api";

    const string Version = "?api-version=2026-08-01";

    const string AppRedirectUri = "https://acme.example/admin/cb";

    [Fact]
    public async Task AnOwnerListsResendsAndRevokesInvitationsAndRemovesAMember() {
        var (owner, token, _) = await OwnerAsync();
        using var ownerBrowser = owner.Browser;

        await using var gateway = await StartGatewayAsync();
        using var http = new HttpClient { BaseAddress = new(gateway.Urls.First()) };

        // ── Invite, then find it in the list. ──────────────────────────────────────────────────
        var email = $"admin-{Guid.NewGuid():N}@grants.example";
        var invited = await SendAsync(http, HttpMethod.Post, IdentityAddress.Invitations(Tenant).Path, token, new { email });

        invited.Status.ShouldBe(HttpStatusCode.Created, invited.Text);

        var invitationId = Guid.ParseExact(invited.Json.GetProperty("name").GetString()!, "N");
        var userId = Guid.ParseExact(invited.Json.GetProperty("properties").GetProperty("userId").GetString()!, "N");
        var firstLink = BrowserClient.Query(LinkIn((await fixture.MessagesToAsync(email)).ShouldHaveSingleItem()));

        var listed = await SendAsync(http, HttpMethod.Get, IdentityAddress.Invitations(Tenant).Path, token);

        listed.Status.ShouldBe(HttpStatusCode.OK, listed.Text);
        Item(listed.Json, invitationId).GetProperty("properties").GetProperty("status").GetString().ShouldBe("pending");
        Item(listed.Json, invitationId).GetProperty("properties").GetProperty("invitedBy").GetString()
            .ShouldBe(owner.UserId.ToString("N", CultureInfo.InvariantCulture));

        // ── Resend: a second mail, a second link, and the first stops opening the page. ─────────
        var resent = await SendAsync(
            http,
            HttpMethod.Post,
            new IdentityAddress(Tenant, IdentityAddressKind.InvitationResend, invitationId).Path,
            token
        );

        resent.Status.ShouldBe(HttpStatusCode.OK, resent.Text);
        resent.Json.GetProperty("properties").GetProperty("sendings").GetInt32().ShouldBe(2);
        resent.Text.ShouldNotContain("token", Case.Insensitive, "the new link reached the sender");

        var links = (await fixture.MessagesToAsync(email, atLeast: 2)).Select(LinkIn).Select(BrowserClient.Query).ToList();

        links.Count.ShouldBe(2, "the resend reached no mailbox");

        var secondLink = links.Single(x => x["token"] != firstLink["token"]);

        (await DescribeAsync(firstLink)).GetProperty("found").GetBoolean().ShouldBeFalse("the first link still opens the page");
        (await DescribeAsync(secondLink)).GetProperty("status").GetString().ShouldBe("pending");

        // ── Revoke: the second link now says so on the page, and the user stays invited. ────────
        var revoked = await SendAsync(http, HttpMethod.Delete, IdentityAddress.Invitations(Tenant).Item(invitationId).Path, token);

        revoked.Status.ShouldBe(HttpStatusCode.OK, revoked.Text);
        revoked.Json.GetProperty("properties").GetProperty("status").GetString().ShouldBe("revoked");
        (await DescribeAsync(secondLink)).GetProperty("status").GetString().ShouldBe("revoked");

        // ── The members: the owner and the invited colleague; remove the colleague. ─────────────
        var members = await SendAsync(http, HttpMethod.Get, IdentityAddress.Members(Tenant).Path, token);

        members.Status.ShouldBe(HttpStatusCode.OK, members.Text);
        Item(members.Json, owner.UserId).GetProperty("properties").GetProperty("status").GetString().ShouldBe("active");
        Item(members.Json, userId).GetProperty("properties").GetProperty("status").GetString().ShouldBe("invited");

        var removed = await SendAsync(http, HttpMethod.Delete, IdentityAddress.Members(Tenant).Item(userId).Path, token);

        removed.Status.ShouldBe(HttpStatusCode.OK, removed.Text);
        removed.Json.GetProperty("properties").GetProperty("status").GetString().ShouldBe("deprovisioned");

        // And not the owner themselves.
        var self = await SendAsync(http, HttpMethod.Delete, IdentityAddress.Members(Tenant).Item(owner.UserId).Path, token);

        self.Status.ShouldBe(HttpStatusCode.Conflict, self.Text);
    }

    [Fact]
    public async Task AClientRegisteredThroughTheGatewaySignsInWithItsSecretUntilItIsRotated() {
        var (owner, token, _) = await OwnerAsync();
        using var browser = owner.Browser;

        await using var gateway = await StartGatewayAsync();
        using var http = new HttpClient { BaseAddress = new(gateway.Urls.First()) };

        // ── Register a confidential client: the one answer with the secret, marked no-store. ────
        var created = await SendAsync(
            http,
            HttpMethod.Post,
            IdentityAddress.Applications(Tenant).Path,
            token,
            new {
                displayName = "Acme admin",
                redirectUris = new[] { AppRedirectUri },
                scopes = Scope.Split(' '),
                publicClient = false
            }
        );

        created.Status.ShouldBe(HttpStatusCode.Created, created.Text);
        created.CacheControl.ShouldBe("no-store");

        var properties = created.Json.GetProperty("properties");
        var applicationId = Guid.ParseExact(created.Json.GetProperty("name").GetString()!, "N");
        var clientId = properties.GetProperty("clientId").GetString()!;
        var secret = properties.GetProperty("clientSecret").GetString()!;

        // A read never shows it again.
        var read = await SendAsync(http, HttpMethod.Get, IdentityAddress.Applications(Tenant).Item(applicationId).Path, token);

        read.Status.ShouldBe(HttpStatusCode.OK, read.Text);
        read.Text.ShouldNotContain(secret);
        read.Json.GetProperty("properties").TryGetProperty("clientSecret", out _).ShouldBeFalse();

        // ── The person consents, and the client exchanges the code with the secret it was shown. ─
        var code = await ConsentedCodeAsync(browser, clientId);
        using var server = new BrowserClient(fixture.BaseAddress, "https://acme.example");

        // RFC 6749 § 5.2: invalid_client, and a 401 — the code isn't burnt by a wrong secret.
        using (var wrong = await ExchangeAsync(server, code.Code, code.Verifier, clientId, "not-the-secret")) {
            var body = await BrowserClient.JsonAsync(wrong, Ct);

            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, body.GetRawText());
            body.GetProperty("error").GetString().ShouldBe("invalid_client");
        }

        using var exchanged = await ExchangeAsync(server, code.Code, code.Verifier, clientId, secret);
        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        var refreshToken = tokens.GetProperty("refresh_token").GetString()!;

        // ── Rotate: the old secret is refused at /token at once; the new one works. ─────────────
        var rotated = await SendAsync(
            http,
            HttpMethod.Post,
            new IdentityAddress(Tenant, IdentityAddressKind.ApplicationSecret, applicationId).Path,
            token
        );

        rotated.Status.ShouldBe(HttpStatusCode.OK, rotated.Text);
        rotated.CacheControl.ShouldBe("no-store");

        var newSecret = rotated.Json.GetProperty("properties").GetProperty("clientSecret").GetString()!;

        newSecret.ShouldNotBe(secret);

        using (var stale = await RefreshAsync(server, refreshToken, clientId, secret)) {
            var body = await BrowserClient.JsonAsync(stale, Ct);

            stale.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, body.GetRawText());
            body.GetProperty("error").GetString().ShouldBe("invalid_client", "the rotated-out secret still authenticated");
        }

        using (var fresh = await RefreshAsync(server, refreshToken, clientId, newSecret)) {
            fresh.StatusCode.ShouldBe(HttpStatusCode.OK, await fresh.Content.ReadAsStringAsync(Ct));
        }

        // ── Delete: gone, and its client id resolves to nothing. ────────────────────────────────
        var deleted = await SendAsync(http, HttpMethod.Delete, IdentityAddress.Applications(Tenant).Item(applicationId).Path, token);

        deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.Text);
        (await SendAsync(http, HttpMethod.Get, IdentityAddress.Applications(Tenant).Item(applicationId).Path, token)).Status
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task APersonSeesTheirOwnSessionsAndSigningOneOutEndsItsRefreshChain() {
        var (owner, token, refresh) = await OwnerAsync();
        using var browser = owner.Browser;

        await using var gateway = await StartGatewayAsync();
        using var http = new HttpClient { BaseAddress = new(gateway.Urls.First()) };

        var sid = BrowserClient.Payload(token).GetProperty("sid").GetString()!;

        var listed = await SendAsync(http, HttpMethod.Get, IdentityAddress.Sessions(Tenant).Path, token);

        listed.Status.ShouldBe(HttpStatusCode.OK, listed.Text);

        var current = listed.Json.GetProperty("value").EnumerateArray().Single(x => x.GetProperty("name").GetString() == sid);

        current.GetProperty("properties").GetProperty("current").GetBoolean().ShouldBeTrue("the token's own session is not marked");
        current.GetProperty("properties").GetProperty("clientId").GetString().ShouldBe(FirstPartyClients.Cli);
        listed.Json.GetProperty("value").GetArrayLength().ShouldBeGreaterThan(1, "the browser's sign-in is a session too");

        // ── Sign this one out: 204, and the device's refresh token is dead. ────────────────────
        var signedOut = await SendAsync(
            http,
            HttpMethod.Delete,
            IdentityAddress.Sessions(Tenant).Item(Guid.ParseExact(sid, "N")).Path,
            token
        );

        signedOut.Status.ShouldBe(HttpStatusCode.NoContent, signedOut.Text);

        using var device = new BrowserClient(fixture.BaseAddress, "http://127.0.0.1");
        using var refused = await RefreshAsync(device, refresh, FirstPartyClients.Cli, null);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a signed-out session still refreshed: " + await refused.Content.ReadAsStringAsync(Ct));

        // ⚠ The access token still validates for its ten minutes — by design, docs/plan/11
        // § Sessions and revocation — so the list answers, without the session.
        var after = await SendAsync(http, HttpMethod.Get, IdentityAddress.Sessions(Tenant).Path, token);

        after.Status.ShouldBe(HttpStatusCode.OK, after.Text);
        after.Json.GetProperty("value").EnumerateArray().ShouldNotContain(x => x.GetProperty("name").GetString() == sid);

        // Somebody else's session id is a 404 — the colleague's, from a second person's sign-in.
        var colleague = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);
        using var colleagueBrowser = colleague.Browser;
        var (colleagueToken, _) = await DeviceTokenAsync(colleagueBrowser);
        var theirs = BrowserClient.Payload(colleagueToken).GetProperty("sid").GetString()!;

        (await SendAsync(
            http,
            HttpMethod.Delete,
            IdentityAddress.Sessions(Tenant).Item(Guid.ParseExact(theirs, "N")).Path,
            token
        )).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    sealed record Answer(HttpStatusCode Status, string Text, JsonElement Json, string CacheControl);

    /// <summary>A person with Owner on the tenant, signed in, holding a device-flow token and its refresh token.</summary>
    async Task<((BrowserClient Browser, Guid UserId, string Email) Owner, string Token, string Refresh)> OwnerAsync() {
        var owner = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);

        await GrantOwnerAsync(owner.UserId);
        await EnsureTenantRecordAsync();

        var (token, refresh) = await DeviceTokenAsync(owner.Browser);

        return (owner, token, refresh);
    }

    async Task<WebApplication> StartGatewayAsync() {
        var gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                .. fixture.ClusterClientSettings(),
                "--CyberCloud:Gateway:Identity:Issuer=" + fixture.BaseAddress.GetLeftPart(UriPartial.Authority)
            ]
        );

        gateway.MapGateway();
        await gateway.StartAsync(Ct);

        return gateway;
    }

    static async Task<Answer> SendAsync(HttpClient http, HttpMethod method, string path, string token, object? body = null) {
        using var request = new HttpRequestMessage(method, path + Version);

        request.Headers.Authorization = new("Bearer", token);

        if (body is not null) {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        return new(
            response.StatusCode,
            text,
            text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default,
            response.Headers.CacheControl?.ToString() ?? ""
        );
    }

    static JsonElement Item(JsonElement collection, Guid id) =>
        collection.GetProperty("value")
            .EnumerateArray()
            .Single(x => x.GetProperty("name").GetString() == id.ToString("N", CultureInfo.InvariantCulture));

    async Task<JsonElement> DescribeAsync(Dictionary<string, string> link) {
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        using var described = await browser.PostJsonAsync(
            "/api/invitations/describe",
            new { tenant = link["tenant"], invitation = link["invitation"], token = link["token"] },
            Ct
        );

        return await BrowserClient.JsonAsync(described, Ct);
    }

    /// <summary>A token pair for the person <paramref name="browser" /> is signed in as, taken the way <c>cyc login --device-code</c> takes it.</summary>
    async Task<(string Access, string Refresh)> DeviceTokenAsync(BrowserClient browser) {
        using var device = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
            BaseAddress = fixture.BaseAddress
        };

        using var started = await device.PostAsync(
            IdentityHostOpenIddict.DeviceAuthorizationPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["client_id"] = FirstPartyClients.Cli, ["scope"] = Scope }
            ),
            Ct
        );
        var codes = await BrowserClient.JsonAsync(started, Ct);

        started.StatusCode.ShouldBe(HttpStatusCode.OK, codes.GetRawText());

        using (var decided = await browser.PostJsonAsync(
                   "/api/device/decision",
                   new { userCode = codes.GetProperty("user_code").GetString(), decision = "allow" },
                   Ct
               )) {
            (await BrowserClient.JsonAsync(decided, Ct)).GetProperty("status").GetString().ShouldBe("approved");
        }

        using var collected = await device.PostAsync(
            IdentityHostOpenIddict.TokenPath,
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = FirstPartyClients.Cli,
                    ["device_code"] = codes.GetProperty("device_code").GetString()!
                }
            ),
            Ct
        );
        var tokens = await BrowserClient.JsonAsync(collected, Ct);

        collected.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        return (tokens.GetProperty("access_token").GetString()!, tokens.GetProperty("refresh_token").GetString()!);
    }

    /// <summary>An authorization code for the tenant client, after the person allowed it on the consent page.</summary>
    static async Task<(string Code, string Verifier)> ConsentedCodeAsync(BrowserClient browser, string clientId) {
        var (verifier, challenge) = BrowserClient.Pkce();
        var authorize = IdentityHostOpenIddict.AuthorizationPath
            + "?response_type=code&client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(AppRedirectUri)
            + "&scope=" + Uri.EscapeDataString(Scope)
            + "&state=s-admin&code_challenge=" + challenge + "&code_challenge_method=S256&nonce=n-admin"
            + "&tenant=" + Uri.EscapeDataString(IdentityHostFixture.Slug);

        using var asked = await browser.GetAsync(authorize, Ct);

        BrowserClient.Location(asked).GetLeftPart(UriPartial.Path)
            .ShouldBe(IdentityHostFixture.SignInPageBaseUri + AuthorizeApi.ConsentPagePath, "a new tenant client was not sent to the consent page");

        browser.Origin = IdentityHostFixture.SignInPageBaseUri;

        using var allowed = await browser.PostFormAsync(
            IdentityHostOpenIddict.AuthorizationPath,
            new Dictionary<string, string>(BrowserClient.Query(new Uri("http://x" + authorize)), StringComparer.Ordinal) {
                [AuthorizeApi.ConsentParameter] = "allow"
            },
            Ct
        );

        return (BrowserClient.Query(BrowserClient.Location(allowed))["code"], verifier);
    }

    static Task<HttpResponseMessage> ExchangeAsync(BrowserClient client, string code, string verifier, string clientId, string secret) =>
        client.PostFormAsync(
            IdentityHostOpenIddict.TokenPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["redirect_uri"] = AppRedirectUri,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["client_secret"] = secret
            },
            Ct
        );

    static Task<HttpResponseMessage> RefreshAsync(BrowserClient client, string refreshToken, string clientId, string? secret) {
        var form = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken
        };

        if (secret is not null) {
            form["client_secret"] = secret;
        }

        return client.PostFormAsync(IdentityHostOpenIddict.TokenPath, form, Ct);
    }

    async Task GrantOwnerAsync(Guid user) {
        var tuple = RelationTuple.Create(
                AuthObjectRef.Create(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow(),
                Relations.Owner,
                SubjectRef.Create(SubjectTypes.User, user.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow()
            )
            .GetValueOrThrow();

        (await fixture.For(Tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant)).WriteAsync(tuple)).IsSuccess.ShouldBeTrue();
    }

    async Task EnsureTenantRecordAsync() {
        var tenant = fixture.For(Tenant).GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant));

        if ((await tenant.GetAsync()).IsSuccess) {
            return;
        }

        (await tenant.CreateAsync(IdentityHostFixture.Slug, "Grants tests", "local")).IsSuccess.ShouldBeTrue();
    }

    static Uri LinkIn(JsonElement message) {
        var text = message.GetProperty("Text").GetString()!;
        var match = Regex.Match(text, @"https?://\S+/invitation\?\S+", RegexOptions.CultureInvariant);

        match.Success.ShouldBeTrue("no invitation link in the mail: " + text);

        return new(match.Value);
    }
}
