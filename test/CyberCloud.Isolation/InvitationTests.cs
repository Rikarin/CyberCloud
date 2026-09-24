using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.Tenancy.Contracts;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     Step 7 of the M1 exit story end to end, below HTTP: <i>"invite a colleague and grant them
///     Reader on one resource group"</i> — the invitation through the real <c>InvitationService</c>
///     and the gateway's <c>GrainInvitationIssuer</c>, the acceptance through the real invitation
///     grain, and the grant through the existing <c>IRoleAssignmentManager</c>, asserted at
///     <c>ICheckGrain</c> on a resource in the group. Issue #43.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The two halves are two requests, and this suite is where they meet.</b> The
///         invitation makes a member with no role — every check on the tenant's resources is still
///         false after the accept — and Reader is <c>PUT …/roleAssignments/reader-user-{id}</c> on
///         the group, answered by the same <c>GrainPrincipalDirectory</c> the gateway registers,
///         which now finds the invited user. The mail is captured at the seam here; it is sent to a
///         real SMTP server in <c>Identity.Host.Tests</c>' <c>InvitationsOverHttpTests</c>.
///     </para>
///     <para>
///         ⚠ <b>A tenant of this class's own</b>, whose owner's subject id is a GUID — an invitation
///         records who sent it, and the suite's other owners are named by label.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class InvitationTests(IsolationCluster cluster) {
    // ⚠ Its own prefix: the collection shares one cluster, and 99999999 is ManagementGroupTests' tree.
    static Guid Tenant { get; } = Guid.Parse("abababab-0000-4000-8000-000000000009");

    static Guid Subscription { get; } = Guid.Parse("abababab-0000-4000-8000-0000000000b1");

    static readonly string Owner = Guid.Parse("abababab-0000-4000-8000-00000000a0a0").ToString("N", CultureInfo.InvariantCulture);

    static readonly SemaphoreSlim Seeding = new(1, 1);
    static Guid? seededResource;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInvitedColleagueAcceptsWithNoRoleAndIsGrantedReaderOnOneGroup() {
        var resource = await SeedAsync();
        var email = $"colleague-{Guid.NewGuid():N}@isolation.test";

        // ── Invite, as the tenant owner. ───────────────────────────────────────────────────────
        var invited = await cluster.Invitations.InviteAsync(
            new() { TenantId = Tenant, Email = email, Caller = IsolationCluster.Caller(Tenant, Owner) },
            Ct
        );

        invited.IsSuccess.ShouldBeTrue(invited.Error?.Message);

        var invitation = invited.GetValueOrThrow();
        var colleague = invitation.UserId.ToString("N", CultureInfo.InvariantCulture);

        invitation.Status.ShouldBe("pending");

        var mail = IsolationCluster.InvitationMail.Deliveries.Single(x => x.InvitationId == invitation.InvitationId);

        // The tenant's own name, from its grain — never one the request could have chosen.
        mail.TenantName.ShouldBe("invite-tenant");
        mail.Email.ShouldBe(email);

        // ── Accept, as the colleague would through the identity host's page. ───────────────────
        var accepted = await cluster.For(Tenant)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(invitation.InvitationId))
            .AcceptAsync(mail.Secret, "Colleague", "a-password-for-the-colleague-1");

        accepted.IsSuccess.ShouldBeTrue(accepted.Error?.Message);

        // ⚠ A member and nothing more: no check on anything in the tenant passes yet.
        (await AllowedAsync(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture), Permissions.Read, colleague))
            .ShouldBeFalse("accepting the invitation granted a role on the tenant");
        (await AllowedAsync(ObjectTypes.Resource, resource.ToString("N", CultureInfo.InvariantCulture), Permissions.Read, colleague))
            .ShouldBeFalse("accepting the invitation reached a resource");

        // ── Reader on the group: the existing role-assignment API, by the owner. ────────────────
        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Tenant, Subscription, IsolationCluster.Group),
            new(Relations.Reader, SubjectTypes.User, colleague)
        );

        var granted = await cluster.Roles.AssignAsync(
            new() { Path = assignment.Path, Body = "{}", Caller = IsolationCluster.Caller(Tenant, Owner) },
            Ct
        );

        // ⚠ The principal directory is asked whether the colleague exists, and the invited user is
        // what answers — #86's check, met by the object the invitation created.
        granted.IsSuccess.ShouldBeTrue("Reader could not be granted to the invited member: " + granted.Error?.Message);

        (await AllowedAsync(ObjectTypes.Resource, resource.ToString("N", CultureInfo.InvariantCulture), Permissions.Read, colleague))
            .ShouldBeTrue("Reader on the group did not reach a resource in it");
        (await AllowedAsync(ObjectTypes.Resource, resource.ToString("N", CultureInfo.InvariantCulture), Permissions.Write, colleague))
            .ShouldBeFalse("Reader granted write");
        (await AllowedAsync(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture), Permissions.Read, colleague))
            .ShouldBeFalse("Reader on one group reached the whole tenant");
    }

    [Fact]
    public async Task OnlyAnOwnerOfTheTenantMayInvite() {
        await SeedAsync();

        var reader = await cluster.CreateUserAsync(Tenant, Guid.NewGuid());

        // A member with Reader on the tenant can see it and may not invite: 403, not 404.
        var readerGrant = await cluster.Roles.AssignAsync(
            new() {
                Path = RoleAssignmentId.OnScope(ScopeId.Tenant(Tenant), new(Relations.Reader, SubjectTypes.User, reader)).Path,
                Body = "{}",
                Caller = IsolationCluster.Caller(Tenant, Owner)
            },
            Ct
        );

        readerGrant.IsSuccess.ShouldBeTrue(readerGrant.Error?.Message);

        var byReader = await cluster.Invitations.InviteAsync(
            new() {
                TenantId = Tenant, Email = $"x-{Guid.NewGuid():N}@isolation.test", Caller = IsolationCluster.Caller(Tenant, reader)
            },
            Ct
        );

        byReader.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed, "a reader may see the tenant, so 403");

        // Somebody with nothing on the tenant: the canonical 404.
        var byStranger = await cluster.Invitations.InviteAsync(
            new() {
                TenantId = Tenant,
                Email = $"y-{Guid.NewGuid():N}@isolation.test",
                Caller = IsolationCluster.Caller(Tenant, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture))
            },
            Ct
        );

        byStranger.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // And an owner of ANOTHER tenant, naming this one: 404 before any check — the address is the
        // token's tenant, and a disagreement is absence.
        var crossTenant = await cluster.Invitations.InviteAsync(
            new() {
                TenantId = Tenant,
                Email = $"z-{Guid.NewGuid():N}@isolation.test",
                Caller = IsolationCluster.Caller(IsolationCluster.Victim, Owner)
            },
            Ct
        );

        crossTenant.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    /// <summary>
    ///     A pending link opens an invited user and nothing else: not a member who joined through
    ///     another link, not one suspended since, not one deprovisioned.
    /// </summary>
    /// <remarks>
    ///     ⚠ The review of #43 found all three open, with probes shaped like this one: the grain
    ///     renamed the user, set the password and made them <c>Active</c> without reading their
    ///     status, so the second link un-suspended a member and a link to a deprovisioned invitee
    ///     brought the account back — signed in, past any second factor enrolled since.
    /// </remarks>
    [Fact]
    public async Task ALinkCannotBringBackAMemberWhoWasSuspendedOrRemoved() {
        await SeedAsync();

        // ── Two links for one user: re-inviting an invited address reuses the user. ─────────────
        var email = $"twice-{Guid.NewGuid():N}@isolation.test";
        var first = await InviteAsync(email);
        var second = await InviteAsync(email);

        second.Invitation.UserId.ShouldBe(first.Invitation.UserId, "re-inviting an invited address reuses its user");

        (await AcceptAsync(first, "Joined Once", "the-first-password-1")).IsSuccess.ShouldBeTrue();

        var user = cluster.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(first.Invitation.UserId));

        // A member through the first link: the second is withdrawn, and accepting it changes nothing.
        (await DescribeAsync(second)).Status.ShouldBe(InvitationStatus.Withdrawn);

        var overMember = await AcceptAsync(second, "Somebody Else", "a-second-password-2");

        overMember.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await user.GetAsync()).GetValueOrThrow().DisplayName.ShouldBe("Joined Once", "the second link renamed the member");
        (await user.VerifyPasswordAsync("the-first-password-1")).GetValueOrThrow()
            .ShouldBeTrue("the second link replaced the member's password");

        // Suspended: the second link still changes nothing.
        (await user.SetStatusAsync(UserStatus.Suspended)).IsSuccess.ShouldBeTrue();

        var overSuspended = await AcceptAsync(second, "Somebody Else", "a-second-password-2");

        overSuspended.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await user.GetAsync()).GetValueOrThrow().Status.ShouldBe(UserStatus.Suspended, "a pending link un-suspended the member");
        (await DescribeAsync(second)).Status.ShouldBe(InvitationStatus.Withdrawn);

        // ⚠ And the user grain refuses on its own, not only behind the invitation's read of the
        // status: that check is in the same turn as the writes, and it is what two links accepted
        // at the same moment meet.
        var direct = await user.AcceptInvitationAsync("Somebody Else", "a-second-password-2");

        direct.IsSuccess.ShouldBeFalse("the user grain accepted an invitation for a suspended member");
        direct.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        (await user.GetAsync()).GetValueOrThrow().Status.ShouldBe(UserStatus.Suspended);

        // ── One link, and the invitee deprovisioned before using it. ───────────────────────────
        var removed = await InviteAsync($"removed-{Guid.NewGuid():N}@isolation.test");
        var invitee = cluster.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(removed.Invitation.UserId));

        (await invitee.SetStatusAsync(UserStatus.Deprovisioned)).IsSuccess.ShouldBeTrue();
        (await DescribeAsync(removed)).Status.ShouldBe(InvitationStatus.Withdrawn);

        var overRemoved = await AcceptAsync(removed, "Back Again", "a-third-password-3");

        overRemoved.Error!.Code.ShouldBe(ErrorCode.Conflict);
        (await invitee.GetAsync()).GetValueOrThrow().Status.ShouldBe(UserStatus.Deprovisioned, "a link resurrected a deprovisioned invitee");
    }

    /// <summary>
    ///     Joining with an account in another tenant makes a member with no credential of their own,
    ///     linked to that account, and only from an invited user — the grains' half of the invited
    ///     path's <i>"signs in"</i>. The identity host's half is
    ///     <c>InvitationsOverHttpTests.APersonSignedInElsewhereJoinsWithThatAccountAndSignsInHereThroughIt</c>.
    /// </summary>
    [Fact]
    public async Task JoiningWithAnAccountElsewhereLinksTheMemberAndSetsNoCredential() {
        await SeedAsync();

        var link = await InviteAsync($"home-{Guid.NewGuid():N}@isolation.test");
        var invitation = cluster.For(Tenant).GetGrain<IInvitationGrain>(GrainKeys.Invitation(link.Invitation.InvitationId));
        var user = cluster.For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(link.Invitation.UserId));
        var home = new HomeAccount { TenantId = IsolationCluster.Victim, UserId = Guid.NewGuid() };

        // This tenant is not another one: refused, and the link is still the person's to use.
        var own = await invitation.AcceptWithHomeAccountAsync(link.Secret, "Home Person", home with { TenantId = Tenant });

        own.IsSuccess.ShouldBeFalse("a member was linked to an account in their own tenant");
        own.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        (await DescribeAsync(link)).Status.ShouldBe(InvitationStatus.Pending, "a refused join spent the link");

        // ── Join. ──────────────────────────────────────────────────────────────────────────────
        var joined = await invitation.AcceptWithHomeAccountAsync(link.Secret, "Home Person", home);

        joined.IsSuccess.ShouldBeTrue(joined.Error?.Message);

        var member = (await user.GetAsync()).GetValueOrThrow();

        member.Status.ShouldBe(UserStatus.Active);
        member.HomeAccount.ShouldBe(home);
        member.EnrolledCredentials.ShouldBeEmpty("joining with an account set a credential");
        (await user.VerifyPasswordAsync(string.Empty)).GetValueOrThrow().ShouldBeFalse();

        // ── A member now: neither kind of accept changes them, even called on the user directly. ──
        var relinked = await user.JoinWithHomeAccountAsync("Somebody Else", new() { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid() });

        relinked.IsSuccess.ShouldBeFalse("a member was linked to a second account");
        relinked.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);

        var passworded = await user.AcceptInvitationAsync("Somebody Else", "a-password-nobody-chose-4");

        passworded.IsSuccess.ShouldBeFalse("a member who joined with an account was given a password");
        passworded.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        (await user.GetAsync()).GetValueOrThrow().HomeAccount.ShouldBe(home);

        // Deprovisioned, the link goes with every other credential.
        (await user.SetStatusAsync(UserStatus.Deprovisioned)).IsSuccess.ShouldBeTrue();
        (await user.GetAsync()).GetValueOrThrow().HomeAccount.ShouldBeNull("the home account outlived a deprovision");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Invites <paramref name="email" /> as the owner, and returns the invitation with the secret its mail carried.</summary>
    async Task<(InvitationSnapshot Invitation, string Secret)> InviteAsync(string email) {
        var invited = await cluster.Invitations.InviteAsync(
            new() { TenantId = Tenant, Email = email, Caller = IsolationCluster.Caller(Tenant, Owner) },
            Ct
        );

        invited.IsSuccess.ShouldBeTrue(invited.Error?.Message);

        var invitation = invited.GetValueOrThrow();

        return (invitation, IsolationCluster.InvitationMail.Deliveries.Single(x => x.InvitationId == invitation.InvitationId).Secret);
    }

    Task<Result<Invitation>> AcceptAsync((InvitationSnapshot Invitation, string Secret) link, string name, string password) =>
        cluster.For(Tenant)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(link.Invitation.InvitationId))
            .AcceptAsync(link.Secret, name, password);

    async Task<Invitation> DescribeAsync((InvitationSnapshot Invitation, string Secret) link) =>
        (await cluster.For(Tenant)
            .GetGrain<IInvitationGrain>(GrainKeys.Invitation(link.Invitation.InvitationId))
            .DescribeAsync(link.Secret)).GetValueOrThrow();

    async Task<bool> AllowedAsync(string type, string id, string permission, string user) {
        var check = await cluster.For(Tenant)
            .GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, id))
            .CheckAsync(permission, SubjectRef.Of(SubjectTypes.User, user), Consistency.FullyConsistent);

        check.IsSuccess.ShouldBeTrue($"the check on {type}:{id} did not answer: " + check.Error?.Message);

        return check.GetValueOrThrow().Allowed;
    }

    /// <summary>The tenant, its owner, one subscription, one group and one resource in it — once.</summary>
    async Task<Guid> SeedAsync() {
        await Seeding.WaitAsync(Ct);

        try {
            if (seededResource is { } existing) {
                return existing;
            }

            var created = await cluster.For(Tenant)
                .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant))
                .CreateAsync("invite-tenant", "Invite Tenant", "eu-west-1");

            created.IsSuccess.ShouldBeTrue(created.Error?.Message);

            await cluster.GrantTenantOwnerAsync(Tenant, Owner);

            var owner = IsolationCluster.Caller(Tenant, Owner);

            (await cluster.Scopes.CreateAsync(
                    new() {
                        Path = ScopeId.Subscription(Tenant, Subscription).Path,
                        Body = """{"displayName":"Invite"}""",
                        Caller = owner
                    },
                    Ct
                )).IsSuccess.ShouldBeTrue();

            (await cluster.Scopes.CreateAsync(
                    new() {
                        Path = ScopeId.Group(Tenant, Subscription, IsolationCluster.Group).Path,
                        Body = """{"location":"eu-west-1"}""",
                        Caller = owner
                    },
                    Ct
                )).IsSuccess.ShouldBeTrue();

            seededResource = await cluster.CreateAsync(
                IsolationCatalog.Targets[0],
                "invited-resource",
                Tenant,
                Subscription,
                Owner
            );

            return seededResource.Value;
        } finally {
            Seeding.Release();
        }
    }
}
