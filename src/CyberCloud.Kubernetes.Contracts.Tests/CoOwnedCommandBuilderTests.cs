using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using Shouldly;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     The co-owned apply mode of the ADR-013 builder — <see cref="IKubeCommandBuilder.CoWriting" />,
///     issue #89 — rule by rule, without a cluster.
/// </summary>
/// <remarks>
///     <para>
///         The rules under test, each one a section below: the field manager is the owner's shared
///         co-writer manager; the mandatory labels are the owner's and a co-writer writes none; a
///         hash and a path per fragment sit beside the owner's two annotations; the live object's
///         <c>resourceVersion</c> is carried; two co-writers' fragments merge and never clobber; a
///         withdrawal removes only this co-writer's; and everything that would let a co-writer say
///         whose the object is, is refused by name.
///     </para>
///     <para>
///         What a real API server does with the result — the merge under one manager, the stale
///         409, the owner's delete winning — is <c>CoOwnedApplyTests</c> against k3s. This file is
///         the pure half.
///     </para>
/// </remarks>
public sealed class CoOwnedCommandBuilderTests {
    static readonly GroupVersionKind Vpcs =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "Vpc", Plural = "vpcs" };

    static readonly Guid Tenant = Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a");
    static readonly Guid Subscription = Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6");

    /// <summary>The network that owns the Vpc.</summary>
    static readonly ResourceId Owner = new(
        Tenant,
        Subscription,
        "prod",
        new("CyberCloud.Network", "virtualNetworks"),
        "hub",
        Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
    );

    /// <summary>The first peering, a child of the owner.</summary>
    static readonly ResourceId PeeringA = new(
        Tenant,
        Subscription,
        "prod",
        new("CyberCloud.Network", "virtualNetworks/peerings"),
        "to-spoke-a",
        Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a"),
        "hub"
    );

    /// <summary>The second peering on the same Vpc.</summary>
    static readonly ResourceId PeeringB = PeeringA with {
        Name = "to-spoke-b", Id = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b")
    };

    // ── The field manager ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFieldManagerIsNamedForTheOwnerAndReadOffTheObject() {
        // cybercloud/{ownerType}/{ownerId}, both halves from the live object's labels rather than
        // from anything the co-writer said — see KubeLabels.CoWriterFieldManager for why it is the
        // owner's and not the co-writer's.
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        command.FieldManager.ShouldBe(
            "cybercloud/cybercloud.network_virtualnetworks/3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"
        );

        command.FieldManager.Length.ShouldBeLessThanOrEqualTo(128, "the API server caps a field manager at 128");
    }

    [Fact]
    public void TwoCoWritersOfOneObjectShareTheManagerAndTheOwnersOwnManagerDiffers() {
        var live = LiveVpc();

        var a = CoWrite(PeeringA, live).Build();
        var b = CoWrite(PeeringB, live).Build();

        a.FieldManager.ShouldBe(b.FieldManager, "an atomic list has one set of owners, so co-writers share one manager");

        // The owner applies under cybercloud/{provider}; the co-writers must not, or the owner's
        // next apply prunes their fields as fields its manager no longer applies.
        a.FieldManager.ShouldNotBe("cybercloud/cybercloud.network");
    }

    [Fact]
    public void WithFieldManagerIsRefusedInTheCoOwnedMode() {
        var refused = CoWrite(PeeringA, LiveVpc()).WithFieldManager("cybercloud/cybercloud.network").TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("WithFieldManager");
        refused.Error.Message.ShouldContain("derived, not chosen");
    }

    // ── The labels are the owner's ──────────────────────────────────────────────────────────────

    [Fact]
    public void ACoWriterWritesNoLabelsAtAll() {
        // ⚠ THE RULE THE ISSUE NAMES FIRST. The seven say whose the object is; a co-writer applying
        // them at its own values is a FieldManagerConflict on every one, and applying them at the
        // owner's values would make the co-writers' manager a co-owner of the owner's identity.
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        command.Labels.ShouldBeEmpty();

        using var document = JsonDocument.Parse(command.Body);
        document.RootElement.GetProperty("metadata").TryGetProperty("labels", out _)
            .ShouldBeFalse("the apply body must not touch metadata.labels");
    }

    [Fact]
    public void TheOwnersTwoAnnotationsAreNotWrittenEither() {
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        command.Annotations.Keys.ShouldNotContain(KubeLabels.ReconcileHashAnnotation);
        command.Annotations.Keys.ShouldNotContain(KubeLabels.ResourcePathAnnotation);
    }

    [Fact]
    public void WithLabelsWithTemplateLabelsAndWithOwnerAreRefusedByName() {
        var live = LiveVpc();

        var labels = CoWrite(PeeringA, live).WithLabels(("tier", "gold")).TryBuild();
        labels.IsFailure.ShouldBeTrue();
        labels.Error!.Message.ShouldContain("WithLabels");

        var templates = CoWrite(PeeringA, live).WithTemplateLabels("spec/template").TryBuild();
        templates.IsFailure.ShouldBeTrue();
        templates.Error!.Message.ShouldContain("WithTemplateLabels");

        var owner = CoWrite(PeeringA, live).WithOwner(Owner, Vpcs, "hub", "uid-1").TryBuild();
        owner.IsFailure.ShouldBeTrue();
        owner.Error!.Message.ShouldContain("WithOwner");

        var subscription = CoWrite(PeeringA, live).WithSubscriptionId(Guid.NewGuid()).TryBuild();
        subscription.IsFailure.ShouldBeTrue();
        subscription.Error!.Message.ShouldContain("WithSubscriptionId");

        // ⚠ An annotation is not identity, and it is refused for a different reason: it would ride
        // under the shared manager without being in the fragment the next co-writer merges, so that
        // co-writer's apply would prune it — the clobber, by the side door.
        var annotations = CoWrite(PeeringA, live).WithAnnotations(("tenant.example/note", "mine")).TryBuild();
        annotations.IsFailure.ShouldBeTrue();
        annotations.Error!.Message.ShouldContain("WithAnnotations");
        annotations.Error.Message.ShouldContain("the next co-writer's apply would remove it");
    }

    [Fact]
    public void AFragmentThatCarriesMetadataBeyondItsAddressIsRefused() {
        var live = LiveVpc();

        var labelled = Builder(PeeringA, live)
            .ObjectJson("""{ "metadata": { "labels": { "mine": "yes" } }, "spec": { "vpcPeerings": [] } }""")
            .TryBuild();

        labelled.IsFailure.ShouldBeTrue();
        labelled.Error!.Message.ShouldContain("metadata.labels");

        var owned = Builder(PeeringA, live)
            .ObjectJson("""{ "metadata": { "ownerReferences": [] }, "spec": { "x": 1 } }""")
            .TryBuild();

        owned.IsFailure.ShouldBeTrue();
        owned.Error!.Message.ShouldContain("metadata.ownerReferences");

        // name and namespace are the address and are allowed, as long as they agree with the read.
        Builder(PeeringA, live)
            .ObjectJson($$"""{ "metadata": { "name": "{{live.Ref.Name}}" }, "spec": { "x": 1 } }""")
            .TryBuild()
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AFragmentThatCarriesStatusIsRefused() {
        var refused = Builder(PeeringA, LiveVpc())
            .ObjectJson("""{ "spec": { "x": 1 }, "status": { "ready": true } }""")
            .TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("status");
    }

    [Fact]
    public void AnEmptyFragmentIsRefusedAndPointsAtTheWithdrawal() {
        var refused = Builder(PeeringA, LiveVpc())
            .ObjectJson("""{ "apiVersion": "kubeovn.io/v1", "kind": "Vpc", "metadata": { "name": "x" } }""")
            .TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("withdrawal");
    }

    [Fact]
    public void AHandWrittenFragmentAnnotationIsRefusedInEitherMode() {
        // ⚠ In the ordinary mode it would make the owner claim a co-writer's bookkeeping; in the
        // co-owned mode it would let one co-writer forge what another applied.
        Should.Throw<ArgumentException>(() => Builder(PeeringA, LiveVpc())
                .WithAnnotations((KubeLabels.FragmentAnnotation(PeeringB.Id), "{}"))
            )
            .Message.ShouldContain("per-fragment annotation");

        Should.Throw<ArgumentException>(() => KubeCommand.For(new NullConnection())
                .WithTenantId(Owner.TenantId)
                .WithResourceId(Owner)
                .WithAnnotations((KubeLabels.FragmentHashAnnotation(PeeringA.Id), "sha256:x"))
            )
            .Message.ShouldContain("per-fragment annotation");
    }

    // ── Whose object it is ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnObjectWithoutTheSevenLabelsIsNotOursToCoWrite() {
        var unlabelled = new KubeObject {
            Ref = new() { Kind = Vpcs, Name = "somebody-elses" },
            Json = """{ "apiVersion": "kubeovn.io/v1", "kind": "Vpc", "metadata": { "name": "somebody-elses", "resourceVersion": "7" } }""",
            ResourceVersion = "7"
        };

        var refused = CoWrite(PeeringA, unlabelled).TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain(KubeLabels.TenantId);
        refused.Error.Message.ShouldContain("does not own");
    }

    [Fact]
    public void AnotherTenantsObjectIsRefused() {
        // ⚠ The tenant boundary, read off the object: a peering is VPC-to-VPC within a tenant.
        var theirs = LiveVpc(tenant: Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));

        var refused = CoWrite(PeeringA, theirs).TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("never reaches across a tenant");
    }

    [Fact]
    public void AResourceCoWritingItsOwnObjectIsRefused() {
        var refused = Builder(Owner, LiveVpc()).ObjectJson("""{ "spec": { "x": 1 } }""").TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("owns itself");
    }

    [Fact]
    public void TheOwnerIsRecordedOnTheCommandSoAConnectionCanRefuseToCreate() {
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        command.IsCoOwned.ShouldBeTrue();
        command.OwnerResourceId.ShouldBe(Owner.Id);
        command.ResourceId.ShouldBe(PeeringA.Id, "the command is the co-writer's — its drift event names the co-writer");

        // And the ordinary mode has no owner.
        KubeCommand.For(new NullConnection())
            .WithTenantId(Owner.TenantId)
            .WithResourceId(Owner)
            .WithKind(Vpcs)
            .ObjectJson("""{ "metadata": { "name": "hub" }, "spec": {} }""")
            .Build()
            .IsCoOwned.ShouldBeFalse();
    }

    // ── The address is the owner's object's ─────────────────────────────────────────────────────

    [Fact]
    public void TheNameComesFromTheLiveObjectAndNeverFromTheCoWritersOwnName() {
        // ⚠ The ordinary mode falls back to resource.Name. A peering called "to-spoke-a" applying a
        // fragment under its own name would create a new Vpc rather than write onto the owner's.
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        command.Target.Name.ShouldBe("hub-vpc");
        command.Target.Name.ShouldNotBe(PeeringA.Name);

        using var document = JsonDocument.Parse(command.Body);
        document.RootElement.GetProperty("metadata").GetProperty("name").GetString().ShouldBe("hub-vpc");
    }

    [Fact]
    public void AFragmentNamingADifferentObjectThanTheOneReadIsRefused() {
        var refused = Builder(PeeringA, LiveVpc())
            .ObjectJson("""{ "metadata": { "name": "some-other-vpc" }, "spec": { "x": 1 } }""")
            .TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("some-other-vpc");
        refused.Error.Message.ShouldContain("hub-vpc");
    }

    [Fact]
    public void TheKindMustMatchTheObjectThatWasRead() {
        var refused = KubeCommand.For(new NullConnection())
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" })
            .CoWriting(LiveVpc())
            .ObjectJson("""{ "data": { "k": "v" } }""")
            .TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("ConfigMap");
        refused.Error.Message.ShouldContain("Vpc");
    }

    // ── The per-fragment hash and path ──────────────────────────────────────────────────────────

    [Fact]
    public void EachFragmentGetsItsOwnHashPathAndBodyAnnotations() {
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        command.Annotations[KubeLabels.FragmentHashAnnotation(PeeringA.Id)].ShouldStartWith("sha256:");
        command.Annotations[KubeLabels.FragmentPathAnnotation(PeeringA.Id)].ShouldBe(PeeringA.Path);

        var stored = JsonNode.Parse(command.Annotations[KubeLabels.FragmentAnnotation(PeeringA.Id)])!.AsObject();
        stored["spec"]!["vpcPeerings"]!.AsArray().Count.ShouldBe(1);

        command.ReconcileHash.ShouldBe(command.Annotations[KubeLabels.FragmentHashAnnotation(PeeringA.Id)]);

        foreach (var key in command.Annotations.Keys) {
            LabelSyntax.ValidateKey(key).IsSuccess.ShouldBeTrue($"'{key}' must be a legal annotation key");
        }
    }

    [Fact]
    public void TheHashIsOverThisFragmentAloneAndIsStableAcrossOrderingAndOtherCoWriters() {
        var alone = CoWrite(PeeringA, LiveVpc()).Build();

        // Same fragment, members in the other order.
        var reordered = Builder(PeeringA, LiveVpc())
            .ObjectJson(
                """{ "spec": { "vpcPeerings": [ { "localConnectIP": "10.0.0.1/30", "remoteVpc": "spoke-a-vpc" } ] } }"""
            )
            .Build();

        reordered.ReconcileHash.ShouldBe(alone.ReconcileHash, "property order is not a change");

        // Same fragment, onto an object another co-writer has already written onto.
        var beside = CoWrite(PeeringA, LiveVpc(WithFragmentOf(PeeringB))).Build();

        beside.ReconcileHash.ShouldBe(alone.ReconcileHash, "the hash is over MY slice, not the union");

        // A different fragment hashes differently.
        Builder(PeeringA, LiveVpc())
            .ObjectJson("""{ "spec": { "vpcPeerings": [ { "remoteVpc": "spoke-c-vpc", "localConnectIP": "10.0.0.9/30" } ] } }""")
            .Build()
            .ReconcileHash.ShouldNotBe(alone.ReconcileHash);
    }

    // ── The resourceVersion ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLiveResourceVersionIsCarriedIntoTheApplyBody() {
        var command = CoWrite(PeeringA, LiveVpc(resourceVersion: "4711")).Build();

        using var document = JsonDocument.Parse(command.Body);
        document.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString().ShouldBe("4711");
    }

    [Fact]
    public void ALiveObjectWithoutAResourceVersionIsRefused() {
        var live = LiveVpc(resourceVersion: "") with { ResourceVersion = string.Empty };

        var refused = CoWrite(PeeringA, live).TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("resourceVersion");
        refused.Error.Message.ShouldContain("Stale");
    }

    [Fact]
    public void TheResourceVersionFallsBackToTheBodyWhenTheRecordCarriesNone() {
        var live = LiveVpc(resourceVersion: "99") with { ResourceVersion = string.Empty };

        var command = CoWrite(PeeringA, live).Build();

        using var document = JsonDocument.Parse(command.Body);
        document.RootElement.GetProperty("metadata").GetProperty("resourceVersion").GetString().ShouldBe("99");
    }

    // ── Two co-writers' fragments do not clobber each other ─────────────────────────────────────

    [Fact]
    public void ASecondCoWritersApplyCarriesTheFirstsFragmentAndAnnotations() {
        // ⚠ THE PRUNE THIS MODE EXISTS TO PREVENT. Both co-writers share a manager, and a manager's
        // apply is the whole set of fields it owns — so B's apply must carry A's fragment, or A's
        // entry in the atomic list and A's three annotations are removed as fields the manager no
        // longer applies.
        var live = LiveVpc(WithFragmentOf(PeeringA));

        var b = CoWrite(PeeringB, live).Build();

        using var document = JsonDocument.Parse(b.Body);
        var peerings = document.RootElement.GetProperty("spec").GetProperty("vpcPeerings");

        peerings.GetArrayLength().ShouldBe(2, "A's entry and B's entry, in co-writer order");
        peerings[0].GetProperty("remoteVpc").GetString().ShouldBe("spoke-a-vpc");
        peerings[1].GetProperty("remoteVpc").GetString().ShouldBe("spoke-b-vpc");

        b.Annotations.Keys.ShouldContain(KubeLabels.FragmentAnnotation(PeeringA.Id));
        b.Annotations.Keys.ShouldContain(KubeLabels.FragmentHashAnnotation(PeeringA.Id));
        b.Annotations.Keys.ShouldContain(KubeLabels.FragmentPathAnnotation(PeeringA.Id));
        b.Annotations[KubeLabels.FragmentPathAnnotation(PeeringA.Id)].ShouldBe(PeeringA.Path);
        b.Annotations.Keys.ShouldContain(KubeLabels.FragmentAnnotation(PeeringB.Id));
    }

    [Fact]
    public void TheUnionIsInCoWriterOrderWhicheverCoWriterApplies() {
        // The union is one atomic value; an order that depended on who applied last would make
        // every apply an Updated that changed nothing.
        var fromA = CoWrite(PeeringA, LiveVpc(WithFragmentOf(PeeringB))).Build();
        var fromB = CoWrite(PeeringB, LiveVpc(WithFragmentOf(PeeringA))).Build();

        Spec(fromA).ShouldBe(Spec(fromB));
    }

    [Fact]
    public void ObjectsMergeRecursivelyAndArraysConcatenate() {
        var live = LiveVpc(
            annotations: new() {
                [KubeLabels.FragmentAnnotation(PeeringB.Id)] =
                    """{"spec":{"staticRoutes":[{"cidr":"10.2.0.0/16","nextHopIP":"10.0.0.6"}],"vpcPeerings":[{"remoteVpc":"spoke-b-vpc"}],"policyRoutes":{"b":true}}}"""
            }
        );

        var a = Builder(PeeringA, live)
            .ObjectJson(
                """{ "spec": { "staticRoutes": [ { "cidr": "10.1.0.0/16", "nextHopIP": "10.0.0.2" } ], "policyRoutes": { "a": true } } }"""
            )
            .Build();

        var spec = JsonNode.Parse(a.Body)!["spec"]!.AsObject();

        spec["staticRoutes"]!.AsArray().Count.ShouldBe(2);
        spec["vpcPeerings"]!.AsArray().Count.ShouldBe(1, "B's list survives although A contributed nothing to it");
        spec["policyRoutes"]!["a"]!.GetValue<bool>().ShouldBeTrue();
        spec["policyRoutes"]!["b"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void TwoFragmentsSettingOneScalarDifferentlyIsARefusalNamingThePathAndBothCoWriters() {
        var live = LiveVpc(
            annotations: new() {
                [KubeLabels.FragmentAnnotation(PeeringB.Id)] = """{"spec":{"enableExternal":false}}"""
            }
        );

        var refused = Builder(PeeringA, live).ObjectJson("""{ "spec": { "enableExternal": true } }""").TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("/spec/enableExternal");
        refused.Error.Message.ShouldContain(KubeLabels.GuidValue(PeeringA.Id));
        refused.Error.Message.ShouldContain(KubeLabels.GuidValue(PeeringB.Id));

        // And agreeing is fine — a scalar two co-writers both want is one value.
        Builder(PeeringA, live).ObjectJson("""{ "spec": { "enableExternal": false } }""").TryBuild()
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void AFragmentAnnotationThatIsNotJsonIsRefusedRatherThanSkipped() {
        // Skipping it would apply a union WITHOUT that co-writer's fragment — the prune.
        var live = LiveVpc(
            annotations: new() { [KubeLabels.FragmentAnnotation(PeeringB.Id)] = "not json at all" }
        );

        var refused = CoWrite(PeeringA, live).TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain(KubeLabels.FragmentAnnotation(PeeringB.Id));
        refused.Error.Message.ShouldContain("prune");
    }

    [Fact]
    public void ReApplyingTheSameFragmentOverItsOwnPreviousOneDoesNotDoubleIt() {
        // A's previous fragment is on the object; A applies again. The stored copy under A's own key
        // is A's previous state, not another co-writer's, so it is replaced rather than merged.
        var live = LiveVpc(WithFragmentOf(PeeringA));

        var again = CoWrite(PeeringA, live).Build();

        JsonNode.Parse(again.Body)!["spec"]!["vpcPeerings"]!.AsArray().Count.ShouldBe(1);
    }

    // ── Teardown: a withdrawal removes only this co-writer's fragment ───────────────────────────

    [Fact]
    public async Task AWithdrawalAppliesTheOthersUnionWithoutThisCoWritersFragmentOrAnnotations() {
        var connection = new RecordingConnection();
        var live = LiveVpc(WithFragmentOf(PeeringA, PeeringB));

        var result = await KubeCommand.For(connection)
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(Vpcs)
            .CoWriting(live)
            .DeleteAsync(CascadePolicy.Foreground, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        // ⚠ Applied, not deleted. The object is the owner's.
        connection.Deleted.ShouldBeEmpty("a co-owned DeleteAsync must never delete the owner's object");
        connection.Applied.Count.ShouldBe(1);

        var withdrawal = connection.Applied[0];
        withdrawal.IsCoOwned.ShouldBeTrue();
        withdrawal.FieldManager.ShouldBe(CoWrite(PeeringA, live).Build().FieldManager);

        var spec = JsonNode.Parse(withdrawal.Body)!["spec"]!.AsObject();
        var peerings = spec["vpcPeerings"]!.AsArray();

        peerings.Count.ShouldBe(1, "only B's entry remains");
        peerings[0]!["remoteVpc"]!.GetValue<string>().ShouldBe("spoke-b-vpc");

        withdrawal.Annotations.Keys.ShouldNotContain(KubeLabels.FragmentAnnotation(PeeringA.Id));
        withdrawal.Annotations.Keys.ShouldNotContain(KubeLabels.FragmentHashAnnotation(PeeringA.Id));
        withdrawal.Annotations.Keys.ShouldNotContain(KubeLabels.FragmentPathAnnotation(PeeringA.Id));
        withdrawal.Annotations.Keys.ShouldContain(KubeLabels.FragmentAnnotation(PeeringB.Id));
        withdrawal.Annotations.Keys.ShouldContain(KubeLabels.FragmentHashAnnotation(PeeringB.Id));

        JsonNode.Parse(withdrawal.Body)!["metadata"]!["resourceVersion"]!.GetValue<string>().ShouldBe(live.ResourceVersion);
    }

    [Fact]
    public async Task TheLastCoWritersWithdrawalAppliesABareBodySoTheManagerOwnsNothing() {
        var connection = new RecordingConnection();

        var result = await KubeCommand.For(connection)
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(Vpcs)
            .CoWriting(LiveVpc(WithFragmentOf(PeeringA)))
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);

        var body = JsonNode.Parse(connection.Applied[0].Body)!.AsObject();

        body.ContainsKey("spec").ShouldBeFalse("nothing of anybody's remains to apply");
        body["metadata"]!.AsObject().ContainsKey("annotations").ShouldBeFalse();
        body["metadata"]!["name"]!.GetValue<string>().ShouldBe("hub-vpc");
        body["metadata"]!["resourceVersion"].ShouldNotBeNull();
        connection.Applied[0].Annotations.ShouldBeEmpty();
        connection.Applied[0].Labels.ShouldBeEmpty();
    }

    [Fact]
    public async Task AWithdrawalWithABodySetIsRefused() {
        var connection = new RecordingConnection();

        var result = await CoWrite(PeeringA, LiveVpc(), connection)
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain("Drop the Object/ObjectJson call");
        connection.Applied.ShouldBeEmpty();
        connection.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task AStaleWithdrawalIsAPreconditionFailedAndAConflictIsAConflict() {
        // The interface's DeleteAsync answers a bare Result, so the "not applied" outcomes have to
        // arrive as coded failures rather than as a success that withdrew nothing.
        var stale = new RecordingConnection { Answer = ApplyResult.Stale };

        var refused = await KubeCommand.For(stale)
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(Vpcs)
            .CoWriting(LiveVpc(WithFragmentOf(PeeringA)))
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);

        var conflicting = new RecordingConnection { Answer = ApplyResult.Conflict };

        var conflicted = await KubeCommand.For(conflicting)
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(Vpcs)
            .CoWriting(LiveVpc(WithFragmentOf(PeeringA)))
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        conflicted.IsFailure.ShouldBeTrue();
        conflicted.Error!.Code.ShouldBe(ErrorCode.Conflict);
    }

    // ── The other refusals ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChartIsRefusedInTheCoOwnedMode() {
        var refused = KubeCommand.For(new NullConnection())
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(Vpcs)
            .CoWriting(LiveVpc())
            .Chart("managed/kube-ovn-vpc", JsonDocument.Parse("{}").RootElement)
            .TryBuild();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("Chart");
    }

    [Fact]
    public void ForceIsStillFalseAndTheCoOwnedModeAddsNoWayToSetIt() {
        CoWrite(PeeringA, LiveVpc()).Build().Force.ShouldBeFalse();

        typeof(IKubeCommandBuilder).GetMethods()
            .ShouldNotContain(m => m.Name.Contains("Force", StringComparison.OrdinalIgnoreCase));
    }

    // ── Over the tunnel ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACoOwnedCommandRoundTripsThroughTheTunnelWithoutLabelsAndWithItsOwner() {
        // ⚠ The agent re-checks the seven labels on every command it receives; a co-owned command
        // carries none by design, so the check has to know the shape or every peering would be
        // refused at the agent.
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        var back = KubeCommandJson.FromJson(KubeCommandJson.ToJson(command));

        back.IsSuccess.ShouldBeTrue(back.Error?.Message);
        back.GetValueOrThrow().OwnerResourceId.ShouldBe(Owner.Id);
        back.GetValueOrThrow().IsCoOwned.ShouldBeTrue();
        back.GetValueOrThrow().Labels.ShouldBeEmpty();
        back.GetValueOrThrow().FieldManager.ShouldBe(command.FieldManager);
    }

    [Fact]
    public void TheAgentRefusesACoOwnedCommandThatCarriesLabels() {
        // A co-writer claiming the owner's identity is the FieldManagerConflict this mode avoids;
        // the agent refuses the shape rather than applying it.
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var wire = JsonNode.Parse(KubeCommandJson.ToJson(command))!.AsObject();

        wire["labels"]!.AsObject()[KubeLabels.TenantId] = KubeLabels.GuidValue(Tenant);

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("co-owned");
    }

    [Fact]
    public void TheAgentRefusesAnOrdinaryCommandThatMerelyClaimsAnOwner() {
        // ⚠ THE HOLE THE #89 REVIEW NAMED. Setting ownerResourceId on a command switched the agent's
        // seven-label check off, and "no labels" was the only thing it asked of the co-owned shape —
        // so a control-plane bug that set the field on an ordinary command, labels dropped, would
        // have applied an unlabelled body under the owner's own manager onto any object by name.
        // The agent now asks for the shape the builder produces: this one has the ordinary manager
        // and no resourceVersion, and is refused on the first.
        var ordinary = KubeCommand.For(new RecordingConnection())
            .WithTenantId(Owner.TenantId)
            .WithResourceId(Owner)
            .WithKind(Vpcs)
            .ObjectJson("""{ "metadata": { "name": "hub-vpc" }, "spec": { "namespaces": [ "tenant-space" ] } }""")
            .Build();

        var wire = JsonNode.Parse(KubeCommandJson.ToJson(ordinary))!.AsObject();
        wire["ownerResourceId"] = KubeLabels.GuidValue(PeeringA.Id);
        wire["labels"] = new JsonObject();

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue("an ordinary command with an owner stamped on it is not a co-owned command");
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("field manager");
        refused.Error.Message.ShouldContain("KubeLabels.CoWriterFieldManager");
    }

    [Fact]
    public void TheAgentRefusesACoOwnedCommandWhoseManagerIsNotDerivedFromTheOwnerItClaims() {
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var wire = JsonNode.Parse(KubeCommandJson.ToJson(command))!.AsObject();

        // The right shape of name, the wrong owner in it.
        wire["fieldManager"] = KubeLabels.CoWriterFieldManager(KubeLabels.ResourceTypeValue(Owner.Type), KubeLabels.GuidValue(PeeringB.Id));

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain(KubeLabels.GuidValue(Owner.Id), customMessage: "the message names the owner the manager should be derived from");
    }

    [Fact]
    public void TheAgentRefusesACoOwnedCommandWithoutTheLiveResourceVersion() {
        // Without the version there is no optimistic lock, and two co-writers racing onto one
        // object would each apply a union computed from a version the other replaced.
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var wire = JsonNode.Parse(KubeCommandJson.ToJson(command))!.AsObject();

        var body = JsonNode.Parse(wire["body"]!.GetValue<string>())!.AsObject();
        body["metadata"]!.AsObject().Remove("resourceVersion");
        wire["body"] = body.ToJsonString();

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("resourceVersion");
    }

    [Fact]
    public void TheAgentRefusesAnApplyThatCarriesNoFragmentBookkeepingOfItsOwn() {
        // A hash with no fragment annotation beside it is a slice the next co-writer's union will
        // not include — the prune the mode exists to prevent, arriving by the wire.
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var wire = JsonNode.Parse(KubeCommandJson.ToJson(command))!.AsObject();

        wire["annotations"]!.AsObject().Remove(KubeLabels.FragmentAnnotation(PeeringA.Id));

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("fragment bookkeeping");
    }

    [Fact]
    public void TheAgentRefusesACoOwnedCommandWhoseBodyCarriesLabels() {
        // The wire's `labels` map is empty and the body smuggles them in instead: the same claim on
        // the owner's identity, one level down.
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var wire = JsonNode.Parse(KubeCommandJson.ToJson(command))!.AsObject();

        var body = JsonNode.Parse(wire["body"]!.GetValue<string>())!.AsObject();
        body["metadata"]!.AsObject()["labels"] = new JsonObject { [KubeLabels.ResourceId] = KubeLabels.GuidValue(PeeringA.Id) };
        wire["body"] = body.ToJsonString();

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("metadata.labels");
    }

    [Fact]
    public async Task AWithdrawalRoundTripsThroughTheTunnelWithNoBookkeepingOfItsOwn() {
        // The other shape the builder produces: no hash, none of its own three annotations, the
        // others' carried. The agent's check has to admit it, or every teardown is refused at the
        // agent and a peering can never be deleted.
        var connection = new RecordingConnection();

        await KubeCommand.For(connection)
            .WithTenantId(PeeringA.TenantId)
            .WithResourceId(PeeringA)
            .WithKind(Vpcs)
            .CoWriting(LiveVpc(WithFragmentOf(PeeringA, PeeringB)))
            .DeleteAsync(cancellationToken: TestContext.Current.CancellationToken);

        var withdrawal = connection.Applied.ShouldHaveSingleItem();
        withdrawal.ReconcileHash.ShouldBeEmpty();

        var back = KubeCommandJson.FromJson(KubeCommandJson.ToJson(withdrawal));

        back.IsSuccess.ShouldBeTrue(back.Error?.Message);
        back.GetValueOrThrow().Annotations.Keys.ShouldContain(KubeLabels.FragmentAnnotation(PeeringB.Id));
        back.GetValueOrThrow().Annotations.Keys.ShouldNotContain(KubeLabels.FragmentAnnotation(PeeringA.Id));
    }

    [Fact]
    public void AWithdrawalThatKeepsItsOwnBookkeepingIsRefused() {
        // No hash says "withdrawal"; its own fragment annotation still present says "apply". A
        // command that is both withdraws nothing, and the agent says so rather than guessing.
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var wire = JsonNode.Parse(KubeCommandJson.ToJson(command))!.AsObject();

        wire["reconcileHash"] = string.Empty;

        var refused = KubeCommandJson.FromJson(wire.ToJsonString());

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("withdrawal");
    }

    // ── Against the live object — the client's half of the check ───────────────────────────────

    [Fact]
    public void TheBuildersOwnCommandPassesBothChecksAgainstTheObjectItWasBuiltFrom() {
        var live = LiveVpc(WithFragmentOf(PeeringB));
        var command = CoWrite(PeeringA, live).Build();

        command.CheckCoOwnedShape().IsSuccess.ShouldBeTrue(command.CheckCoOwnedShape().Error?.Message);
        command.CheckCoOwnedAgainst(live).IsSuccess.ShouldBeTrue(command.CheckCoOwnedAgainst(live).Error?.Message);
    }

    [Fact]
    public void AnOrdinaryCommandPassesBothChecksBecauseNeitherApplies() {
        var ordinary = KubeCommand.For(new RecordingConnection())
            .WithTenantId(Owner.TenantId)
            .WithResourceId(Owner)
            .WithKind(Vpcs)
            .ObjectJson("""{ "metadata": { "name": "hub-vpc" }, "spec": {} }""")
            .Build();

        ordinary.CheckCoOwnedShape().IsSuccess.ShouldBeTrue();
        ordinary.CheckCoOwnedAgainst(LiveVpc()).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ANameTakenByAnotherResourceBetweenTheReadAndTheApplyIsAConflictNamingBoth() {
        // ⚠ The replaced-owner case: the command was built from a read of the owner's object, the
        // owner deleted it, and another resource created one under the same name. The command's
        // claim and the object's label disagree, and the client that read the object a moment
        // before the PATCH is the only thing placed to notice.
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        var replacement = JsonNode.Parse(LiveVpc().Json)!.AsObject();
        replacement["metadata"]!["labels"]![KubeLabels.ResourceId] = KubeLabels.GuidValue(PeeringB.Id);
        var taken = LiveVpc() with { Json = replacement.ToJsonString() };

        var refused = command.CheckCoOwnedAgainst(taken);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.Conflict);
        refused.Error.Message.ShouldContain(KubeLabels.GuidValue(Owner.Id));
        refused.Error.Message.ShouldContain(KubeLabels.GuidValue(PeeringB.Id));
    }

    [Fact]
    public void AnObjectInAnotherTenantFailsTheLiveCheckEvenWhenTheOwnerMatches() {
        var command = CoWrite(PeeringA, LiveVpc()).Build();
        var elsewhere = LiveVpc(tenant: Guid.Parse("0d0d0d0d-0d0d-4d0d-8d0d-0d0d0d0d0d0d"));

        var refused = command.CheckCoOwnedAgainst(elsewhere);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("across a tenant");
    }

    [Fact]
    public void AManagerThatIsNotTheOneDerivedFromTheObjectFailsTheLiveCheck() {
        // The shape check accepts any cybercloud/{type}/{ownerId} for the claimed owner; the type
        // half is the object's to confirm, because the shape alone cannot know it.
        var command = CoWrite(PeeringA, LiveVpc()).Build();

        var retyped = JsonNode.Parse(LiveVpc().Json)!.AsObject();
        retyped["metadata"]!["labels"]![KubeLabels.ResourceType] = "cybercloud.network_somethingelse";
        var live = LiveVpc() with { Json = retyped.ToJsonString() };

        var refused = command.CheckCoOwnedAgainst(live);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("cybercloud/cybercloud.network_somethingelse/");
    }

    [Fact]
    public void TheManagerNameRoundTripsAndTheOwnersOwnDoesNotParse() {
        KubeLabels.TryReadCoWriterFieldManager(
            KubeLabels.CoWriterFieldManager("cybercloud.network_virtualnetworks", KubeLabels.GuidValue(Owner.Id)),
            out var type,
            out var id
        ).ShouldBeTrue();

        type.ShouldBe("cybercloud.network_virtualnetworks");
        id.ShouldBe(Owner.Id);

        KubeLabels.TryReadCoWriterFieldManager("cybercloud/cybercloud.network", out _, out _).ShouldBeFalse("the owner's own manager has one segment");
        KubeLabels.TryReadCoWriterFieldManager("cybercloud/a/b/" + KubeLabels.GuidValue(Owner.Id), out _, out _).ShouldBeFalse("three segments is not the shape");
        KubeLabels.TryReadCoWriterFieldManager("cybercloud/type/not-a-guid", out _, out _).ShouldBeFalse();
        KubeLabels.TryReadCoWriterFieldManager("cybercloud/type/" + KubeLabels.GuidValue(Guid.Empty), out _, out _).ShouldBeFalse("an empty owner is no owner");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    static string Spec(KubeCommand command) => JsonNode.Parse(command.Body)!["spec"]!.ToJsonString();

    /// <summary>A builder in the co-owned mode with no body yet.</summary>
    static IKubeCommandBuilder Builder(ResourceId writer, KubeObject live, IKubeClusterConnection? connection = null) =>
        KubeCommand.For(connection ?? new NullConnection())
            .WithTenantId(writer.TenantId)
            .WithResourceId(writer)
            .WithKind(Vpcs)
            .CoWriting(live);

    /// <summary>A builder in the co-owned mode carrying <paramref name="writer" />'s peering entry.</summary>
    static IKubeCommandBuilder CoWrite(ResourceId writer, KubeObject live, IKubeClusterConnection? connection = null) =>
        Builder(writer, live, connection).ObjectJson(FragmentOf(writer));

    static string FragmentOf(ResourceId writer) =>
        writer.Id == PeeringA.Id
            ? """{ "spec": { "vpcPeerings": [ { "remoteVpc": "spoke-a-vpc", "localConnectIP": "10.0.0.1/30" } ] } }"""
            : """{ "spec": { "vpcPeerings": [ { "remoteVpc": "spoke-b-vpc", "localConnectIP": "10.0.0.5/30" } ] } }""";

    /// <summary>The annotations an object carries after each of <paramref name="writers" /> applied its fragment.</summary>
    static Dictionary<string, string> WithFragmentOf(params ResourceId[] writers) {
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var writer in writers) {
            var canonical = JsonNode.Parse(FragmentOf(writer))!.ToJsonString();
            annotations[KubeLabels.FragmentAnnotation(writer.Id)] = canonical;
            annotations[KubeLabels.FragmentHashAnnotation(writer.Id)] = KubeLabels.ReconcileHash(canonical);
            annotations[KubeLabels.FragmentPathAnnotation(writer.Id)] = writer.Path;
        }

        return annotations;
    }

    /// <summary>The owner's Vpc as a read would return it: the seven labels, its two annotations, a version.</summary>
    static KubeObject LiveVpc(
        Dictionary<string, string>? annotations = null,
        string resourceVersion = "12",
        Guid? tenant = null
    ) {
        var labels = new JsonObject {
            [KubeLabels.TenantId] = KubeLabels.GuidValue(tenant ?? Tenant),
            [KubeLabels.SubscriptionId] = KubeLabels.GuidValue(Subscription),
            [KubeLabels.ResourceGroup] = "prod",
            [KubeLabels.ResourceId] = KubeLabels.GuidValue(Owner.Id),
            [KubeLabels.ResourceType] = KubeLabels.ResourceTypeValue(Owner.Type),
            [KubeLabels.ApiVersion] = "2026-08-01",
            [KubeLabels.ManagedBy] = KubeLabels.ManagedByValue
        };

        var annotationNode = new JsonObject {
            [KubeLabels.ResourcePathAnnotation] = Owner.Path,
            [KubeLabels.ReconcileHashAnnotation] = "sha256:owner"
        };

        foreach (var (key, value) in annotations ?? []) {
            annotationNode[key] = value;
        }

        var metadata = new JsonObject {
            ["name"] = "hub-vpc", ["labels"] = labels, ["annotations"] = annotationNode
        };

        if (resourceVersion.Length > 0) {
            metadata["resourceVersion"] = resourceVersion;
        }

        var document = new JsonObject {
            ["apiVersion"] = "kubeovn.io/v1",
            ["kind"] = "Vpc",
            ["metadata"] = metadata,
            ["spec"] = new JsonObject { ["namespaces"] = new JsonArray("tenant-space") }
        };

        return new() {
            Ref = new() { Kind = Vpcs, Namespace = string.Empty, Name = "hub-vpc" },
            Json = document.ToJsonString(),
            ResourceVersion = resourceVersion
        };
    }

    /// <summary>Records what the builder sends, and answers what it is told to.</summary>
    sealed class RecordingConnection : IKubeClusterConnection {
        public List<KubeCommand> Applied { get; } = [];

        public List<KubeCommand> Deleted { get; } = [];

        public ApplyResult Answer { get; init; } = ApplyResult.Updated;

        public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

        public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
            Applied.Add(command);

            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = Answer,
                        Target = command.Target,
                        ResourceVersion = "13",
                        Drift = Answer == ApplyResult.Conflict
                            ? new() { ResourceId = command.ResourceId, Target = command.Target, FieldManager = command.FieldManager }
                            : null
                    }
                )
            );
        }

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, "not here"));

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) {
            Deleted.Add(command);
            return Task.FromResult(Result.Success);
        }
    }
}
