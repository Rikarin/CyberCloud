using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Seams;
using CyberCloud.ResourceManager;
using CyberCloud.Tenancy.Contracts;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     The identity administration API below HTTP — the real <c>IdentityAdministrationService</c>
///     and its checks over the real ReBAC engine, the gateway's own <c>GrainIdentityDirectory</c>
///     over the real identity grains. Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Who may do each thing is this suite's question.</b> <c>IdentityRoutingTests</c>
///         proves which call a verb reaches against a substitute, and
///         <c>IdentityAdministrationThroughTheGatewayTests</c> runs a few of these over the wire with
///         a real token; the owner rule, the self-removal refusal, the tuple sweep on removal, the
///         secret's life and the "only your own sessions" rule are asserted here, where the
///         engine that decides them is real.
///     </para>
///     <para>
///         ⚠ <b>A tenant of this class's own</b>, prefix <c>cdcdcdcd</c>: the collection shares one
///         cluster, and every list asserted here is the whole of one tenant's directory.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class IdentityAdministrationTests(IsolationCluster cluster) {
    static Guid Tenant { get; } = Guid.Parse("cdcdcdcd-0000-4000-8000-000000000041");

    static readonly Guid OwnerId = Guid.Parse("cdcdcdcd-0000-4000-8000-00000000a0a0");

    static readonly string Owner = OwnerId.ToString("N", CultureInfo.InvariantCulture);

    static readonly SemaphoreSlim Seeding = new(1, 1);
    static bool seeded;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IdentityAdministrationRequest As(string subject, Guid? tenant = null, string subjectType = SubjectTypes.User) =>
        new() {
            TenantId = Tenant,
            Caller = IsolationCluster.Caller(tenant ?? Tenant, subject) with { SubjectType = subjectType }
        };

    // ── The check ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyAnOwnerMayAdministerTheDirectoryAndEveryoneElseIsToldSoOrToldNothing() {
        await SeedAsync();

        var reader = await cluster.CreateUserAsync(Tenant, Guid.NewGuid());

        (await cluster.Roles.AssignAsync(
            new() {
                Path = RoleAssignmentId.OnScope(ScopeId.Tenant(Tenant), new(Relations.Reader, SubjectTypes.User, reader)).Path,
                Body = "{}",
                Caller = IsolationCluster.Caller(Tenant, Owner)
            },
            Ct
        )).IsSuccess.ShouldBeTrue();

        // Every directory call: the owner gets an answer, a reader a 403, a stranger and an owner of
        // another tenant the canonical 404.
        //
        // ⚠ Each refusal is asserted non-null before its code is read. `(await call(r))?.Code.ShouldBe(…)`
        // reads naturally and asserts nothing when the call succeeds — the null-conditional skips the
        // assertion — which is how a sabotage that let a reader through (Read in place of AssignRole)
        // first went green here. #43's review found the same shape in InvitationTests.
        foreach (var (name, call) in DirectoryCalls()) {
            (await call(As(Owner))).ShouldBeNull($"the owner was refused {name}");
            (await call(As(reader))).ShouldNotBeNull($"a reader was allowed {name}")
                .Code.ShouldBe(ErrorCode.AuthorizationFailed, $"a reader was not refused {name} with 403");
            (await call(As(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)))).ShouldNotBeNull($"a stranger was allowed {name}")
                .Code.ShouldBe(ErrorCode.ResourceNotFound, $"a stranger was not told 404 by {name}");
            (await call(As(Owner, IsolationCluster.Victim))).ShouldNotBeNull($"another tenant's caller was allowed {name}")
                .Code.ShouldBe(ErrorCode.ResourceNotFound, $"another tenant's caller was not told 404 by {name}");
        }
    }

    // ── Members ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheMemberListIsEveryUserTheDirectoryCreatedInvitedOnesIncluded() {
        await SeedAsync();

        var joined = await cluster.CreateUserAsync(Tenant, Guid.NewGuid());
        var (invited, _) = await InviteAsync($"listed-{Guid.NewGuid():N}@isolation.test");

        var members = (await cluster.Identity.ListMembersAsync(As(Owner), Ct)).GetValueOrThrow();

        members.ShouldContain(x => x.UserId == OwnerId && x.Status == "active");
        members.ShouldContain(x => x.UserId.ToString("N", CultureInfo.InvariantCulture) == joined);
        members.ShouldContain(x => x.UserId == invited.UserId && x.Status == "invited" && x.DisplayName.Length == 0);

        // Nothing from another tenant: a user created there is in its own directory.
        var elsewhere = Guid.NewGuid();
        await cluster.CreateUserAsync(IsolationCluster.Victim, elsewhere);

        (await cluster.Identity.ListMembersAsync(As(Owner), Ct)).GetValueOrThrow()
            .ShouldNotContain(x => x.UserId == elsewhere, "another tenant's user is listed here");
    }

    [Fact]
    public async Task RemovingAMemberEndsTheirSessionsAndDeletesEveryTupleThatNamesThem() {
        await SeedAsync();

        var userId = Guid.NewGuid();
        var user = await cluster.CreateUserAsync(Tenant, userId);
        var group = await cluster.CreateGroupAsync(Tenant, Guid.NewGuid());

        // Reader on the tenant, and a member of a group: two tuples naming user:{id} directly.
        (await cluster.Roles.AssignAsync(
            new() {
                Path = RoleAssignmentId.OnScope(ScopeId.Tenant(Tenant), new(Relations.Reader, SubjectTypes.User, user)).Path,
                Body = "{}",
                Caller = IsolationCluster.Caller(Tenant, Owner)
            },
            Ct
        )).IsSuccess.ShouldBeTrue();

        (await cluster.For(Tenant)
            .GetGrain<IGroupGrain>(GrainKeys.Group(Guid.ParseExact(group, "N")))
            .AddMemberAsync(SubjectRef.Of(SubjectTypes.User, user))).IsSuccess.ShouldBeTrue();

        var session = await OpenSessionAsync(userId);

        (await AllowedAsync(Permissions.Read, user)).ShouldBeTrue("the seed did not grant Reader");

        // ── Nobody removes themselves. ────────────────────────────────────────────────────────
        var self = await cluster.Identity.RemoveMemberAsync(As(Owner), OwnerId, Ct);

        self.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await cluster.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(OwnerId)).GetAsync())
            .GetValueOrThrow().Status.ShouldBe(UserStatus.Active, "the refused self-removal deprovisioned the owner");

        // ── The member. ───────────────────────────────────────────────────────────────────────
        var removed = await cluster.Identity.RemoveMemberAsync(As(Owner), userId, Ct);

        removed.IsSuccess.ShouldBeTrue(removed.Error?.Message);
        removed.GetValueOrThrow().Status.ShouldBe("deprovisioned");

        (await cluster.For(Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(session)).IsLiveAsync())
            .GetValueOrThrow().ShouldBeFalse("removing a member left their session live");

        (await AllowedAsync(Permissions.Read, user)).ShouldBeFalse("the removed member can still read the tenant");

        (await cluster.For(Tenant)
            .GetGrain<ISubjectRelationsGrain>(GrainKeys.SubjectRelations(SubjectTypes.User, user))
            .ListAsync()).GetValueOrThrow().ShouldBeEmpty("a tuple still names the removed member");

        (await cluster.For(Tenant)
            .GetGrain<IGroupGrain>(GrainKeys.Group(Guid.ParseExact(group, "N")))
            .IsMemberAsync(SubjectRef.Of(SubjectTypes.User, user), Consistency.FullyConsistent))
            .GetValueOrThrow().ShouldBeFalse("the removed member is still in the group");

        // A user this tenant never had is 404, not a deprovision of an empty grain.
        (await cluster.Identity.RemoveMemberAsync(As(Owner), Guid.NewGuid(), Ct)).Error!.Code
            .ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── Invitations ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AResendMailsANewLinkAndTheOldOneOpensNothing() {
        await SeedAsync();

        var (invitation, firstSecret) = await InviteAsync($"resent-{Guid.NewGuid():N}@isolation.test");

        var resent = await cluster.Identity.ResendInvitationAsync(As(Owner), invitation.InvitationId, Ct);

        resent.IsSuccess.ShouldBeTrue(resent.Error?.Message);
        resent.GetValueOrThrow().Sendings.ShouldBe(2);
        resent.GetValueOrThrow().Status.ShouldBe("pending");

        var mails = IsolationCluster.InvitationMail.Deliveries.Where(x => x.InvitationId == invitation.InvitationId).ToList();

        mails.Count.ShouldBe(2, "the resend mailed nothing");
        mails[1].Secret.ShouldNotBe(firstSecret, "the resend mailed the old link");
        mails[1].Sending.ShouldBe(2);

        // ⚠ The second mail has its own idempotency key; with the first one's, the communication
        // service would answer the resend with the first mail's receipt and send nothing.
        CommunicationInvitationDelivery.IdempotencyKeyFor(mails[1])
            .ShouldNotBe(CommunicationInvitationDelivery.IdempotencyKeyFor(mails[0]));

        var grain = cluster.For(Tenant).GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitation.InvitationId));

        (await grain.DescribeAsync(firstSecret)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "the old link still opens the invitation");
        (await grain.AcceptAsync(mails[1].Secret, "Resent Colleague", "a-password-for-the-resend-1")).IsSuccess.ShouldBeTrue();

        // Accepted: nobody is waiting for a link, so a resend is refused.
        (await cluster.Identity.ResendInvitationAsync(As(Owner), invitation.InvitationId, Ct)).Error!.Code
            .ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task ARevokedInvitationOpensNothingAndReinvitingReusesTheInvitedUser() {
        await SeedAsync();

        var email = $"revoked-{Guid.NewGuid():N}@isolation.test";
        var (invitation, secret) = await InviteAsync(email);

        var revoked = await cluster.Identity.RevokeInvitationAsync(As(Owner), invitation.InvitationId, Ct);

        revoked.GetValueOrThrow().Status.ShouldBe("revoked");

        // A repeat is the same answer, not an error.
        (await cluster.Identity.RevokeInvitationAsync(As(Owner), invitation.InvitationId, Ct)).GetValueOrThrow()
            .Status.ShouldBe("revoked");

        var grain = cluster.For(Tenant).GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitation.InvitationId));

        (await grain.DescribeAsync(secret)).GetValueOrThrow().Status.ShouldBe(InvitationStatus.Revoked);
        (await grain.AcceptAsync(secret, "Too Late", "a-password-too-late-1")).Error!.Code.ShouldBe(ErrorCode.Conflict);

        (await cluster.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(invitation.UserId)).GetAsync())
            .GetValueOrThrow().Status.ShouldBe(UserStatus.Invited, "revoking the invitation touched the user");

        // Invited again: the same user, a new invitation, and the list shows both.
        var (again, _) = await InviteAsync(email);

        again.UserId.ShouldBe(invitation.UserId);

        var listed = (await cluster.Identity.ListInvitationsAsync(As(Owner), Ct)).GetValueOrThrow();

        listed.ShouldContain(x => x.InvitationId == invitation.InvitationId && x.Status == "revoked");
        listed.ShouldContain(x => x.InvitationId == again.InvitationId && x.Status == "pending" && x.InvitedBy == OwnerId);

        // A revoked invitation can't be resent — send a new one instead.
        (await cluster.Identity.ResendInvitationAsync(As(Owner), invitation.InvitationId, Ct)).Error!.Code
            .ShouldBe(ErrorCode.Conflict);
    }

    // ── Applications ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AConfidentialClientsSecretIsShownOnceVerifiesRotatesAndDiesWithTheRegistration() {
        await SeedAsync();

        var created = await cluster.Identity.CreateApplicationAsync(
            As(Owner),
            new() {
                DisplayName = "Acme server",
                RedirectUris = ["https://acme.example/server/cb"],
                Scopes = ["openid", "profile", "cyc.api"],
                IsPublicClient = false
            },
            Ct
        );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var registered = created.GetValueOrThrow();
        var app = cluster.For(Tenant).GetGrain<IApplicationGrain>(GrainKeys.Application(registered.Application.ApplicationId));

        registered.ClientSecret.Length.ShouldBeGreaterThanOrEqualTo(43, "256 bits, base64url");
        registered.Application.ClientSecretIssuedAt.ShouldNotBeNull();
        (await app.VerifyClientSecretAsync(registered.ClientSecret)).GetValueOrThrow().ShouldBeTrue();
        (await app.VerifyClientSecretAsync(registered.ClientSecret + "x")).GetValueOrThrow().ShouldBeFalse();

        // The client id is minted, and the index resolves it to this registration.
        (await cluster.For(Tenant)
            .GetGrain<IClientIndexGrain>(GrainKeys.ClientIndex(Tenant, registered.Application.ClientId))
            .GetAsync()).GetValueOrThrow().BoundTo.ShouldBe(registered.Application.ApplicationId);

        // Listed, and read, with no way to get the secret back.
        (await cluster.Identity.ListApplicationsAsync(As(Owner), Ct)).GetValueOrThrow()
            .ShouldContain(x => x.ApplicationId == registered.Application.ApplicationId);

        // ── Rotate: the old secret stops working in the same turn. ──────────────────────────────
        var rotated = (await cluster.Identity.RotateApplicationSecretAsync(As(Owner), registered.Application.ApplicationId, Ct))
            .GetValueOrThrow();

        rotated.ClientSecret.ShouldNotBe(registered.ClientSecret);
        (await app.VerifyClientSecretAsync(registered.ClientSecret)).GetValueOrThrow().ShouldBeFalse("the old secret survived a rotation");
        (await app.VerifyClientSecretAsync(rotated.ClientSecret)).GetValueOrThrow().ShouldBeTrue();

        // ── Delete: gone from the list, and the client id is free. ──────────────────────────────
        (await cluster.Identity.DeleteApplicationAsync(As(Owner), registered.Application.ApplicationId, Ct)).IsSuccess.ShouldBeTrue();

        (await cluster.Identity.ListApplicationsAsync(As(Owner), Ct)).GetValueOrThrow()
            .ShouldNotContain(x => x.ApplicationId == registered.Application.ApplicationId);
        (await cluster.Identity.GetApplicationAsync(As(Owner), registered.Application.ApplicationId, Ct)).Error!.Code
            .ShouldBe(ErrorCode.ResourceNotFound);
        (await cluster.For(Tenant)
            .GetGrain<IClientIndexGrain>(GrainKeys.ClientIndex(Tenant, registered.Application.ClientId))
            .GetAsync()).GetValueOrThrow().State.ShouldBe(IndexEntryState.Free, "the deleted registration still holds its client id");
    }

    [Fact]
    public async Task APublicClientGetsNoSecretAndARegistrationIsRefusedWhatTheHostWouldRefuse() {
        await SeedAsync();

        var spa = (await cluster.Identity.CreateApplicationAsync(
            As(Owner),
            new() { DisplayName = "Acme SPA", RedirectUris = ["https://acme.example/cb"], Scopes = ["openid"], IsPublicClient = true },
            Ct
        )).GetValueOrThrow();

        spa.ClientSecret.ShouldBeEmpty();
        spa.Application.ClientSecretIssuedAt.ShouldBeNull();
        (await cluster.Identity.RotateApplicationSecretAsync(As(Owner), spa.Application.ApplicationId, Ct)).Error!.Code
            .ShouldBe(ErrorCode.InvalidRequestBody, "a public client was issued a secret");

        ApplicationDraft[] refused = [
            new() { DisplayName = "Scopes", RedirectUris = ["https://a.example/cb"], Scopes = ["email"], IsPublicClient = true },
            new() { DisplayName = "Fragment", RedirectUris = ["https://a.example/cb#x"], Scopes = ["openid"], IsPublicClient = true },
            new() { DisplayName = "Relative", RedirectUris = ["/cb"], Scopes = ["openid"], IsPublicClient = true },
            new() { DisplayName = "", RedirectUris = ["https://a.example/cb"], Scopes = ["openid"], IsPublicClient = true },
            new() { DisplayName = "No redirect", RedirectUris = [], Scopes = ["openid"], IsPublicClient = true }
        ];

        foreach (var draft in refused) {
            (await cluster.Identity.CreateApplicationAsync(As(Owner), draft, Ct)).Error!.Code
                .ShouldBe(ErrorCode.InvalidRequestBody, $"'{draft.DisplayName}' was registered");
        }
    }

    // ── Sessions ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APersonListsAndEndsTheirOwnSessionsAndNobodyElses() {
        await SeedAsync();

        var aliceId = Guid.NewGuid();
        var alice = await cluster.CreateUserAsync(Tenant, aliceId);
        var bobId = Guid.NewGuid();
        await cluster.CreateUserAsync(Tenant, bobId);

        var laptop = await OpenSessionAsync(aliceId, "Firefox on Windows");
        var phone = await OpenSessionAsync(aliceId, "Safari on iOS");
        var bobs = await OpenSessionAsync(bobId, "Chrome on Linux");

        var mine = await cluster.Identity.ListOwnSessionsAsync(As(alice) with { CurrentSessionId = laptop }, Ct);

        mine.IsSuccess.ShouldBeTrue(mine.Error?.Message);
        mine.GetValueOrThrow().Select(x => x.SessionId).ShouldBe([phone, laptop], ignoreOrder: true);
        mine.GetValueOrThrow().Single(x => x.SessionId == laptop).IsCurrent.ShouldBeTrue();
        mine.GetValueOrThrow().Single(x => x.SessionId == phone).IsCurrent.ShouldBeFalse();
        mine.GetValueOrThrow().Single(x => x.SessionId == phone).DeviceLabel.ShouldBe("Safari on iOS");

        // Bob's session is not Alice's to end — and she is told it doesn't exist, not that it's his.
        (await cluster.Identity.RevokeOwnSessionAsync(As(alice), bobs, Ct)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await cluster.For(Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(bobs)).IsLiveAsync()).GetValueOrThrow()
            .ShouldBeTrue("Alice ended Bob's session");

        // Her own: ended, and gone from the list.
        (await cluster.Identity.RevokeOwnSessionAsync(As(alice), phone, Ct)).IsSuccess.ShouldBeTrue();
        (await cluster.For(Tenant).GetGrain<ISessionGrain>(GrainKeys.Session(phone)).IsLiveAsync()).GetValueOrThrow()
            .ShouldBeFalse();
        (await cluster.Identity.ListOwnSessionsAsync(As(alice), Ct)).GetValueOrThrow().Select(x => x.SessionId)
            .ShouldBe([laptop]);

        // A service principal holds no sessions to list.
        (await cluster.Identity.ListOwnSessionsAsync(
            As(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), subjectType: SubjectTypes.ServicePrincipal),
            Ct
        )).Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every directory call, as a probe that answers the refusal or <c>null</c>.</summary>
    IEnumerable<(string Name, Func<IdentityAdministrationRequest, Task<Error?>> Call)> DirectoryCalls() {
        var anyone = Guid.NewGuid();

        yield return ("ListMembersAsync", async r => (await cluster.Identity.ListMembersAsync(r, Ct)).Error);
        yield return ("ListInvitationsAsync", async r => (await cluster.Identity.ListInvitationsAsync(r, Ct)).Error);
        yield return ("ListApplicationsAsync", async r => (await cluster.Identity.ListApplicationsAsync(r, Ct)).Error);

        // Past the check, these reach a directory object that doesn't exist, so the owner's answer
        // is the grain's 404, which names the id — the probe maps exactly that to "not refused".
        yield return ("RemoveMemberAsync", async r => Refusal((await cluster.Identity.RemoveMemberAsync(r, anyone, Ct)).Error, anyone));
        yield return ("RevokeInvitationAsync", async r => Refusal((await cluster.Identity.RevokeInvitationAsync(r, anyone, Ct)).Error, anyone));
        yield return ("DeleteApplicationAsync", async r => Refusal((await cluster.Identity.DeleteApplicationAsync(r, anyone, Ct)).Error, anyone));
    }

    /// <summary>
    ///     A refusal the check made, or <c>null</c> for an answer from behind it: a 404 from the
    ///     object's own grain, which names <paramref name="id" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ By the id and not by the wording. The first cut matched "does not exist", which is
    ///     also how the scope check words its canonical 404 — so a stranger's refusal read as "got
    ///     past the check", and the strict assertion above failed on a correct service. The check's
    ///     sentence names the tenant's path, never the probed object's id.
    /// </remarks>
    static Error? Refusal(Error? error, Guid id) =>
        error is not null
        && error.Code == ErrorCode.ResourceNotFound
        && error.Message.Contains(id.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? null
            : error;

    async Task<(InvitationSnapshot Invitation, string Secret)> InviteAsync(string email) {
        var invited = await cluster.Invitations.InviteAsync(
            new() { TenantId = Tenant, Email = email, Caller = IsolationCluster.Caller(Tenant, Owner) },
            Ct
        );

        invited.IsSuccess.ShouldBeTrue(invited.Error?.Message);

        var invitation = invited.GetValueOrThrow();

        return (invitation, IsolationCluster.InvitationMail.Deliveries.Last(x => x.InvitationId == invitation.InvitationId).Secret);
    }

    async Task<Guid> OpenSessionAsync(Guid userId, string device = "Isolation device") {
        var sessionId = Guid.NewGuid();

        var opened = await cluster.For(Tenant)
            .GetGrain<ISessionGrain>(GrainKeys.Session(sessionId))
            .OpenAsync(userId, "cyc-portal", device, "0000000000000000", [AuthenticationMethod.Password]);

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        (await cluster.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(userId)).TrackSessionAsync(sessionId)).IsSuccess.ShouldBeTrue();

        return sessionId;
    }

    async Task<bool> AllowedAsync(string permission, string user) {
        var check = await cluster.For(Tenant)
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture)))
            .CheckAsync(permission, SubjectRef.Of(SubjectTypes.User, user), Consistency.FullyConsistent);

        check.IsSuccess.ShouldBeTrue(check.Error?.Message);

        return check.GetValueOrThrow().Allowed;
    }

    /// <summary>The tenant and its owner — a real user, so the member list has somebody in it — once.</summary>
    async Task SeedAsync() {
        await Seeding.WaitAsync(Ct);

        try {
            if (seeded) {
                return;
            }

            (await cluster.For(Tenant)
                .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant))
                .CreateAsync("identity-admin", "Identity Admin", "eu-west-1")).IsSuccess.ShouldBeTrue();

            await cluster.CreateUserAsync(Tenant, OwnerId);
            await cluster.GrantTenantOwnerAsync(Tenant, Owner);

            seeded = true;
        } finally {
            Seeding.Release();
        }
    }
}
