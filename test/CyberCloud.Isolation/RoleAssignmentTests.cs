using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.ResourceManager;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Isolation;

/// <summary>
///     Role assignment, driven through the real <c>RoleAssignmentService</c>, the real
///     <c>ReBacRoleAssignmentStore</c>, the real <c>GrainPrincipalDirectory</c> over the real
///     identity grains, both real authorizers and the real <c>CyberCloudSchema</c> — and asserted
///     by asking <c>ICheckGrain</c> about a resource two hops below the scope the tuple was written
///     on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE STORY THIS SUITE EXISTS TO MAKE BUILDABLE: "invite a colleague and grant them
///             Reader on one resource group" (issue #70, the M1 exit story).
///         </b> Until
///         <c>IRoleAssignmentManager</c> existed, nothing above <c>ITupleStoreGrain</c> could write
///         a <c>reader</c> or <c>contributor</c> tuple at all, so the story's second half had no
///         request. <see cref="AnOwnerGrantsReaderOnAGroupAndTheReaderCanReadButNotWriteAResourceInIt" />
///         is that half, end to end.
///     </para>
///     <para>
///         ⚠ <b>Asserted through <c>ICheckGrain</c> and through <c>IResourceManager</c> both.</b>
///         The grain is the authority — a tuple the schema cannot rewrite through would be visible
///         only there — and the manager is what a tenant actually calls, with the 404-versus-403
///         choice on top. A reader who can read but not write must get the <c>403</c>, because a
///         <c>404</c> there would tell them the resource they can see does not exist.
///     </para>
///     <para>
///         ⚠ <b>Every principal granted to is a directory object this class created (issue #86).</b>
///         A grant to a principal the tenant's directory does not hold is refused, so the users,
///         the group, the service principal and the managed identity below are written through
///         the real identity grains first, and their subject ids are the <c>N</c>-form GUIDs a
///         token would carry. The <i>callers</i> — the tenant owner and the caller with no grant —
///         are not looked up: who is asking comes from the token, and only who is being granted to
///         is the directory's question.
///     </para>
///     <para>
///         ⚠ <b>A tenant the fixture does not touch</b>, for the reason <c>ScopeCreationTests</c>
///         gives: <c>IsolationCluster.Victim</c> has its group owner written directly, and a grant
///         asserted inside it could be riding that shortcut.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class RoleAssignmentTests(IsolationCluster cluster) {
    /// <summary>A tenant this class owns outright.</summary>
    static Guid Grant { get; } = Guid.Parse("88888888-0000-4000-8000-000000000008");

    /// <summary>The tenant's owner, granted at the tenant and nowhere else.</summary>
    const string Owner = "olivia";

    /// <summary>A user granted nothing anywhere, until a test grants them something.</summary>
    const string Nobody = "nemo";

    /// <summary>The resource group's name — <see cref="IsolationCluster.Group" />, which <c>CreateAsync</c> addresses.</summary>
    const string Group = IsolationCluster.Group;

    static readonly SemaphoreSlim Seeding = new(1, 1);
    static readonly Dictionary<Guid, Guid> Seeded = [];

    // ── The M1 exit story ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOwnerGrantsReaderOnAGroupAndTheReaderCanReadButNotWriteAResourceInIt() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a1");
        var resource = await SeedAsync(subscription);
        var colleague = await UserAsync("rita");

        // ── Before: the colleague holds nothing, and the resource does not exist to them ────────
        (await AllowedAsync(resource, Permissions.Read, colleague)).ShouldBeFalse("the fixture leaked a grant");

        // ── The grant: PUT reader on the GROUP, by the tenant owner ─────────────────────────────
        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, SubjectTypes.User, colleague)
        );

        var granted = await cluster.Roles.AssignAsync(
            new() {
                Path = assignment.Path,
                Body =
                    $$$"""{"{{{RoleAssignmentBodyProperties.PrincipalId}}}":"{{{colleague}}}","{{{RoleAssignmentBodyProperties.RoleDefinitionId}}}":"reader"}""",
                Caller = IsolationCluster.Caller(Grant, Owner)
            },
            TestContext.Current.CancellationToken
        );

        // ⚠ THE GRANT THAT MAKES THIS SUCCEED IS ON THE TENANT AND NOTHING ELSE. `assignRole` on
        // the group is Rel(owner) & !Rel(suspended), and olivia's only tuple is tenant:{t}#owner —
        // so this line alone walks resourceGroup → subscription → tenant through the two edges the
        // scope path wrote. Delete either and this is a 404.
        granted.IsSuccess.ShouldBeTrue(
            "a tenant owner could not grant Reader on a group in their own tenant: " + granted.Error?.Message
        );

        var snapshot = granted.GetValueOrThrow();
        snapshot.Created.ShouldBeTrue();
        snapshot.RoleDefinitionId.ShouldBe(Relations.Reader);
        snapshot.PrincipalType.ShouldBe(SubjectTypes.User);
        snapshot.PrincipalId.ShouldBe(colleague);
        snapshot.Scope.ShouldBe(ScopeId.Group(Grant, subscription, Group).Path);
        snapshot.Inherited.ShouldBeFalse("a PUT addresses the tuple itself");

        // ── After, at the grain: read passes and write fails, on a RESOURCE in the group ────────
        //
        // ⚠ TWO HOPS BELOW THE TUPLE. The tuple is resourceGroup:{s}-prod#reader@user:rita and the
        // question is asked of resource:{id}. What carries it is Role(reader, This | From(parent,
        // reader) | Rel(contributor)) on `resource` plus the resource → group parent edge step 8
        // wrote — docs/plan/07 § Azure RBAC, expressed in it's third row, "no role tuples written
        // per resource". A tuple the store spelled on a different object, or with a relation the
        // schema does not rewrite, would leave this line false with the PUT above reporting success.
        (await AllowedAsync(resource, Permissions.Read, colleague)).ShouldBeTrue(
            "Reader on the group did not reach a resource in it — the tuple was written on an object "
            + "the rewrite does not visit, or under a relation it does not follow"
        );

        (await AllowedAsync(resource, Permissions.Write, colleague)).ShouldBeFalse(
            "Reader on the group granted `write` on a resource in it — `write` is Rel(contributor) and "
            + "a reader is not one"
        );

        (await AllowedAsync(resource, Permissions.Delete, colleague)).ShouldBeFalse();

        // ── And through the manager, which is what the colleague actually calls ─────────────────
        var target = IsolationCatalog.Targets[0];
        var address = IsolationCluster.Address(target, ResourceName(subscription), Grant, subscription);
        var asColleague = IsolationCluster.Caller(Grant, colleague);

        var read = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = target.ApiVersion, Caller = asColleague },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue("the reader could not read the resource: " + read.Error?.Message);

        var write = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = asColleague
            },
            TestContext.Current.CancellationToken
        );

        write.IsFailure.ShouldBeTrue("a reader wrote a resource");

        // ⚠ 403 AND NOT 404. The reader CAN read this resource, so its existence is not news to
        // them and "forbidden" is both true and useful — docs/plan/07 § The enforcement seam. A
        // 404 here would be the manager telling somebody that a thing they just read does not exist.
        write.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    [Fact]
    public async Task ARepeatedGrantIsTheSameTupleAndReportsCreatedFalse() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a2");
        await SeedAsync(subscription);
        var repeat = await UserAsync("repeat");

        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, SubjectTypes.User, repeat)
        );

        var first = await Assign(assignment, Owner);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.GetValueOrThrow().Created.ShouldBeTrue();

        // ⚠ The name is the tuple, so there is nothing to conflict with and nothing to record —
        // RoleAssignmentName's remarks. What has to be true is that the second call is a success
        // that says it did nothing, which is the whole of what PUT promises a retrying client.
        var second = await Assign(assignment, Owner);
        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        second.GetValueOrThrow().Created.ShouldBeFalse();

        var subjects = await SubjectsOfAsync(ScopeId.Group(Grant, subscription, Group), Relations.Reader);
        subjects.Count(x => x.Id == repeat).ShouldBe(1, "two PUTs wrote two tuples for one assignment");
    }

    [Fact]
    public async Task AGetReadsBackWhatWasGrantedAndNothingInherited() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a3");
        var resource = await SeedAsync(subscription);
        var gwen = await UserAsync("gwen");

        var onGroup = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Contributor, SubjectTypes.User, gwen)
        );

        (await Assign(onGroup, Owner)).IsSuccess.ShouldBeTrue();

        var read = await cluster.Roles.ReadAsync(
            new() { Path = onGroup.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.GetValueOrThrow().Created.ShouldBeFalse("a read is never a create");
        read.GetValueOrThrow().Path.ShouldBe(onGroup.Path);

        // ⚠ THE INHERITED GRANT HAS NO TUPLE AT THE RESOURCE AND READS AS ABSENT THERE, while the
        // check on the resource says gwen may write. Both are true at once, and that is the whole
        // claim of docs/plan/07's third table row — an assignment is written once and inherited
        // everywhere below, so the by-name read at a lower scope must not invent a row. The
        // COLLECTION at the resource is where the inherited grant is visible, marked as such —
        // TheCollectionListsDirectAndInheritedAssignmentsEachUnderItsOwnAddress.
        (await AllowedAsync(resource, Permissions.Write, gwen)).ShouldBeTrue();

        var onResource = RoleAssignmentId.OnResource(
            IsolationCluster.Address(IsolationCatalog.Targets[0], ResourceName(subscription), Grant, subscription),
            new(Relations.Contributor, SubjectTypes.User, gwen)
        );

        var inherited = await cluster.Roles.ReadAsync(
            new() { Path = onResource.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

        inherited.IsFailure.ShouldBeTrue("an inherited grant was read back as a direct tuple on the resource");
        inherited.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task ARevokeRemovesAccessAndARepeatedRevokeIsStillASuccess() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a4");
        var resource = await SeedAsync(subscription);
        var rex = await UserAsync("rex");

        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, SubjectTypes.User, rex)
        );

        (await Assign(assignment, Owner)).IsSuccess.ShouldBeTrue();
        (await AllowedAsync(resource, Permissions.Read, rex)).ShouldBeTrue();

        var revoked = await cluster.Roles.RevokeAsync(
            new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

        revoked.IsSuccess.ShouldBeTrue(revoked.Error?.Message);

        // ⚠ Asked FullyConsistent, so this is the durable answer and not a cache the revoke should
        // have invalidated — CheckCacheInvalidationTests owns the cache claim.
        (await AllowedAsync(resource, Permissions.Read, rex)).ShouldBeFalse("the revoke left the grant in place");

        var read = await cluster.Roles.ReadAsync(
            new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // The goal of a revoke is the absence of the grant; a re-driven DELETE has reached it.
        (await cluster.Roles.RevokeAsync(
                new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();
    }

    // ── Who may grant ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AContributorCanReadTheScopeAndIsToldItMayNotAssign() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000b1");
        await SeedAsync(subscription);
        var carl = await UserAsync("carl");

        var group = ScopeId.Group(Grant, subscription, Group);

        (await Assign(RoleAssignmentId.OnScope(group, new(Relations.Contributor, SubjectTypes.User, carl)), Owner))
            .IsSuccess.ShouldBeTrue();

        // ⚠ A CONTRIBUTOR HOLDING assignRole WOULD BE A CONTRIBUTOR WHO CAN MAKE THEMSELVES AN
        // OWNER. `assignRole` is Rel(owner) & !Rel(suspended) — Azure's roleAssignments/write sits
        // in Owner and in no built-in role beneath it.
        var refused = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Owner, SubjectTypes.User, carl)),
            carl
        );

        refused.IsFailure.ShouldBeTrue("a contributor granted a role");

        // 403 and not 404: carl can READ the group, so its existence is not news to him.
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        (await SubjectsOfAsync(group, Relations.Owner)).ShouldNotContain(x => x.Id == carl);
    }

    [Fact]
    public async Task ACallerWithNoGrantIsToldTheScopeDoesNotExist() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000b2");
        await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);

        var refused = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, Nobody)),
            Nobody
        );

        refused.IsFailure.ShouldBeTrue("a caller with no grant granted themselves a role");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // ⚠ Byte-identical to the sentence a genuinely absent scope gets — the identity is the
        // property. Two messages would be the oracle the shared status code closed.
        refused.Error.Message.ShouldBe($"'{group.Path}' does not exist.");
    }

    [Fact]
    public async Task ASuspendedOwnerMayNotAssign() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000b3");
        await SeedAsync(subscription);
        var sue = await UserAsync("sue");
        var x1 = await UserAsync("x1");
        var x2 = await UserAsync("x2");

        var group = ScopeId.Group(Grant, subscription, Group);

        (await Assign(RoleAssignmentId.OnScope(group, new(Relations.Owner, SubjectTypes.User, sue)), Owner))
            .IsSuccess.ShouldBeTrue();

        (await Assign(RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, x1)), sue))
            .IsSuccess.ShouldBeTrue("an owner could not assign");

        // docs/plan/07 § Azure RBAC, expressed in it, row 4: a deny assignment is `#suspended`, and
        // it is the negation on `assignRole` that makes adding a tuple REMOVE access.
        var (type, id) = ReBacScopeAuthorizer.ObjectOf(group);
        await cluster.WriteTupleAsync(
            Grant,
            Authorization.Contracts.ObjectRef.Of(type, id),
            Relations.Suspended,
            SubjectRef.Of(SubjectTypes.User, sue)
        );

        var refused = await Assign(RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, x2)), sue);

        refused.IsFailure.ShouldBeTrue("a suspended owner assigned a role");
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed, "a suspended owner can still read");
    }

    // ── The decision: delete stays Rel(owner) ──────────────────────────────────────────────────

    /// <summary>
    ///     <b>Decided, not owed:</b> <c>delete</c> is <c>Rel(owner)</c>, so a Contributor cannot
    ///     delete, and that is a deliberate divergence from Azure's Contributor.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Issue #70 framed widening <c>delete</c> to <c>Rel(contributor)</c> as the decision
    ///         underneath #14, and the decision is <b>not to</b>. docs/plan/07 § Azure RBAC,
    ///         expressed in it records why: <c>purge</c> is defined in terms of <c>owner</c>, so an
    ///         owner-only <c>delete</c> is what keeps <c>purge</c> — the end of a recovery window,
    ///         the platform's one irreversible verb — unreachable to a contributor. Widening
    ///         <c>delete</c> alone would make a contributor able to park a resource that only an
    ///         owner could bring back, and widening both would hand the irreversible verb to the
    ///         role Azure gives it to only through a <c>notActions</c> row this schema cannot
    ///         express.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Driven through the real grant path</b>, which is what makes it a test of the
    ///         decision rather than of a fixture: the contributor tuple is written by the same
    ///         <c>PUT</c> a tenant would use, and the verbs are asked of a resource two hops below
    ///         it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AContributorCanWriteButNotDeleteByDecision() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000c1");
        var resource = await SeedAsync(subscription);
        var cora = await UserAsync("cora");

        (await Assign(
                RoleAssignmentId.OnScope(
                    ScopeId.Group(Grant, subscription, Group),
                    new(Relations.Contributor, SubjectTypes.User, cora)
                ),
                Owner
            )).IsSuccess.ShouldBeTrue();

        (await AllowedAsync(resource, Permissions.Write, cora)).ShouldBeTrue("a contributor may write");

        (await AllowedAsync(resource, Permissions.Delete, cora)).ShouldBeFalse(
            "⚠ IF THIS FAILS, `delete` HAS BEEN WIDENED TO Rel(contributor). That is a decision "
            + "docs/plan/07 § Azure RBAC, expressed in it records as taken the other way — owner-only "
            + "delete is what keeps `purge` unreachable to a contributor — so update that paragraph "
            + "and RoleAssignmentViewTests.ADenyAssignmentRemovesPurgeAndLeavesDeleteAndNoGrantSeparatesThem "
            + "before this line. Azure's Contributor CAN delete; this schema's cannot, on purpose."
        );

        (await AllowedAsync(resource, Permissions.Purge, cora)).ShouldBeFalse(
            "a contributor holds purge, which is the thing owner-only delete exists to prevent"
        );

        // And through the manager: the contributor can read, so the refusal is a 403.
        var target = IsolationCatalog.Targets[0];
        var address = IsolationCluster.Address(target, ResourceName(subscription), Grant, subscription);

        var refused = await cluster.Manager.DeleteAsync(
            new() {
                Path = address.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Delete,
                Caller = IsolationCluster.Caller(Grant, cora)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a contributor deleted a resource");
        refused.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
    }

    // ── The other scopes and the other principals ──────────────────────────────────────────────

    [Fact]
    public async Task AGroupPrincipalIsGrantedThroughItsMemberUserset() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000d1");
        var resource = await SeedAsync(subscription);
        var eng = await GroupAsync("eng");
        var gina = await UserAsync("gina");
        var notGina = await UserAsync("notgina");

        // docs/plan/07 § The model's third example: resourceGroup:prod#reader@group:eng#member.
        await cluster.WriteTupleAsync(
            Grant,
            Authorization.Contracts.ObjectRef.Of(ObjectTypes.Group, eng),
            Relations.Member,
            SubjectRef.Of(SubjectTypes.User, gina)
        );

        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, ObjectTypes.Group, eng)
        );

        (await Assign(assignment, Owner)).IsSuccess.ShouldBeTrue();

        // ⚠ The tuple's subject must be the USERSET group:eng#member and not the object group:eng —
        // an object subject is matched only against itself and would grant to no member.
        var tuple = ReBacRoleAssignmentStore.TupleOf(assignment).GetValueOrThrow();
        tuple.Subject.IsUserset.ShouldBeTrue();
        tuple.Subject.Relation.ShouldBe(Relations.Member);

        (await AllowedAsync(resource, Permissions.Read, gina)).ShouldBeTrue(
            "a member of the granted group cannot read"
        );
        (await AllowedAsync(resource, Permissions.Read, notGina)).ShouldBeFalse();
    }

    [Fact]
    public async Task AResourceScopedAssignmentLandsOnTheResourceAndNotItsGroup() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000d2");
        var resource = await SeedAsync(subscription);
        var rob = await UserAsync("rob");

        var target = IsolationCatalog.Targets[0];
        var address = IsolationCluster.Address(target, ResourceName(subscription), Grant, subscription);

        var assignment = RoleAssignmentId.OnResource(address, new(Relations.Reader, SubjectTypes.User, rob));

        var granted = await Assign(assignment, Owner);
        granted.IsSuccess.ShouldBeTrue(granted.Error?.Message);

        // ⚠ THE TUPLE IS ON resource:{guid:N}, RESOLVED THROUGH THE INDEX, AND NOT ON THE ADDRESS.
        // A parsed path carries Guid.Empty; a store handed the unresolved id would write a tuple on
        // resource:0000… and this line would be false while the PUT reported success.
        (await AllowedAsync(resource, Permissions.Read, rob)).ShouldBeTrue(
            "the resource-scoped grant did not reach the resource"
        );

        var (groupType, groupId) = ReBacScopeAuthorizer.ObjectOf(ScopeId.Group(Grant, subscription, Group));
        (await AllowedOnAsync(groupType, groupId, Permissions.Read, rob)).ShouldBeFalse(
            "a grant on one resource leaked up to its group"
        );

        // And the by-name read finds it at the resource.
        var read = await cluster.Roles.ReadAsync(
            new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
    }

    [Fact]
    public async Task AnAssignmentOnAResourceThatDoesNotExistIsTheCanonicalNotFound() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000d3");
        await SeedAsync(subscription);

        var target = IsolationCatalog.Targets[0];
        var missing = IsolationCluster.Address(target, "never-made", Grant, subscription);

        var refused = await Assign(
            RoleAssignmentId.OnResource(missing, new(Relations.Reader, SubjectTypes.User, "x")),
            Owner
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Message.ShouldBe($"'{missing.Path}' does not exist.");
    }

    [Fact]
    public async Task AnAssignmentOnASubscriptionThatDoesNotExistIsRefusedEvenForTheTenantOwner() {
        await SeedTenantAsync();

        var ghost = ScopeId.Subscription(Grant, Guid.Parse("88888888-0000-4000-8000-0000000000e1"));

        // ⚠ The check alone would not catch this if a parent edge had been left behind by a failed
        // create — RoleAssignmentService.ResolveAsync reads the scope's own grain for exactly that
        // reason.
        var refused = await Assign(
            RoleAssignmentId.OnScope(ghost, new(Relations.Reader, SubjectTypes.User, "x")),
            Owner
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The principal must exist (issue #86) ───────────────────────────────────────────────────

    [Fact]
    public async Task AnAssignmentToAPrincipalTheDirectoryDoesNotHoldIsRefusedAndNoTupleIsWritten() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000e2");
        await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);

        // A well-formed subject id — the N form of a GUID, exactly what a token carries — for a
        // user nobody created. Until #86 this wrote resourceGroup:…#reader@user:{ghost} and reported
        // success; nothing could ever authenticate as that subject, and nothing could see the tuple.
        var ghost = IsolationCluster.SubjectId(Guid.Parse("88888888-0000-4000-8000-00000000dead"));

        var refused = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, ghost)),
            Owner
        );

        refused.IsFailure.ShouldBeTrue("a role was granted to a principal that does not exist");
        refused.Error!.Code.ShouldBe(
            ErrorCode.InvalidResourceId,
            "the refusal is a 400 naming the principal, not a 404 on the scope"
        );
        refused.Error.Message.ShouldContain(ghost);
        refused.Error.Message.ShouldContain("not a principal");

        (await SubjectsOfAsync(group, Relations.Reader)).ShouldNotContain(
            x => x.Id == ghost,
            "the tuple was written despite the refusal"
        );

        // ⚠ And a name that is not even a GUID — the shape every principal in this suite had before
        // #86 — is refused the same way, not folded into one. Five spellings of one principal would
        // be five assignments.
        var named = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, "alice")),
            Owner
        );
        named.IsFailure.ShouldBeTrue();
        named.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    [Fact]
    public async Task AnAssignmentToAnotherTenantsPrincipalIsRefused() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000e3");
        await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);

        // ⚠ A REAL USER, IN THE WRONG TENANT. docs/plan/11 § Sign-up and tenant creation: a user
        // belongs to exactly one tenant, so the victim's user is not a principal of this tenant even
        // though the grain exists. What refuses it is ForTenant on the directory's lookup and
        // nothing else — the same mechanism that keeps every other cross-tenant read a 404.
        var stranger = await cluster.CreateUserAsync(
            IsolationCluster.Victim,
            Guid.Parse("88888888-0000-4000-8000-00000000be11")
        );
        var strangersGroup = await cluster.CreateGroupAsync(
            IsolationCluster.Victim,
            Guid.Parse("88888888-0000-4000-8000-00000000be12")
        );

        var refusedUser = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, stranger)),
            Owner
        );
        refusedUser.IsFailure.ShouldBeTrue("a user of another tenant was granted a role in this one");
        refusedUser.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
        refusedUser.Error.Message.ShouldContain("another tenant");

        var refusedGroup = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, ObjectTypes.Group, strangersGroup)),
            Owner
        );
        refusedGroup.IsFailure.ShouldBeTrue("a group of another tenant was granted a role in this one");
        refusedGroup.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);

        (await SubjectsOfAsync(group, Relations.Reader)).ShouldNotContain(x => x.Id == stranger
            || x.Id == strangersGroup
        );

        // The same user, created in THIS tenant under the same GUID, is a different directory
        // object and can be granted to — which is what "two user objects with two GUIDs" means when
        // the GUIDs happen to coincide.
        var local = await cluster.CreateUserAsync(Grant, Guid.Parse("88888888-0000-4000-8000-00000000be11"));
        (await Assign(RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, local)), Owner))
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TheDirectoryIsAskedOnlyAfterTheCheckSoAnUnknownPrincipalIsNoOracle() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000e4");
        await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);
        var ghost = IsolationCluster.SubjectId(Guid.Parse("88888888-0000-4000-8000-00000000dea1"));
        var real = await UserAsync("oracle-target");

        // ⚠ A caller with no grant gets the SAME canonical 404 whether the principal exists or not.
        // If the directory were asked before the check, the difference between "does not exist"
        // and "is not a principal" would tell a stranger which GUIDs are users of this tenant.
        var unknown = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, ghost)),
            Nobody
        );
        var known = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, real)),
            Nobody
        );

        unknown.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        known.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        unknown.Error.Message.ShouldBe(
            known.Error.Message,
            "two refusals for a stranger differed by whether the principal exists"
        );
    }

    [Fact]
    public async Task ADeprovisionedUserCannotBeGrantedToButAnExistingGrantCanStillBeRevoked() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000e5");
        var resource = await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);
        var leaverId = Guid.Parse("88888888-0000-4000-8000-00000000b1e5");
        var leaver = await cluster.CreateUserAsync(Grant, leaverId);

        var assignment = RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, leaver));
        (await Assign(assignment, Owner)).IsSuccess.ShouldBeTrue();
        (await AllowedAsync(resource, Permissions.Read, leaver)).ShouldBeTrue();

        var deprovisioned = await cluster.For(Grant)
            .GetGrain<IUserGrain>(GrainKeys.User(leaverId))
            .SetStatusAsync(UserStatus.Deprovisioned);
        deprovisioned.IsSuccess.ShouldBeTrue(deprovisioned.Error?.Message);

        // A new grant is refused — the object is terminal, as a deleted principal is to Azure.
        var again = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Contributor, SubjectTypes.User, leaver)),
            Owner
        );
        again.IsFailure.ShouldBeTrue("a deprovisioned user was granted a role");
        again.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);

        // ⚠ But the grant that already exists can still be revoked. A DELETE that asked the
        // directory would leave a tuple nobody can remove — RoleAssignmentService's remarks.
        var revoked = await cluster.Roles.RevokeAsync(
            new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

        revoked.IsSuccess.ShouldBeTrue(
            "a grant to a deprovisioned user could not be revoked: " + revoked.Error?.Message
        );
        (await AllowedAsync(resource, Permissions.Read, leaver)).ShouldBeFalse();
    }

    [Fact]
    public async Task EveryPrincipalTypeIsLookedUpInItsOwnGrain() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000e6");
        var resource = await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);

        var servicePrincipal = await cluster.CreateServicePrincipalAsync(
            Grant,
            Guid.Parse("88888888-0000-4000-8000-00000000e6a1")
        );
        var managedIdentity = await cluster.CreateManagedIdentityAsync(
            Grant,
            Guid.Parse("88888888-0000-4000-8000-00000000e6a2")
        );
        var team = await cluster.CreateGroupAsync(Grant, Guid.Parse("88888888-0000-4000-8000-00000000e6a3"));

        foreach (var (type, id) in new[] {
                     (SubjectTypes.ServicePrincipal, servicePrincipal), (SubjectTypes.ManagedIdentity, managedIdentity),
                     (ObjectTypes.Group, team)
                 }) {
            var granted = await Assign(RoleAssignmentId.OnScope(group, new(Relations.Reader, type, id)), Owner);
            granted.IsSuccess.ShouldBeTrue($"a real {type} could not be granted to: " + granted.Error?.Message);
        }

        // ⚠ The same GUID under a DIFFERENT type is a different grain that was never created. A
        // directory that looked the id up without its type would say the service principal's GUID
        // is a user, and the tuple would name a subject no token ever presents.
        var crossed = await Assign(
            RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, servicePrincipal)),
            Owner
        );
        crossed.IsFailure.ShouldBeTrue("a service principal's GUID was accepted as a user");
        crossed.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);

        // And the service principal's grant is one the evaluator honours.
        var check = cluster.For(Grant)
            .GetGrain<ICheckGrain>(
                GrainKeys.CheckCache(ObjectTypes.Resource, resource.ToString("N", CultureInfo.InvariantCulture))
            );
        var allowed = await check.CheckAsync(
            Permissions.Read,
            SubjectRef.Of(SubjectTypes.ServicePrincipal, servicePrincipal),
            Consistency.FullyConsistent
        );
        allowed.GetValueOrThrow().Allowed.ShouldBeTrue("the service principal's grant did not reach the resource");
    }

    // ── The collection (issue #86) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCollectionListsDirectAndInheritedAssignmentsEachUnderItsOwnAddress() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000f5");
        await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);
        var target = IsolationCatalog.Targets[0];
        var address = IsolationCluster.Address(target, ResourceName(subscription), Grant, subscription);

        var onGroup = await UserAsync("listed-on-group");
        var onResource = await UserAsync("listed-on-resource");

        var groupGrant = RoleAssignmentId.OnScope(group, new(Relations.Contributor, SubjectTypes.User, onGroup));
        var resourceGrant = RoleAssignmentId.OnResource(address, new(Relations.Reader, SubjectTypes.User, onResource));
        (await Assign(groupGrant, Owner)).IsSuccess.ShouldBeTrue();
        (await Assign(resourceGrant, Owner)).IsSuccess.ShouldBeTrue();

        var page = await ListAsync(RoleAssignmentCollectionId.OnResource(address), Owner);
        page.IsSuccess.ShouldBeTrue("the owner could not list role assignments at a resource: " + page.Error?.Message);
        page.GetValueOrThrow().HasMore.ShouldBeFalse();

        var rows = page.GetValueOrThrow().Assignments;

        // ── The direct row: written on the resource, addressed at the resource ─────────────────
        var direct = rows.Where(x => x.PrincipalId == onResource).ShouldHaveSingleItem();
        direct.Inherited.ShouldBeFalse();
        direct.Scope.ShouldBe(address.Path);
        direct.Path.ShouldBe(resourceGrant.Path);
        direct.RoleDefinitionId.ShouldBe(Relations.Reader);

        // ── The inherited rows: the group's contributor and the tenant's owner, each marked and
        //    each carrying the ANCESTOR's address — the one a GET answers for it ─────────────────
        var fromGroup = rows.Where(x => x.PrincipalId == onGroup).ShouldHaveSingleItem();
        fromGroup.Inherited.ShouldBeTrue("a grant on the group was listed at the resource as if written there");
        fromGroup.Scope.ShouldBe(group.Path);
        fromGroup.Path.ShouldBe(groupGrant.Path, "the inherited row's address is not the tuple's own");
        fromGroup.RoleDefinitionId.ShouldBe(Relations.Contributor);

        var fromTenant = rows.Where(static x => x.PrincipalId == Owner).ShouldHaveSingleItem();
        fromTenant.Inherited.ShouldBeTrue();
        fromTenant.Scope.ShouldBe(ScopeId.Tenant(Grant).Path);
        fromTenant.RoleDefinitionId.ShouldBe(Relations.Owner);

        // ⚠ THE ADDRESS IS REAL: a by-name GET on the inherited row's own id finds the tuple, and a
        // by-name GET on the same name at the RESOURCE does not — which is the whole reason the row
        // carries the ancestor's address rather than this scope's.
        (await cluster.Roles.ReadAsync(
                new() { Path = fromGroup.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();

        (await cluster.Roles.ReadAsync(
                new() {
                    Path = RoleAssignmentId.OnResource(address, groupGrant.Name).Path,
                    Caller = IsolationCluster.Caller(Grant, Owner)
                },
                TestContext.Current.CancellationToken
            )).IsFailure.ShouldBeTrue();

        // And the group's own listing sees its direct grant as direct, the tenant's as inherited,
        // and nothing from the resource below it — inheritance runs one way.
        var atGroup = (await ListAsync(RoleAssignmentCollectionId.OnScope(group), Owner)).GetValueOrThrow().Assignments;
        atGroup.Where(x => x.PrincipalId == onGroup).ShouldHaveSingleItem().Inherited.ShouldBeFalse();
        atGroup.Where(static x => x.PrincipalId == Owner).ShouldHaveSingleItem().Inherited.ShouldBeTrue();
        atGroup.ShouldNotContain(
            x => x.PrincipalId == onResource,
            "a grant on a resource leaked up into its group's listing"
        );
    }

    [Fact]
    public async Task TheCollectionAtAChildResourceListsWhatItsParentResourceGrants() {
        // ⚠ THE ONE REVERSE MAPPING THAT NEEDS A LOOKUP. A scope object's id is the scope spelled
        // forwards; a resource object's id is a GUID, and the store reads the parent's own grain
        // for its path. A child's `parent` edge names its parent RESOURCE (IsolationCatalog's
        // remarks), so a grant on the parent is what this listing must show, under the parent's
        // address.
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000f6");
        await SeedAsync(subscription);

        var parentTarget = IsolationCatalog.Targets[1];
        var childTarget = IsolationCatalog.Targets[2];
        childTarget.Ancestors.ShouldHaveSingleItem()
            .ShouldBe(parentTarget, "the catalogue moved and this test names the wrong pair");

        var parentName = IsolationTarget.AncestorName(0);
        await cluster.CreateAsync(parentTarget, parentName, Grant, subscription, Owner);
        await cluster.CreateAsync(childTarget, "kid", Grant, subscription, Owner);

        var parent = IsolationCluster.Address(parentTarget, parentName, Grant, subscription);
        var child = IsolationCluster.Address(childTarget, "kid", Grant, subscription);

        var onParent = await UserAsync("listed-on-parent");
        var parentGrant = RoleAssignmentId.OnResource(parent, new(Relations.Reader, SubjectTypes.User, onParent));
        (await Assign(parentGrant, Owner)).IsSuccess.ShouldBeTrue();

        var page = await ListAsync(RoleAssignmentCollectionId.OnResource(child), Owner);
        page.IsSuccess.ShouldBeTrue(page.Error?.Message);

        var fromParent = page.GetValueOrThrow()
            .Assignments.Where(x => x.PrincipalId == onParent)
            .ShouldHaveSingleItem();
        fromParent.Inherited.ShouldBeTrue();
        fromParent.Scope.ShouldBe(parent.Path);
        fromParent.Path.ShouldBe(parentGrant.Path);
    }

    [Fact]
    public async Task TheCollectionIsPagedByAddressAndAPageIsNeverSilentlyShort() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000f7");
        await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);

        for (var i = 0; i < 4; i++) {
            var user = await UserAsync("paged-" + i.ToString(CultureInfo.InvariantCulture));
            (await Assign(RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, user)), Owner))
                .IsSuccess.ShouldBeTrue();
        }

        var everything = (await ListAsync(RoleAssignmentCollectionId.OnScope(group), Owner)).GetValueOrThrow();
        everything.Assignments.Length.ShouldBeGreaterThanOrEqualTo(5, "four readers and the inherited tenant owner");
        everything.Assignments.Select(static x => x.Path)
            .ShouldBe(
                everything.Assignments.Select(static x => x.Path).Order(StringComparer.Ordinal),
                "the listing is not in address order"
            );

        List<RoleAssignmentSnapshot> walked = [];
        var continuation = "";
        var pages = 0;

        do {
            var page = await cluster.Roles.ListAsync(
                new() {
                    Path = RoleAssignmentCollectionId.OnScope(group).Path,
                    Caller = IsolationCluster.Caller(Grant, Owner),
                    Top = 2,
                    Continuation = continuation
                },
                TestContext.Current.CancellationToken
            );

            page.IsSuccess.ShouldBeTrue(page.Error?.Message);
            var value = page.GetValueOrThrow();

            // ⚠ Every page but the last is exactly $top rows. There is no per-row filter to make a
            // page short for a reason the caller is not told — RoleAssignmentPage's remarks.
            if (value.HasMore) {
                value.Assignments.Length.ShouldBe(2);
                value.Continuation.ShouldBe(value.Assignments[^1].Path, "the token is the last address served");
            }

            walked.AddRange(value.Assignments);
            continuation = value.Continuation;
            pages++;
        } while (continuation.Length > 0 && pages < 10);

        walked.Select(static x => x.Path)
            .ShouldBe(everything.Assignments.Select(static x => x.Path), "paging skipped or repeated a row");
    }

    [Fact]
    public async Task TheCollectionIsReadAuthorizedOnTheScopeAndAStrangerGetsTheCanonicalNotFound() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000f8");
        var resource = await SeedAsync(subscription);

        var group = ScopeId.Group(Grant, subscription, Group);
        var reader = await UserAsync("collection-reader");
        (await Assign(RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, reader)), Owner))
            .IsSuccess.ShouldBeTrue();

        // A reader holds `read` on the group and therefore sees everything assigned there — Azure's
        // roleAssignments/read sits in Reader for the same reason.
        var asReader = await ListAsync(RoleAssignmentCollectionId.OnScope(group), reader);
        asReader.IsSuccess.ShouldBeTrue(
            "a reader could not list the assignments at a scope they can read: " + asReader.Error?.Message
        );
        asReader.GetValueOrThrow().Assignments.ShouldContain(x => x.PrincipalId == Owner && x.Inherited);
        asReader.GetValueOrThrow().Assignments.ShouldContain(x => x.PrincipalId == reader && !x.Inherited);

        // And at a resource in the group, through the inherited read.
        var address = IsolationCluster.Address(
            IsolationCatalog.Targets[0],
            ResourceName(subscription),
            Grant,
            subscription
        );
        (await ListAsync(RoleAssignmentCollectionId.OnResource(address), reader)).IsSuccess.ShouldBeTrue();
        _ = resource;

        // A stranger gets the sentence a scope that does not exist gets — byte-identical.
        var stranger = await ListAsync(RoleAssignmentCollectionId.OnScope(group), Nobody);
        stranger.IsFailure.ShouldBeTrue("a caller with no grant listed the assignments at a scope");
        stranger.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        stranger.Error.Message.ShouldBe($"'{group.Path}' does not exist.");

        // And a scope that really does not exist gets the same one.
        var ghost = ScopeId.Group(Grant, subscription, "never-made");
        var missing = await ListAsync(RoleAssignmentCollectionId.OnScope(ghost), Owner);
        missing.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        missing.Error.Message.ShouldBe($"'{ghost.Path}' does not exist.");
    }

    [Fact]
    public async Task OneTenantCannotListAnothersAssignments() {
        var victimGroup = ScopeId.Group(
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.Group
        );

        var refused = await cluster.Roles.ListAsync(
            new() {
                Path = RoleAssignmentCollectionId.OnScope(victimGroup).Path,
                Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("the attacker listed the victim's role assignments");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The tenant boundary ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OneTenantCannotAssignARoleInsideAnother() {
        var victimGroup = ScopeId.Group(
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.Group
        );

        var refused = await cluster.Roles.AssignAsync(
            new() {
                Path = RoleAssignmentId.OnScope(
                    victimGroup,
                    new(Relations.Owner, SubjectTypes.User, IsolationCluster.AttackerUser)
                ).Path,
                Caller = IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("the attacker granted themselves owner on the victim's group");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        var (type, id) = ReBacScopeAuthorizer.ObjectOf(victimGroup);
        var owners = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(type, id))
            .ReadDurableAsync();

        owners.GetValueOrThrow().Subjects(Relations.Owner).ShouldNotContain(x => x.Id == IsolationCluster.AttackerUser);
    }

    // ── The address and the body ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABodyThatDisagreesWithTheAddressIsRefusedBeforeAnyCheck() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000f1");
        await SeedAsync(subscription);
        var bea = await UserAsync("bea");

        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, SubjectTypes.User, bea)
        );

        var refused = await cluster.Roles.AssignAsync(
            new() {
                Path = assignment.Path,
                Body = $$$"""{"{{{RoleAssignmentBodyProperties.RoleDefinitionId}}}":"owner"}""",
                Caller = IsolationCluster.Caller(Grant, Owner)
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue("a body naming a different role was accepted");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("owner");
        refused.Error.Message.ShouldContain("reader");

        (await SubjectsOfAsync(ScopeId.Group(Grant, subscription, Group), Relations.Reader)).ShouldNotContain(x => x.Id
            == bea
        );
        (await SubjectsOfAsync(ScopeId.Group(Grant, subscription, Group), Relations.Owner)).ShouldNotContain(x => x.Id
            == bea
        );
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("parent")]
    [InlineData("member")]
    [InlineData("delete")]
    public async Task OnlyTheThreeRolesCanBeAssigned(string relation) {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000f2");
        await SeedAsync(subscription);

        // ⚠ `suspended`, `parent` and `member` are relations the tuple store would accept; `delete`
        // is a permission it would refuse. All four are a 400 here, before any grain is touched,
        // because none of them is a role assignment wearing its address.
        var refused = await Assign(
            RoleAssignmentId.OnScope(ScopeId.Group(Grant, subscription, Group), new(relation, SubjectTypes.User, "x")),
            Owner
        );

        refused.IsFailure.ShouldBeTrue($"'{relation}' was assigned as a role");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidResourceId);
    }

    [Fact]
    public void EveryGrantableRoleIsARelationTheSchemaRewritesOnEveryScopeType() {
        // ⚠ THE SAME KIND OF ASSERTION ScopeCreationTests MAKES FOR `parent`: the strings this path
        // writes must be relations — not permissions, not missing — on every object type an
        // assignment can name, or a PUT would succeed against a tuple nothing evaluates.
        var schema = CyberCloudSchema.Instance;

        foreach (var type in new[] {
                     ObjectTypes.Tenant, ObjectTypes.Subscription, ObjectTypes.ResourceGroup, ObjectTypes.Resource
                 }) {
            var defined = schema.Type(type).ShouldNotBeNull($"'{type}' is not in the schema");

            foreach (var role in RoleAssignmentService.GrantableRoles) {
                var member = defined.Member(role).ShouldNotBeNull($"'{type}' declares no '{role}'");
                member.IsPermission.ShouldBeFalse(
                    $"'{role}' is a permission on '{type}', and a tuple on it grants nothing"
                );
            }
        }
    }

    // ── Just-in-time roles (issue #49) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AJustInTimeGrantReadsBackItsExpiryAndEndsOnItsOwnWithNoRevoke() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a5");
        var resource = await SeedAsync(subscription);
        var jane = await UserAsync("jane");
        var group = ScopeId.Group(Grant, subscription, Group);
        var assignment = RoleAssignmentId.OnScope(group, new(Relations.Reader, SubjectTypes.User, jane));
        var expiresOn = cluster.Clock.UtcNow.AddHours(1);

        try {
            // ── The grant: the same PUT as any other, with the one property the address lacks ──
            var granted = await AssignWithBody(
                assignment,
                $$$"""{"{{{RoleAssignmentBodyProperties.ExpiresOn}}}":"{{{expiresOn.ToString("O", CultureInfo.InvariantCulture)}}}"}"""
            );

            granted.IsSuccess.ShouldBeTrue(granted.Error?.Message);
            granted.GetValueOrThrow().Created.ShouldBeTrue();
            granted.GetValueOrThrow().ExpiresOn.ShouldBe(expiresOn);

            var read = await ReadAsync(assignment);
            read.IsSuccess.ShouldBeTrue(read.Error?.Message);
            read.GetValueOrThrow().ExpiresOn.ShouldBe(expiresOn, "a GET must say when the grant ends");

            (await ListAsync(RoleAssignmentCollectionId.OnScope(group), Owner)).GetValueOrThrow()
                .Assignments.ShouldContain(
                    x => x.Path == assignment.Path && x.ExpiresOn == expiresOn,
                    "the collection must say when the grant ends"
                );

            // Two hops below the tuple, as the M1 exit story asks — a time-bounded Reader reads.
            (await AllowedAsync(resource, Permissions.Read, jane)).ShouldBeTrue();

            // ── The hour passes. Nobody revokes. ──────────────────────────────────────────────
            cluster.Clock.Advance(TimeSpan.FromHours(1));

            (await AllowedAsync(resource, Permissions.Read, jane)).ShouldBeFalse(
                "a just-in-time Reader still reads after the grant's expiry"
            );

            var gone = await ReadAsync(assignment);
            gone.IsFailure.ShouldBeTrue("an expired assignment still reads back");
            gone.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

            (await ListAsync(RoleAssignmentCollectionId.OnScope(group), Owner)).GetValueOrThrow()
                .Assignments.ShouldNotContain(x => x.Path == assignment.Path, "an expired assignment is still listed");

            // A revoke of an expired grant is the absence it asks for — a success, as ever.
            (await cluster.Roles.RevokeAsync(
                    new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
                    TestContext.Current.CancellationToken
                )).IsSuccess.ShouldBeTrue();
        } finally {
            cluster.Clock.Reset();
        }
    }

    [Fact]
    public async Task APutWithoutExpiresOnMakesAJustInTimeGrantPermanent() {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a6");
        var resource = await SeedAsync(subscription);
        var perry = await UserAsync("perry");
        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, SubjectTypes.User, perry)
        );

        try {
            var expiresOn = cluster.Clock.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture);
            (await AssignWithBody(assignment, $$$"""{"{{{RoleAssignmentBodyProperties.ExpiresOn}}}":"{{{expiresOn}}}"}"""))
                .IsSuccess.ShouldBeTrue();

            // ⚠ A PUT states the whole assignment. One that kept the old expiry because the body
            // didn't mention it would end a grant at a time this caller never sent.
            var permanent = await Assign(assignment, Owner);
            permanent.IsSuccess.ShouldBeTrue(permanent.Error?.Message);
            permanent.GetValueOrThrow().Created.ShouldBeFalse("the tuple was already there");
            permanent.GetValueOrThrow().ExpiresOn.ShouldBeNull();

            cluster.Clock.Advance(TimeSpan.FromDays(2));

            (await AllowedAsync(resource, Permissions.Read, perry)).ShouldBeTrue(
                "a PUT without expiresOn left the earlier expiry in place"
            );
            (await ReadAsync(assignment)).GetValueOrThrow().ExpiresOn.ShouldBeNull();
        } finally {
            cluster.Clock.Reset();
        }
    }

    [Theory]
    [InlineData("past")]
    [InlineData("now")]
    [InlineData("no-offset")]
    [InlineData("number")]
    [InlineData("not-a-date")]
    public async Task AnExpiresOnThatIsNotAFutureInstantWithAnOffsetIsRefusedAndNothingIsGranted(string shape) {
        var subscription = Guid.Parse("88888888-0000-4000-8000-0000000000a7");
        await SeedAsync(subscription);
        var nora = await UserAsync("nora-" + shape);
        var assignment = RoleAssignmentId.OnScope(
            ScopeId.Group(Grant, subscription, Group),
            new(Relations.Reader, SubjectTypes.User, nora)
        );

        var now = cluster.Clock.UtcNow;
        var value = shape switch {
            "past" => $"\"{now.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture)}\"",
            "now" => $"\"{now.ToString("O", CultureInfo.InvariantCulture)}\"",
            "no-offset" => $"\"{now.AddHours(1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\"",
            "number" => "1758628800",
            _ => "\"tomorrow\""
        };

        var refused = await AssignWithBody(
            assignment,
            $$$"""{"{{{RoleAssignmentBodyProperties.ExpiresOn}}}":{{{value}}}}"""
        );

        refused.IsFailure.ShouldBeTrue($"an expiresOn of {value} was accepted");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        var subjects = await SubjectsOfAsync(ScopeId.Group(Grant, subscription, Group), Relations.Reader);
        subjects.ShouldNotContain(x => x.Id == nora, "a refused grant wrote its tuple anyway");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    Task<Result<RoleAssignmentSnapshot>> AssignWithBody(RoleAssignmentId assignment, string body) =>
        cluster.Roles.AssignAsync(
            new() { Path = assignment.Path, Body = body, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

    Task<Result<RoleAssignmentSnapshot>> ReadAsync(RoleAssignmentId assignment) =>
        cluster.Roles.ReadAsync(
            new() { Path = assignment.Path, Caller = IsolationCluster.Caller(Grant, Owner) },
            TestContext.Current.CancellationToken
        );

    Task<Result<RoleAssignmentSnapshot>> Assign(RoleAssignmentId assignment, string caller) =>
        cluster.Roles.AssignAsync(
            new() { Path = assignment.Path, Body = "{}", Caller = IsolationCluster.Caller(Grant, caller) },
            TestContext.Current.CancellationToken
        );

    Task<Result<RoleAssignmentPage>> ListAsync(RoleAssignmentCollectionId collection, string caller) =>
        cluster.Roles.ListAsync(
            new() { Path = collection.Path, Caller = IsolationCluster.Caller(Grant, caller) },
            TestContext.Current.CancellationToken
        );

    /// <summary>
    ///     A user of the <see cref="Grant" /> tenant, created through the real grain, named by a
    ///     label so a test reads as prose. The GUID is derived from the label, so two tests naming
    ///     the same person get the same principal.
    /// </summary>
    Task<string> UserAsync(string label) => cluster.CreateUserAsync(Grant, GuidFor("user:" + label));

    /// <summary>A group of the <see cref="Grant" /> tenant, by label — see <see cref="UserAsync" />.</summary>
    Task<string> GroupAsync(string label) => cluster.CreateGroupAsync(Grant, GuidFor("group:" + label));

    static Guid GuidFor(string label) {
        // A stable, label-derived GUID: the first sixteen bytes of a SHA-256, with the version and
        // variant nibbles set so the value is a well-formed v4-shaped GUID rather than an arbitrary
        // one. Which GUID does not matter; that it is the same GUID on every call for one label does.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(label));
        var bytes = digest[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new(bytes);
    }

    Task<bool> AllowedAsync(Guid resource, string permission, string user) =>
        AllowedOnAsync(ObjectTypes.Resource, resource.ToString("N", CultureInfo.InvariantCulture), permission, user);

    async Task<bool> AllowedOnAsync(string type, string id, string permission, string user) {
        var check = cluster.For(Grant).GetGrain<ICheckGrain>(GrainKeys.CheckCache(type, id));

        var result = await check.CheckAsync(
            permission,
            SubjectRef.Of(SubjectTypes.User, user),
            Consistency.FullyConsistent
        );

        result.IsSuccess.ShouldBeTrue($"the check on {type}:{id} did not answer: " + result.Error?.Message);

        return result.GetValueOrThrow().Allowed;
    }

    async Task<IReadOnlyList<SubjectRef>> SubjectsOfAsync(ScopeId scope, string relation) {
        var (type, id) = ReBacScopeAuthorizer.ObjectOf(scope);

        var snapshot = await cluster.For(Grant)
            .GetGrain<IObjectRelationsGrain>(GrainKeys.ObjectRelations(type, id))
            .ReadDurableAsync();

        return snapshot.IsSuccess ? snapshot.GetValueOrThrow().Subjects(relation) : [];
    }

    static string ResourceName(Guid subscription) =>
        "granted-" + subscription.ToString("N", CultureInfo.InvariantCulture)[^2..];

    /// <summary>
    ///     The tenant, once — created and owned by <see cref="Owner" /> and nothing else.
    /// </summary>
    async Task SeedTenantAsync() {
        await Seeding.WaitAsync(TestContext.Current.CancellationToken);

        try {
            if (Seeded.ContainsKey(Guid.Empty)) {
                return;
            }

            var created = await cluster.For(Grant)
                .GetGrain<ITenantGrain>(GrainKeys.Tenant(Grant))
                .CreateAsync("grant-tenant", "Grant Tenant", "eu-west-1");

            created.IsSuccess.ShouldBeTrue(created.Error?.Message);

            await cluster.GrantTenantOwnerAsync(Grant, Owner);
            Seeded[Guid.Empty] = Guid.Empty;
        } finally {
            Seeding.Release();
        }
    }

    /// <summary>
    ///     A subscription, its <c>prod</c> group and one resource in it, all created through the
    ///     real paths by the tenant owner — so every edge the grant will be rewritten through was
    ///     written by the platform and not by this test.
    /// </summary>
    /// <returns>The resource's GUID — the object the assertions are asked on.</returns>
    async Task<Guid> SeedAsync(Guid subscription) {
        await SeedTenantAsync();

        await Seeding.WaitAsync(TestContext.Current.CancellationToken);

        try {
            if (Seeded.TryGetValue(subscription, out var existing)) {
                return existing;
            }

            var owner = IsolationCluster.Caller(Grant, Owner);

            var madeSubscription = await cluster.Scopes.CreateAsync(
                new() {
                    Path = ScopeId.Subscription(Grant, subscription).Path,
                    Body = """{"displayName":"Grant"}""",
                    Caller = owner
                },
                TestContext.Current.CancellationToken
            );

            madeSubscription.IsSuccess.ShouldBeTrue(madeSubscription.Error?.Message);

            var madeGroup = await cluster.Scopes.CreateAsync(
                new() {
                    Path = ScopeId.Group(Grant, subscription, Group).Path,
                    Body = """{"location":"eu-west-1"}""",
                    Caller = owner
                },
                TestContext.Current.CancellationToken
            );

            madeGroup.IsSuccess.ShouldBeTrue(madeGroup.Error?.Message);

            var resource = await cluster.CreateAsync(
                IsolationCatalog.Targets[0],
                ResourceName(subscription),
                Grant,
                subscription,
                Owner
            );

            Seeded[subscription] = resource;
            return resource;
        } finally {
            Seeding.Release();
        }
    }
}
