using System.Text.Json.Nodes;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     Pins what <see cref="StructuralSchema" /> refuses and what <see cref="FakeKubeCluster" /> does
///     with the refusal — against the committed definitions, not against schemas written for the test.
/// </summary>
/// <remarks>
///     ⚠ <b>The Bucket cases are the regression pin for issue #91.</b> Each of the three shapes
///     <c>charts/managed/seaweedfs-bucket</c> shipped for a month is applied here as it was shipped,
///     and each must be refused with the field named. Sabotage-verified on 2026-09-17: with the type
///     check in <c>StructuralSchema.Validate</c> switched off, five of the fourteen go red — the
///     string <c>clusterRef</c>, the string <c>quota</c>, the boolean <c>versioning</c>, the
///     int-or-string object and the fake's own refusal — and the required, enum, pattern and
///     undeclared-field cases stay green, which is the right shape: each of those is a different rule.
/// </remarks>
public sealed class StructuralSchemaTests {
    static readonly GroupVersionKind BucketKind = new() {
        Group = "seaweed.seaweedfs.com",
        Version = "v1",
        Kind = "Bucket",
        Plural = "buckets"
    };

    static readonly ObjectRef Bucket = new() { Kind = BucketKind, Namespace = "tenant-a", Name = "media-assets" };

    static CustomResourceDefinition Definition => CommittedDefinitions.Find(BucketKind)
        ?? throw new InvalidOperationException("charts/bundle/seaweedfs-operator/crds/buckets.seaweed.seaweedfs.com.yaml is not committed.");

    static JsonObject ValidBucket() =>
        new() {
            ["apiVersion"] = "seaweed.seaweedfs.com/v1",
            ["kind"] = "Bucket",
            ["metadata"] = new JsonObject {
                ["name"] = "media-assets",
                ["namespace"] = "tenant-a",
                ["labels"] = new JsonObject { ["cybercloud.io/managed-by"] = "cybercloud" }
            },
            ["spec"] = new JsonObject {
                ["name"] = "assets",
                ["clusterRef"] = new JsonObject { ["name"] = "media" },
                ["quota"] = new JsonObject { ["size"] = "10Gi" },
                ["versioning"] = "Enabled"
            }
        };

    static IReadOnlyList<string> Admit(JsonObject body) =>
        StructuralSchema.Admit(Definition, Definition.Versions["v1"], body);

    [Fact]
    public void EveryCommittedDefinitionParsesAndServesAtLeastOneVersionWithASchema() {
        CommittedDefinitions.All.ShouldNotBeEmpty("charts/bundle/*/crds/ holds nothing, so the fake validates nothing");

        foreach (var (key, definition) in CommittedDefinitions.All) {
            definition.Versions.ShouldNotBeEmpty($"{definition.File} serves no version");
            definition.Plural.ShouldNotBeNullOrEmpty($"{definition.File} has no plural");
            key.ShouldBe(definition.Group + "/" + definition.Kind);

            foreach (var version in definition.Versions.Values) {
                version.Schema["type"]?.GetValue<string>().ShouldBe("object", $"{definition.File} {version.Name} is not an object schema");
            }
        }
    }

    [Fact]
    public void AValidBucketIsAdmittedAndCarriesTheDefinitionsDefaults() {
        var body = ValidBucket();

        Admit(body).ShouldBeEmpty();

        // What the API server adds on admission, read off the committed schema rather than assumed:
        // `quota.enforce` defaults to true, `reclaimPolicy` to Retain, `objectLock` to false.
        body["spec"]!["quota"]!["enforce"]!.GetValue<bool>().ShouldBeTrue();
        body["spec"]!["reclaimPolicy"]!.GetValue<string>().ShouldBe("Retain");
        body["spec"]!["objectLock"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void AStringClusterRefIsRefusedAsTheOperatorWouldRefuseIt() {
        var body = ValidBucket();
        body["spec"]!["clusterRef"] = "media";

        var causes = Admit(body);

        causes.ShouldHaveSingleItem();
        causes[0].ShouldBe("spec.clusterRef: Invalid value: \"string\": spec.clusterRef in body must be of type object: \"string\"");
    }

    [Fact]
    public void ABooleanVersioningIsRefusedWithTheThreeSupportedValues() {
        var body = ValidBucket();
        body["spec"]!["versioning"] = true;

        var causes = Admit(body);

        causes.ShouldHaveSingleItem();
        causes[0].ShouldBe("spec.versioning: Invalid value: \"boolean\": spec.versioning in body must be of type string: \"boolean\"");

        body["spec"]!["versioning"] = "true";
        causes = Admit(body);

        causes.ShouldHaveSingleItem();
        causes[0].ShouldBe("spec.versioning: Unsupported value: \"true\": supported values: \"Off\", \"Enabled\", \"Suspended\"");
    }

    [Fact]
    public void AStringQuotaIsRefused() {
        var body = ValidBucket();
        body["spec"]!["quota"] = "10Gi";

        var causes = Admit(body);

        causes.ShouldHaveSingleItem();
        causes[0].ShouldBe("spec.quota: Invalid value: \"string\": spec.quota in body must be of type object: \"string\"");
    }

    [Fact]
    public void AMissingRequiredFieldIsRefused() {
        var body = ValidBucket();
        ((JsonObject)body["spec"]!).Remove("clusterRef");

        Admit(body).ShouldBe(["spec.clusterRef: Required value"]);

        body = ValidBucket();
        ((JsonObject)body["spec"]!["quota"]!).Remove("size");

        Admit(body).ShouldBe(["spec.quota.size: Required value"]);
    }

    [Fact]
    public void AFieldTheDefinitionDoesNotDeclareIsRefusedAsTheApplyPatchRefusesIt() {
        var body = ValidBucket();
        body["spec"]!["lifecycle"] = new JsonObject { ["days"] = 30 };

        Admit(body).ShouldBe([".spec.lifecycle: field not declared in schema"]);

        body = ValidBucket();
        body["metadata"]!["owner"] = "somebody";

        Admit(body).ShouldBe([".metadata.owner: field not declared in schema"]);
    }

    [Fact]
    public void APatternAndAnAssociativeListKeyAreEnforced() {
        var body = ValidBucket();
        body["spec"]!["name"] = "Assets";

        Admit(body).ShouldBe(["spec.name: Invalid value: \"Assets\": spec.name in body should match '^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$'"]);

        body = ValidBucket();
        body["spec"]!["access"] = new JsonArray(new JsonObject { ["actions"] = new JsonArray("Read") });

        Admit(body).ShouldBe([
            "spec.access: element 0: associative list with keys has an element that omits key field \"user\" (and doesn't have default value)",
            "spec.access[0].user: Required value"
        ]);
    }

    [Fact]
    public void AnIntOrStringAcceptsBothAndRefusesAnObject() {
        var body = ValidBucket();
        body["spec"]!["quota"]!["size"] = 10737418240;

        Admit(body).ShouldBeEmpty();

        body["spec"]!["quota"]!["size"] = new JsonObject { ["gb"] = 10 };

        Admit(body).ShouldContain("spec.quota.size: Invalid value: \"object\": spec.quota.size in body must be of type integer|string: \"object\"");
    }

    [Fact]
    public void ANullOnANonNullableFieldIsPrunedOrDefaultedRatherThanRefused() {
        // ⚠ THE CRD REFERENCE'S § Defaulting and Nullable, which the review of #91 measured this class
        // against: "null values for fields that either don't specify the nullable flag, or give it a
        // false value, will be pruned before defaulting happens. If a default is present, it will be
        // applied." The first version refused the null as a type error, stricter than the real thing.
        // `owner` has no default and is removed; `reclaimPolicy` has one and carries it.
        var body = ValidBucket();
        body["spec"]!["owner"] = null;
        body["spec"]!["reclaimPolicy"] = null;

        Admit(body).ShouldBeEmpty();

        body["spec"]!.AsObject().ContainsKey("owner").ShouldBeFalse("a non-nullable null with no default is pruned");
        body["spec"]!["reclaimPolicy"]!.GetValue<string>().ShouldBe("Retain", "a non-nullable null with a default is defaulted");

        // A null the schema has no property for is still a type error: an array item.
        body = ValidBucket();
        body["spec"]!["access"] = new JsonArray((JsonNode?)null);

        Admit(body).ShouldContain("spec.access[0]: Invalid value: \"null\": spec.access[0] in body must be of type object: \"null\"");
    }

    [Fact]
    public void AStatusTheSubresourceOwnsIsDroppedRatherThanValidated() {
        var body = ValidBucket();
        body["status"] = new JsonObject { ["phase"] = 42, ["madeUp"] = true };

        Admit(body).ShouldBeEmpty();
        body.ContainsKey("status").ShouldBeFalse();
    }

    [Fact]
    public async Task TheFakeRefusesAnInvalidCustomResourceWithTheClustersOwnWordsAndWritesNothing() {
        var cluster = new FakeKubeCluster(Guid.NewGuid());
        var body = ValidBucket();
        body["spec"]!["clusterRef"] = "media";

        var applied = await cluster.ApplyAsync(Command(cluster, body), TestContext.Current.CancellationToken);

        applied.TryGetError(out var error).ShouldBeTrue();
        error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        error.Message.ShouldContain("Bucket.seaweed.seaweedfs.com \"media-assets\" is invalid: spec.clusterRef: Invalid value: \"string\"");
        error.Message.ShouldContain("charts/bundle/seaweedfs-operator/crds/buckets.seaweed.seaweedfs.com.yaml");
        cluster.Holds(Bucket).ShouldBeFalse("a refused object must not be stored");
    }

    [Fact]
    public async Task TheFakeRefusesAnUndeclaredFieldAsATypedPatchFailure() {
        var cluster = new FakeKubeCluster(Guid.NewGuid());
        var body = ValidBucket();
        body["spec"]!["lifecycle"] = "30d";

        var applied = await cluster.ApplyAsync(Command(cluster, body), TestContext.Current.CancellationToken);

        applied.TryGetError(out var error).ShouldBeTrue();
        error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        error.Message.ShouldContain("failed to create typed patch object");
        error.Message.ShouldContain(".spec.lifecycle: field not declared in schema");
    }

    [Fact]
    public async Task TheFakeRefusesAVersionThePluralOrTheScopeTheDefinitionDoesNotServe() {
        var cluster = new FakeKubeCluster(Guid.NewGuid());

        var retired = await cluster.ApplyAsync(Command(cluster, ValidBucket(), Bucket with { Kind = BucketKind with { Version = "v1alpha1" } }), TestContext.Current.CancellationToken);
        retired.TryGetError(out var error).ShouldBeTrue();
        error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
        error.Message.ShouldContain("does not serve seaweed.seaweedfs.com/v1alpha1 Bucket");

        var misspelt = await cluster.ApplyAsync(Command(cluster, ValidBucket(), Bucket with { Kind = BucketKind with { Plural = "bucket" } }), TestContext.Current.CancellationToken);
        misspelt.TryGetError(out error).ShouldBeTrue();
        error!.Code.ShouldBe(ErrorCode.InvalidResourceType);

        var clusterScoped = await cluster.ApplyAsync(Command(cluster, ValidBucket(), Bucket with { Namespace = string.Empty }), TestContext.Current.CancellationToken);
        clusterScoped.TryGetError(out error).ShouldBeTrue();
        error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
    }

    [Fact]
    public async Task TheFakeStoresTheDefaultedBodyAndEchoesAKindWithNoDefinition() {
        var cluster = new FakeKubeCluster(Guid.NewGuid());

        var applied = await cluster.ApplyAsync(Command(cluster, ValidBucket()), TestContext.Current.CancellationToken);
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);

        var stored = JsonNode.Parse(cluster.Read(Bucket)!)!;
        stored["spec"]!["reclaimPolicy"]!.GetValue<string>().ShouldBe("Retain");

        // A kind nothing under charts/bundle/*/crds/ defines is echoed as it always was — and
        // ProviderConformanceTests.EveryCustomKindTheCaseRendersHasACommittedDefinition is what
        // keeps a provider from relying on that.
        var unknown = new ObjectRef {
            Kind = new() { Group = "widgets.example.test", Version = "v1", Kind = "Widget", Plural = "widgets" },
            Namespace = "tenant-a",
            Name = "anything"
        };

        var echoed = await cluster.ApplyAsync(
            Command(cluster, new JsonObject { ["metadata"] = new JsonObject { ["name"] = "anything" }, ["spec"] = new JsonObject { ["shape"] = "any" } }, unknown),
            TestContext.Current.CancellationToken
        );
        echoed.IsSuccess.ShouldBeTrue(echoed.Error?.Message);
        cluster.Holds(unknown).ShouldBeTrue();
    }

    /// <summary>
    ///     A command built the way a reconciler builds one — through the type-state builder, so the
    ///     seven labels are injected and the target is derived from the body exactly as in production.
    /// </summary>
    static KubeCommand Command(FakeKubeCluster cluster, JsonObject body, ObjectRef? target = null) {
        var address = target ?? Bucket;

        return KubeCommand.For(cluster)
            .WithTenantId(Guid.NewGuid())
            .WithResourceId(
                new ResourceId(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "rg",
                    new ResourceTypeName("CyberCloud.Storage", "accounts/buckets"),
                    "media-assets",
                    Guid.NewGuid(),
                    "media"
                )
            )
            .InNamespace(address.Namespace)
            .WithKind(address.Kind)
            .WithApiVersion("2026-08-01")
            .ObjectJson(body.ToJsonString())
            .Build();
    }
}
