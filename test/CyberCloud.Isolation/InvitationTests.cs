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

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

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
