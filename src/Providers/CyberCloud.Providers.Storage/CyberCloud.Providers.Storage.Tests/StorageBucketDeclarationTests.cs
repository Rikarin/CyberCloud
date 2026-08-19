using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The bucket declaration, checked the way a silo checks it at start — plus the four facts that
///     only exist because it is a <b>child</b>.
/// </summary>
public sealed class StorageBucketDeclarationTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    /// <remarks>
    ///     ⚠ It is the fifth independent copy after docs/plan/15 § The three kinds,
    ///     <c>charts/managed/seaweedfs-bucket/Chart.yaml</c>'s <c>cybercloud.io/resource-type</c>,
    ///     that chart's <c>conformance.yaml</c>, and <c>values.yaml</c>'s
    ///     <c>platform.resourceType</c>. A previous provider's casing sabotage stayed green because
    ///     the expectation was built from the same constants the emitter reads — two things derived
    ///     from one constant agree however that constant is spelled.
    /// </remarks>
    const string QualifiedType = "CyberCloud.Storage/accounts/buckets";

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a duplicate namespace, a duplicate type, a type with no
        // api-version, a duplicate short name, and a RequiresCluster type whose schema does not
        // declare the pointer as a required string. ⚠ The account and the bucket now go through it
        // TOGETHER, which is the first time this provider has had two registrations to disagree.
        var registry = ProviderRegistry.Build([new StorageProvider()]);

        registry.TryGetType(StorageBuckets.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(StorageBuckets.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(StorageBuckets.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(StorageBucketReconciler));

        // ⚠ Its own reconciler CLASS, not the account's. ProviderRegistry stores a reconciler by
        // CONCRETE TYPE and ReconcileDriver resolves it from the container by that type, so one class
        // cannot serve two registrations — its `Type` can only name one of them, and Build refuses
        // exactly that.
        registration.ReconcilerType.ShouldNotBe(typeof(StorageAccountReconciler));

        registration.Actions.ShouldContain(x => x.Name == StorageBuckets.StatsAction);

        // ⚠ NOT secret, which is the contrast with the account's `listKeys` and the reason this one
        // shares the read permission. docs/plan/07 § Consistency puts a KEY EXPORT in the
        // fully-consistent row; a size and an object count are neither a credential nor a capability.
        var stats = registration.Actions.Single(x => x.Name == StorageBuckets.StatsAction);
        stats.Secret.ShouldBeFalse();
        stats.Permission.ShouldBe(registration.ReadPermission);
    }

    [Fact]
    public void TheTypeIsNestedTwoDeepSoTheParentExistenceAssertionCannotSelfSkip() {
        // ⚠ THE SABOTAGE GUARD FOR FAILURE CLASS (a), AND IT IS NOT OBVIOUS THAT IT IS ONE.
        // ProviderConformanceTests.CreatingUnderAParentThatDoesNotExistIsTheSame404AsAnAbsentResource
        // begins `if (Case.Type.Depth == 1) { Assert.Skip(...); }`. So flattening this type to
        // `buckets` — which somebody will propose, because the URL is shorter — does not fail
        // anything: it makes the ONE assertion this type exists to exercise SKIP, silently, while the
        // suite still reports 28 passing and the run still says "Passed!".
        //
        // ⚠ AND THE COST OF THE FLATTENED FORM IS NOT AESTHETIC. A flattened address has nowhere to
        // put the account's name, so the ReBAC `parent` edge would have to name the resource GROUP —
        // and granting somebody an account would then grant nothing on its buckets, which is the
        // failure test/CyberCloud.Isolation/ParentEdgeTests exists to catch.
        //
        // Both sides are literals: `Depth` derived from a constant compared against a constant would
        // be the same thing twice.
        StorageBuckets.Type.Depth.ShouldBe(
            2,
            "the bucket type is no longer nested, so the parent-existence assertion self-skips and "
            + "nothing in the tree exercises the child grammar."
        );

        StorageBuckets.TypePath.ShouldBe("accounts/buckets");
        StorageBuckets.Type.ToString().ShouldBe(QualifiedType);

        // ⚠ And its parent segment is the ACCOUNT'S type path. A child whose first segment named some
        // other type would address a parent this provider does not serve.
        StorageBuckets.TypePath.Split('/')[0].ShouldBe(StorageAccounts.TypePath);
    }

    [Fact]
    public void TheShortNameIsNeitherAGroupNameNorTheAccountsAlias() {
        // ⚠ THE COLLISION THE PARENT FOUND, CHECKED AGAIN FOR THE SECOND TYPE IN THE NAMESPACE.
        // CliEmitter derives the CLI GROUP key from the provider namespace, and System.CommandLine's
        // ValidTokens builds ONE dictionary over every command token AND every alias in the whole
        // tree — so a group and an alias sharing a string throw `An item with the same key has
        // already been added` on the first parse of ANY command line, before any verb runs.
        //
        // ⚠ IT IS NO LONGER SATISFIED BY HAND, AND THE LIST THAT USED TO DO IT IS GONE. This test
        // held five group keys as literals — `sample`, `dbforpostgresql`, `cache`, `messaging`,
        // `storage` — while nine more were in the tree, and the same list in the network suite was
        // stale on two consecutive passes. CliTokens derives the whole question from what is
        // registered, ProviderRegistry.Build refuses it at silo start, and
        // charts/managed/seaweedfs/conformance.yaml § owed no longer carries the row.
        //
        // ⚠ AND THE LIST WAS THE WRONG QUESTION ANYWAY. Measured against System.CommandLine 2.0.10,
        // the token dictionary is per PARENT command: a short name equal to ANOTHER group's key
        // cannot collide, so four of those five assertions could never have failed. The one that
        // could — `storage`, this namespace's own group — is what the derived check keeps.
        var registry = ProviderRegistry.Build([new StorageProvider()]);

        registry.TryGetType(StorageBuckets.Type, out var bucket).ShouldBeTrue();
        registry.TryGetType(StorageAccounts.Type, out var account).ShouldBeTrue();

        CliTokens.Collisions(
            registry.Types.Select(x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        ).ShouldBeEmpty();

        // ⚠ The half the derived check cannot make: that the short name is the word a person would
        // reach for, rather than merely a string nothing else has taken.
        bucket.Display.Alias.ShouldBe("bucket");
        bucket.Display.Alias.ShouldNotBe(account.Display.Alias);
    }

    [Fact]
    public void TheOnlyMeterIsTheResourceCountAndNoStorageIsReservedTwice() {
        // ⚠ A DECLARATION ABOUT WHAT IS *NOT* DECLARED, WHICH IS THE ONLY KIND OF TEST THAT CATCHES
        // AN ADDED METER. StorageProvider's StorageDrawn already reserves every volume server's PVC
        // plus the filer's volume for the ACCOUNT. A derived storage meter on the bucket would
        // reserve the same gibibyte twice — once as the disk it is written to and once as the ceiling
        // on part of it — which is the same double-count StorageDrawn refuses when it declines to
        // multiply by `replication`.
        //
        // What a bucket genuinely costs is MEASURED rather than reserved: docs/plan/15 § Metering
        // samples storage.object.gb_month "hourly from SeaweedFS volume stats per bucket", which is
        // docs/plan/22's usage pipeline and not a pure function of a body.
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageBuckets.Type, out var registration).ShouldBeTrue();

        registration.Meters.Select(x => x.Meter).ShouldBe([QuotaMeter.Resources]);

        registration.Meters.ShouldAllBe(
            x => x.Derivation == null,
            "the bucket declares a derived meter. Everything a bucket's body could derive a number "
            + "from is capacity its account already reserved, so a derivation here bills it twice."
        );
    }

    [Fact]
    public void TheResourceNameGrammarIsWiderThanS3sByExactlyOneNumber() {
        // ⚠ THE GAP WITH NOWHERE TO PUT THE RULE, PINNED SO IT GOES RED THE DAY EITHER SIDE MOVES.
        // A resource's NAME is part of its ADDRESS, not a schema property, so ResourceSchema never
        // sees it and there is no @pattern to carry a constraint. Everything else in S3's bucket-name
        // rule is already implied by ResourceNaming's character class — no upper case, no
        // underscores, no dots, so a name cannot look like an IPv4 address or fail a SigV4 signer.
        // What is left is the minimum length: 1 here, 3 there.
        //
        // ⚠ The refusal a tenant actually meets comes from their own S3 CLIENT, which validates the
        // name before it opens a socket. So the symptom is a bucket the platform created, the
        // operator reconciled, and no SDK will address.
        // charts/managed/seaweedfs-bucket/conformance.yaml § owed, `name-grammar-is-wider-than-s3s`.
        ResourceNaming.MinLength.ShouldBe(1);
        StorageBuckets.MinimumS3NameLength.ShouldBe(3);

        ResourceNaming.IsValid("ab").ShouldBeTrue(
            "the platform's name rule now refuses a two-character name. If it refuses everything S3 "
            + "does, delete this test and the owed entry it points at."
        );

        "ab".Length.ShouldBeLessThan(
            StorageBuckets.MinimumS3NameLength,
            "S3's minimum is no longer three, so the discrepancy this test describes has changed shape"
        );

        // The upper bound is the one place they agree, and it agrees for an unrelated reason: 63 is
        // the Kubernetes label-value cap, which is why ResourceNaming picked it.
        ResourceNaming.MaxLength.ShouldBe(63);
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        foreach (var property in StorageBuckets.Schema2026.Properties.Where(x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(StorageBuckets.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            StorageBuckets.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue(
                $"the declared default for '{property.JsonPointer}' does not validate inside an "
                + "otherwise-valid body."
            );
        }
    }

    [Fact]
    public void TheQuantityGrammarIsTheSharedOneRatherThanAFifthCopy() {
        // ⚠ Four providers kept their own copy of this grammar and one of them grew a second PARSER
        // next to it, in double, which disagreed on VALUE rather than on verdict. QuantityParserTests
        // fails if a fifth copy appears; this asserts the bucket reached for the shared one rather
        // than reaching for the ACCOUNT'S alias of it, which would be a fifth copy one indirection
        // away from looking like one.
        StorageBuckets.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/quota/size")
            .Pattern.ShouldBe(KubeQuantity.OptionalPattern);
    }

    [Fact]
    public void NothingInTheBodyNamesTheAccount() {
        // ⚠ THE WHOLE OF WHAT A CHILD TYPE IS, ASSERTED AGAINST THE SCHEMA RATHER THAN INTENDED IN A
        // COMMENT. docs/plan/12 § Child resources makes the parent a pure function of the address; a
        // `parentAccount`, `accountName` or `accountId` property would be a second spelling of the
        // same fact, and the two would disagree the first time a body was sent under the wrong path.
        foreach (var property in StorageBuckets.Schema2026.Properties) {
            foreach (var forbidden in new[] { "account", "parent", "cluster ref", "clusterRef" }) {
                property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"'{property.JsonPointer}' names the parent in the body. A child's parent lives in "
                    + "its address, and ResourceId.Parent is the function that reads it."
                );
            }
        }
    }

    // ⚠ "The bucket runs the same 28 assertions as the account" is a claim about a COUNT, and it is
    // asserted in CyberCloud.Providers.Storage.Conformance —
    // StorageSuiteShapeTests.TheChildRunsEveryAssertionItsParentDoesRatherThanASubset — because that
    // is where both test classes are in scope. This project deliberately does not reference the
    // conformance one.

    /// <summary>A body with one pointer replaced by a raw JSON value.</summary>
    static string Overridden(string body, string pointer, string json) {
        var node = JsonNode.Parse(body)!.AsObject();
        var segments = pointer.Trim('/').Split('/');

        JsonObject cursor = node;
        for (var i = 0; i < segments.Length - 1; i++) {
            cursor = cursor[segments[i]]?.AsObject() ?? Insert(cursor, segments[i]);
        }

        cursor[segments[^1]] = JsonNode.Parse(json);
        return node.ToJsonString();
    }

    static JsonObject Insert(JsonObject parent, string name) {
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}
