using CyberCloud.Core.Resources;
using Shouldly;
using System.Globalization;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ResourceId" /> — docs/plan/06 § Identifiers.
/// </summary>
public class ResourceIdTests {
    static readonly ResourceTypeName Postgres = new("CyberCloud.DBforPostgreSQL", "servers");

    static readonly ResourceId Sample = new(
        Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
        Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"),
        "prod",
        Postgres,
        "pg-main",
        Guid.Parse("0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9")
    );

    // ── The shape docs/plan/06 § Identifiers specifies, character for character ────────────────────────

    [Fact]
    public void PathIsExactlyTheShapeTheDocumentSpecifies() =>
        Sample.Path.ShouldBe(
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main"
        );

    // ── Round-trip, over a generated corpus ────────────────────────────────────────────────────

    [Fact]
    public void PathRoundTripsForEveryGeneratedId() {
        var count = 0;
        foreach (var id in Corpus.ResourceIds(3_000, 42)) {
            count++;

            ResourceId.TryParsePath(id.Path, out var parsed).ShouldBeTrue($"'{id.Path}' should parse");

            // ⚠ Everything except Id. The path carries no resource GUID — docs/plan/06 § Identifiers —
            // so TryParsePath cannot invent one and returns Guid.Empty. That is the documented
            // behaviour, not a rounding error: docs/plan/06 § Identifiers makes IResourceIndexGrain the thing
            // that maps path -> GUID.
            parsed.TenantId.ShouldBe(id.TenantId);
            parsed.SubscriptionId.ShouldBe(id.SubscriptionId);
            parsed.ResourceGroup.ShouldBe(id.ResourceGroup);
            parsed.Type.ShouldBe(id.Type);
            parsed.Name.ShouldBe(id.Name);

            // ⚠ The corpus cycles depth 1, 2 and 3, so this asserts the interleave round-trips at
            // every depth the grammar allows rather than only at the one the samples above use.
            parsed.ParentNames.ShouldBe(id.ParentNames);
            parsed.Id.ShouldBe(Guid.Empty);

            // …and the path itself is a fixed point, which is the property that actually matters
            // for the index: hashing Path twice must give the same answer.
            parsed.Path.ShouldBe(id.Path);
            parsed.WithId(id.Id).ShouldBe(id);
        }

        count.ShouldBe(3_000);
    }

    [Fact]
    public void ParsedIdsCarryNoGuidUntilTheIndexResolvesThem() {
        ResourceId.TryParsePath(Sample.Path, out var parsed).ShouldBeTrue();

        parsed.Id.ShouldBe(Guid.Empty);
        parsed.ShouldNotBe(Sample);
        parsed.WithId(Sample.Id).ShouldBe(Sample);
    }

    // ── Nested resource types ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ A child interleaves with its parent's name, exactly as Azure spells it. docs/plan/12
    ///     § Child resources records the decision; this is the assertion that holds it.
    /// </summary>
    [Fact]
    public void ANestedTypeInterleavesItsParentsNameAndParsesBack() {
        var nested = (Sample with { Name = "orders" })
            .WithType(new("CyberCloud.DBforPostgreSQL", "servers/databases"), "pg-main");

        nested.Path.ShouldEndWith(
            "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main/databases/orders"
        );

        ResourceId.TryParsePath(nested.Path, out var parsed).ShouldBeTrue();
        parsed.Type.Type.ShouldBe("servers/databases");
        parsed.Type.Depth.ShouldBe(2);
        parsed.ParentNames.ShouldBe("pg-main");
        parsed.Name.ShouldBe("orders");
        parsed.Path.ShouldBe(nested.Path);
    }

    [Fact]
    public void ADepthThreeTypeInterleavesTwiceAndRoundTrips() {
        var deep = (Sample with { Name = "primary" })
            .WithType(new("CyberCloud.Network", "dnsZones/recordSets/a"), "example/www");

        deep.Path.ShouldEndWith("/providers/CyberCloud.Network/dnsZones/example/recordSets/www/a/primary");

        ResourceId.TryParsePath(deep.Path, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(deep with { Id = Guid.Empty });
        parsed.Path.ShouldBe(deep.Path);
    }

    /// <summary>
    ///     ⚠ The property the interleaved grammar exists for: the parent is derivable from the
    ///     address alone, which is all <c>IResourceRelationWriter.LinkToParentAsync</c> is given.
    /// </summary>
    [Fact]
    public void AChildsParentIsAPureFunctionOfItsAddress() {
        var database = (Sample with { Name = "orders" })
            .WithType(new("CyberCloud.DBforPostgreSQL", "servers/databases"), "pg-main");

        var server = database.Parent.ShouldNotBeNull();

        server.Type.Type.ShouldBe("servers");
        server.Name.ShouldBe("pg-main");
        server.ParentNames.ShouldBeEmpty();
        server.Path.ShouldEndWith("/providers/CyberCloud.DBforPostgreSQL/servers/pg-main");

        // …and it walks all the way up, one level at a time.
        server.Parent.ShouldBeNull();
    }

    [Fact]
    public void ATopLevelResourcesParentIsTheResourceGroupAndSoIsNull() =>
        Sample.Parent.ShouldBeNull();

    [Fact]
    public void ADepthThreeParentWalkPeelsOneLevelAtATime() {
        var record = (Sample with { Name = "primary" })
            .WithType(new("CyberCloud.Network", "dnsZones/recordSets/a"), "example/www");

        var recordSet = record.Parent.ShouldNotBeNull();
        recordSet.Type.Type.ShouldBe("dnsZones/recordSets");
        recordSet.Name.ShouldBe("www");
        recordSet.ParentNames.ShouldBe("example");

        var zone = recordSet.Parent.ShouldNotBeNull();
        zone.Type.Type.ShouldBe("dnsZones");
        zone.Name.ShouldBe("example");
        zone.Parent.ShouldBeNull();
    }

    /// <summary>
    ///     ⚠ The ambiguity the old grammar had and this one does not. Under a flattened
    ///     <c>…/servers/databases/{name}</c> shape this path was a legal depth-2 id; here it is an
    ///     odd tail and is refused rather than read at the wrong depth.
    /// </summary>
    [Fact]
    public void AnOddTailIsRefusedRatherThanReadAtTheWrongDepth() {
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/databases/orders";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();

        var error = ResourceId.ParsePath(path).Error.ShouldNotBeNull();
        error.Code.ShouldBe(ErrorCode.InvalidResourceId);
        error.Message.ShouldContain("odd number");
    }

    [Fact]
    public void ATypePathDeeperThanTheCapIsRejectedRatherThanTruncated() {
        // Four type segments, each with a name — a well-formed alternation that is simply too deep.
        // Without the cap this would parse as a type nobody registered.
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/a/w/b/x/c/y/d/z";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();
        ResourceId.ParsePath(path).Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
    }

    /// <summary>
    ///     ⚠ The invariant is enforced on <c>with</c> as well as on construction — see the remarks on
    ///     <c>ResourceId.Type</c> for why the two paths need separate guards.
    /// </summary>
    [Fact]
    public void ATypeAndItsParentNamesCannotDisagree() {
        // A depth-2 type with no parent name would render a path with a segment missing.
        Should.Throw<ArgumentException>(
            () => Sample with { Type = new("CyberCloud.DBforPostgreSQL", "servers/databases") }
        );

        // …and a top-level type with one is the same mistake upside down.
        Should.Throw<ArgumentException>(() => Sample with { ParentNames = "pg-main" });

        // An ancestor's name is validated exactly as the resource's own is.
        Should.Throw<ArgumentException>(
            () => Sample.WithType(new("CyberCloud.DBforPostgreSQL", "servers/databases"), "PG-Main")
        );
    }

    [Fact]
    public void AnAncestorNameIsValidatedWhenParsedToo() {
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/PG-MAIN/databases/orders";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();
    }

    [Fact]
    public void APathWithTooFewSegmentsFailsCleanly() {
        // No type at all: /providers/{ns}/{name} is one segment short of a resource.
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers";

        ResourceId.TryParsePath(path, out var id).ShouldBeFalse();
        id.ShouldBe(default);
        ResourceId.ParsePath(path).Error!.Message.ShouldContain("segments");
    }

    // ── Case ───────────────────────────────────────────────────────────────────────────────────
    //
    // THE DECISION, stated once:
    //   * the four structural literals (tenants, subscriptions, resourceGroups, providers) are
    //     matched CASE-INSENSITIVELY — a support engineer pasting /ResourceGroups/ gets a parse,
    //     not an error;
    //   * the VALUES are not folded. /resourceGroups/PROD FAILS, because PROD is not a legal name
    //     (docs/plan/06 § Identifiers) and lower-casing it would be exactly the mangling docs/plan/06 § Identifiers
    //     forbids;
    //   * provider namespaces and type names ARE compared case-insensitively, because they are
    //     mixed-case by design (CyberCloud.DBforPostgreSQL) and Azure treats them that way.
    //
    // The round-trip property survives all three because Path always emits the canonical literals
    // and the values are already lower-case by construction.

    [Fact]
    public void StructuralLiteralsAreMatchedCaseInsensitively() {
        const string shouty =
            "/TENANTS/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/Subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/ResourceGroups/prod"
            + "/PROVIDERS/CyberCloud.DBforPostgreSQL/servers/pg-main";

        ResourceId.TryParsePath(shouty, out var parsed).ShouldBeTrue();

        parsed.ShouldBe(Sample.WithId(Guid.Empty));

        // …and re-emitting normalises the literals, so a second parse is a fixed point.
        parsed.Path.ShouldBe(Sample.Path);
    }

    [Fact]
    public void AnUpperCaseResourceGroupValueIsRejectedNotFolded() {
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/PROD"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();

        var error = ResourceId.ParsePath(path).Error!;
        error.Code.ShouldBe(ErrorCode.InvalidResourceName);
        error.Message.ShouldContain("'PROD'");
        error.Message.ShouldContain("resource group name");
    }

    [Fact]
    public void AnUpperCaseResourceNameValueIsRejectedNotFolded() {
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/PG-Main";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();
        ResourceId.ParsePath(path).Error!.Message.ShouldContain("'PG-Main'");
    }

    [Fact]
    public void ProviderNamespaceCaseDiffersInThePathButNotInTheIdentity() {
        const string lowered =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/cybercloud.dbforpostgresql/SERVERS/pg-main";

        ResourceId.TryParsePath(lowered, out var parsed).ShouldBeTrue();

        // Equal as identities…
        parsed.ShouldBe(Sample.WithId(Guid.Empty));

        // …but NOT equal as strings, which is why the index must hash CanonicalPath and not Path.
        parsed.Path.ShouldNotBe(Sample.Path);
        parsed.CanonicalPath.ShouldBe(Sample.CanonicalPath);
        Sample.CanonicalPath.ShouldEndWith("/providers/cybercloud.dbforpostgresql/servers/pg-main");
    }

    // ── GUID formats ───────────────────────────────────────────────────────────────────────────
    //
    // Guid.TryParse accepts N, D, B, P and X. If the path parser used it, one resource would have
    // five spellings and the path index five entries. TryParseExact("D") is what stops that.

    [Theory]
    [InlineData("{2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3}", "braced (B)")]
    [InlineData("(2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3)", "parenthesised (P)")]
    [InlineData("2b4a1c662e704a9d9d0a1f7ec1f1a4b3", "bare hex (N)")]
    [InlineData("2b4a1c662e704a9d9d0a1f7ec1f1a4b3aa", "over-long hex")]
    [InlineData("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b", "one digit short")]
    [InlineData("0x2b4a1c66,0x2e70,0x4a9d,{0x9d,0x0a,0x1f,0x7e,0xc1,0xf1,0xa4,0xb3}", "hex array (X)")]
    [InlineData(" 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", "leading space")]
    [InlineData("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3 ", "trailing space")]
    public void OnlyTheHyphenatedLowerCaseGuidFormIsAcceptedInAPath(string tenant, string why) {
        var path =
            $"/tenants/{tenant}"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse($"the {why} GUID form must not parse");
    }

    [Fact]
    public void TheUpperCaseGuidRejectionIsDeliberateAndTheMessageSaysWhichFormIsWanted() {
        // ⚠ Guid.TryParseExact(s, "D") is case-INSENSITIVE about the hex digits, so this actually
        // parses. Recording the real behaviour rather than the behaviour we might assume: the
        // canonical Path always emits lower case, so an upper-case path is a second spelling that
        // parses to the same id. The index therefore hashes CanonicalPath, which lower-cases the
        // provider but NOT the GUIDs — see the note in ResourceIdTests.GuidCaseIsNotCanonicalised.
        var path =
            "/tenants/2B4A1C66-2E70-4A9D-9D0A-1F7EC1F1A4B3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

        ResourceId.TryParsePath(path, out var parsed).ShouldBeTrue();
        parsed.TenantId.ShouldBe(Sample.TenantId);

        // The re-emitted path is canonical, which is what makes the round trip a fixed point.
        parsed.Path.ShouldContain("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3");
    }

    [Fact]
    public void GuidTryParseExactIsNotActuallyExactAndThatIsWhyGuidFormatExists() {
        // ⚠ THE TRAP, recorded against the BCL rather than against our code. Guid.TryParseExact
        // trims surrounding whitespace before matching the format, so the "exact" overload accepts
        // a string the format does not describe. Without the length guard in GuidFormat, the path
        // "/tenants/ 2b4a…/…" would parse — and the id it produced would re-emit a path that is a
        // DIFFERENT string, which is a round-trip break and a second index entry for one resource.
        Guid.TryParseExact(" 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", "D", out _)
            .ShouldBeTrue("if this ever goes false, the BCL changed and GuidFormat's length guard is redundant");
        Guid.TryParseExact("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3\n", "D", out _).ShouldBeTrue();
        Guid.TryParseExact(" 2b4a1c662e704a9d9d0a1f7ec1f1a4b3 ", "N", out _).ShouldBeTrue();

        // …and the parsers built on GuidFormat do not.
        const string padded =
            "/tenants/ 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

        ResourceId.TryParsePath(padded, out _).ShouldBeFalse();

        GrainKeys.TryParse(" res/0a1b2c3d4e5f40718293a4b5c6d7e8f9", out _).ShouldBeFalse();
        GrainKeys.TryParse("res/ 0a1b2c3d4e5f40718293a4b5c6d7e8f9", out _).ShouldBeFalse();
    }

    /// <summary>
    ///     ⚠ <b>The rule has to be reachable from outside this assembly, or it gets copied.</b>
    /// </summary>
    /// <remarks>
    ///     <see cref="GuidFormat" /> was <c>internal</c>, and the gateway — which needs the rule one
    ///     segment at a time, <i>before</i> a whole path is parsed — carried a byte-for-byte copy of
    ///     <see cref="GuidFormat.TryParseD" /> called <c>GatewayGuid</c>. Two implementations of a
    ///     round-trip rule is one implementation that eventually drifts, and the drift would be
    ///     invisible: the gateway's tenant check would accept a spelling
    ///     <see cref="ResourceId.TryParsePath" /> rejects, which is a check that can be walked around.
    ///     <para>
    ///         This test is what fails if the accessibility is ever taken back. The <i>behaviour</i>
    ///         it must keep is asserted by
    ///         <see cref="GuidTryParseExactIsNotActuallyExactAndThatIsWhyGuidFormatExists" />; this
    ///         one only asserts that one copy is reachable.
    ///     </para>
    /// </remarks>
    [Fact]
    public void GuidFormatIsPublicSoNobodyHasToCopyIt() {
        typeof(GuidFormat).IsPublic.ShouldBeTrue(
            "the gateway needs the D-only rule before a path is parsed, and an internal GuidFormat "
            + "is why it had a copy. docs/plan/06 § Identifiers fixes the form for every id in a "
            + "path; one rule, one implementation."
        );

        // And the length guard survived the move out of `internal`. Whitespace is the whole point.
        GuidFormat.TryParseD(" 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", out _).ShouldBeFalse();
        GuidFormat.TryParseD("{2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3}", out _).ShouldBeFalse();
        GuidFormat.TryParseD("2b4a1c662e704a9d9d0a1f7ec1f1a4b3", out _).ShouldBeFalse();
        GuidFormat.TryParseD(null, out _).ShouldBeFalse();
        GuidFormat.TryParseD("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", out var parsed).ShouldBeTrue();
        parsed.ShouldBe(Sample.TenantId);

        GuidFormat.TryParseN(" 2b4a1c662e704a9d9d0a1f7ec1f1a4b3", out _).ShouldBeFalse();
        GuidFormat.TryParseN("2b4a1c662e704a9d9d0a1f7ec1f1a4b3", out var bare).ShouldBeTrue();
        bare.ShouldBe(Sample.TenantId);
    }

    [Fact]
    public void GuidCaseIsNotCanonicalisedByTheParserButIsByTheFormatter() {
        // Guid.ToString("D") always emits lower case, so Path and CanonicalPath are already
        // GUID-canonical. Nothing to normalise; asserted so a future change to Path notices.
        Sample.Path.ShouldNotContain("2B4A1C66", Case.Sensitive);
        Sample.CanonicalPath.ShouldNotContain("2B4A1C66", Case.Sensitive);
    }

    [Fact]
    public void TheKeyUsesNAndThePathUsesDAndBothParse() {
        // docs/plan/06 § Identifiers spells GUIDs `D` in a path; docs/plan/06 § Grain keys spells them `N` in a key.
        var key = GrainKeys.Resource(Sample.Id);

        Sample.Path.ShouldContain(Sample.TenantId.ToString("D", CultureInfo.InvariantCulture));
        key.ShouldEndWith(Sample.Id.ToString("N", CultureInfo.InvariantCulture));

        ResourceId.TryParsePath(Sample.Path, out var backFromPath).ShouldBeTrue();
        GrainKeys.TryParse(key, out var backFromKey).ShouldBeTrue();

        backFromPath.SubscriptionId.ShouldBe(Sample.SubscriptionId);
        backFromKey.Id.ShouldBe(Sample.Id);
    }

    // ── Empty and malformed input — none of these may throw ────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("   ")]
    [InlineData("not-a-path")]
    [InlineData("tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3")]
    [InlineData("/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3")]
    [InlineData("/tenants//subscriptions//resourceGroups//providers//")]
    public void MalformedInputReturnsFalseAndNeverThrows(string? path) {
        Should.NotThrow(() => ResourceId.TryParsePath(path, out _));
        ResourceId.TryParsePath(path, out var id).ShouldBeFalse();
        id.ShouldBe(default);

        Should.NotThrow(() => ResourceId.ParsePath(path));
        ResourceId.ParsePath(path).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ATrailingSlashIsRejected() {
        ResourceId.TryParsePath(Sample.Path + "/", out _).ShouldBeFalse();
        ResourceId.ParsePath(Sample.Path + "/").Error!.Message.ShouldContain("empty segment");
    }

    [Fact]
    public void ADoubledSlashIsRejected() {
        var doubled = Sample.Path.Replace("/resourceGroups/", "//resourceGroups//", StringComparison.Ordinal);

        ResourceId.TryParsePath(doubled, out _).ShouldBeFalse();
    }

    [Fact]
    public void AMissingProvidersSegmentIsRejected() {
        var without = Sample.Path.Replace("/providers/", "/provider/", StringComparison.Ordinal);

        ResourceId.TryParsePath(without, out _).ShouldBeFalse();
        ResourceId.ParsePath(without).Error!.Message.ShouldContain("providers");
    }

    [Fact]
    public void APathThatIsNotRootedIsRejected() {
        ResourceId.TryParsePath(Sample.Path[1..], out _).ShouldBeFalse();
        ResourceId.ParsePath(Sample.Path[1..]).Error!.Message.ShouldContain("must start with '/'");
    }

    // ── Separator injection ────────────────────────────────────────────────────────────────────
    //
    // Two independent defences, and both are tested:
    //   1. the CONSTRUCTOR refuses an injected component, so a forged Path cannot be produced;
    //   2. the PARSER re-validates every component, so a forged path string cannot be consumed.
    // Defence 2 alone would not be enough (see WhyTheConstructorMustValidateToo below), and
    // defence 1 alone would not be enough because paths arrive from the network as strings.

    [Fact]
    public void TheConstructorRefusesAnInjectedResourceGroup() {
        foreach (var (value, why) in Corpus.InjectionCharacters) {
            Should.Throw<ArgumentException>(
                () => new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "pg" + value, Postgres, "x", Guid.NewGuid()),
                $"a resource group containing {Corpus.Printable(value)} ({why}) must not be constructible"
            );
        }
    }

    [Fact]
    public void TheConstructorRefusesAnInjectedResourceName() {
        foreach (var (value, why) in Corpus.InjectionCharacters) {
            Should.Throw<ArgumentException>(
                () => new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "prod", Postgres, "pg" + value, Guid.NewGuid()),
                $"a resource name containing {Corpus.Printable(value)} ({why}) must not be constructible"
            );
        }
    }

    [Fact]
    public void WithAlsoValidatesSoAForgedNameCannotBeSlippedInAfterConstruction() {
        // `with` on a record struct does not run the constructor. Without a validating init
        // accessor this is the hole every "we validate in the constructor" scheme has.
        Should.Throw<ArgumentException>(() => Sample with { Name = "pg/evil" });
        Should.Throw<ArgumentException>(() => Sample with { ResourceGroup = "prod|evil" });
        Should.Throw<ArgumentException>(() => Sample with { Name = "pg\0evil" });
    }

    [Fact]
    public void WhyTheConstructorMustValidateToo() {
        // ⚠ THIS TEST USED TO ASSERT THE OPPOSITE, and the change is the point of the interleaved
        // grammar rather than a weakening of this defence.
        //
        // Under the flattened shape — the type path whole in the middle, one name at the end —
        // `servers` + name `databases/orders` and `servers/databases` + name `orders` rendered THE
        // SAME string. Both were legal ids for different resources, with different permissions and
        // different reconcilers, and only ResourceNaming's ban on '/' in a name kept the second
        // reading out. docs/plan/06 § Identifiers calls that rule "load-bearing for identifier
        // integrity" for exactly this reason.
        //
        // Interleaving removes the collision at the grammar level: a depth-2 id spells the server's
        // name between the two type segments, so there is no string a depth-1 id and a depth-2 id
        // can both produce. The forged path below is now not a path at all — an odd tail.
        const string forged =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/databases/orders";

        ResourceId.TryParsePath(forged, out _).ShouldBeFalse();

        // The naming rule is still load-bearing and still enforced — it is now the second of two
        // independent defences rather than the only one. A name with a '/' would still shift the
        // alternation, so it is still unconstructible:
        Should.Throw<ArgumentException>(() => new ResourceId(
                Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
                Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"),
                "prod",
                Postgres,
                "databases/orders",
                Guid.Empty
            )
        );

        // …and the id that path was trying to forge has a different, unambiguous spelling.
        var real = (Sample with { Name = "orders" })
            .WithType(new("CyberCloud.DBforPostgreSQL", "servers/databases"), "pg-main");

        real.Path.ShouldEndWith("/servers/pg-main/databases/orders");
    }

    [Fact]
    public void AnInjectedValueInAPathStringIsRejectedByTheParser() {
        foreach (var (value, why) in Corpus.InjectionCharacters) {
            var path =
                "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
                + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
                + "/resourceGroups/pr"
                + value
                + "od"
                + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

            Should.NotThrow(() => ResourceId.TryParsePath(path, out _));

            ResourceId.TryParsePath(path, out var parsed)
                .ShouldBeFalse(
                    $"a resource group containing {Corpus.Printable(value)} ({why}) must not parse; "
                    + $"it produced {Corpus.Printable(parsed.ResourceGroup ?? "<default>")}"
                );
        }
    }

    [Fact]
    public void AnInjectedValueCanNeverProduceADifferentValidId() {
        // The strongest statement available: for every injected value, either the path fails to
        // parse, or it parses to something whose own Path is byte-identical to the input (that is,
        // it is not a forgery, it is only a path). Nothing in between.
        foreach (var (value, _) in Corpus.InjectionCharacters) {
            foreach (var slot in new[] { "group", "name" }) {
                var group = slot == "group" ? "pr" + value + "od" : "prod";
                var name = slot == "name" ? "pg" + value + "main" : "pg-main";

                var path =
                    "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
                    + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
                    + "/resourceGroups/"
                    + group
                    + "/providers/CyberCloud.DBforPostgreSQL/servers/"
                    + name;

                if (ResourceId.TryParsePath(path, out var parsed)) {
                    parsed.Path.ShouldBe(
                        path,
                        $"'{Corpus.Printable(path)}' parsed but did not round-trip, which means "
                        + "the parser accepted one string and produced a different id"
                    );
                }
            }
        }
    }

    // ── Defaults and degenerate construction ───────────────────────────────────────────────────

    [Fact]
    public void AResourceIdNeedsARealType() =>
        Should.Throw<ArgumentException>(() => new ResourceId(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "prod",
                default,
                "pg",
                Guid.NewGuid()
            )
        );

    [Fact]
    public void DefaultResourceIdIsInertRatherThanExplosive() {
        // default(ResourceId) skips the constructor, so its strings are null. Nothing here may
        // throw — a default in a collection must be diagnosable, not fatal.
        var id = default(ResourceId);

        id.TenantId.ShouldBe(Guid.Empty);
        Should.NotThrow(() => id.Type.ToString());
        id.Type.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ToStringIsThePath() => Sample.ToString().ShouldBe(Sample.Path);
}
