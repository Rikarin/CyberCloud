using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     Server-side apply against a real k3s — ADR-013's payoff, and the failure class that only a
///     real API server can demonstrate.
/// </summary>
/// <remarks>
///     <para>
///         ADR-013:
///         <i>
///             "Server-side apply, always, with a stable field manager per provider. That
///             gives us conflict detection for free: if a tenant hand-edits a field we own, the next
///             apply reports a conflict rather than silently reverting, and <b>that</b> becomes a drift
///             event with a name."
///         </i>
///     </para>
///     <para>
///         The sequence below is exactly that sentence: apply as our field manager, mutate the field
///         as a different one, apply again, and assert (a) a conflict is reported, (b) it surfaces as
///         a drift event naming the field and the rival manager, and (c) the field was <b>not</b>
///         force-overwritten.
///     </para>
/// </remarks>
[Collection(K3sSuite.Name)]
public sealed class ServerSideApplyTests(K3sFixture k3s) {
    const string OurManager = "cybercloud/cybercloud.dbforpostgresql";
    const string RivalManager = "tenant-kubectl";

    static readonly GroupVersionKind Deployments =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    static readonly GroupVersionKind ConfigMaps =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    // ── The basics ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnApplyCreatesTheObjectWithTheSevenLabels() {
        var token = TestContext.Current.CancellationToken;
        var outcome = (await k3s.Api.ApplyAsync(Command("ssa-create", 1), token)).GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Created);
        outcome.ResourceVersion.ShouldNotBeEmpty();

        // The labels are on the real object, read back from the real API server — which is the only
        // way to know they survived admission rather than merely being in our JSON.
        foreach (var key in KubeLabels.Mandatory) {
            (await k3s.ReadAppsFieldAsync("deployments", "ssa-create", "metadata", "labels", key))
                .ShouldNotBeNull($"the object in the cluster is missing {key}.");
        }

        (await k3s.ReadAppsFieldAsync("deployments", "ssa-create", "metadata", "labels", KubeLabels.ManagedBy))
            .ShouldBe("cybercloud");

        (await k3s.ReadAppsFieldAsync(
                "deployments",
                "ssa-create",
                "metadata",
                "annotations",
                KubeLabels.ResourcePathAnnotation
            ))
            .ShouldBe(Resource("ssa-create").Path);
    }

    [Fact]
    public async Task ReApplyingTheSameBodyIsAnUnchangedNoOp() {
        var token = TestContext.Current.CancellationToken;

        (await k3s.Api.ApplyAsync(Command("ssa-noop", 1), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        (await k3s.Api.ApplyAsync(Command("ssa-noop", 1), token)).GetValueOrThrow()
            .Result.ShouldBe(
                ApplyResult.Unchanged,
                "the API server no-ops an identical apply and does not bump resourceVersion."
            );
    }

    [Fact]
    public async Task ChangingAFieldWeOwnIsAnUpdate() {
        var token = TestContext.Current.CancellationToken;

        (await k3s.Api.ApplyAsync(Command("ssa-update", 1), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        (await k3s.Api.ApplyAsync(Command("ssa-update", 3), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Updated);

        (await k3s.ReadAppsFieldAsync("deployments", "ssa-update", "spec", "replicas")).ShouldBe("3");
    }

    [Fact]
    public async Task OurFieldManagerIsRecordedOnTheObject() {
        // ADR-013 wants "a stable field manager per provider". The API server records it in
        // metadata.managedFields, and that recording is what makes the next conflict possible.
        var token = TestContext.Current.CancellationToken;
        await k3s.Api.ApplyAsync(Command("ssa-manager", 1), token);

        var managed = await k3s.ReadAppsFieldAsync("deployments", "ssa-manager", "metadata", "managedFields");

        managed.ShouldNotBeNull();
        managed.ShouldContain(OurManager);
        managed.ShouldContain("Apply");
    }

    [Fact]
    public void TheFieldManagerIsDerivedFromTheProviderAndIsStable() {
        // "cybercloud/{provider}". Two commands for the same provider must agree, or every reconcile
        // would claim its fields under a new manager and conflict detection would never fire.
        var a = Command("ssa-fm-a", 1);
        var b = Command("ssa-fm-b", 1);

        a.FieldManager.ShouldBe(b.FieldManager);

        var id = Resource("x");
        var defaulted = KubeCommand.For(new UnusedConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(Deployments)
            .InNamespace(K3sFixture.Namespace)
            .ObjectJson(DeploymentJson("x", 1))
            .Build();

        defaulted.FieldManager.ShouldBe("cybercloud/cybercloud.dbforpostgresql");
    }

    // ── THE FAILURE CLASS ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATenantHandEditingAFieldWeOwnProducesAConflictAndNotASilentRevert() {
        // ⚠ ADR-013, end to end, against a real API server.
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-conflict";

        // 1. We apply, owning .spec.replicas at 2.
        (await k3s.Api.ApplyAsync(Command(name, 2), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);
        (await k3s.ReadAppsFieldAsync("deployments", name, "spec", "replicas")).ShouldBe("2");

        // 2. The tenant hand-edits it as a DIFFERENT field manager, taking ownership.
        await k3s.RivalApplyAsync(
            RivalManager,
            "deployments",
            name,
            $$"""
              { "apiVersion": "apps/v1", "kind": "Deployment",
                "metadata": { "name": "{{name}}" },
                "spec": { "replicas": 5 } }
              """
        );

        (await k3s.ReadAppsFieldAsync("deployments", name, "spec", "replicas")).ShouldBe("5");

        // 3. We apply again — unchanged desired state, still 2.
        var outcome = (await k3s.Api.ApplyAsync(Command(name, 2), token)).GetValueOrThrow();

        // (a) It is a CONFLICT, and it is a successful Result carrying one — not a failure.
        //     A failed Result would become a failed operation and a "provisioning failed" in the
        //     portal, and a tenant editing their own cluster is not a provisioning failure.
        outcome.Result.ShouldBe(ApplyResult.Conflict);

        // (b) It is a drift event WITH A NAME: the field, and the manager that took it.
        outcome.Drift.ShouldNotBeNull();
        outcome.Drift!.FieldManager.ShouldBe(OurManager);
        outcome.Drift.ClusterId.ShouldBe(K3sFixture.ClusterId);
        outcome.Drift.Conflicts.ShouldNotBeEmpty(
            "the API server's 409 must be parsed into named fields, not reported as 'conflict'."
        );

        outcome.Drift.Conflicts.ShouldContain(x => x.Field.Contains("replicas", StringComparison.Ordinal));
        outcome.Drift.Conflicts.ShouldContain(x => x.OwnedBy == RivalManager);
        outcome.Drift.Describe().ShouldContain("replicas");
        outcome.Drift.Describe().ShouldContain(RivalManager);

        // (c) ⚠ THE FIELD WAS NOT FORCE-OVERWRITTEN. This is the half that distinguishes a conflict
        //     from a silent revert: the tenant's 5 is still there.
        (await k3s.ReadAppsFieldAsync("deployments", name, "spec", "replicas")).ShouldBe(
            "5",
            "force is pinned false, so a conflicting apply must leave the rival's value alone."
        );
    }

    [Fact]
    public async Task TheConflictNamesEveryContestedFieldAndNotJustTheFirst() {
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-multi";

        await k3s.Api.ApplyAsync(Command(name, 2), token);

        await k3s.RivalApplyAsync(
            RivalManager,
            "deployments",
            name,
            $$"""
              { "apiVersion": "apps/v1", "kind": "Deployment",
                "metadata": { "name": "{{name}}", "labels": { "app": "stolen" } },
                "spec": { "replicas": 9 } }
              """
        );

        var outcome = (await k3s.Api.ApplyAsync(Command(name, 2), token)).GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Conflict);
        outcome.Drift!.Conflicts.Count.ShouldBeGreaterThanOrEqualTo(
            1,
            "every cause on the 409 must become a FieldConflict: "
            + string.Join(", ", outcome.Drift.Conflicts.Select(x => x.ToString()))
        );

        foreach (var conflict in outcome.Drift.Conflicts) {
            conflict.Field.ShouldNotBeEmpty();
            conflict.OwnedBy.ShouldBe(RivalManager);
        }
    }

    [Fact]
    public async Task AConflictOnAMandatoryLabelIsAlsoDetected() {
        // ⚠ The case ADR-013's admission policy is the belt to this braces: "a cluster we manage may
        // also be written to by the tenant". If a tenant takes ownership of cybercloud.io/tenant-id,
        // billing attribution is at stake, and we must find out rather than silently accept it.
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-label-conflict";

        await k3s.Api.ApplyAsync(Command(name, 1), token);

        await k3s.RivalApplyAsync(
            RivalManager,
            "deployments",
            name,
            $$"""
              { "apiVersion": "apps/v1", "kind": "Deployment",
                "metadata": { "name": "{{name}}",
                  "labels": { "cybercloud.io/tenant-id": "00000000-0000-0000-0000-0000000000ff" } } }
              """
        );

        var outcome = (await k3s.Api.ApplyAsync(Command(name, 1), token)).GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Conflict);
        outcome.Drift!.Conflicts.ShouldContain(x => x.Field.Contains("tenant-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConflictParsesTheRivalManagerOutOfTheApiServersOwnMessage() {
        // ⚠ The manager is only in the PROSE of the 409's causes[].message — there is no structured
        // field for it. ConflictParser depends on that format, so it is asserted against a real
        // response rather than against a string this repository invented.
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-parse";

        await k3s.Api.ApplyAsync(Command(name, 1), token);
        await k3s.RivalApplyAsync(
            "a-very-distinctive-manager-name",
            "deployments",
            name,
            $$"""
              { "apiVersion": "apps/v1", "kind": "Deployment",
                "metadata": { "name": "{{name}}" }, "spec": { "replicas": 7 } }
              """
        );

        var outcome = (await k3s.Api.ApplyAsync(Command(name, 1), token)).GetValueOrThrow();

        outcome.Drift!.Conflicts.ShouldContain(x => x.OwnedBy == "a-very-distinctive-manager-name");
        outcome.Drift.Conflicts.ShouldNotContain(x => x.OwnedBy == ConflictParser.UnknownManager);
    }

    [Fact]
    public async Task TakingTheFieldBackIsPossibleButOnlyDeliberately() {
        // Drift with a name is only useful if there is a repair. The repair is an apply with
        // force — which the builder cannot express, so it is necessarily a deliberate, separate act
        // rather than something a reconciler can drift into.
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-reclaim";

        await k3s.Api.ApplyAsync(Command(name, 2), token);
        await k3s.RivalApplyAsync(
            RivalManager,
            "deployments",
            name,
            $$"""
              { "apiVersion": "apps/v1", "kind": "Deployment",
                "metadata": { "name": "{{name}}" }, "spec": { "replicas": 8 } }
              """
        );

        (await k3s.Api.ApplyAsync(Command(name, 2), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Conflict);

        // The builder has no WithForce(). Taking the field back means constructing the command with
        // Force set, which only this assembly's own record initialiser can do — and there is no
        // public path to it. Asserted as an absence:
        typeof(IKubeCommandBuilder).GetMethods()
            .ShouldNotContain(
                x => x.Name.Contains("Force", StringComparison.OrdinalIgnoreCase),
                "the builder must not offer a way to force-overwrite a tenant's edit."
            );

        Command(name, 2).Force.ShouldBeFalse();
    }

    // ── Delete ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteRemovesTheObjectAndIsIdempotent() {
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-delete";

        await k3s.Api.ApplyAsync(Command(name, 1), token);

        var target = new ObjectRef { Kind = Deployments, Namespace = K3sFixture.Namespace, Name = name };

        (await k3s.Api.DeleteAsync(target, CascadePolicy.Background, token)).IsSuccess.ShouldBeTrue();

        // docs/plan/06 § Two-phase create leaves a failed teardown in `Deleting` with a retry
        // reminder, so a second delete must converge rather than alternate between 404 and success.
        (await k3s.Api.DeleteAsync(target, CascadePolicy.Background, token)).IsSuccess.ShouldBeTrue();
    }

    // ── Reads, lists and the informer's selector ───────────────────────────────────────────────

    [Fact]
    public async Task AListFilteredByManagedByFindsOnlyOurObjects() {
        // docs/plan/09 § Observing's selector, against a real API server — including an object that
        // is deliberately NOT ours.
        var token = TestContext.Current.CancellationToken;

        await k3s.Api.ApplyAsync(Command("ssa-ours", 1), token);

        await k3s.RivalApplyAsync(
            "someone-else",
            "deployments",
            "ssa-theirs",
            """
            { "apiVersion": "apps/v1", "kind": "Deployment",
              "metadata": { "name": "ssa-theirs", "labels": { "app": "theirs" } },
              "spec": { "replicas": 1,
                "selector": { "matchLabels": { "app": "theirs" } },
                "template": { "metadata": { "labels": { "app": "theirs" } },
                  "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] } } } }
            """
        );

        var page = (await k3s.Api.ListAsync(
            Deployments,
            K3sFixture.Namespace,
            KubeLabels.ManagedBySelector,
            cancellationToken: token
        )).GetValueOrThrow();

        page.ResourceVersion.ShouldNotBeEmpty("a list must yield a watch cursor.");
        page.Items.ShouldContain(x => x.Contains("\"ssa-ours\"", StringComparison.Ordinal));
        page.Items.ShouldNotContain(x => x.Contains("\"ssa-theirs\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadOfSomethingAbsentIsResourceNotFoundAndNotATransportFailure() {
        // ⚠ The distinction docs/plan/09 § Cluster connections turns on: a 404 is the cluster
        // ANSWERING. Classifying it as unreachable would mark a perfectly healthy cluster Degraded
        // the first time a reconciler looked for an object that did not exist yet.
        var outcome = await k3s.Api.GetAsync(
            new() { Kind = Deployments, Namespace = K3sFixture.Namespace, Name = "no-such-thing" },
            TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The core group ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCoreGroupWorksThroughTheSameUntypedPath() {
        // ⚠ VERIFIED RATHER THAN ASSUMED. Every path in KubeApiClient goes through
        // ICustomObjectsOperations, whose URL template is /apis/{group}/{version}/... — and the core
        // group's real path is /api/{version}/..., with no group segment at all. Whether the client
        // special-cases an empty group is not documented anywhere, and the fabric applies core kinds
        // (ConfigMap, Secret, Service, PVC) constantly, so guessing was not an option.
        var token = TestContext.Current.CancellationToken;

        var command = Command(
            "ssa-core",
            1,
            ConfigMaps,
            """{ "metadata": { "name": "ssa-core" }, "data": { "k": "v" } }"""
        );

        var outcome = (await k3s.Api.ApplyAsync(command, token)).GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Created);

        (await k3s.ReadFieldAsync("configmaps", "ssa-core", "", "v1", "data", "k"))
            .ShouldBe("v");

        (await k3s.ReadFieldAsync("configmaps", "ssa-core", "", "v1", "metadata", "labels", KubeLabels.ManagedBy))
            .ShouldBe("cybercloud");
    }

    [Fact]
    public async Task ACoreGroupConflictIsDetectedToo() {
        var token = TestContext.Current.CancellationToken;
        const string name = "ssa-core-conflict";

        await k3s.Api.ApplyAsync(
            Command(name, 1, ConfigMaps, $$"""{ "metadata": { "name": "{{name}}" }, "data": { "k": "ours" } }"""),
            token
        );

        await k3s.RivalApplyAsync(
            RivalManager,
            "configmaps",
            name,
            $$"""{ "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "{{name}}" }, "data": { "k": "theirs" } }""",
            ""
        );

        var outcome = (await k3s.Api.ApplyAsync(
            Command(name, 1, ConfigMaps, $$"""{ "metadata": { "name": "{{name}}" }, "data": { "k": "ours" } }"""),
            token
        )).GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Conflict);
        (await k3s.ReadFieldAsync("configmaps", name, "", "v1", "data", "k"))
            .ShouldBe("theirs", "not force-overwritten.");
    }

    // ── Health ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PingAnswersWithTheServerVersion() {
        var version = await k3s.Api.PingAsync(TestContext.Current.CancellationToken);

        version.IsSuccess.ShouldBeTrue();
        version.GetValueOrThrow().ShouldStartWith("v1.");
    }

    static ResourceId Resource(string name) =>
        new(
            Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            name,
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

    static string DeploymentJson(string name, int replicas) =>
        $$"""
          {
            "metadata": { "name": "{{name}}" },
            "spec": {
              "replicas": {{replicas}},
              "selector": { "matchLabels": { "app": "{{name}}" } },
              "template": {
                "metadata": { "labels": { "app": "{{name}}" } },
                "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] }
              }
            }
          }
          """;

    KubeCommand Command(string name, int replicas, GroupVersionKind? kind = null, string? json = null) {
        var id = Resource(name);

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(kind ?? Deployments)
            .InNamespace(K3sFixture.Namespace)
            .WithFieldManager(OurManager)
            .ObjectJson(json ?? DeploymentJson(name, replicas))
            .Build();
    }
}
