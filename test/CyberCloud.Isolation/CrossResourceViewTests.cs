using CyberCloud.Authorization.Contracts;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Isolation;

/// <summary>
///     The cross-resource view of docs/plan/08 § What the resource manager deliberately does not do,
///     attacked through the real <c>ReBacResourceAuthorizer</c>: another tenant's resource is
///     <c>404</c>, an ungranted resource in the same tenant is the same <c>404</c>, a granted one
///     reads — and there is no member on the seam that could write.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rule under attack</b> — <see cref="IResourceView" />'s remarks state it: the
///         owning resource is the ReBAC subject, <c>resource:{owner}</c>, and the check is the one
///         the gateway makes for a <c>GET</c>. So every grant in this class is written the way a
///         tenant would write it, through <see cref="IRoleAssignmentManager" /> with
///         <c>principalType: "resource"</c>, and every refusal is asserted to be indistinguishable
///         from a name that was never used.
///     </para>
///     <para>
///         ⚠ <b>The view is built the way the driver builds it</b> — <c>IsolationCluster.Views</c>
///         — and bound to an owner the test chose, because that is what a reconciler receives. What a
///         reconciler cannot do is choose the owner, and <c>CrossResourceSeamTests</c> in the manager
///         suite is where the driver's binding is pinned.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class CrossResourceViewTests(IsolationCluster cluster) {
    static IsolationTarget Widgets => IsolationCatalog.Targets[0];

    static IsolationTarget Probes => IsolationCatalog.Targets[1];

    [Fact]
    public async Task AResourceInAnotherTenantIs404BeforeAnyGrainOfThatTenantIsAsked() {
        var attacker = await AttackerResourceAsync(Widgets, "view-attacker");
        var victim = await VictimResourceAsync(Probes, "view-victim");

        // ⚠ The tenant gate runs before the engine, so no tuple in the victim's tenant could help
        // the attacker — AGrantIsNotTransitiveAcrossTenants writes one by hand and shows it does not.
        // This test is the plain case: nothing granted, nothing seen, three ways of asking.
        var (view, _) = cluster.Views.For(attacker);

        var refused = await view.ReadAsync(victim, TestContext.Current.CancellationToken);
        ShouldBeInvisible(refused, victim);

        var objects = await view.RenderedObjectsAsync(victim, TestContext.Current.CancellationToken);
        ShouldBeInvisible(objects, victim);

        // And with the id withheld, so the view has to resolve the path: still nothing — the
        // attacker's tenant has no index entry for a victim path.
        var byPath = await view.ReadAsync(victim.WithId(Guid.Empty), TestContext.Current.CancellationToken);
        ShouldBeInvisible(byPath, victim);
    }

    [Fact]
    public async Task ASpoofedTenantIdInTheAddressResolvesNothing() {
        // The attacker's reconciler writes the victim's path but its own tenant id, hoping the
        // resolve happens in its tenant and the read in the victim's. It resolves nothing.
        var attacker = await AttackerResourceAsync(Widgets, "spoof-attacker");
        var victim = await VictimResourceAsync(Probes, "spoof-victim");

        var spoofed = victim with { TenantId = IsolationCluster.Attacker, Id = Guid.Empty };

        var refused = await cluster.Views.For(attacker).View.ReadAsync(spoofed, TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // And the other way: the victim's GUID with the attacker's tenant. The GUID is real, the
        // tenant-qualified grain for it in the attacker's tenant is empty.
        var byGuid = victim with { TenantId = IsolationCluster.Attacker };
        var stillRefused = await cluster.Views.For(attacker).View.ReadAsync(byGuid, TestContext.Current.CancellationToken);

        stillRefused.IsFailure.ShouldBeTrue();
        stillRefused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AnUngrantedResourceInTheSameTenantIsTheSame404AsAnAbsentOne() {
        // ⚠ THE ORACLE, ONE TENANT IN. A vault that has not been granted read on a share must not be
        // able to tell "there is a share here I may not see" from "there is nothing here" — the same
        // argument docs/plan/07 § The enforcement seam makes for a user, made for a resource.
        var reader = await VictimResourceAsync(Widgets, "ungranted-reader");
        var target = await VictimResourceAsync(Probes, "ungranted-target");
        var absent = IsolationCluster.Address(Probes, "ungranted-absent", IsolationCluster.Victim, IsolationCluster.VictimSubscription);

        var (view, _) = cluster.Views.For(reader);

        var onReal = await view.ReadAsync(target, TestContext.Current.CancellationToken);
        var onAbsent = await view.ReadAsync(absent, TestContext.Current.CancellationToken);

        onReal.IsFailure.ShouldBeTrue("a resource read another it was never granted");
        onReal.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        onReal.Error.Code.ShouldNotBe(ErrorCode.AuthorizationFailed);
        onAbsent.Error!.Code.ShouldBe(onReal.Error.Code);

        onReal.Error.Message.Replace(target.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(onAbsent.Error.Message.Replace(absent.Path, "PATH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGrantedResourceReadsTheSnapshotAndTheRenderedObjectAddresses() {
        var reader = await VictimResourceAsync(Widgets, "granted-reader");
        var target = await VictimResourceAsync(Probes, "granted-target");

        // ── Before the grant: nothing ───────────────────────────────────────────────────────────
        var (view, _) = cluster.Views.For(reader);
        (await view.ReadAsync(target, TestContext.Current.CancellationToken)).IsFailure.ShouldBeTrue("the fixture leaked a grant");

        // ── The grant, written the way a tenant writes it: reader on the GROUP to resource:{reader} ──
        var granted = await GrantReaderToResourceAsync(
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            reader.Id,
            IsolationCluster.VictimUser
        );

        granted.IsSuccess.ShouldBeTrue("a group owner could not grant reader to a resource in their own tenant: " + granted.Error?.Message);
        granted.GetValueOrThrow().PrincipalType.ShouldBe(ObjectTypes.Resource);
        granted.GetValueOrThrow().PrincipalId.ShouldBe(reader.Id.ToString("N", CultureInfo.InvariantCulture));

        // ── After: the snapshot, as the gateway would return it ─────────────────────────────────
        var read = await view.ReadAsync(target, TestContext.Current.CancellationToken);
        read.IsSuccess.ShouldBeTrue(read.Error?.Message);

        var snapshot = read.GetValueOrThrow();
        snapshot.Id.ShouldBe(target.Id);
        snapshot.Path.ShouldBe(target.Path);
        snapshot.ApiVersion.ShouldBe(Probes.ApiVersion);
        snapshot.Body.ShouldContain("\"note\"", customMessage: "the other provider's public contract is what comes back");

        // By path as well as by id — the view resolves through the tenant's index like a GET.
        var byPath = await view.ReadAsync(target.WithId(Guid.Empty), TestContext.Current.CancellationToken);
        byPath.IsSuccess.ShouldBeTrue(byPath.Error?.Message);
        byPath.GetValueOrThrow().Id.ShouldBe(target.Id);

        // ── And the objects the other provider rendered, as addresses ──────────────────────────
        var rendered = await view.RenderedObjectsAsync(target, TestContext.Current.CancellationToken);
        rendered.IsSuccess.ShouldBeTrue(rendered.Error?.Message);

        var addresses = rendered.GetValueOrThrow();
        addresses.Length.ShouldBe(1, "a probe is one ConfigMap");
        addresses[0].Kind.Kind.ShouldBe("ConfigMap");
        addresses[0].Name.ShouldBe(Conformance.Reference.Probes.ObjectNameOf(target));
        addresses[0].Namespace.ShouldBe(ReconcileDriver.NamespaceFor(target));

        // ⚠ Only the target's objects. The reader's own ConfigMap sits in the same namespace, under
        // a different resource-id label, and must not be attributed to the target.
        addresses.ShouldNotContain(x => x.Name == reader.Name);
    }

    [Fact]
    public async Task AGrantIsNotTransitiveAcrossTenants() {
        // A grant in the victim's tenant to a GUID that happens to be an attacker resource's is a
        // grant to nobody: the tenant gate refuses before the tuple is ever consulted.
        var attacker = await AttackerResourceAsync(Widgets, "transitive-attacker");
        var victim = await VictimResourceAsync(Probes, "transitive-victim");

        // The victim's owner cannot even write it — a resource that exists only in another tenant
        // is not a principal here. (RoleAssignmentService.ExistsAsync, tenant-qualified.)
        var refused = await GrantReaderToResourceAsync(
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            attacker.Id,
            IsolationCluster.VictimUser
        );

        refused.IsFailure.ShouldBeTrue("a resource from another tenant was accepted as a principal");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);

        // And had the tuple been written by hand, the gate would still refuse the read.
        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.ResourceGroup,
                ReBacResourceAuthorizer.GroupObjectId(victim)
            ),
            Relations.Reader,
            SubjectRef.Of(ObjectTypes.Resource, attacker.Id)
        );

        var read = await cluster.Views.For(attacker).View.ReadAsync(victim, TestContext.Current.CancellationToken);
        ShouldBeInvisible(read, victim);
    }

    [Fact]
    public void TheViewHasNoMemberThatCouldWrite() {
        // ⚠ "A write attempt has no API to call" is a claim about the interface, so it is asserted on
        // the interface: two methods, each taking an address and a token and nothing else — no body,
        // no verb, no caller — and returning a snapshot or a list of addresses. A third member added
        // later has to be argued past this test, and IResourceView's remarks say what the argument
        // would have to defeat.
        var members = typeof(IResourceView).GetMembers(BindingFlags.Public | BindingFlags.Instance);

        members.Select(x => x.Name).Order(StringComparer.Ordinal)
            .ShouldBe([nameof(IResourceView.ReadAsync), nameof(IResourceView.RenderedObjectsAsync)]);

        foreach (var method in typeof(IResourceView).GetMethods()) {
            method.GetParameters().Select(x => x.ParameterType)
                .ShouldBe([typeof(ResourceId), typeof(CancellationToken)], $"{method.Name} takes something a write could ride in on");

            method.ReturnType.ShouldBeOneOf(
                typeof(Task<Result<ResourceSnapshot>>),
                typeof(Task<Result<ImmutableArray<Kubernetes.Contracts.ObjectRef>>>)
            );
        }

        // And the seam a reconciler is handed exposes nothing that writes a resource, either.
        typeof(ReconcileContext).GetProperties()
            .Select(x => x.PropertyType)
            .ShouldNotContain(typeof(IResourceManager));
        typeof(ReconcileContext).GetProperties()
            .Select(x => x.PropertyType)
            .ShouldNotContain(typeof(IResourceGrain));
    }

    [Fact]
    public void TheCallerEquivalentIsTheOwningResourceAndNothingElse() {
        var owner = IsolationCluster
            .Address(Widgets, "caller", IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(Guid.Parse("66666666-0000-4000-8000-000000000006"));

        var caller = ResourceViews.CallerFor(owner);

        caller.TenantId.ShouldBe(IsolationCluster.Victim);
        caller.SubjectType.ShouldBe(ObjectTypes.Resource);
        caller.SubjectId.ShouldBe("66666666000040008000000000000006");
        caller.ImpersonatedBy.ShouldBeEmpty("a resource acts as itself, never on behalf of a user");

        // A resource with no GUID yet cannot be a subject: it does not exist to be granted anything.
        Should.Throw<ArgumentException>(() => ResourceViews.CallerFor(owner.WithId(Guid.Empty)));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    Task<Result<RoleAssignmentSnapshot>> GrantReaderToResourceAsync(Guid tenant, Guid subscription, Guid resourceId, string user) {
        var principal = resourceId.ToString("N", CultureInfo.InvariantCulture);

        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(tenant, subscription, IsolationCluster.Group),
            new(Relations.Reader, ObjectTypes.Resource, principal)
        );

        return cluster.Roles.AssignAsync(
            new() {
                Path = assignment.Path,
                Body = $$$"""{"{{{RoleAssignmentBodyProperties.PrincipalId}}}":"{{{principal}}}","{{{RoleAssignmentBodyProperties.PrincipalType}}}":"{{{ObjectTypes.Resource}}}","{{{RoleAssignmentBodyProperties.RoleDefinitionId}}}":"reader"}""",
                Caller = IsolationCluster.Caller(tenant, user)
            },
            TestContext.Current.CancellationToken
        );
    }

    async Task<ResourceId> VictimResourceAsync(IsolationTarget target, string name) {
        var id = await cluster.CreateAsync(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription, IsolationCluster.VictimUser);
        return IsolationCluster.Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription).WithId(id);
    }

    async Task<ResourceId> AttackerResourceAsync(IsolationTarget target, string name) {
        var id = await cluster.CreateAsync(target, name, IsolationCluster.Attacker, IsolationCluster.AttackerSubscription, IsolationCluster.AttackerUser);
        return IsolationCluster.Address(target, name, IsolationCluster.Attacker, IsolationCluster.AttackerSubscription).WithId(id);
    }

    static void ShouldBeInvisible<T>(Result<T> refused, ResourceId victim)
        where T : notnull {
        refused.IsFailure.ShouldBeTrue($"'{victim.Path}' was viewable from another tenant's resource");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Code.ShouldNotBe(ErrorCode.AuthorizationFailed, "403 across a tenant boundary confirms the resource exists");
        refused.Error.Message.Contains("permission", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(refused.Error.Message);
    }
}
