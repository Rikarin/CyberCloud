using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Tests.Infrastructure;
using CyberCloud.Identity.Host.Tokens;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Identity.Host.Tests;

/// <summary>
///     Inviting a colleague, from the mail to the member: the invitation grain mails a one-time,
///     expiring link through the platform's communication service to a real SMTP server, the link is
///     read back out of Mailpit, and the identity host's invitation API accepts it for a new person
///     and for one who already has an account elsewhere. Issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The invitation is created at the grain, which is where the gateway's route ends.</b>
///         Who may invite is the resource manager's <c>assignRole</c> check, asked by
///         <c>InvitationService</c> before the grain is reached, and <c>CyberCloud.Isolation</c>'s
///         <c>InvitationTests</c> drive that half through the real manager and the real engine —
///         with the Reader grant that follows it, and <see cref="InvitationThroughTheGatewayTests" />
///         sends the invite through the real gateway with a real token. What this suite owns is the
///         half after: the mail and its link, and what the link does over HTTP.
///     </para>
///     <para>
///         ⚠ <b>The link is taken from the message Mailpit parsed</b>, not from a value the test
///         built, so a link the template mangled — wrapped, escaped twice, pointed at the wrong page
///         — fails here and nowhere else.
///     </para>
/// </remarks>
[Collection(MailpitIdentityHostSuite.Name)]
public sealed partial class InvitationsOverHttpTests(MailpitIdentityHostFixture fixture) {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Guid Tenant => IdentityHostFixture.Tenant;

    [Fact]
    public async Task ANewPersonAcceptsTheMailedLinkOnceAndIsAMemberWithNoRole() {
        var email = $"colleague-{Guid.NewGuid():N}@grants.example";
        var inviter = fixture.UserId;

        // ── The invitation — and a retry of it, which must not be a second mail. ──────────────
        var (invitationId, secret) = (Guid.NewGuid(), Secret());
        var created = await Invite(invitationId, email, secret, inviter);

        created.Status.ShouldBe(InvitationStatus.Pending);
        (await Invite(invitationId, email, secret, inviter)).UserId.ShouldBe(created.UserId, "a retry is the same invitation");

        var mails = await fixture.MessagesToAsync(email);

        mails.Count.ShouldBe(1, "the retry sent a second mail — the idempotency key is the invitation id");

        var mail = mails[0];

        mail.GetProperty("Subject").GetString().ShouldBe($"You're invited to {IdentityHostFixture.Slug} on Cyber Cloud");

        // ── The link, as Mailpit parsed it out of the bytes on the wire. ─────────────────────────
        var link = LinkIn(mail);

        link.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.SignInPageBaseUri + InvitationPolicy.PagePath);

        var query = BrowserClient.Query(link);

        query["tenant"].ShouldBe(Tenant.ToString("N"));
        query["invitation"].ShouldBe(invitationId.ToString("N"));
        query["token"].ShouldBe(secret, "the secret in the link is the one the grain was given");

        // The invited user exists and cannot sign in yet.
        (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(created.UserId)).GetAsync())
            .GetValueOrThrow()
            .Status.ShouldBe(UserStatus.Invited);

        // ── The page describes it, anonymously. ─────────────────────────────────────────────────
        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);

        var described = await Describe(browser, query);

        described.GetProperty("found").GetBoolean().ShouldBeTrue(described.GetRawText());
        described.GetProperty("email").GetString().ShouldBe(email);
        described.GetProperty("tenantName").GetString().ShouldBe(IdentityHostFixture.Slug);
        described.GetProperty("status").GetString().ShouldBe("pending");

        // ── Accept: a name and a password; the person is signed in and is a member. ──────────────
        using var accepted = await Accept(browser, query, "Colleague", IdentityHostFixture.Password);
        var body = await BrowserClient.JsonAsync(accepted, Ct);

        body.GetProperty("succeeded").GetBoolean().ShouldBeTrue(body.GetRawText());
        body.GetProperty("portalUrl").GetString().ShouldBe(FirstPartyClients.DevelopmentPortalPostLogoutRedirectUri);
        browser.Cookies.ShouldContainKey(IdentityHostAuthentication.CookieName);

        var profile = (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(created.UserId)).GetAsync())
            .GetValueOrThrow();

        profile.Status.ShouldBe(UserStatus.Active);
        profile.DisplayName.ShouldBe("Colleague");
        profile.EnrolledCredentials.ShouldContain(CredentialKind.Password);

        // ⚠ A member with no role: nothing on the tenant, not even read. Reader on a group is a
        // separate role assignment — CyberCloud.Isolation's InvitationTests.
        var check = await fixture.For(Tenant)
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(ObjectTypes.Tenant, Tenant.ToString("N")))
            .CheckAsync(
                Permissions.Read,
                new SubjectRef { Type = SubjectTypes.User, Id = created.UserId.ToString("N") },
                null
            );

        check.GetValueOrThrow().Allowed.ShouldBeFalse("accepting an invitation granted a role");

        // ── The link is one-time: a second use is refused, and says why. ────────────────────────
        using var again = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        using var reused = await Accept(again, query, "Somebody Else", "another-password-entirely-1");
        var refused = await BrowserClient.JsonAsync(reused, Ct);

        refused.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
        refused.GetProperty("message").GetString()!.ShouldContain("already been used");
        again.Cookies.ShouldNotContainKey(IdentityHostAuthentication.CookieName);
        (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(created.UserId)).GetAsync())
            .GetValueOrThrow()
            .DisplayName.ShouldBe("Colleague", "the second use changed the member");

        (await Describe(again, query)).GetProperty("status").GetString().ShouldBe("accepted");

        // And a member cannot be invited again: they are in the organisation already.
        var twice = await fixture.For(Tenant)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(Guid.NewGuid()))
            .CreateAsync(Request(email, inviter), Secret());

        twice.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task APersonWithAnAccountInAnotherOrganisationAcceptsIntoThisOne() {
        var email = $"elsewhere-{Guid.NewGuid():N}@grants.example";

        // ⚠ One user per tenant (docs/plan/11 § Sign-up): the same address already has a user in the
        // other organisation, and accepting makes them a second one here, leaving that one alone.
        var elsewhere = await fixture.CreatePersonAsync(email, IdentityHostFixture.OtherTenant);

        var invitationId = Guid.NewGuid();
        var secret = Secret();
        var created = await Invite(invitationId, email, secret, fixture.UserId);

        created.UserId.ShouldNotBe(elsewhere);

        var query = BrowserClient.Query(LinkIn((await fixture.MessagesToAsync(email)).Single()));

        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);
        using var accepted = await Accept(browser, query, "Elsewhere Person", "a-password-for-this-org-2");

        (await BrowserClient.JsonAsync(accepted, Ct)).GetProperty("succeeded").GetBoolean().ShouldBeTrue();

        (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(created.UserId)).GetAsync())
            .GetValueOrThrow()
            .Status.ShouldBe(UserStatus.Active);

        var other = (await fixture.For(IdentityHostFixture.OtherTenant)
                .GetGrain<IUserGrain>(GrainKeys.User(elsewhere))
                .GetAsync())
            .GetValueOrThrow();

        other.Status.ShouldBe(UserStatus.Active);
        other.DisplayName.ShouldBe(IdentityHostFixture.DisplayName, "the account in the other organisation was touched");
    }

    /// <summary>
    ///     The invited path's <i>"signs in"</i>: a person signed into their own organisation opens
    ///     the link, joins with that account instead of a new name and password, and from then on
    ///     signs into this organisation through it.
    /// </summary>
    /// <remarks>
    ///     ⚠ The review of #43's second round found this path missing — the page only asked for a
    ///     second name and password — and the plan's sentence asked for it. Every refusal here is a
    ///     way the link, the cookie or the address could have been mismatched.
    /// </remarks>
    [Fact]
    public async Task APersonSignedInElsewhereJoinsWithThatAccountAndSignsInHereThroughIt() {
        var email = $"home-{Guid.NewGuid():N}@grants.example";
        var home = await fixture.CreatePersonAsync(email, IdentityHostFixture.OtherTenant);

        using var browser = await SignInElsewhereAsync(email);

        var invitationId = Guid.NewGuid();
        var secret = Secret();
        var created = await Invite(invitationId, email, secret, fixture.UserId);
        var query = Link(Tenant, invitationId, secret);

        // ── The page offers the account the browser is signed into, and only to that browser. ──
        var described = await Describe(browser, query);

        described.GetProperty("account").GetString().ShouldBe(email);
        described.GetProperty("canJoinWithAccount").GetBoolean().ShouldBeTrue(described.GetRawText());

        using (var anonymous = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri)) {
            var offered = await Describe(anonymous, query);

            offered.GetProperty("account").GetString().ShouldBeEmpty();
            offered.GetProperty("canJoinWithAccount").GetBoolean().ShouldBeFalse();
        }

        // ── From another origin the cookie counts for nothing, and nothing is spent. ───────────
        var page = browser.Origin;
        browser.Origin = "https://elsewhere.example";

        using (var foreign = await JoinWithAccount(browser, query)) {
            var answer = await BrowserClient.JsonAsync(foreign, Ct);

            answer.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
            answer.GetProperty("message").GetString().ShouldBe(InvitationApi.SignInFirst);
        }

        browser.Origin = page;

        // ── Somebody signed in with another address is not offered it, and is refused. ────────
        var strangerEmail = $"stranger-{Guid.NewGuid():N}@grants.example";

        await fixture.CreatePersonAsync(strangerEmail, IdentityHostFixture.OtherTenant);

        using (var stranger = await SignInElsewhereAsync(strangerEmail)) {
            (await Describe(stranger, query)).GetProperty("canJoinWithAccount").GetBoolean().ShouldBeFalse();

            using var mismatched = await JoinWithAccount(stranger, query);
            var answer = await BrowserClient.JsonAsync(mismatched, Ct);

            answer.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
            answer.GetProperty("message").GetString()!.ShouldContain("this invitation is for");
        }

        (await Describe(browser, query)).GetProperty("status").GetString().ShouldBe("pending", "a refusal spent the link");

        // ── Join: no name, no password, and the home cookie stays. ─────────────────────────────
        var homeCookie = browser.Cookies[IdentityHostAuthentication.CookieName];

        using (var joined = await JoinWithAccount(browser, query)) {
            var answer = await BrowserClient.JsonAsync(joined, Ct);

            answer.GetProperty("succeeded").GetBoolean().ShouldBeTrue(answer.GetRawText());
        }

        browser.Cookies[IdentityHostAuthentication.CookieName].ShouldBe(homeCookie, "joining replaced the home account's cookie");

        var member = (await fixture.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(created.UserId)).GetAsync())
            .GetValueOrThrow();

        member.Status.ShouldBe(UserStatus.Active);
        member.DisplayName.ShouldBe(IdentityHostFixture.DisplayName, "the member takes the home account's name");
        member.HomeAccount.ShouldBe(new HomeAccount { TenantId = IdentityHostFixture.OtherTenant, UserId = home });
        member.EnrolledCredentials.ShouldBeEmpty("joining with an account set a credential nobody chose");

        // ── Signing in here is the home account's cookie at /authorize, for this tenant. ────────
        var (verifier, challenge) = BrowserClient.Pkce();
        var token = await TokenAsync(browser, challenge, verifier);
        var claims = BrowserClient.Payload(token);

        claims.GetProperty("sub").GetString().ShouldBe(created.UserId.ToString("N"));
        claims.GetProperty("tid").GetString().ShouldBe(Tenant.ToString("N"));
        claims.GetProperty("amr").EnumerateArray().Select(static x => x.GetString()).ShouldBe(["pwd", "otp"]);

        // The member has no password here: the home one is not a credential of this organisation.
        using (var direct = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri)) {
            using var password = await direct.PostJsonAsync(
                "/api/signin/password",
                new { email, password = IdentityHostFixture.Password, returnUrl = "/", tenant = IdentityHostFixture.Slug },
                Ct
            );

            (await BrowserClient.JsonAsync(password, Ct)).GetProperty("succeeded").GetBoolean().ShouldBeFalse();
        }

        // ── The home tenant's suspension ends the next sign-in here. ───────────────────────────
        (await fixture.For(IdentityHostFixture.OtherTenant).GetGrain<IUserGrain>(GrainKeys.User(home)).SetStatusAsync(UserStatus.Suspended))
            .IsSuccess.ShouldBeTrue();

        var (_, again) = BrowserClient.Pkce();

        using var refused = await browser.GetAsync(AuthorizePath(again, "joined-2"), Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        BrowserClient.Location(refused).AbsolutePath.ShouldBe(AuthorizeApi.SignInPagePath, "a suspended home account still opened a session here");
    }

    [Fact]
    public async Task AnExpiredLinkIsRefusedAndInvitingAgainReplacesIt() {
        var email = $"late-{Guid.NewGuid():N}@grants.example";
        var first = await Invite(Guid.NewGuid(), email, Secret(), fixture.UserId);
        var stale = BrowserClient.Query(LinkIn((await fixture.MessagesToAsync(email)).Single()));

        fixture.SiloClock.Advance(InvitationPolicy.Lifetime + TimeSpan.FromMinutes(1));

        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);

        (await Describe(browser, stale)).GetProperty("status").GetString().ShouldBe("expired");

        using (var refused = await Accept(browser, stale, "Late", "late-password-for-this-org-3")) {
            (await BrowserClient.JsonAsync(refused, Ct)).GetProperty("message").GetString()!.ShouldContain("expired");
        }

        // ⚠ Re-inviting lands on the same invited user — an expired link is replaced, not a second user.
        var second = await Invite(Guid.NewGuid(), email, Secret(), fixture.UserId);

        second.UserId.ShouldBe(first.UserId);

        var fresh = (await fixture.MessagesToAsync(email, 2))
            .Select(LinkIn)
            .Select(BrowserClient.Query)
            .Single(x => x["token"] != stale["token"]);

        using var accepted = await Accept(browser, fresh, "Late", "late-password-for-this-org-3");

        (await BrowserClient.JsonAsync(accepted, Ct)).GetProperty("succeeded").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AWrongOrMadeUpLinkIsOneSentence() {
        var email = $"guess-{Guid.NewGuid():N}@grants.example";
        var invitationId = Guid.NewGuid();

        await Invite(invitationId, email, Secret(), fixture.UserId);

        using var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);

        foreach (var query in new[] {
                     Link(Tenant, invitationId, Secret()),
                     Link(Guid.NewGuid(), invitationId, Secret()),
                     Link(Tenant, Guid.NewGuid(), Secret()),
                     new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = "nope", ["invitation"] = "nope", ["token"] = "" }
                 }) {
            var described = await Describe(browser, query);

            described.GetProperty("found").GetBoolean().ShouldBeFalse();
            described.GetProperty("message").GetString().ShouldBe(InvitationApi.NotFound);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    async Task<Invitation> Invite(Guid invitationId, string email, string secret, Guid inviter) {
        var created = await fixture.For(Tenant)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitationId))
            .CreateAsync(Request(email, inviter), secret);

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        return created.GetValueOrThrow();
    }

    static InvitationRequest Request(string email, Guid inviter) =>
        new() { Email = email, InvitedBy = inviter, TenantName = IdentityHostFixture.Slug };

    static string Secret() => BrowserClient.Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>The one link in a message's text — alone on its line, as the template puts it.</summary>
    static Uri LinkIn(JsonElement message) {
        var text = message.GetProperty("Text").GetString()!;
        var match = LinkPattern().Match(text);

        match.Success.ShouldBeTrue("no invitation link in the mail: " + text);

        return new(match.Value);
    }

    static Dictionary<string, string> Link(Guid tenant, Guid invitation, string token) =>
        new(StringComparer.Ordinal) {
            ["tenant"] = tenant.ToString("N"), ["invitation"] = invitation.ToString("N"), ["token"] = token
        };

    static async Task<JsonElement> Describe(BrowserClient browser, Dictionary<string, string> query) {
        using var response = await browser.PostJsonAsync(
            "/api/invitations/describe",
            new { tenant = query["tenant"], invitation = query["invitation"], token = query["token"] },
            Ct
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await BrowserClient.JsonAsync(response, Ct);
    }

    static Task<HttpResponseMessage> Accept(
        BrowserClient browser,
        Dictionary<string, string> query,
        string displayName,
        string password
    ) =>
        browser.PostJsonAsync(
            "/api/invitations/accept",
            new {
                tenant = query["tenant"],
                invitation = query["invitation"],
                token = query["token"],
                displayName,
                password
            },
            Ct
        );

    static Task<HttpResponseMessage> JoinWithAccount(BrowserClient browser, Dictionary<string, string> query) =>
        browser.PostJsonAsync(
            "/api/invitations/accept",
            new { tenant = query["tenant"], invitation = query["invitation"], token = query["token"], withSignedInAccount = true },
            Ct
        );

    /// <summary>
    ///     A tab on the pages' origin, signed into the other organisation as <paramref name="email" />
    ///     with the password and the delivered code.
    /// </summary>
    async Task<BrowserClient> SignInElsewhereAsync(string email) {
        var browser = new BrowserClient(fixture.BaseAddress, IdentityHostFixture.SignInPageBaseUri);

        using var password = await browser.PostJsonAsync(
            "/api/signin/password",
            new { email, password = IdentityHostFixture.Password, returnUrl = "/", tenant = IdentityHostFixture.OtherSlug },
            Ct
        );

        (await BrowserClient.JsonAsync(password, Ct)).GetProperty("succeeded").GetBoolean().ShouldBeTrue();

        using var send = await browser.PostJsonAsync("/api/signin/otp/send", new { returnUrl = "/" }, Ct);
        using var otp = await browser.PostJsonAsync("/api/signin/otp", new { code = fixture.Otp.LastCode, returnUrl = "/" }, Ct);

        (await BrowserClient.JsonAsync(otp, Ct)).GetProperty("secondFactorRequired").GetBoolean().ShouldBeFalse();

        return browser;
    }

    /// <summary>The portal's code flow for this tenant, from the tab's cookie to an access token.</summary>
    static async Task<string> TokenAsync(BrowserClient browser, string challenge, string verifier) {
        using var authorized = await browser.GetAsync(AuthorizePath(challenge, "joined-1"), Ct);

        authorized.StatusCode.ShouldBe(HttpStatusCode.Redirect, await authorized.Content.ReadAsStringAsync(Ct));

        var location = BrowserClient.Location(authorized);

        location.GetLeftPart(UriPartial.Path).ShouldBe(IdentityHostFixture.PortalRedirectUri, "no code: " + location);

        // The portal's own origin, as the portal's callback page posts the exchange from it.
        var page = browser.Origin;
        browser.Origin = IdentityHostFixture.PortalOrigin;

        using var exchanged = await browser.PostFormAsync(
            IdentityHostOpenIddict.TokenPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                ["grant_type"] = "authorization_code",
                ["client_id"] = FirstPartyClients.Portal,
                ["redirect_uri"] = IdentityHostFixture.PortalRedirectUri,
                ["code"] = BrowserClient.Query(location)["code"],
                ["code_verifier"] = verifier
            },
            Ct
        );
        var tokens = await BrowserClient.JsonAsync(exchanged, Ct);

        browser.Origin = page;
        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK, tokens.GetRawText());

        return tokens.GetProperty("access_token").GetString()!;
    }

    static string AuthorizePath(string challenge, string state) =>
        IdentityHostOpenIddict.AuthorizationPath
        + "?response_type=code&client_id="
        + FirstPartyClients.Portal
        + "&redirect_uri="
        + Uri.EscapeDataString(IdentityHostFixture.PortalRedirectUri)
        + "&scope="
        + Uri.EscapeDataString("openid profile offline_access cyc.api")
        + "&state="
        + state
        + "&code_challenge="
        + challenge
        + "&code_challenge_method=S256&nonce=n-"
        + state
        + "&tenant="
        + IdentityHostFixture.Slug;

    [GeneratedRegex(@"https?://\S+/invitation\?\S+")]
    private static partial Regex LinkPattern();
}
