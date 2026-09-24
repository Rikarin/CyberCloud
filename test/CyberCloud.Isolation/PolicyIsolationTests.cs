using CyberCloud.Authorization.Contracts;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     Policy (#46) through the real authorization engine and the real engine behind step 5: who may
///     write a definition or an assignment, and that one tenant's policy governs that tenant's writes
///     and nobody else's.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Real on every side.</b> <c>PolicyManagerService</c> checks <c>assignRole</c> through
///         <c>ReBacScopeAuthorizer</c> over <c>CyberCloudSchema</c>; the write path evaluates through
///         <c>CatalogPolicyEvaluator</c> over each tenant's catalog grain, reached <c>ForTenant</c> from
///         the resource's own address. A doubled seam on either side would let this suite pass with a
///         policy nobody was stopped from writing, or one that stopped the wrong tenant.
///     </para>
///     <para>
///         ⚠ <b>The fixture's two tenants, and the grants are direct tuples on the scopes they name</b>
///         — the fixture creates its scopes without parent edges, so a grant here reaches exactly the
///         object it is written on and nothing an inheritance walk could add. Everything assigned is
///         removed before the test returns, because every other class in the suite now writes through
///         the real engine.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class PolicyIsolationTests(IsolationCluster cluster) {
    /// <summary>The victim's subscription owner, granted on the subscription object and nowhere else.</summary>
    const string Sully = "sully";

    /// <summary>A contributor on the victim's resource group — may read it and write in it, not govern it.</summary>
    const string Carl = "carl";

    static ScopeId VictimSubscriptionScope => ScopeId.Subscription(IsolationCluster.Victim, IsolationCluster.VictimSubscription);

    static ScopeId VictimGroup => ScopeId.Group(IsolationCluster.Victim, IsolationCluster.VictimSubscription, IsolationCluster.Group);

    [Fact]
    public async Task OnlyAnOwnerOfTheScopeWritesAPolicyAndAContributorIsRefusedWithA403() {
        await SeedGrantsAsync();

        var definition = PolicyAddress.Definition(VictimSubscriptionScope, "iso-contributor");
        var assignment = PolicyAddress.Assignment(VictimGroup, "iso-contributor");

        try {
            (await PutAsync(definition, DenyAll, Sully)).IsSuccess.ShouldBeTrue("the subscription's owner writes its definitions");

            // ⚠ A contributor can read the group and write in it, and cannot govern it: assignRole is
            // Rel(owner) & !Rel(suspended). A contributor who could assign a deny would stop every other
            // contributor's writes. 403 and not 404, because Carl can see the group he was refused on.
            var refused = await PutAsync(assignment, AssignmentBody(definition), Carl);
            refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

            // The group's owner holds assignRole on the group, and a definition on the subscription above
            // it is one the group may use.
            (await PutAsync(assignment, AssignmentBody(definition), IsolationCluster.VictimUser))
                .IsSuccess.ShouldBeTrue("the group owner assigns a definition from the subscription above it");

            (await cluster.Policies.ReadAsync(Request(assignment, "{}", Carl), TestContext.Current.CancellationToken))
                .IsSuccess.ShouldBeTrue("reading a policy on a scope is `read` on the scope, which a contributor has");
        } finally {
            await cluster.Policies.DeleteAsync(Request(assignment, "{}", IsolationCluster.VictimUser), TestContext.Current.CancellationToken);
            await cluster.Policies.DeleteAsync(Request(definition, "{}", Sully), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AnotherTenantOrAnUngrantedCallerLearnsNothingAboutTheVictimsPolicy() {
        await SeedGrantsAsync();

        var definition = PolicyAddress.Definition(VictimSubscriptionScope, "iso-hidden");

        try {
            (await PutAsync(definition, DenyAll, Sully)).IsSuccess.ShouldBeTrue();

            // The attacker's own tenant on the token, the victim's in the path: refused before any
            // grain is asked — the tenant comparison — with the canonical sentence.
            var crossTenant = await cluster.Policies.ReadAsync(
                new() { Path = definition.Path, Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser) },
                TestContext.Current.CancellationToken
            );
            crossTenant.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
            crossTenant.Error.Message.ShouldBe($"'{definition.Path}' does not exist.");

            // A caller in the victim's tenant with no grant: the same 404, for a definition that exists.
            var ungranted = await cluster.Policies.ReadAsync(
                new() { Path = definition.Path, Caller = IsolationCluster.Caller(IsolationCluster.Victim, "nobody") },
                TestContext.Current.CancellationToken
            );
            ungranted.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

            var overwrite = await cluster.Policies.PutAsync(
                new() { Path = definition.Path, Body = DenyAll, Caller = IsolationCluster.Caller(IsolationCluster.Victim, "nobody") },
                TestContext.Current.CancellationToken
            );
            overwrite.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "a caller who cannot read the scope cannot learn it has a policy by writing one");

            var states = await cluster.Policies.ListAsync(
                new() { Path = PolicyAddress.States(VictimGroup).Path, Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser) },
                TestContext.Current.CancellationToken
            );
            states.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        } finally {
            await cluster.Policies.DeleteAsync(Request(definition, "{}", Sully), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task TheVictimsDenyStopsTheVictimsWriteAndNotTheAttackersThroughTheRealSeams() {
        await SeedGrantsAsync();

        var definition = PolicyAddress.Definition(VictimSubscriptionScope, "iso-deny");
        var assignment = PolicyAddress.Assignment(VictimGroup, "iso-deny");
        var target = IsolationCatalog.Targets[0];

        try {
            (await PutAsync(definition, DenyAll, Sully)).IsSuccess.ShouldBeTrue();
            (await PutAsync(assignment, AssignmentBody(definition), IsolationCluster.VictimUser)).IsSuccess.ShouldBeTrue();

            // ⚠ Victor owns the group through the real schema, so step 3 lets him through; step 5 is
            // what refuses him, and says which assignment did.
            var victims = await WriteAsync(target, IsolationCluster.Victim, IsolationCluster.VictimSubscription, IsolationCluster.VictimUser);
            victims.Error!.Code.ShouldBe(ErrorCode.PolicyViolation);
            victims.Error.Message.ShouldContain(assignment.Path);

            // The attacker's identical write in their own tenant: their catalog holds nothing of the
            // victim's, so nothing applies — and step 5 still ran.
            var attackers = await WriteAsync(target, IsolationCluster.Attacker, IsolationCluster.AttackerSubscription, IsolationCluster.AttackerUser);
            attackers.IsSuccess.ShouldBeTrue(attackers.Error?.Message);
            attackers.GetValueOrThrow().Trace.Reached.ShouldContain(WriteStep.Policy);
            attackers.GetValueOrThrow().Trace.Policy.ShouldBeEmpty();
        } finally {
            await cluster.Policies.DeleteAsync(Request(assignment, "{}", IsolationCluster.VictimUser), TestContext.Current.CancellationToken);
            await cluster.Policies.DeleteAsync(Request(definition, "{}", Sully), TestContext.Current.CancellationToken);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    const string DenyAll = """{ "properties": { "policyRule": { "if": { "field": "type", "like": "*" }, "then": { "effect": "deny" } } } }""";

    static string AssignmentBody(PolicyAddress definition) =>
        $$"""{ "properties": { "policyDefinitionId": "{{definition.Path}}" } }""";

    async Task SeedGrantsAsync() {
        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.Subscription,
                IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture)
            ),
            Relations.Owner,
            SubjectRef.Of(SubjectTypes.User, Sully)
        );

        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.ResourceGroup,
                IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture) + "-" + IsolationCluster.Group
            ),
            Relations.Contributor,
            SubjectRef.Of(SubjectTypes.User, Carl)
        );
    }

    Task<Result<PolicyObjectSnapshot>> PutAsync(PolicyAddress address, string body, string user) =>
        cluster.Policies.PutAsync(Request(address, body, user), TestContext.Current.CancellationToken);

    static PolicyRequest Request(PolicyAddress address, string body, string user) =>
        new() { Path = address.Path, Body = body, Caller = IsolationCluster.Caller(address.TenantId, user) };

    Task<Result<WriteAccepted>> WriteAsync(IsolationTarget target, Guid tenant, Guid subscription, string user) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = IsolationCluster.Address(target, "policed", tenant, subscription).Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = IsolationCluster.Caller(tenant, user)
            },
            TestContext.Current.CancellationToken
        );
}
