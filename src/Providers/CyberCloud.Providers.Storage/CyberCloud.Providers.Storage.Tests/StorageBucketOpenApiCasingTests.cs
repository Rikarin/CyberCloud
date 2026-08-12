using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     Every name of the bucket type, spelled the same in all three places a caller meets it — plus
///     the two spellings that only exist because the type is nested.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE CHARACTER OF CASING.</b> A <c>resourcegroup</c>/<c>resourceGroup</c> mismatch was
///         once failing every create in the platform and surfaced as a <c>404</c> whose reason was
///         only in a log line.
///     </para>
///     <para>
///         ⚠ <b>EVERY EXPECTATION BELOW IS A LITERAL, AND A PREVIOUS PROVIDER'S CASING SABOTAGE
///         STAYED GREEN BECAUSE IT WAS NOT.</b> That version built the expected path from the same
///         constants the emitter reads, so re-casing a constant left the whole suite green — two
///         things derived from one constant agree however that constant is spelled.
///     </para>
///     <para>
///         ⚠ <b>A CHILD TYPE ADDS TWO SPELLINGS NOTHING ELSE IN THE TREE HAS.</b> The interleaved
///         path segment — <c>/accounts/{accountsName}/buckets/{resourceName}</c> — and the label
///         value, which has to fold <b>two</b> slashes rather than one. Both are asserted against
///         literals below.
///     </para>
/// </remarks>
public sealed class StorageBucketOpenApiCasingTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be.</b></summary>
    const string QualifiedType = "CyberCloud.Storage/accounts/buckets";

    [Fact]
    public void EveryDeclaredPropertyNameReachesTheOpenApiDocumentWithItsExactCasing() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        foreach (var property in StorageBuckets.Schema2026.Properties) {
            names.ShouldContain(
                property.Name,
                $"'{property.JsonPointer}' does not appear in the emitted OpenAPI document under that "
                + "exact spelling. A property whose document name differs from its registry name by "
                + "one character is a property every generated client sends to the wrong key, and the "
                + "write path then refuses it as unknown."
            );
        }
    }

    [Fact]
    public void TheCamelCasedNamesAreSpelledOutHereRatherThanDerived() {
        // ⚠ THE LITERALS ARE THE TEST. Reading these off Schema2026 would compare the schema to
        // itself. `objectCount` and `sizeBytes` are the stats response's, and they are the two most
        // likely to arrive lower-cased because both halves are ordinary English words.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        // ⚠ Every name here has a capital in it, deliberately: a lower-cased `versioning` compared
        // against its own `ToLowerInvariant()` would assert that the document does not contain the
        // string it was just asserted to contain, which is a test that can only fail.
        foreach (var expected in new[] { "clusterId", "objectCount", "sizeBytes", "sampledAt" }) {
            expected.ShouldNotBe(expected.ToLowerInvariant(), "this list is for camelCased names only");

            names.ShouldContain(expected, $"'{expected}' is not in the document under that casing");
            names.ShouldNotContain(
                expected.ToLowerInvariant(),
                $"the document carries '{expected.ToLowerInvariant()}' as well as '{expected}'. Two "
                + "spellings of one property is a body half the generated clients send to the wrong "
                + "key."
            );
        }

        // The all-lower-case one, checked for presence only — there is no second casing of it to
        // collide with.
        names.ShouldContain("versioning");
    }

    [Fact]
    public void TheApiSpellsQuotaSizeAndTheCustomResourceSpellsQuota() {
        // ⚠ TWO VOCABULARIES, AND ONLY ONE OF THEM IS THE API. The BucketSpec field is a bare
        // `quota` holding a quantity; the resource body nests it as `quota.size`, because a bucket's
        // quota is the kind of thing that grows a second dimension (object count) and a bare scalar
        // could not. A document carrying the operator's flat spelling would be a body every
        // generated client sends to a key the write path refuses as unknown — and the mistake reads
        // as correct, because the value it carries genuinely is the quota.
        var names = new HashSet<string>(StringComparer.Ordinal);
        Collect(Document(), names);

        names.ShouldContain("quota");
        names.ShouldContain("size");

        // The nesting, not just the two names: `/properties/quota/size` is the pointer a client sends
        // and `/properties/quota` alone is not.
        StorageBuckets.Schema2026.Declares("/properties/quota/size").ShouldBeTrue();
    }

    [Fact]
    public void TheInterleavedPathSurvivesIntoThePathsExactly() {
        // ⚠ THE SPELLING A CHILD TYPE ADDS, AND THE ONE A FLATTENED ADDRESS WOULD SILENTLY CHANGE.
        // docs/plan/12 § Child resources: `…/{parentType}/{parentName}/{childType}/{childName}`.
        StorageBuckets.Type.ToString().ShouldBe(
            QualifiedType,
            "the declared type is spelled differently from docs/plan/15 § The three kinds and from "
            + "charts/managed/seaweedfs-bucket/Chart.yaml's cybercloud.io/resource-type annotation."
        );

        var paths = Paths();

        // ⚠ THE WHOLE SEGMENT, WRITTEN OUT. `Contains("buckets")` would pass on a flattened
        // `/providers/CyberCloud.Storage/accounts/buckets/{name}` — which is a legal-looking path
        // that addresses no parent and whose ReBAC edge would name the resource group.
        paths.ShouldContain(
            x => x.EndsWith(
                "/providers/CyberCloud.Storage/accounts/{accountsName}/buckets/{resourceName}",
                StringComparison.Ordinal
            ),
            "no path interleaves the account's name between the two type segments. Every path in the "
            + "document: " + string.Join(", ", paths)
        );

        paths.ShouldNotContain(
            x => x.EndsWith(
                "/providers/CyberCloud.Storage/accounts/buckets/{resourceName}",
                StringComparison.Ordinal
            ),
            "the type path was flattened on the way into the document, so a bucket is addressed with "
            + "no account in its URL at all."
        );
    }

    [Fact]
    public void TheActionIsRoutedUnderItsDeclaredCasingRatherThanLowerCased() {
        Paths().ShouldContain(
            x => x.EndsWith("/" + StorageBuckets.StatsAction, StringComparison.Ordinal),
            $"no path ends with '/{StorageBuckets.StatsAction}'"
        );

        // ⚠ And it hangs off the CHILD's address, not the account's. An action routed one level up
        // would be a POST that names no bucket.
        Paths().ShouldContain(
            x => x.EndsWith("/buckets/{resourceName}/stats", StringComparison.Ordinal),
            "the stats action is not routed under a bucket's own address"
        );
    }

    [Fact]
    public void TheKubeLabelValueFoldsBothSlashesAndIsLegalLabelSyntax() {
        // ⚠ THE FIRST TYPE IN THE TREE WITH TWO SLASHES TO FOLD, AND A SINGLE-REPLACEMENT
        // IMPLEMENTATION WOULD PASS EVERY EXISTING TEST. A `/` is not a legal Kubernetes label VALUE
        // character, so the type is lower-cased with `/` replaced by `_`. Every type before this one
        // had exactly one, so `Replace(first occurrence)` and `Replace(all)` were indistinguishable —
        // and the failure arrives at admission, per object, rather than at build time.
        var value = KubeLabels.ResourceTypeValue(StorageBuckets.Type);

        value.ShouldBe("cybercloud.storage_accounts_buckets");

        value.ShouldNotContain(
            "/",
            Case.Sensitive,
            "the resource-type label value still carries a slash, so only the first of the child "
            + "type's two was folded. Every Bucket this provider applies would be refused at "
            + "admission."
        );

        LabelSyntax.ValidateValue(value, KubeLabels.ResourceType).IsSuccess.ShouldBeTrue(
            "the resource-type label value is not legal Kubernetes label syntax."
        );
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
    }

    static ImmutableArray<string> Paths() => [.. Document()["paths"]!.AsObject().Select(x => x.Key)];

    static void Collect(JsonNode? node, HashSet<string> names) {
        switch (node) {
            case JsonObject map:
                foreach (var (key, value) in map) {
                    names.Add(key);
                    Collect(value, names);
                }

                break;

            case JsonArray array:
                foreach (var item in array) {
                    Collect(item, names);
                }

                break;
        }
    }
}
