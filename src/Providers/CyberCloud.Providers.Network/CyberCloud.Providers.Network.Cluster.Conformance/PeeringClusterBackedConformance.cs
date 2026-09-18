using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Conformance;
using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Network.Conformance;
using CyberCloud.Providers.Network.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Drift;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.Tenancy.Contracts;
using Shouldly;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.ClusterConformance;

/// <summary>
///     The cluster-backed half against <c>CyberCloud.Network/virtualNetworks/peerings</c> — a second
///     writer's contract, held against a real API server.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NOT A <c>ClusterConformanceTests</c> DERIVATION, AND THAT IS A GAP RECORDED RATHER
///         THAN HIDDEN.</b> Every other class in this assembly is one line: the shared cluster suite
///         over the type's case. That suite presumes the type <i>owns</i> what it applies — it
///         asserts <c>cybercloud.io/resource-id</c> equals the resource's on every object, takes the
///         tenant-id label with a rival manager to provoke a conflict, and <c>kubectl delete</c>s the
///         objects expecting the reconciler to put them back and the drift scan to call the resource
///         a stray. Every one of those is the opposite reading for a co-writer, and the Docker-free
///         suite grew a branch per object (<c>ProviderConformanceTests.IsCoOwned</c>) while this one
///         did not. So this class asserts the co-writer's own contract against k3s directly, over
///         the same fixture: <c>charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed</c>,
///         <c>the-shared-cluster-suite-presumes-ownership</c>.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS PROVES AND WHAT IT DOES NOT.</b> The k3s has <b>no Kube-OVN</b>. What is
///         established: two co-owned applies land on two real cluster-scoped objects under the
///         co-writer manager the API server records in <c>managedFields</c> as an <c>Apply</c>; the
///         owners' seven labels survive them; the fragment bookkeeping is on the stored objects; the
///         teardown withdraws and both <c>Vpc</c>s stand; the drift scan reads the fragments off a
///         real <c>LIST</c> and joins the peering on them. What is <b>not</b> established: that OVN
///         builds the peer ports or that a packet crosses — <c>status.vpcPeerings</c> stays empty
///         here for the life of the resource, and <c>routing-is-unproven-until-the-vm-lane</c>
///         (#95) says where that is measured.
///     </para>
/// </remarks>
/// <param name="fixture">The harness, with <c>ancestor-0</c> and the sibling <c>spoke</c> created.</param>
public sealed class VirtualNetworkPeeringClusterBackedConformance(
    ClusterConformanceFixture<VirtualNetworkPeeringCase> fixture
) : IClassFixture<ClusterConformanceFixture<VirtualNetworkPeeringCase>> {
    const int MaxDrives = 40;

    static ProviderConformanceCase Case => VirtualNetworkPeeringCase.ProviderCase;

    [Fact]
    public async Task APeeringLandsOnBothRealVpcsUnderTheOwnersManagerAndWithdrawsFromBoth() {
        var harness = fixture.Require(
            "that a co-owned apply reaches two real cluster-scoped Vpcs under the co-writer manager "
            + "the API server records, leaves the owners' seven labels standing, writes the fragment "
            + "bookkeeping, and withdraws from both on teardown without deleting either."
        );

        var token = TestContext.Current.CancellationToken;
        const string name = "real-peering";

        var accepted = (await harness.Manager.WriteAsync(
            new() {
                Path = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Verb = WriteVerb.Put,
                Body = Case.Body(ClusterConformanceHarness<VirtualNetworkPeeringCase>.ClusterId),
                Caller = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Caller()
            },
            token
        )).GetValueOrThrow();

        var status = await ConvergeAsync(harness, accepted.OperationId);
        status.State.ShouldBe(OperationState.Succeeded, $"the peering ended {status.State} against a real API server: {status.Error?.Message}");

        var address = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address(name).WithId(accepted.Resource.Id);
        var ns = ReconcileDriver.NamespaceFor(address);
        var objects = Case.Objects(address, ns);
        objects.Length.ShouldBe(2);

        using var desired = JsonDocument.Parse(Case.Body(ClusterConformanceHarness<VirtualNetworkPeeringCase>.ClusterId));

        foreach (var target in objects) {
            // ⚠ READ AROUND EVERY LINE OF OUR OWN CODE, with the raw client.
            var json = await ReadAsync(harness, target, token);
            json.ShouldNotBeNull($"'{target}' is not in the real cluster");

            var root = JsonNode.Parse(json)!.AsObject();
            var metadata = root["metadata"]!.AsObject();
            var labels = metadata["labels"]!.AsObject();
            var annotations = metadata["annotations"]!.AsObject();

            // The owner's identity, untouched by two co-owned applies.
            foreach (var label in KubeLabels.Mandatory) {
                labels[label].ShouldNotBeNull($"'{target}' lost its owner's '{label}' to the co-owned apply");
            }

            labels[KubeLabels.ResourceId]!.GetValue<string>().ShouldNotBe(KubeLabels.GuidValue(accepted.Resource.Id), "a co-writer writes no labels");
            labels[KubeLabels.ResourceType]!.GetValue<string>().ShouldBe(KubeLabels.ResourceTypeValue(VirtualNetworks.Type));

            // The fragment bookkeeping, stored by the API server.
            annotations[KubeLabels.FragmentAnnotation(accepted.Resource.Id)].ShouldNotBeNull($"'{target}' carries no fragment of the peering's");
            annotations[KubeLabels.FragmentHashAnnotation(accepted.Resource.Id)]!.GetValue<string>().ShouldStartWith("sha256:");
            annotations[KubeLabels.ReconcileHashAnnotation].ShouldNotBeNull("the owner's own annotation stays beside the fragment's");

            // ⚠ THE MANAGER, as the API SERVER recorded it: one entry named for the OWNER, operation
            // Apply — the property CoOwnedApplyTests measured a manager-per-peering to lack.
            var ownerId = labels[KubeLabels.ResourceId]!.GetValue<string>();
            var manager = KubeLabels.CoWriterFieldManager(labels[KubeLabels.ResourceType]!.GetValue<string>(), ownerId);

            var managed = metadata["managedFields"]!.AsArray().Select(x => x!.AsObject()).ToList();

            managed.ShouldContain(
                x => x["manager"]!.GetValue<string>() == manager && x["operation"]!.GetValue<string>() == "Apply",
                $"the API server recorded no Apply entry for '{manager}' on '{target}'. managedFields holds: "
                + string.Join(", ", managed.Select(x => x["manager"]?.GetValue<string>()))
            );

            // And the slice is what the case says it should be, on the right side.
            Case.ObjectMatchesDesired(
                new() { ObjectJson = json, DesiredJson = desired.RootElement.GetRawText(), Id = address, Target = target, Namespace = ns }
            ).ShouldBeTrue($"'{target}' does not carry the peering's entries: {json}");

            // ⚠ AND NOTHING CONNECTED THEM, which is the honest reading of a cluster with no Kube-OVN.
            VirtualNetworkPeerings.ConnectedPeers(json).ShouldBeEmpty("there is no controller in this k3s to build a peer port");
        }

        // ── The drift scan joins a co-writer on its fragment, off a real LIST ──────────────────
        var inventory = new ListBackedClusterObjectInventory(harness.Api, [VirtualNetworks.VpcKind], string.Empty);
        var seen = (await inventory.ListManagedAsync(ClusterConformanceHarness<VirtualNetworkPeeringCase>.ClusterId, token)).GetValueOrThrow();

        seen.ShouldContain(x => x.Fragments.Any(f => f.Writer == accepted.Resource.Id), "the real LIST did not surface the peering's fragment annotations");

        // ⚠ ONE HASH PER OBJECT, off the last co-owned apply onto each. The two fragments are mirror
        // images and hash differently; this test once took the LAST apply's hash for both and
        // asserted only strays and orphans, which is how "every converged peering is Diverged on
        // every scan" went unseen until the #31 review. The assertion is now the whole report.
        var fragments = harness.Connection.Applied
            .Where(x => x.IsCoOwned && x.ResourceId == accepted.Resource.Id)
            .GroupBy(x => x.Target)
            .Select(x => new ExpectedFragment(x.Key, x.Last().ReconcileHash))
            .ToImmutableArray();

        fragments.Length.ShouldBe(2, "a peering co-writes exactly two objects");
        fragments.Select(x => x.Hash).Distinct().Count().ShouldBe(2, "the two fragments are mirror images and cannot hash the same");

        var report = new DriftScanner(harness.Clock).Scan(
            ClusterConformanceHarness<VirtualNetworkPeeringCase>.ClusterId,
            seen,
            [new ExpectedResource(accepted.Resource.Id, address.Path, "sha256:not-what-a-fragment-is-judged-by", ProvisioningState.Succeeded, fragments)]
        );

        report.Findings.Where(x => x.ResourceId == accepted.Resource.Id).ShouldBeEmpty(
            "a converged peering owns no labelled object, its grain exists, and each Vpc carries the fragment expected on it — "
            + "so it is neither a stray, nor an orphan, nor diverged: " + string.Join(" | ", report.Findings)
        );

        // ── delete → withdrawn, and both networks stand ──────────────────────────────────────
        var deleted = await harness.Manager.DeleteAsync(
            new() {
                Path = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Caller()
            },
            token
        );

        deleted.IsSuccess.ShouldBeTrue(deleted.Error?.Message);

        var teardown = await ConvergeAsync(harness, deleted.GetValueOrThrow().OperationId);
        teardown.State.ShouldBe(OperationState.Succeeded, $"the withdrawal ended {teardown.State}: {teardown.Error?.Message}");

        foreach (var target in objects) {
            var remaining = await ReadAsync(harness, target, token);
            remaining.ShouldNotBeNull($"'{target}' is gone after a peering's teardown — a co-writer never deletes the owner's object");

            var root = JsonNode.Parse(remaining)!.AsObject();
            var annotations = root["metadata"]!["annotations"]!.AsObject();

            annotations.ContainsKey(KubeLabels.FragmentAnnotation(accepted.Resource.Id)).ShouldBeFalse($"'{target}' still carries the peering's fragment");
            annotations[KubeLabels.ReconcileHashAnnotation].ShouldNotBeNull("the owner's own annotation survives the withdrawal");
            root["metadata"]!["labels"]![KubeLabels.ResourceId].ShouldNotBeNull("the owner's labels survive the withdrawal");
            root["spec"]!["enableExternal"].ShouldNotBeNull("the owner's own field survives the withdrawal");

            // ⚠ The API server drops what the shared manager stopped applying: the two lists are gone
            // or empty, not left holding the withdrawn entries.
            (root["spec"]!["vpcPeerings"] is JsonArray peerings && peerings.Count > 0)
                .ShouldBeFalse($"'{target}' still carries a vpcPeerings entry after the withdrawal: {remaining}");
        }

        // The scan after the withdrawal sees no fragment of the peering's anywhere.
        var after = (await inventory.ListManagedAsync(ClusterConformanceHarness<VirtualNetworkPeeringCase>.ClusterId, token)).GetValueOrThrow();
        after.ShouldNotContain(x => x.Fragments.Any(f => f.Writer == accepted.Resource.Id), "a withdrawn fragment is still on a real object");
    }

    [Fact]
    public async Task TwoPeeringsOnOneNetworkShareTheListAndWithdrawingOneLeavesTheOther() {
        // ⚠ THE SIBLING CASE, against the real atomic list. Two peerings hang off ancestor-0 and both
        // name spoke — on a real Kube-OVN the second would be refused as a duplicate port, and here,
        // with no controller, it is exactly the two-co-writers-one-atomic-list shape the co-owned mode
        // exists for: both entries on both Vpcs, under ONE manager, and the first peering's teardown
        // takes only its own entry away.
        var harness = fixture.Require(
            "that two co-writers of one real Vpc share the co-writer manager on its atomic lists — "
            + "two entries, one manager — and that one's withdrawal leaves the other's entry standing."
        );

        var token = TestContext.Current.CancellationToken;

        var first = await CreateAsync(harness, "real-peering-a", "10.255.255.0/30", token);
        var second = await CreateAsync(harness, "real-peering-b", "10.255.255.4/30", token);

        var address = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address("real-peering-a").WithId(first.Resource.Id);
        var local = VirtualNetworkPeerings.LocalVpcRef(ReconcileDriver.NamespaceFor(address), address);

        var both = JsonNode.Parse((await ReadAsync(harness, local, token))!)!.AsObject();

        both["spec"]!["vpcPeerings"]!.AsArray().Count.ShouldBe(2, "two peerings, two entries on one atomic list");
        both["spec"]!["staticRoutes"]!.AsArray().Count.ShouldBe(2);

        var managers = both["metadata"]!["managedFields"]!.AsArray()
            .Select(x => x!["manager"]!.GetValue<string>())
            .Where(x => KubeLabels.TryReadCoWriterFieldManager(x, out _, out _))
            .Distinct()
            .ToList();

        managers.Count.ShouldBe(1, "every co-writer of one object applies under ONE manager named for the owner; found: " + string.Join(", ", managers));

        var annotations = both["metadata"]!["annotations"]!.AsObject();
        annotations.ContainsKey(KubeLabels.FragmentAnnotation(first.Resource.Id)).ShouldBeTrue();
        annotations.ContainsKey(KubeLabels.FragmentAnnotation(second.Resource.Id)).ShouldBeTrue();

        // Withdraw the first; the second's entry and bookkeeping stay.
        var deleted = (await harness.Manager.DeleteAsync(
            new() {
                Path = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address("real-peering-a").Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Caller()
            },
            token
        )).GetValueOrThrow();

        (await ConvergeAsync(harness, deleted.OperationId)).State.ShouldBe(OperationState.Succeeded);

        var one = JsonNode.Parse((await ReadAsync(harness, local, token))!)!.AsObject();

        one["spec"]!["vpcPeerings"]!.AsArray().Count.ShouldBe(1, "the first peering's entry went and the second's stayed");
        one["spec"]!["vpcPeerings"]![0]!["localConnectIP"]!.GetValue<string>().ShouldBe("10.255.255.5/30");
        one["metadata"]!["annotations"]!.AsObject().ContainsKey(KubeLabels.FragmentAnnotation(first.Resource.Id)).ShouldBeFalse();
        one["metadata"]!["annotations"]!.AsObject().ContainsKey(KubeLabels.FragmentAnnotation(second.Resource.Id)).ShouldBeTrue();

        // Not this test's subject: leave the fixture as it was found.
        var second2 = (await harness.Manager.DeleteAsync(
            new() {
                Path = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address("real-peering-b").Path,
                ApiVersion = Case.ApiVersion,
                Caller = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Caller()
            },
            token
        )).GetValueOrThrow();

        (await ConvergeAsync(harness, second2.OperationId)).State.ShouldBe(OperationState.Succeeded);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    static async Task<WriteAccepted> CreateAsync(
        ClusterConformanceHarness<VirtualNetworkPeeringCase> harness,
        string name,
        string link,
        CancellationToken token
    ) {
        var accepted = (await harness.Manager.WriteAsync(
            new() {
                Path = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Address(name).Path,
                ApiVersion = Case.ApiVersion,
                Verb = WriteVerb.Put,
                Body = VirtualNetworkPeerings.Body(
                    ClusterConformanceHarness<VirtualNetworkPeeringCase>.ClusterId,
                    remoteNetwork: VirtualNetworkPeeringCase.RemoteNetworkName,
                    linkV4: link
                ),
                Caller = ClusterConformanceHarness<VirtualNetworkPeeringCase>.Caller()
            },
            token
        )).GetValueOrThrow();

        var status = await ConvergeAsync(harness, accepted.OperationId);
        status.State.ShouldBe(OperationState.Succeeded, $"'{name}' ended {status.State}: {status.Error?.Message}");

        return accepted;
    }

    static async Task<OperationStatus> ConvergeAsync(ClusterConformanceHarness<VirtualNetworkPeeringCase> harness, Guid operationId) {
        var operation = harness.Operation(ConformanceIds.Tenant, operationId);
        OperationStatus? last = null;

        for (var i = 0; i < MaxDrives; i++) {
            last = (await operation.DriveAsync()).GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        last.ShouldNotBeNull();
        return last;
    }

    /// <summary>One cluster-scoped object, read with the raw client around every line of our own code.</summary>
    static async Task<string?> ReadAsync(
        ClusterConformanceHarness<VirtualNetworkPeeringCase> harness,
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        try {
            using var response = await harness.Raw.CustomObjects.GetClusterCustomObjectWithHttpMessagesAsync(
                target.Kind.Group,
                target.Kind.Version,
                target.Kind.Plural,
                target.Name,
                cancellationToken: cancellationToken
            );

            return ((JsonElement)response.Body!).GetRawText();
        } catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return null;
        }
    }
}

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the peering.</summary>
/// <remarks>
///     ⚠ The shared kill suite asks only that the objects are there and match after the successor
///     converges, which is true of a co-writer's two <c>Vpc</c>s exactly as of an owner's objects —
///     so this one IS the shared class, unlike the lifecycle above.
/// </remarks>
public sealed class VirtualNetworkPeeringSiloKillConformance : SiloKillConformanceTests<VirtualNetworkPeeringCase>;
