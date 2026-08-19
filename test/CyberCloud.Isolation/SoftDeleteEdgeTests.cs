using CyberCloud.Authorization;
// ⚠ ObjectTypes and Relations live in the contracts assembly — see
// CyberCloud.Authorization.Contracts/AuthorizationVocabulary.cs. Safe to import unqualified here
// because nothing in this file names ObjectRef, which GlobalUsings.cs pins to the Kubernetes
// spelling.
using CyberCloud.Authorization.Contracts;
using CyberCloud.ResourceManager;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     The edge a soft-deleted resource hangs off — against the real schema, the real writer and the
///     real evaluator.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/08 § Soft delete asks for two tests by description and this is where the
///         load-bearing half of one of them can actually be made.</b> The document wants <i>"a restored
///         resource is visible to a subscription role holder and to its resource-group role
///         holder"</i>. <c>SoftDeletePathTests</c> asserts the <c>parent</c> edge <b>moves</b>, which is
///         a claim about the delete path and is checked against a recording double. Whether an edge
///         naming <c>subscription:{sub}</c> actually grants anything is a claim about
///         <c>CyberCloudSchema</c>, and no double can answer it: a double writes whatever tuple its
///         author believed in and answers whatever its author believed. That is the same argument
///         <see cref="ParentEdgeTests" /> opens with, and it applies here more sharply — nothing in the
///         platform had ever written a <c>subscription:</c> tuple before soft delete, so the rewrite
///         being composable was reasoning rather than a measurement.
///     </para>
///     <para>
///         <b>What it establishes.</b> <c>CyberCloudSchema</c> gives <c>resource</c>
///         <c>Role(owner, This | From(parent, owner))</c> and declares <c>parent</c> with no
///         subject-type constraint, and gives <c>subscription</c> the same
///         <c>owner</c>/<c>contributor</c>/<c>reader</c> shape it gives <c>resourceGroup</c>. So
///         <c>resource → subscription</c> should resolve in one hop and need no <c>SchemaVersion</c>
///         bump. Should. This runs it.
///     </para>
///     <para>
///         ⚠ <b>The resource here is live rather than soft-deleted, and that is deliberate.</b> A
///         soft-deleted resource does not resolve through <c>IResourceIndexGrain</c> — that refusal is
///         the whole of its <c>404</c> — so driving a read through the manager would answer "does not
///         exist" for index reasons and prove nothing about the tuple. Moving the edge under a live
///         resource isolates the authorization question from the addressability one, which is the only
///         way to ask it.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class SoftDeleteEdgeTests(IsolationCluster cluster) {
    /// <summary>The user who holds a role on the subscription and on nothing else.</summary>
    /// <remarks>
    ///     ⚠ Deliberately not <c>victor</c>, who holds <c>owner</c> on the resource group. Reusing him
    ///     would make every assertion below pass through the group edge that is being taken away, which
    ///     is a test that cannot fail.
    /// </remarks>
    const string SubscriptionUser = "simone";

    [Fact]
    public async Task ASubscriptionEdgeIsWhatMakesASoftDeletedResourceVisibleToSubscriptionRoleHolders() {
        var target = IsolationCatalog.Targets[0];
        const string name = "parked-edge";

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var address = IsolationCluster
            .Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);

        // simone owns the SUBSCRIPTION and nothing below it.
        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                ObjectTypes.Subscription,
                IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture)
            ),
            Relations.Owner,
            SubjectRef.Of(ObjectTypes.User, SubscriptionUser)
        );

        // ── The control: a subscription grant reaches nothing today ─────────────────────────────
        //
        // ⚠ THIS IS WHAT MAKES THE ASSERTION AFTER THE RE-PARENT MEAN SOMETHING. Nothing writes
        // `resourceGroup:{x}#parent@subscription:{y}`, so the chain from a live resource stops at its
        // group and simone's grant is inert. If she could read it here, the test below would pass with
        // the re-parent deleted.
        (await ReadAsync(address, target, SubscriptionUser)).IsFailure.ShouldBeTrue(
            "a subscription role holder cannot reach a resource whose parent edge names its resource "
            + "group — nothing writes the group's own parent tuple, so the walk stops there"
        );

        (await ReadAsync(address, target, IsolationCluster.VictimUser)).IsSuccess.ShouldBeTrue(
            "and the group owner can, which is the edge the create wrote"
        );

        // ── The move, through the real writer ───────────────────────────────────────────────────
        var writer = new ReBacResourceRelationWriter(
            cluster.Grains,
            NullLogger<ReBacResourceRelationWriter>.Instance
        );

        var parked = await writer.ReparentToSubscriptionAsync(address, Guid.Empty, TestContext.Current.CancellationToken);
        parked.IsSuccess.ShouldBeTrue(parked.Error?.Message);

        // ⚠ THE MEASUREMENT. `resource:{id}#parent@subscription:{sub}` composes through
        // CyberCloudSchema's `From(parent, owner)` with no schema change and no SchemaVersion bump —
        // which is what docs/plan/08 § Soft delete assumed when it chose the subscription as the place
        // a deleted resource lives, and what nothing had ever run.
        (await ReadAsync(address, target, SubscriptionUser)).IsSuccess.ShouldBeTrue(
            "the re-parented resource is not reachable from a subscription role holder, so a "
            + "soft-deleted resource would be visible to nobody — docs/plan/08 § Soft delete chose "
            + "re-parenting precisely so that cannot happen"
        );

        // ⚠ And the group owner has LOST it, which is the other half of "it left its resource group".
        // An implementation that wrote the new edge and never removed the old one would keep both
        // readers happy and leave CheckGrain.WalkAncestorsAsync choosing between two parents by store
        // order.
        (await ReadAsync(address, target, IsolationCluster.VictimUser)).IsFailure.ShouldBeTrue(
            "the resource left its resource group, so a tuple naming that group asserts a containment "
            + "that is no longer true — preserving it is not the conservative choice, it is the wrong "
            + "one"
        );

        // ── And the restore puts it back ────────────────────────────────────────────────────────
        var restored = await writer.ReparentFromSubscriptionAsync(address, Guid.Empty, TestContext.Current.CancellationToken);
        restored.IsSuccess.ShouldBeTrue(restored.Error?.Message);

        (await ReadAsync(address, target, IsolationCluster.VictimUser)).IsSuccess.ShouldBeTrue(
            "a restored resource is visible to its resource-group role holder — docs/plan/08 § Soft "
            + "delete asks for exactly this test"
        );

        (await ReadAsync(address, target, SubscriptionUser)).IsFailure.ShouldBeTrue(
            "and the subscription edge is gone with it, rather than accumulating one row per resource "
            + "that was ever recovered"
        );
    }

    /// <summary>
    ///     ⚠ <b>A direct role assignment on the resource is dropped, and only the direct one.</b>
    /// </summary>
    /// <remarks>
    ///     docs/plan/08 § Soft delete's second test by description, and the real engine is where the
    ///     "only the direct one" half is worth making: a drop implemented as "remove every relation on
    ///     this object" would take the <c>parent</c> edge with it and leave the resource visible to
    ///     nobody — the failure the re-parent exists to prevent, arriving through the security fix
    ///     rather than the modelling one. A recording double cannot tell the two implementations apart
    ///     unless somebody thought to make it.
    /// </remarks>
    [Fact]
    public async Task DroppingDirectAssignmentsLeavesTheInheritedPathIntact() {
        var target = IsolationCatalog.Targets[0];
        const string name = "dropped-grants";

        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        var address = IsolationCluster
            .Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);

        // A grant written directly on the resource, which is what an administrator does when somebody
        // needs one database and not the whole group.
        const string direct = "dana";

        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(ObjectTypes.Resource, id.ToString("N", CultureInfo.InvariantCulture)),
            Relations.Reader,
            SubjectRef.Of(ObjectTypes.User, direct)
        );

        (await ReadAsync(address, target, direct)).IsSuccess.ShouldBeTrue("the direct grant works");

        var writer = new ReBacResourceRelationWriter(
            cluster.Grains,
            NullLogger<ReBacResourceRelationWriter>.Instance
        );

        var dropped = await writer.DropDirectRoleAssignmentsAsync(address, TestContext.Current.CancellationToken);
        dropped.IsSuccess.ShouldBeTrue(dropped.Error?.Message);
        dropped.GetValueOrThrow().ShouldBe(1, "exactly the one assignment that was there");

        (await ReadAsync(address, target, direct)).IsFailure.ShouldBeTrue(
            "assignments go with the resource and must be recreated on recovery — Azure's behaviour, "
            + "and docs/plan/08 § Soft delete takes it because silently restoring a grant an "
            + "administrator deliberately removed is an error nobody observes"
        );

        // ⚠ THE OTHER HALF, AND THE ONE ONLY THE REAL ENGINE CAN ANSWER. The parent edge survived, so
        // everything inherited still works.
        (await ReadAsync(address, target, IsolationCluster.VictimUser)).IsSuccess.ShouldBeTrue(
            "the drop took the parent edge with it, so the resource is now visible to nobody — which "
            + "is the exact failure re-parenting exists to prevent, reintroduced by the security fix"
        );
    }

    /// <summary>
    ///     Can this user read this resource? Asked of the real authorizer, <b>fully consistent</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NOT through <c>IResourceManager.ReadAsync</c>, and the first version of this file
    ///         was and failed for a reason worth keeping.</b> A <c>GET</c> authorizes with
    ///         <c>fullyConsistent: false</c> — docs/plan/07 § Consistency makes an ordinary read cached
    ///         and eventually consistent on purpose — so a check taken before a tuple moved is still
    ///         answerable from cache after it. Both tests here move a tuple <i>under a resource they
    ///         have already read</i>, which is precisely the shape that reads stale: the control read
    ///         cached a negative, the re-parent changed the answer, and the second read returned the
    ///         negative it had.
    ///     </para>
    ///     <para>
    ///         That is the platform behaving as designed and it is not what these tests are about. The
    ///         question is whether <c>CyberCloudSchema</c> composes <c>#parent@subscription:</c> at all,
    ///         so the check is taken the way the <i>delete path</i> takes it — through the same
    ///         <c>ReBacResourceAuthorizer</c> the manager holds, with the same
    ///         <c>fullyConsistent: true</c> docs/plan/07 § Consistency puts deletion in the row for.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     ⚠ <b>Every purge permission the registry names is one <c>CyberCloudSchema</c> actually
    ///     defines — the check that would have caught a purge nobody could perform.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What went wrong, because the shape of it is the reason this exists.</b>
    ///         <c>SoftDeletePolicy.DefaultPurgePermission</c> is <c>"purge"</c> and
    ///         <c>ResourceManagerService.PurgeAsync</c> checks it. <c>CyberCloudSchema</c> defined
    ///         <c>read</c>, <c>write</c>, <c>delete</c> and <c>assignRole</c> on
    ///         <see cref="ObjectTypes.Resource" /> and nothing else. A permission the schema does not
    ///         declare can only evaluate false, and docs/plan/07 § The enforcement seam turns a false
    ///         into the canonical <c>404</c> — so on a real silo every purge answered "does not exist"
    ///         to every caller, for ever: the name stayed held, the committed quota was never returned,
    ///         and the recovery window had no end. The two constants live in assemblies that do not
    ///         reference each other, so nothing in the compiler could say they had drifted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It survived because every purge in the repository ran against a doubled
    ///         authorizer.</b> <c>SwitchableAuthorizer</c> and <c>PermissiveAuthorizer</c> answer
    ///         whatever their author believed about a permission name, which is the same argument
    ///         <see cref="ParentEdgeTests" /> opens with — and this is the second defect it has caught.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A registry sweep rather than one assertion about one constant, because
    ///         <c>SupportsSoftDelete(days, purgePermission)</c> lets a provider name its own.</b> A type
    ///         declaring <c>purgePermission: "destroy"</c> would reintroduce exactly this, silently, and
    ///         a test pinned to <c>"purge"</c> would stay green through it. ⚠ It sweeps
    ///         <b>this cluster's</b> registry, which holds three providers — so it is a gate on the
    ///         shape rather than a census of the catalogue, and a type declaring a window outside these
    ///         three is not covered here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void EveryDeclaredPurgePermissionIsOneTheSchemaDefinesOnAResource() {
        var windows = 0;

        foreach (var registration in cluster.Registry.Types) {
            if (registration.SoftDeleteDays == 0) {
                registration.PurgePermission.ShouldBeEmpty(
                    $"'{registration.Type}' declares no recovery window and carries a purge permission "
                    + "anyway, so the registry answers two questions that can disagree"
                );

                continue;
            }

            windows++;

            registration.PurgePermission.ShouldNotBeEmpty(
                $"'{registration.Type}' declares a recovery window and no permission to end it"
            );

            CyberCloudSchema.Instance
                .Member(ObjectTypes.Resource, registration.PurgePermission)
                .ShouldNotBeNull(
                    $"'{registration.Type}' declares purgePermission '{registration.PurgePermission}' "
                    + "and CyberCloudSchema defines no such member on `resource`. An undeclared "
                    + "permission evaluates false and the enforcement seam turns that into the "
                    + "canonical 404, so every purge of this type answers 'does not exist' to "
                    + "everybody — its name is held and its committed quota is never returned, for "
                    + "the whole window and then for ever. docs/plan/08 § Soft delete."
                );
        }

        windows.ShouldBeGreaterThan(
            0,
            "no type in this cluster's registry declares a recovery window, so the loop above asserted "
            + "nothing. A gate that inspects nothing is not a gate that found nothing wrong"
        );
    }

    Task<Result> ReadAsync(ResourceId address, IsolationTarget target, string user) {
        cluster.Registry.TryGetType(target.Type, out var registration).ShouldBeTrue();

        var authorizer = new ReBacResourceAuthorizer(
            cluster.Grains,
            NullLogger<ReBacResourceAuthorizer>.Instance
        );

        return authorizer.AuthorizeAsync(
            address,
            registration.ReadPermission,
            registration.ReadPermission,
            IsolationCluster.Caller(IsolationCluster.Victim, user),
            true,
            TestContext.Current.CancellationToken
        );
    }
}
