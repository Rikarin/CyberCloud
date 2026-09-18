using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.Tenancy.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage.Tests;

/// <summary>
///     The file-share declaration, checked the way a silo checks it at start — plus the facts that
///     only exist because it renders a shared object and declines three of docs/plan/15's properties.
/// </summary>
public sealed class StorageFileShareDeclarationTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be</b> — see <c>StorageBucketDeclarationTests</c>.</summary>
    const string QualifiedType = "CyberCloud.Storage/accounts/fileShares";

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ⚠ Three registrations through Build together now, which is the first time two CHILDREN of
        // one parent have had the chance to disagree — on a short name, on a reconciler class, on
        // an api-version.
        var registry = ProviderRegistry.Build([new StorageProvider()]);

        registry.TryGetType(StorageFileShares.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(StorageFileShares.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(StorageFileShares.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(StorageFileShareReconciler));
        registration.ReconcilerType.ShouldNotBe(typeof(StorageBucketReconciler));

        var action = registration.Actions.Single(static x => x.Name == StorageFileShares.ListMountTargetsAction);
        action.Secret.ShouldBeFalse("a mount target is an address, not a credential");
        action.Permission.ShouldBe(registration.ReadPermission);
        action.HandlerType.ShouldBe(typeof(StorageFileShareListMountTargetsHandler));
        action.LongRunning.ShouldBeFalse();
    }

    [Fact]
    public void TheBucketsStatsActionHasAHandlerNow() {
        // ⚠ THE ROW THAT CAME OFF actions-without-handlers.txt. The Action handlers gate fails a
        // listed action that has grown a handler, and this is the provider-side half of the same
        // fact: a declaration with a handler type the registry can resolve.
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageBuckets.Type, out var bucket).ShouldBeTrue();

        bucket.Actions.Single(static x => x.Name == StorageBuckets.StatsAction)
            .HandlerType
                .ShouldBe(typeof(StorageBucketStatsHandler));
    }

    [Fact]
    public void TheTypeIsNestedTwoDeepUnderTheAccount() {
        StorageFileShares.Type.Depth.ShouldBe(2);
        StorageFileShares.TypePath.ShouldBe("accounts/fileShares");
        StorageFileShares.Type.ToString().ShouldBe(QualifiedType);
        StorageFileShares.TypePath.Split('/')[0].ShouldBe(StorageAccounts.TypePath);
        StorageFileShares.V2026.ShouldBe(StorageAccounts.V2026, "a parent and a child served at different dates");
    }

    [Fact]
    public void TheShortNameCollidesWithNothingInTheNamespace() {
        var registry = ProviderRegistry.Build([new StorageProvider()]);

        CliTokens.Collisions(
            registry.Types.Select(static x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        )
            .ShouldBeEmpty();

        registry.TryGetType(StorageFileShares.Type, out var share).ShouldBeTrue();
        share.Display.Alias.ShouldBe("fileshare");
    }

    [Fact]
    public void TheOnlyMeterIsTheResourceCountAndNoStorageIsReservedTwice() {
        // ⚠ docs/plan/15 § Metering says "file shares reserve", and that is the BILLING meter
        // (storage.file.gb_month, provisioned) over docs/plan/22's pipeline. A QUOTA derivation here
        // would reserve the account's already-reserved disk a second time — the bucket's argument,
        // and the share's size is a collection quota inside those same volume servers.
        var registry = ProviderRegistry.Build([new StorageProvider()]);
        registry.TryGetType(StorageFileShares.Type, out var registration).ShouldBeTrue();

        registration.Meters.Select(static x => x.Meter).ShouldBe([QuotaMeter.Resources]);
        registration.Meters.ShouldAllBe(x => x.Derivation == null);
    }

    [Fact]
    public void NoProtocolNoTierAndNoAccessRulesAreDeclared() {
        // ⚠ A DECLARATION ABOUT WHAT IS NOT DECLARED. docs/plan/15 § File storage names "size,
        // performance tier, protocol, access rules"; the backend serves no NFS (weed 4.41 has no NFS
        // command), installs no LINSTOR (no second tier), and has no managed identity to rule on. A
        // `protocol: NFS` property over a FUSE mount is the promise the product page makes and the
        // cluster does not — the shape charts/managed/seaweedfs' `encryption-at-rest` refused.
        foreach (var property in StorageFileShares.Schema2026.Properties) {
            foreach (var forbidden in new[] { "protocol", "tier", "nfs", "smb", "accessRule", "subnet", "identity" }) {
                property.JsonPointer.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                    .ShouldBeFalse(
                        $"'{property.JsonPointer}' declares a property nothing behind this type honours. "
                        + "If it is honoured now, close the owed entry that names it."
                    );
            }
        }

        StorageFileShares.Pointers2026.ShouldBe(
            [
                "/location", "/properties", "/properties/clusterId", "/properties/quota", "/properties/quota/size"
            ]
        );
    }

    [Fact]
    public void NothingInTheBodyNamesTheAccount() {
        foreach (var property in StorageFileShares.Schema2026.Properties) {
            foreach (var forbidden in new[] { "account", "parent", "seaweedRef", "filer" }) {
                property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                    .ShouldBeFalse($"'{property.JsonPointer}' names the parent in the body.");
            }
        }
    }

    [Fact]
    public void TheDriverNameIsClusterUniqueLegalAndStable() {
        // ⚠ The driverName is a cluster-scoped singleton the operator refuses to share. Two accounts of
        // one name in two namespaces must get two names; the same account must get the same name on
        // every pass; and every name must satisfy the CRD's pattern and length cap.
        var a = StorageFileShares.DriverNameOf("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-prod", "main");
        var b = StorageFileShares.DriverNameOf("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-prod", "main");

        a.ShouldNotBe(b);
        a.ShouldBe(StorageFileShares.DriverNameOf("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-prod", "main"));
        a.ShouldStartWith("main-");
        a.ShouldEndWith(".csi.cybercloud.io");

        // The longest legal account name, and the name still fits.
        var longest = StorageFileShares.DriverNameOf("ns", new string('x', 63));
        longest.Length.ShouldBeLessThanOrEqualTo(63);
        longest.ShouldMatch("^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$");

        // An account name whose 24th character is a hyphen must not produce `--`, which the pattern
        // allows but a StorageClass name reads badly; the stem is trimmed.
        StorageFileShares.DriverNameOf("ns", "abcdefghijklmnopqrstuvw-xyz").ShouldNotContain("--");
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        foreach (var property in StorageFileShares.Schema2026.Properties.Where(static x => x.DefaultJson.Length > 0)) {
            using var body = JsonDocument.Parse(
                Overridden(StorageFileShares.Body(ClusterId), property.JsonPointer, property.DefaultJson)
            );

            StorageFileShares.Schema2026.Validate(body.RootElement, allowTags: true)
                .IsSuccess.ShouldBeTrue($"the declared default for '{property.JsonPointer}' does not validate.");
        }
    }

    [Fact]
    public void TheSizeIsRequiredAndUsesTheSharedQuantityGrammar() {
        var size = StorageFileShares.Schema2026.Properties.Single(static x => x.JsonPointer == "/properties/quota/size"
        );

        size.Required.ShouldBeTrue("a share with no size is a claim with no request, which the API server refuses");
        size.Pattern.ShouldBe(KubeQuantity.Pattern);

        using var missing = JsonDocument.Parse(
            "{\"location\":\"eu-central\",\"properties\":{\"clusterId\":\"" + ClusterId + "\"}}"
        );
        StorageFileShares.Schema2026.Validate(missing.RootElement, allowTags: true).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void TheResponseShapeIsWhatTheHandlerWrites() {
        // ⚠ The dispatcher validates the handler's body against this; a pointer renamed on one side
        // only is a 500 on every call. The names are literals for the reason every casing test gives.
        StorageFileShares.ListMountTargetsResponse.Properties.Select(static x => x.JsonPointer)
            .ShouldBe(["/claimName", "/accessMode", "/filer", "/path", "/collection"]);

        StorageFileShares.ListMountTargetsResponse.Properties.ShouldAllBe(x => x.Required && !x.Secret);
    }

    /// <summary>A body with one pointer replaced by a raw JSON value.</summary>
    static string Overridden(string body, string pointer, string json) {
        var node = JsonNode.Parse(body)!.AsObject();
        var segments = pointer.Trim('/').Split('/');

        var cursor = node;
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
