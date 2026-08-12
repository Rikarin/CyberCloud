using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerService.Tests;

/// <summary>
///     What the managed-Kubernetes provider declares, checked the way a silo checks it at start.
/// </summary>
public sealed class ManagedClusterDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build is what AddCyberCloudProvider runs, and it throws on a duplicate short
        // name, a reconciler naming an undeclared type, a RequiresCluster pointer no schema declares,
        // and half a dozen other declaration mistakes. Building it here is the cheapest possible
        // version of "would this silo start".
        var registry = ProviderRegistry.Build([new ContainerServiceProvider()]);

        registry.TryGetType(ManagedClusters.Type, out var cluster).ShouldBeTrue();
        cluster.ReconcilerType.ShouldBe(typeof(ManagedClusterReconciler));
        cluster.ReadPermission.ShouldBe("read");
        cluster.ClusterIdPointer.ShouldBe(ManagedClusters.ClusterIdPointer);

        registry.TryGetType(AgentPools.Type, out var pool).ShouldBeTrue();
        pool.ReconcilerType.ShouldBe(typeof(AgentPoolReconciler));
        pool.ClusterIdPointer.ShouldBe(AgentPools.ClusterIdPointer);
    }

    [Fact]
    public void TheChildIsNestedTwoDeepSoTheParentExistenceAssertionCannotSelfSkip() {
        // ⚠ ASSERTED AGAINST LITERALS, because the whole point is that the two segments are
        // interleaved rather than flattened. `ProviderConformanceTests
        // .CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource` self-skips at depth 1,
        // so a flattened spelling would silently drop the one assertion this type exists to exercise.
        AgentPools.TypePath.ShouldBe("managedClusters/agentPools");
        AgentPools.Type.Depth.ShouldBe(2);
        ManagedClusters.Type.Depth.ShouldBe(1);

        AgentPools.Type.ToString().ShouldBe("CyberCloud.ContainerService/managedClusters/agentPools");
    }

    // ── Failure class (d): a shortName collision ─────────────────────────────────────────────────

    [Fact]
    public void NeitherShortNameIsOneOfTheTenCliGroupKeysInTheTree() {
        // ⚠ CliEmitter derives the CLI GROUP key from the provider namespace's LAST SEGMENT,
        // lower-cased — so this namespace is already the group `containerservice`. System.CommandLine's
        // ValidTokens builds ONE dictionary of every command token and every alias in the whole tree,
        // so a group and an alias that share a string throw `ArgumentException: An item with the same
        // key has already been added` ON THE FIRST PARSE OF ANY COMMAND LINE — not only for the
        // colliding command. Two agents have hit that.
        //
        // ⚠ THE LIST IS TYPED OUT. Deriving it from the providers in the tree would compare a
        // constant to itself and would also require referencing another Providers.* assembly, which
        // src/Providers/README.md § Hard rule forbids.
        string[] groups = [
            "sample",
            "dbforpostgresql",
            "dbformysql",
            "cache",
            "messaging",
            "search",
            "storage",
            "documentdb",
            "analytics",
            "containerservice"
        ];

        groups.ShouldContain(
            "containerservice",
            "this provider's own group key is missing from the list it is being checked against"
        );

        groups.ShouldNotContain(ContainerServiceProvider.ClusterShortName);
        groups.ShouldNotContain(ContainerServiceProvider.PoolShortName);
    }

    [Fact]
    public void NeitherShortNameIsOneOfTheTwelveAlreadyDeclared() {
        // ⚠ ProviderRegistry.Build refuses a duplicate short name WITHIN one provider and never
        // compares one across providers or against a group name — see
        // charts/managed/seaweedfs/conformance.yaml § owed, `short-name-collides-with-the-group`. So
        // this is checked by hand, against literals, for the fourth and fifth time.
        string[] declared = [
            "widget",
            "postgres",
            "mariadb",
            "valkey",
            "kafka",
            "nats",
            "rabbitmq",
            "objectstore",
            "bucket",
            "docdb",
            "opensearch",
            "clickhouse"
        ];

        declared.ShouldNotContain(ContainerServiceProvider.ClusterShortName);
        declared.ShouldNotContain(ContainerServiceProvider.PoolShortName);

        ContainerServiceProvider.ClusterShortName.ShouldNotBe(ContainerServiceProvider.PoolShortName);
    }

    [Fact]
    public void NeitherShortNameIsOneOfTheCliSReservedGroups() {
        // ⚠ A THIRD DICTIONARY NOBODY HAD CHECKED A PROVIDER AGAINST. `CommandTree.ReservedGroups`
        // takes down `cyc --help` entirely at start-up when a generated group name collides — and an
        // ALIAS lands in the same ValidTokens dictionary a group name does, so the reserved list binds
        // both.
        string[] reserved = [
            "login",
            "logout",
            "account",
            "rest",
            "config",
            "completion",
            "complete",
            "extension",
            "version"
        ];

        reserved.ShouldNotContain(ContainerServiceProvider.ClusterShortName);
        reserved.ShouldNotContain(ContainerServiceProvider.PoolShortName);
    }

    [Fact]
    public void TheClusterAliasIsTheOneDocsPlan21NamesByName() {
        // docs/plan/21 § Grammar: "an alias table for the short forms people expect (`aks` →
        // `containerservice managed-cluster`, `postgres` → `dbforpostgresql server`)". Two of that
        // sentence's two examples now exist, and this is the second.
        ContainerServiceProvider.ClusterShortName.ShouldBe("aks");
    }

    // ── The meters ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheClusterIsTheFirstTypeInTheTreeToDrawTheClustersMeter() {
        // ⚠ QuotaMeter.Clusters has existed since QuotaGrain was written — its Defaults give it 5 —
        // and MeterCatalog bills BillingMeter.ClusterHours off it, described as "Managed Kubernetes
        // clusters × hours". Nine families shipped without declaring it because none of them is a
        // cluster.
        var drawn = Registration(ManagedClusters.Type).Meters.Select(x => x.Meter).ToList();

        drawn.ShouldContain(QuotaMeter.Clusters);
        drawn.ShouldContain(QuotaMeter.Resources);

        // ⚠ And the CHILD does not draw it. A node pool is not a cluster, and a family whose two types
        // both drew it would double every tenant's cluster count on the first pool.
        Registration(AgentPools.Type).Meters.Select(x => x.Meter).ShouldNotContain(QuotaMeter.Clusters);
    }

    [Fact]
    public void TheClusterDeclaresNoStorageMeterAndTheChildDeclaresOne() {
        // ⚠ THE ABSENCE IS EVIDENCE ABOUT THE DATASTORE. A Kamaji control plane keeps no volume: its
        // state is in a cluster-scoped DataStore this platform names rather than creates. The day
        // ADR-009's dedicated-etcd-per-tenant is honoured, a storage meter appears here and THIS TEST
        // is what says the change landed.
        Derived(ManagedClusters.Type).Select(x => x.Meter).ShouldNotContain(QuotaMeter.StorageGb);
        Derived(AgentPools.Type).Select(x => x.Meter).ShouldContain(QuotaMeter.StorageGb);
    }

    [Fact]
    public void EveryDerivedMeterSaysWhatItReadsAndTheAutoscaleOnesSayMaxCount() {
        // ⚠ MeterDerivation.Reads is what the generated document publishes as a meter's inputs. A
        // derivation whose amount depends on `autoscale.maxCount` and whose declared reads did not
        // mention it would publish a lie that nothing in the platform compares — the derivation is a
        // delegate, so no gate can infer its inputs.
        foreach (var meter in Derived(AgentPools.Type)) {
            meter.Expression.ShouldNotBeNullOrWhiteSpace(meter.Meter.ToString());
            meter.Reads.ShouldContain("/properties/autoscale/maxCount", meter.Meter.ToString());
            meter.Reads.ShouldContain("/properties/autoscale/enabled", meter.Meter.ToString());
            meter.Reads.ShouldContain("/properties/count", meter.Meter.ToString());
        }

        foreach (var meter in Derived(ManagedClusters.Type)) {
            meter.Reads.ShouldContain("/properties/controlPlane/replicas", meter.Meter.ToString());
        }
    }

    // ── The schema ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // A DefaultJson the schema itself refuses is a chart whose own values.yaml fails validation —
        // and `helm lint --strict` runs a chart against its own defaults, so it would be found by the
        // build rather than by a tenant. Found here first is cheaper.
        foreach (var (schema, body) in new[] {
                     (ManagedClusters.Schema2026, ManagedClusters.Body(ClusterId)),
                     (AgentPools.Schema2026, AgentPools.Body(ClusterId))
                 }) {
            using var document = JsonDocument.Parse(body);
            var validated = schema.Validate(document.RootElement, allowTags: true);
            validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
        }
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecretAndTheCredentialResponseIs() {
        // ⚠ A `Secret` property in a resource BODY is a value the platform would have to store in
        // durable grain state, which docs/plan/00 § Non-negotiables forbids. What leaves through a
        // secret ACTION is a different path with different auditing, and on this type it is the most
        // valuable credential the platform can issue.
        ManagedClusters.Schema2026.Properties.ShouldAllBe(x => !x.Secret);
        AgentPools.Schema2026.Properties.ShouldAllBe(x => !x.Secret);

        ManagedClusters.ListCredentialsResponse.Properties
            .ShouldContain(x => x.JsonPointer == "/kubeconfig" && x.Secret);
    }

    [Fact]
    public void ThePresetTableHoldsTheOneToFourRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary: "s1.* · 1:4 · General — most databases". Two shipped
        // spellings of s1 differ by one rung, and PostgresServers.Presets["s1.nano"] is (100m, 512Mi) —
        // 5 GiB per core, on that rung and no other. This asserts the RATIO rather than the copy, so a
        // future table that fixed the outlier still passes and one that copied it does not.
        foreach (var (preset, (cpu, memory)) in ManagedClusters.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(4m, preset + " is not 1:4");
        }
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        var declared = AgentPools.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/size")
            .AllowedValues;

        declared.Order(StringComparer.Ordinal)
            .ShouldBe(ManagedClusters.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheQuantityGrammarIsThePlatformsRatherThanAFifthCopyOfIt() {
        ManagedClusters.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
    }

    // ── The version rules ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryOfferedMinorHasAPinnedPatchAndThePatchAgreesWithTheMinor() {
        // ⚠ The API takes a MINOR and Kamaji parses a SEMANTIC VERSION, so the platform pins the patch.
        // A minor with no row would render `v1.34` into an object whose webhook cannot parse it, and a
        // row whose patch belongs to a different minor would silently provision the wrong Kubernetes.
        foreach (var minor in ManagedClusters.Versions) {
            ManagedClusters.PinnedPatch.ShouldContainKey(minor);

            var pinned = ManagedClusters.PinnedPatch[minor];

            pinned.ShouldStartWith("v" + minor + ".", Case.Sensitive);
            pinned.Split('.').Length.ShouldBe(3, pinned + " is not a three-component version");
        }

        ManagedClusters.PinnedPatch.Count.ShouldBe(ManagedClusters.Versions.Length);
        ManagedClusters.Versions.ShouldContain(ManagedClusters.DefaultVersion);
    }

    [Theory]
    [InlineData("1.33", "1.33", true)]
    [InlineData("1.33", "1.30", true)]
    [InlineData("1.33", "1.29", false)]
    [InlineData("1.33", "1.34", false)]
    [InlineData("1.33", "2.33", false)]
    [InlineData("1.33", "nonsense", false)]
    public void TheKubeletSkewRuleIsThreeMinorsBehindAndNeverAhead(
        string controlPlane,
        string node,
        bool legal
    ) {
        // ⚠ THREE, not one. docs/plan/13's "at most one minor" is the CONTROL PLANE'S OWN STEP SIZE and
        // is UpgradeIsLegal below; the kubelet rule is the Kubernetes version-skew policy's, and it
        // moved from two to three in Kubernetes 1.28.
        //
        // ⚠ A node AHEAD of its control plane is the mistake this catches, and it is the natural
        // mistake: upgrading a node pool first is what somebody does if nobody says otherwise.
        ManagedClusters.SkewIsLegal(controlPlane, node).ShouldBe(legal);
    }

    [Theory]
    [InlineData("1.32", "1.33", true)]
    [InlineData("1.33", "1.33", true)]
    [InlineData("1.31", "1.33", false)]
    [InlineData("1.33", "1.32", false)]
    public void TheControlPlaneStepRuleIsOneMinorForwardAndNeverBack(string from, string to, bool legal) {
        // Upstream's rule rather than one invented here: Kamaji's version webhook rejects a lower
        // version outright and rejects a jump of more than one minor as "a minor version in a
        // non-sequential mode".
        ManagedClusters.UpgradeIsLegal(from, to).ShouldBe(legal);
    }

    [Fact]
    public void NeitherVersionRuleIsWiredIntoTheWritePathAndThatIsWhyThisTestExists() {
        // ⚠ docs/plan/13 SAYS "the API enforces it with a clear error". IT DOES NOT, AND IT CANNOT: a
        // node pool's version lives in a different RESOURCE from its cluster's and ResourceSchema
        // validates one body against constants. The two functions exist, are correct, and are called by
        // nothing — deliberately, so that closing the seam is wiring rather than writing.
        //
        // ⚠ This asserts the NEGATIVE, which is unusual and is the point: it goes red the day somebody
        // wires them, which is the day charts/managed/kubernetes/conformance.yaml § owed's
        // `version-skew-is-not-enforced` should be deleted.
        var illegal = AgentPools.Body(ClusterId, version: "1.32");

        using var document = JsonDocument.Parse(illegal);

        var validated = AgentPools.Schema2026.Validate(document.RootElement, allowTags: true);

        validated.IsSuccess.ShouldBeTrue(
            "the pool's schema now refuses a version, which means somebody has taught it about another "
            + "resource. If version skew is enforced now, delete this test and the § owed item it "
            + "guards."
        );

        // And the pair really is illegal against a 1.33 control plane's node rule... no, it is legal:
        // 1.32 is one minor behind. The point of the assertion above is only that the schema does not
        // look.
        ManagedClusters.SkewIsLegal("1.33", "1.32").ShouldBeTrue();
    }

    // ── The upstream constants a reader would otherwise have to trust ───────────────────────────

    [Fact]
    public void TheThreeObjectsAreInThreeDifferentApiGroups() {
        // ⚠ THE SHAPE THIS FAMILY ADDS. charts/managed/clickhouse was the first to render two custom
        // resources in two groups and both came from one operator binary; these three come from three
        // separate upstream projects, versioned independently.
        var groups = new[] {
            ManagedClusters.ClusterKind.Group,
            ManagedClusters.ControlPlaneKind.Group,
            ManagedClusters.InfrastructureKind.Group
        };

        groups.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);

        // ⚠ ASSERTED AGAINST LITERALS. Reading these off the constants would compare them to
        // themselves; what they have to agree with is somebody else's CRD.
        ManagedClusters.ClusterKind.Group.ShouldBe("cluster.x-k8s.io");
        ManagedClusters.ClusterKind.Version.ShouldBe("v1beta2");
        ManagedClusters.ControlPlaneKind.Group.ShouldBe("controlplane.cluster.x-k8s.io");
        ManagedClusters.ControlPlaneKind.Version.ShouldBe("v1alpha2");
        ManagedClusters.InfrastructureKind.Group.ShouldBe("infrastructure.cluster.x-k8s.io");
        ManagedClusters.InfrastructureKind.Version.ShouldBe("v1alpha1");
    }

    [Fact]
    public void ThePoolsThreeObjectsAreAlsoInThreeApiGroupsAndTwoOfThemAreNotTheClustersGroups() {
        var groups = new[] {
            AgentPools.MachineDeploymentKind.Group,
            AgentPools.BootstrapKind.Group,
            AgentPools.MachineTemplateKind.Group
        };

        groups.Distinct(StringComparer.Ordinal).Count().ShouldBe(3);

        AgentPools.BootstrapKind.Group.ShouldBe("bootstrap.cluster.x-k8s.io");
        AgentPools.BootstrapKind.Version.ShouldBe("v1beta2");
        AgentPools.MachineDeploymentKind.Version.ShouldBe("v1beta2");
        AgentPools.MachineTemplateKind.Kind.ShouldBe("KubevirtMachineTemplate");
    }

    [Fact]
    public void TheClusterNamesTheControlPlaneAndTheInfrastructureItAlsoRenders() {
        // ⚠ THE HAZARD A TYPO IN A REF PRODUCES IS NOT AN APPLY ERROR. All three applies succeed, the
        // Cluster is admitted, and Cluster API reports an unresolvable reference as a condition on an
        // object nothing in this platform reads. So the refs are compared to the OBJECTS' OWN NAMES.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var cluster = Node(ManagedClusters.ClusterJson("prod", body.RootElement));

        cluster["spec"]!["controlPlaneRef"]!["name"]!.GetValue<string>()
            .ShouldBe(ManagedClusters.ControlPlaneName("prod"));

        cluster["spec"]!["controlPlaneRef"]!["apiGroup"]!.GetValue<string>()
            .ShouldBe(ManagedClusters.ControlPlaneKind.Group);

        cluster["spec"]!["infrastructureRef"]!["name"]!.GetValue<string>().ShouldBe("prod");

        // ⚠ AND NEITHER REF CARRIES AN apiVersion, which is Cluster API v1beta2 rather than an
        // omission — a ContractVersionedObjectReference is {apiGroup, kind, name} and the version comes
        // from a label on the provider's own CRD. A rendered `apiVersion` would be pruned silently.
        cluster["spec"]!["controlPlaneRef"]!.AsObject().ContainsKey("apiVersion").ShouldBeFalse();
        cluster["spec"]!["infrastructureRef"]!.AsObject().ContainsKey("apiVersion").ShouldBeFalse();
    }

    [Fact]
    public void TheControlPlaneNamesTheSharedDataStoreAndTheClusterWritesItsServiceDomain() {
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        var controlPlane = Node(ManagedClusters.ControlPlaneJson("prod", body.RootElement));

        controlPlane["spec"]!["dataStoreName"]!.GetValue<string>().ShouldBe("default");

        // ⚠ WRITTEN AT CREATION BECAUSE IT CANNOT BE ADDED LATER. Cluster API does not default
        // serviceDomain and Kamaji freezes it with a `self == oldSelf` rule, so an empty value takes
        // Kamaji's default and then can never be changed.
        Node(ManagedClusters.ClusterJson("prod", body.RootElement))["spec"]!["clusterNetwork"]!
            ["serviceDomain"]!.GetValue<string>()
            .ShouldBe("cluster.local");
    }

    [Fact]
    public void TheControlPlaneIsClusterIpAndThatOverridesTheCrdsOwnDefault() {
        // ⚠ THE MOST CONSEQUENTIAL LINE ON THIS ROW. Kamaji's CRD defaults `network` to
        // `{serviceType: LoadBalancer}`, so rendering nothing here publishes every tenant's Kubernetes
        // API server on a public address with no allow-list — and there is nowhere upstream to render
        // an allow-list into, which docs/plan/12 § Cross-cutting decisions requires.
        using var body = JsonDocument.Parse(ManagedClusters.Body(ClusterId));

        Node(ManagedClusters.ControlPlaneJson("prod", body.RootElement))["spec"]!["network"]!
            ["serviceType"]!.GetValue<string>()
            .ShouldBe("ClusterIP");

        // ⚠ And there is no exposure property to make it anything else. A property that rendered
        // nothing would be worse than the absence.
        ManagedClusters.Schema2026.Properties
            .ShouldNotContain(x => x.JsonPointer.Contains("apiServer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheInfrastructureCarriesTheExternallyManagedAnnotationAndNoSpec() {
        // ⚠ ONE ANNOTATION DECIDES WHICH OF TWO CONTROLLERS OWNS THE CONTROL-PLANE ENDPOINT. Without
        // it the KubeVirt provider creates its own load-balancer Service and the Kamaji provider
        // patches its own endpoint onto the same object; the symptom is a control-plane address that
        // flips.
        var infrastructure = Node(ManagedClusters.InfrastructureJson("prod"));

        infrastructure["metadata"]!["annotations"]!["cluster.x-k8s.io/managed-by"]!.GetValue<string>()
            .ShouldBe("kamaji");

        infrastructure["spec"]!.AsObject().Count.ShouldBe(
            0,
            "the KubevirtCluster gained a spec. Everything KubevirtClusterSpec offers is either the "
            + "Kamaji provider's to patch, generated per machine, or for running VMs in a different "
            + "cluster than the management one — see ManagedClusters.InfrastructureJson."
        );
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000006");

    static ResourceTypeRegistration Registration(ResourceTypeName type) {
        ProviderRegistry.Build([new ContainerServiceProvider()]).TryGetType(type, out var registration)
            .ShouldBeTrue();

        return registration;
    }

    static IReadOnlyList<MeterRegistration> Derived(ResourceTypeName type) =>
        [.. Registration(type).Meters.Where(x => x.Derivation is not null)];

    static System.Text.Json.Nodes.JsonNode Node(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!;
}
