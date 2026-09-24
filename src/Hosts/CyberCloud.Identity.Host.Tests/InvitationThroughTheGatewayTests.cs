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
///     Step 7 of the M1 exit story as a person performs it from <c>cyc</c>: an owner signs in with
///     the device flow, <c>POST</c>s an address to the real gateway, and the real
///     <c>InvitationService</c> and invitation grain behind it mail a link the colleague accepts —
///     after which the colleague's own token is refused the same request, and the owner's
///     <c>PUT …/roleAssignments/{name}</c> gives them Reader on one group. Issue #43, after review.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The join nothing covered.</b> <c>InvitationRoutingTests</c> drives the gateway's
///         stages against a substituted <c>IInvitationManager</c>; <c>CyberCloud.Isolation</c>'s
///         <c>InvitationTests</c> call <c>InvitationService</c> in process with a caller the test
///         built; <see cref="InvitationsOverHttpTests" /> creates the invitation at the grain. Each
///         half was proven and the route a person uses — the one docs/plan/24 § Phase 2 tells a dev
///         run to <c>curl</c> — was not. Here nothing between the socket and the grain is
///         substituted: the token is one this identity host issued through RFC 8628, the gateway is
///         <c>GatewayComposition.BuildAsync</c> told to trust it by the same
///         <c>CyberCloud:Gateway:Identity:Issuer</c> key a deployment sets, and it joins this
///         project's cluster over TCP.
///     </para>
///     <para>
///         ⚠ <b>The owner is a person, not a service principal.</b> The invitation records who sent
///         it, and <c>InvitationService</c> refuses a caller whose subject id is not a GUID; a user
///         token's <c>sub</c> is the user id, which is what this asserts ends up in the record.
///     </para>
/// </remarks>
[Collection(MailpitIdentityHostSuite.Name)]
public sealed class InvitationThroughTheGatewayTests(MailpitIdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Guid Tenant => IdentityHostFixture.Tenant;

    const string Scope = "openid profile offline_access cyc.api";

    /// <summary>The query every non-hub route requires — docs/plan/10 § Versioning.</summary>
    const string Version = "?api-version=2026-08-01";

    static string Address => new InvitationAddress(Tenant).Path + Version;

    [Fact]
    public async Task AnOwnerSignedInFromCycInvitesThroughTheGatewayAndTheColleagueArrivesWithNoRole() {
        // ── The owner: a person, signed in, with Owner on the tenant and a device-flow token. ──────
        var owner = await fixture.SignInFreshPersonAsync(IdentityHostFixture.SignInPageBaseUri);
        using var ownerBrowser = owner.Browser;

        await GrantOwnerAsync(owner.UserId);
        await EnsureTenantRecordAsync();

        var ownerToken = await DeviceTokenAsync(ownerBrowser);

        await using var gateway = await StartGatewayAsync();
        using var http = new HttpClient { BaseAddress = new(gateway.Urls.First()) };

        // ── The pipeline is in front of the route: no token, no invitation. ────────────────────
        var email = $"through-gateway-{Guid.NewGuid():N}@grants.example";

        using (var anonymous = await PostAsync(http, null, email)) {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await anonymous.Content.ReadAsStringAsync(Ct));
        }

        // ── Invite. ─────────────────────────────────────────────────────────────────────────────
        using var invited = await PostAsync(http, ownerToken, email);
        var text = await invited.Content.ReadAsStringAsync(Ct);

        invited.StatusCode.ShouldBe(HttpStatusCode.Created, text);
        text.ShouldNotContain("token", Case.Insensitive, "the link's secret reached the sender");

        var body = JsonDocument.Parse(text).RootElement;
        var properties = body.GetProperty("properties");

        properties.GetProperty("status").GetString().ShouldBe("pending");
        properties.GetProperty("email").GetString().ShouldBe(email);

        var invitationId = Guid.ParseExact(body.GetProperty("name").GetString()!, "N");
        var userId = Guid.ParseExact(properties.GetProperty("userId").GetString()!, "N");

        var record = (await fixture.For(Tenant).GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId)).GetAsync())
            .GetValueOrThrow();

        record.InvitedBy.ShouldBe(owner.UserId, "the invitation records the token's subject as its sender");
        record.UserId.ShouldBe(userId);

        // ── The mail: the tenant's name from its grain, and a link to the page. ─────────────────
        var mail = (await fixture.MessagesToAsync(email)).ShouldHaveSingleItem();

        mail.GetProperty("Subject").GetString().ShouldBe($"You're invited to {IdentityHostFixture.Slug} on Cyber Cloud");

        var link = BrowserClient.Query(LinkIn(mail));

        link["invitation"].ShouldBe(invitationId.ToString("N"));

        // ── The colleague accepts, and is signed in on the identity app. ────────────────────────
        using var colleague = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        using (var accepted = await colleague.PostJsonAsync(
                   "/api/invitations/accept",
                   new {
                       tenant = link["tenant"],
                       invitation = link["invitation"],
                       token = link["token"],
                       displayName = "Through The Gateway",
                       password = "a-password-for-the-gateway-4"
                   },
                   Ct
               )) {
            var answer = await BrowserClient.JsonAsync(accepted, Ct);

            answer.GetProperty("succeeded").GetBoolean().ShouldBeTrue(answer.GetRawText());
        }

        (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(userId)).GetAsync())
            .GetValueOrThrow()
            .Status.ShouldBe(UserStatus.Active);

        // ── The colleague has no role: their own token, the same request, and the canonical 404. ──
        var colleagueToken = await DeviceTokenAsync(colleague);

        BrowserClient.Payload(colleagueToken).GetProperty("sub").GetString().ShouldBe(userId.ToString("N"));

        using var refused = await PostAsync(http, colleagueToken, $"never-{Guid.NewGuid():N}@grants.example");

        refused.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a member with no role reached the invitation manager's check and got past it, or was told the "
            + "tenant exists: "
            + await refused.Content.ReadAsStringAsync(Ct)
        );

        // ── Reader on one group, granted over HTTP by the owner — the other half of step 7. ──────
        // ⚠ The review of #43 found this half asserted only through IRoleAssignmentManager called in
        // process. Here it is the PUT a person sends, through the same gateway, for the invited user.
        var subscription = ScopeId.Subscription(Tenant, Guid.NewGuid());
        var group = ScopeId.Group(Tenant, subscription.SubscriptionId, "invited-" + Guid.NewGuid().ToString("N")[..8]);

        await PutAsync(http, ownerToken, subscription.Path, "{\"displayName\":\"Invited\"}");
        await PutAsync(http, ownerToken, group.Path, "{\"location\":\"local\"}");

        // ⚠ The member doesn't read the group before the grant, and that isn't an omission. A read
        // then is a denial the check grain caches under MinimizeLatency with no TTL (docs/plan/07
        // § Consistency), and the grant below doesn't evict it: measured here, the member read 404
        // on the group after being made Reader. AtLeastAsFresh with the grant's token is the plan's
        // answer, and nothing in the gateway carries one yet.
        var reader = RoleAssignmentId.OnScope(
            group,
            new(Relations.Reader, SubjectTypes.User, userId.ToString("N", CultureInfo.InvariantCulture))
        );

        await PutAsync(http, ownerToken, reader.Path, "{}");

        using (var read = await GetAsync(http, colleagueToken, group.Path)) {
            read.StatusCode.ShouldBe(HttpStatusCode.OK, "Reader on the group did not let the member read it: " + await read.Content.ReadAsStringAsync(Ct));
        }

        using (var write = await SendAsync(http, HttpMethod.Put, colleagueToken, group.Path, "{\"location\":\"local\"}")) {
            write.IsSuccessStatusCode.ShouldBeFalse("Reader wrote the group: " + await write.Content.ReadAsStringAsync(Ct));
        }

        using (var above = await GetAsync(http, colleagueToken, subscription.Path)) {
            above.StatusCode.ShouldBe(HttpStatusCode.NotFound, "Reader on one group reached its subscription");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

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

    static async Task PutAsync(HttpClient http, string token, string path, string body) {
        using var response = await SendAsync(http, HttpMethod.Put, token, path, body);

        ((int)response.StatusCode).ShouldBeInRange(200, 201, $"PUT {path}: " + await response.Content.ReadAsStringAsync(Ct));
    }

    static Task<HttpResponseMessage> GetAsync(HttpClient http, string token, string path) =>
        SendAsync(http, HttpMethod.Get, token, path, null);

    static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string token, string path, string? body) {
        using var request = new HttpRequestMessage(method, path + Version);

        if (body is not null) {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new("Bearer", token);

        return await http.SendAsync(request, Ct);
    }

    static async Task<HttpResponseMessage> PostAsync(HttpClient http, string? token, string email) {
        using var request = new HttpRequestMessage(HttpMethod.Post, Address);

        request.Content = new StringContent(JsonSerializer.Serialize(new { email }), Encoding.UTF8, "application/json");

        if (token is not null) {
            request.Headers.Authorization = new("Bearer", token);
        }

        return await http.SendAsync(request, Ct);
    }

    /// <summary>
    ///     A token for the person <paramref name="browser" /> is signed in as, taken the way
    ///     <c>cyc login --device-code</c> takes it: the device asks, the browser allows, the device
    ///     polls once.
    /// </summary>
    async Task<string> DeviceTokenAsync(BrowserClient browser) {
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
            var decision = await BrowserClient.JsonAsync(decided, Ct);

            decision.GetProperty("status").GetString().ShouldBe("approved", decision.GetRawText());
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

        return tokens.GetProperty("access_token").GetString()!;
    }

    /// <summary>Owner on the tenant, the one tuple — what sign-up writes for the person who made it.</summary>
    async Task GrantOwnerAsync(Guid user) {
        var tuple = RelationTuple.Create(
                AuthObjectRef.Create(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow(),
                Relations.Owner,
                SubjectRef.Create(SubjectTypes.User, user.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow()
            )
            .GetValueOrThrow();

        var written = await fixture.For(Tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant)).WriteAsync(tuple);

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
    }

    /// <summary>
    ///     The tenant's own record, which <c>InvitationService</c> reads the mail's name from — never
    ///     the request. The fixture registers the tenant in the directory only.
    /// </summary>
    async Task EnsureTenantRecordAsync() {
        var tenant = fixture.For(Tenant).GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant));

        if ((await tenant.GetAsync()).IsSuccess) {
            return;
        }

        var created = await tenant.CreateAsync(IdentityHostFixture.Slug, "Grants tests", "local");

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);
    }

    static Uri LinkIn(JsonElement message) {
        var text = message.GetProperty("Text").GetString()!;
        var match = Regex.Match(text, @"https?://\S+/invitation\?\S+", RegexOptions.CultureInvariant);

        match.Success.ShouldBeTrue("no invitation link in the mail: " + text);

        return new(match.Value);
    }
}
