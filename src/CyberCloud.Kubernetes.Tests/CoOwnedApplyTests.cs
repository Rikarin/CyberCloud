using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The co-owned apply mode against a real k3s — <see cref="IKubeCommandBuilder.CoWriting" />,
///     issue #89: two co-writers' fragments coexist on an object a third resource owns, each
///     withdraws only its own, the owner keeps its labels and its fields, and a race is lost loudly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Real, for the reason <see cref="ServerSideApplyTests" /> is.</b> Every property below
///         belongs to <c>kube-apiserver</c>'s field-management machinery: that a manager's apply is
///         the whole set of fields it owns and removes what it no longer applies; that an atomic list
///         has one set of owners; that a <c>metadata.resourceVersion</c> in an apply body is an
///         optimistic lock; and — the one that shaped <c>KubeApiClient</c> — that the lock does
///         <i>not</i> hold against an object that is absent. A fake would assert this repository's
///         belief about each, and the belief is what is worth checking.
///     </para>
///     <para>
///         The owner is a <c>virtualNetworks</c>-shaped resource rendering a <c>ConfigMap</c> — a
///         built-in kind, so the round trip needs no CRD — and, for the atomic-list half, a custom
///         <c>Widget</c> whose stub definition this class installs. The co-writers are two
///         <c>peerings</c>-shaped children.
///     </para>
/// </remarks>
[Collection(K3sSuite.Name)]
public sealed class CoOwnedApplyTests(K3sFixture k3s) {
    const string OwnerManager = "cybercloud/cybercloud.network";

    static readonly GroupVersionKind ConfigMaps =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    static readonly GroupVersionKind Widgets =
        new() { Group = "coowned.cybercloud.test", Version = "v1", Kind = "Widget", Plural = "widgets" };

    static readonly Guid Tenant = Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a");
    static readonly Guid Subscription = Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6");

    // ── Two co-writers, one owner, one object ───────────────────────────────────────────────────

    [Fact]
    public async Task TwoCoWritersFragmentsCoexistWithTheOwnersAndEachRemovesOnlyItsOwn() {
        // ⚠ THE ROUND TRIP THE ISSUE ASKS FOR, end to end against the real API server.
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-round-trip";

        var owner = Owner(name);
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var b = Peering(owner, "to-b", "bbbbbbbb-0000-4000-8000-00000000000b");
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        // 1. The owner renders its object, as any reconciler does.
        (await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        // 2. Two co-writers each add their fragment.
        var coWriter = new KubeCoWriter(Connection());

        (await coWriter.ApplyFragmentAsync(a, target, """{ "data": { "a": "from-a" } }""", token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Updated);

        (await coWriter.ApplyFragmentAsync(b, target, """{ "data": { "b": "from-b" } }""", token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Updated);

        // 3. All three slices are on the one object, and the owner's identity is untouched.
        (await Data(name, "base")).ShouldBe("owner");
        (await Data(name, "a")).ShouldBe("from-a");
        (await Data(name, "b")).ShouldBe("from-b");

        foreach (var key in KubeLabels.Mandatory) {
            (await k3s.ReadFieldAsync("configmaps", name, "", "v1", "metadata", "labels", key))
                .ShouldNotBeNull($"the owner's {key} must survive two co-writers' applies");
        }

        (await k3s.ReadFieldAsync("configmaps", name, "", "v1", "metadata", "labels", KubeLabels.ResourceId))
            .ShouldBe(KubeLabels.GuidValue(owner.Id), "the object is still the owner's");

        (await Annotation(name, KubeLabels.ReconcileHashAnnotation)).ShouldNotBeNull("the owner's hash stays");
        (await Annotation(name, KubeLabels.FragmentHashAnnotation(a.Id))).ShouldStartWith("sha256:");
        (await Annotation(name, KubeLabels.FragmentHashAnnotation(b.Id))).ShouldStartWith("sha256:");
        (await Annotation(name, KubeLabels.FragmentPathAnnotation(a.Id))).ShouldBe(a.Path);

        // 4. Two managers of ours on the object: the owner's, and the one the co-writers share.
        var managers = await ManagersOf(name);

        managers.ShouldContain(OwnerManager);
        managers.ShouldContain(KubeLabels.CoWriterFieldManager(KubeLabels.ResourceTypeValue(owner.Type), KubeLabels.GuidValue(owner.Id)));
        managers.Count(x => x.StartsWith("cybercloud/", StringComparison.Ordinal)).ShouldBe(2);

        // 5. A withdraws. Only A's slice goes.
        (await coWriter.WithdrawFragmentAsync(a, target, token)).GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        (await Data(name, "a")).ShouldBeNull("A's fragment was withdrawn");
        (await Data(name, "b")).ShouldBe("from-b", "B's fragment was not");
        (await Data(name, "base")).ShouldBe("owner", "and the owner's field was not");
        (await Annotation(name, KubeLabels.FragmentAnnotation(a.Id))).ShouldBeNull();
        (await Annotation(name, KubeLabels.FragmentHashAnnotation(a.Id))).ShouldBeNull();
        (await Annotation(name, KubeLabels.FragmentPathAnnotation(a.Id))).ShouldBeNull();
        (await Annotation(name, KubeLabels.FragmentAnnotation(b.Id))).ShouldNotBeNull();

        // 6. B withdraws. The object is the owner's alone again, and the shared manager owns nothing.
        (await coWriter.WithdrawFragmentAsync(b, target, token)).GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        (await Data(name, "b")).ShouldBeNull();
        (await Data(name, "base")).ShouldBe("owner");
        (await Annotation(name, KubeLabels.FragmentAnnotation(b.Id))).ShouldBeNull();

        (await ManagersOf(name)).ShouldNotContain(
            KubeLabels.CoWriterFieldManager(KubeLabels.ResourceTypeValue(owner.Type), KubeLabels.GuidValue(owner.Id)),
            "a manager that applies nothing owns nothing, and the API server drops its entry"
        );

        // 7. And the owner's next apply is a no-op: nothing of its own moved.
        (await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Unchanged);
    }

    [Fact]
    public async Task TheOwnersApplyNeverPrunesACoWritersFragment() {
        // The other direction of "two managers": the owner re-applies its whole desired body, which
        // does not mention the co-writer's key, and the co-writer's key stays — because it is the
        // shared co-writer manager's field, not the owner's.
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-owner-reapply";

        var owner = Owner(name);
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "one")), token);
        await new KubeCoWriter(Connection()).ApplyFragmentAsync(a, target, """{ "data": { "a": "from-a" } }""", token);

        (await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "two")), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Updated);

        (await Data(name, "base")).ShouldBe("two");
        (await Data(name, "a")).ShouldBe("from-a", "the owner's apply does not reach the co-writer's field");
    }

    // ── The owner keeps its fields ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACoWriterSettingAFieldTheOwnerOwnsIsAConflictNamingTheOwnersManager() {
        // ⚠ ADR-013's conflict, pointed the other way: a co-writer reaching for a field the owner
        // renders is drift with a name, and the owner's value stands.
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-owner-field";

        var owner = Owner(name);
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token);

        var outcome = (await new KubeCoWriter(Connection())
            .ApplyFragmentAsync(a, target, """{ "data": { "base": "mine-now" } }""", token)).GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Conflict);
        outcome.Drift.ShouldNotBeNull();
        outcome.Drift!.ResourceId.ShouldBe(a.Id, "the drift event names the co-writer whose desired state lost");
        outcome.Drift.Conflicts.ShouldContain(x => x.OwnedBy == OwnerManager);
        outcome.Drift.Conflicts.ShouldContain(x => x.Field.Contains("base", StringComparison.Ordinal));

        (await Data(name, "base")).ShouldBe("owner", "not force-overwritten");
    }

    // ── The optimistic lock ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnApplyBuiltFromAVersionThatMovedIsStaleAndWritesNothing() {
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-stale";

        var owner = Owner(name);
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token);

        // Read, then somebody else moves the object, then apply from the read.
        var live = (await k3s.Api.GetAsync(target, token)).GetValueOrThrow();

        await k3s.RivalApplyAsync(
            "somebody-else",
            "configmaps",
            name,
            $$"""{ "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "{{name}}", "annotations": { "tenant.example/note": "moved" } } }""",
            ""
        );

        var outcome = (await k3s.Api.ApplyAsync(CoOwnedCommand(a, live, """{ "data": { "a": "from-a" } }"""), token))
            .GetValueOrThrow();

        outcome.Result.ShouldBe(ApplyResult.Stale, "a 409 with no FieldManagerConflict cause is the optimistic lock");
        outcome.Drift.ShouldBeNull("nothing is drifting");
        (await Data(name, "a")).ShouldBeNull("nothing was written");

        // And the loop that reads again gets it in.
        (await new KubeCoWriter(Connection()).ApplyFragmentAsync(a, target, """{ "data": { "a": "from-a" } }""", token))
            .GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        (await Data(name, "a")).ShouldBe("from-a");
    }

    [Fact]
    public async Task TheApiServersOptimisticLockMessageIsWhatConflictParserKeysOn() {
        // ⚠ ConflictParser.OptimisticLockMessage is a sentence copied out of k8s.io/apiserver, and
        // this is what says the copy is right against the pinned k3s rather than against memory.
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-lock-message";

        var owner = Owner(name);
        await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token);

        var thrown = await Should.ThrowAsync<HttpOperationException>(() => k3s.Raw.CustomObjects
            .PatchNamespacedCustomObjectWithHttpMessagesAsync(
                new V1Patch(
                    JsonSerializer.Deserialize<JsonElement>(
                        $$"""{ "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "{{name}}", "resourceVersion": "1" }, "data": { "z": "z" } }"""
                    ),
                    V1Patch.PatchType.ApplyPatch
                ),
                "",
                "v1",
                K3sFixture.Namespace,
                "configmaps",
                name,
                fieldManager: "probe",
                cancellationToken: token
            )
        );

        thrown.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        thrown.Response.Content.ShouldContain(ConflictParser.OptimisticLockMessage);
        ConflictParser.IsOptimisticLock(thrown.Response.Content).ShouldBeTrue();
        ConflictParser.Parse(thrown.Response.Content).ShouldBeEmpty();
    }

    // ── The owner's delete wins ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnceTheOwnerDeletesTheObjectACoWritersApplyIsRefusedAndDoesNotResurrectIt() {
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-owner-delete";

        var owner = Owner(name);
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token);
        var live = (await k3s.Api.GetAsync(target, token)).GetValueOrThrow();

        (await k3s.Api.DeleteAsync(target, CascadePolicy.Background, token)).IsSuccess.ShouldBeTrue();

        // The co-writer applies from the read it made before the delete.
        var refused = await k3s.Api.ApplyAsync(CoOwnedCommand(a, live, """{ "data": { "a": "from-a" } }"""), token);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Message.ShouldContain("never creates");

        (await k3s.Api.GetAsync(target, token)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound, "the object was not resurrected");

        // And the seam's withdrawal on a gone object is converged, not an error.
        (await new KubeCoWriter(Connection()).WithdrawFragmentAsync(a, target, token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Unchanged);
    }

    [Fact]
    public async Task ANameTakenByAnotherResourceAfterTheReadIsRefusedAndNothingIsWritten() {
        // ⚠ THE #89 REVIEW'S CASE. The owner deletes its object; another resource creates one under
        // the same name; the co-writer applies from the read it made before either. The API server
        // cannot tell — the new object has a resourceVersion of its own and the apply body's stale
        // one is only older, which is a 409 the loop would answer by reading again and then
        // co-writing onto the new owner's object as if it were the old one. The client compares the
        // command's owner with the live object's resource-id label before the PATCH and refuses by
        // name, and the answer is Conflict rather than Stale because reading again is not the repair.
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-name-taken";

        var owner = Owner(name);
        var usurper = Owner(name + "-usurper");
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var target = new ObjectRef { Kind = ConfigMaps, Namespace = K3sFixture.Namespace, Name = name };

        await k3s.Api.ApplyAsync(OwnerCommand(owner, name, ("base", "owner")), token);
        var live = (await k3s.Api.GetAsync(target, token)).GetValueOrThrow();

        (await k3s.Api.DeleteAsync(target, CascadePolicy.Background, token)).IsSuccess.ShouldBeTrue();
        (await k3s.Api.ApplyAsync(OwnerCommand(usurper, name, ("base", "usurper")), token)).GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        var refused = await k3s.Api.ApplyAsync(CoOwnedCommand(a, live, """{ "data": { "a": "from-a" } }"""), token);

        refused.IsFailure.ShouldBeTrue("the object under that name is not the one the command was built against");
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain(KubeLabels.GuidValue(owner.Id));
        refused.Error.Message.ShouldContain(KubeLabels.GuidValue(usurper.Id));

        (await Data(name, "a")).ShouldBeNull("nothing was written onto the usurper's object");
        (await Data(name, "base")).ShouldBe("usurper");
        (await ManagersOf(name)).ShouldNotContain(
            KubeLabels.CoWriterFieldManager(KubeLabels.ResourceTypeValue(owner.Type), KubeLabels.GuidValue(owner.Id)),
            "the old owner's co-writer manager never touched the new object"
        );
    }

    [Fact]
    public async Task TheApiServerDoesNotHoldTheLockAgainstAnAbsentObjectWhichIsWhyTheClientRefuses() {
        // ⚠ THE MEASUREMENT BEHIND KubeApiClient's co-owned refusal. If the server refused an apply
        // that carried a resourceVersion against an object that is not there, the client's check
        // would be a courtesy. It does not: the create-on-update path clears the version and creates.
        // Pinned so that the day a Kubernetes release changes it, the comment in KubeApiClient is
        // corrected rather than left describing a server that no longer exists.
        var token = TestContext.Current.CancellationToken;
        const string name = "coowned-server-creates";

        using var response = await k3s.Raw.CustomObjects.PatchNamespacedCustomObjectWithHttpMessagesAsync(
            new V1Patch(
                JsonSerializer.Deserialize<JsonElement>(
                    $$"""{ "apiVersion": "v1", "kind": "ConfigMap", "metadata": { "name": "{{name}}", "resourceVersion": "12345" }, "data": { "z": "z" } }"""
                ),
                V1Patch.PatchType.ApplyPatch
            ),
            "",
            "v1",
            K3sFixture.Namespace,
            "configmaps",
            name,
            fieldManager: "probe",
            cancellationToken: token
        );

        response.Response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "the API server created the object despite the stale resourceVersion — the client's refusal is the only guard"
        );

        (await Data(name, "z")).ShouldBe("z");
    }

    // ── The atomic list, which is why the co-writers share one manager ──────────────────────────

    [Fact]
    public async Task TwoCoWritersEachAddAnEntryToAnAtomicListAndBothEntriesLand() {
        // ⚠ The Kube-OVN shape: Vpc.spec.vpcPeerings declares no x-kubernetes-list-type, so it is
        // one atomic value. The stub Widget's preserve-unknown-fields schema makes every list atomic
        // the same way.
        var token = TestContext.Current.CancellationToken;
        await EnsureWidgetsAsync(token);
        const string name = "coowned-atomic";

        var owner = Owner(name);
        var a = Peering(owner, "to-a", "aaaaaaaa-0000-4000-8000-00000000000a");
        var b = Peering(owner, "to-b", "bbbbbbbb-0000-4000-8000-00000000000b");
        var target = new ObjectRef { Kind = Widgets, Namespace = K3sFixture.Namespace, Name = name };

        var ownerCommand = KubeCommand.For(new UnusedConnection())
            .WithTenantId(owner.TenantId)
            .WithResourceId(owner)
            .WithKind(Widgets)
            .InNamespace(K3sFixture.Namespace)
            .WithFieldManager(OwnerManager)
            .ObjectJson($$"""{ "metadata": { "name": "{{name}}" }, "spec": { "namespaces": [ "tenant-space" ] } }""")
            .Build();

        (await k3s.Api.ApplyAsync(ownerCommand, token)).GetValueOrThrow().Result.ShouldBe(ApplyResult.Created);

        var coWriter = new KubeCoWriter(Connection());

        (await coWriter.ApplyFragmentAsync(a, target, """{ "spec": { "peerings": [ { "remote": "a" } ] } }""", token))
            .GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        (await coWriter.ApplyFragmentAsync(b, target, """{ "spec": { "peerings": [ { "remote": "b" } ] } }""", token))
            .GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        var peerings = JsonNode.Parse(
            (await k3s.ReadFieldAsync("widgets", name, Widgets.Group, Widgets.Version, "spec", "peerings"))!
        )!.AsArray();

        peerings.Count.ShouldBe(2, "both co-writers' entries are on the one atomic list");
        peerings.Select(x => x!["remote"]!.GetValue<string>()).ShouldBe(["a", "b"]);

        (await k3s.ReadFieldAsync("widgets", name, Widgets.Group, Widgets.Version, "spec", "namespaces"))
            .ShouldNotBeNull("the owner's own list is untouched");

        // A withdraws; B's entry stays.
        (await coWriter.WithdrawFragmentAsync(a, target, token)).GetValueOrThrow().Result.ShouldBe(ApplyResult.Updated);

        var remaining = JsonNode.Parse(
            (await k3s.ReadFieldAsync("widgets", name, Widgets.Group, Widgets.Version, "spec", "peerings"))!
        )!.AsArray();

        remaining.Count.ShouldBe(1);
        remaining[0]!["remote"]!.GetValue<string>().ShouldBe("b");
    }

    [Fact]
    public async Task AManagerPerCoWriterWouldConflictOnTheAtomicListWhichIsWhyTheyShareOne() {
        // ⚠ THE MEASUREMENT BEHIND KubeLabels.CoWriterFieldManager's remarks. Two managers, each
        // applying the list with its own entry added, and the second is a FieldManagerConflict on
        // the whole list — without force, forever. A design with a manager per co-writer does not
        // converge on the fields a peering has to reach, and this is the proof rather than the claim.
        var token = TestContext.Current.CancellationToken;
        await EnsureWidgetsAsync(token);
        const string name = "coowned-per-writer-conflicts";

        await k3s.RivalApplyAsync(
            "peer-a",
            "widgets",
            name,
            $$"""{ "apiVersion": "coowned.cybercloud.test/v1", "kind": "Widget", "metadata": { "name": "{{name}}" }, "spec": { "peerings": [ { "remote": "a" } ] } }""",
            Widgets.Group,
            Widgets.Version
        );

        var thrown = await Should.ThrowAsync<HttpOperationException>(() => k3s.Raw.CustomObjects
            .PatchNamespacedCustomObjectWithHttpMessagesAsync(
                new V1Patch(
                    JsonSerializer.Deserialize<JsonElement>(
                        $$"""{ "apiVersion": "coowned.cybercloud.test/v1", "kind": "Widget", "metadata": { "name": "{{name}}" }, "spec": { "peerings": [ { "remote": "a" }, { "remote": "b" } ] } }"""
                    ),
                    V1Patch.PatchType.ApplyPatch
                ),
                Widgets.Group,
                Widgets.Version,
                K3sFixture.Namespace,
                Widgets.Plural,
                name,
                fieldManager: "peer-b",
                force: false,
                cancellationToken: token
            )
        );

        thrown.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var conflicts = ConflictParser.Parse(thrown.Response.Content);

        conflicts.ShouldContain(x => x.OwnedBy == "peer-a");
        conflicts.ShouldContain(x => x.Field.Contains("peerings", StringComparison.Ordinal));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    static ResourceId Owner(string name) =>
        new(
            Tenant,
            Subscription,
            "prod",
            new("CyberCloud.Network", "virtualNetworks"),
            name,
            // One owner GUID per test, derived from the name, so two tests' objects never share a
            // co-writer manager.
            new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).AsSpan(0, 16))
        );

    static ResourceId Peering(ResourceId owner, string name, string id) =>
        new(
            Tenant,
            Subscription,
            "prod",
            new("CyberCloud.Network", "virtualNetworks/peerings"),
            name,
            Guid.Parse(id),
            owner.Name
        );

    KubeCommand OwnerCommand(ResourceId owner, string name, params (string Key, string Value)[] data) {
        var map = new JsonObject();
        foreach (var (key, value) in data) {
            map[key] = value;
        }

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(owner.TenantId)
            .WithResourceId(owner)
            .WithKind(ConfigMaps)
            .InNamespace(K3sFixture.Namespace)
            .WithFieldManager(OwnerManager)
            .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = name }, ["data"] = map }.ToJsonString())
            .Build();
    }

    static KubeCommand CoOwnedCommand(ResourceId writer, KubeObject live, string fragment) =>
        KubeCommand.For(new UnusedConnection())
            .WithTenantId(writer.TenantId)
            .WithResourceId(writer)
            .WithKind(live.Ref.Kind)
            .CoWriting(live)
            .ObjectJson(fragment)
            .Build();

    /// <summary>The fixture's client, as the connection a reconciler would hold.</summary>
    ApiConnection Connection() => new(k3s.Api);

    Task<string?> Data(string name, string key) => k3s.ReadFieldAsync("configmaps", name, "", "v1", "data", key);

    Task<string?> Annotation(string name, string key) =>
        k3s.ReadFieldAsync("configmaps", name, "", "v1", "metadata", "annotations", key);

    async Task<List<string>> ManagersOf(string name) {
        var managed = await k3s.ReadFieldAsync("configmaps", name, "", "v1", "metadata", "managedFields");
        managed.ShouldNotBeNull();

        return JsonNode.Parse(managed)!.AsArray()
            .Select(x => x!["manager"]!.GetValue<string>())
            .ToList();
    }

    /// <summary>Installs the stub <c>Widget</c> definition once, and waits until it is served.</summary>
    async Task EnsureWidgetsAsync(CancellationToken token) {
        var crdName = Widgets.Plural + "." + Widgets.Group;

        try {
            await k3s.Raw.ApiextensionsV1.CreateCustomResourceDefinitionAsync(
                new V1CustomResourceDefinition {
                    ApiVersion = "apiextensions.k8s.io/v1",
                    Kind = "CustomResourceDefinition",
                    Metadata = new() { Name = crdName },
                    Spec = new() {
                        Group = Widgets.Group,
                        Scope = "Namespaced",
                        Names = new() { Kind = Widgets.Kind, ListKind = "WidgetList", Plural = Widgets.Plural, Singular = "widget" },
                        Versions = [
                            new() {
                                Name = Widgets.Version,
                                Served = true,
                                Storage = true,
                                Schema = new() {
                                    OpenAPIV3Schema = new() { Type = "object", XKubernetesPreserveUnknownFields = true }
                                }
                            }
                        ]
                    }
                },
                cancellationToken: token
            );
        } catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict) {
            // Another test in this class won the race to install it.
        }

        for (var attempt = 0; attempt < 60; attempt++) {
            var definition = await k3s.Raw.ApiextensionsV1.ReadCustomResourceDefinitionAsync(crdName, cancellationToken: token);

            if (definition.Status?.Conditions?.Any(x => x.Type == "Established" && x.Status == "True") == true) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }

        throw new InvalidOperationException($"'{crdName}' never became Established.");
    }

    /// <summary>
    ///     <see cref="IKubeClusterConnection" /> straight over the fixture's <see cref="IKubeApiClient" />,
    ///     which is what <c>ClusterConnectionHandle</c> is over the grain.
    /// </summary>
    sealed class ApiConnection(IKubeApiClient api) : IKubeClusterConnection {
        public Guid ClusterId => K3sFixture.ClusterId;

        public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) =>
            api.ApplyAsync(command, cancellationToken);

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            api.GetAsync(target, cancellationToken);

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) =>
            api.DeleteAsync(command.Target, policy, cancellationToken);
    }
}
