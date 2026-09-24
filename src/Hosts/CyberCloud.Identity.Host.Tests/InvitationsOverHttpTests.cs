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

    [GeneratedRegex(@"https?://\S+/invitation\?\S+")]
    private static partial Regex LinkPattern();
}
