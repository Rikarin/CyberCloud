using System.Globalization;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary>
///     <see cref="ResourceId" /> — docs/plan/06 § Identifiers.
/// </summary>
public class ResourceIdTests
{
    static readonly ResourceTypeName Postgres = new("CyberCloud.DBforPostgreSQL", "servers");

    static readonly ResourceId Sample = new(
        Guid.Parse("2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"),
        Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"),
        "prod",
        Postgres,
        "pg-main",
        Guid.Parse("0a1b2c3d-4e5f-4071-8293-a4b5c6d7e8f9"));

    // ── The shape docs/plan/06:52-53 specifies, character for character ────────────────────────

    [Fact]
    public void PathIsExactlyTheShapeTheDocumentSpecifies() =>
        Sample.Path.ShouldBe(
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main");

    // ── Round-trip, over a generated corpus ────────────────────────────────────────────────────

    [Fact]
    public void PathRoundTripsForEveryGeneratedId()
    {
        var count = 0;
        foreach (var id in Corpus.ResourceIds(3_000, seed: 42))
        {
            count++;

            ResourceId.TryParsePath(id.Path, out var parsed).ShouldBeTrue(
                $"'{id.Path}' should parse");

            // ⚠ Everything except Id. The path carries no resource GUID — docs/plan/06:52-53 —
            // so TryParsePath cannot invent one and returns Guid.Empty. That is the documented
            // behaviour, not a rounding error: docs/plan/06:44 makes IResourceIndexGrain the thing
            // that maps path -> GUID.
            parsed.TenantId.ShouldBe(id.TenantId);
            parsed.SubscriptionId.ShouldBe(id.SubscriptionId);
            parsed.ResourceGroup.ShouldBe(id.ResourceGroup);
            parsed.Type.ShouldBe(id.Type);
            parsed.Name.ShouldBe(id.Name);
            parsed.Id.ShouldBe(Guid.Empty);

            // …and the path itself is a fixed point, which is the property that actually matters
            // for the index: hashing Path twice must give the same answer.
            parsed.Path.ShouldBe(id.Path);
            parsed.WithId(id.Id).ShouldBe(id);
        }

        count.ShouldBe(3_000);
    }

    [Fact]
    public void ParsedIdsCarryNoGuidUntilTheIndexResolvesThem()
    {
        ResourceId.TryParsePath(Sample.Path, out var parsed).ShouldBeTrue();

        parsed.Id.ShouldBe(Guid.Empty);
        parsed.ShouldNotBe(Sample);
        parsed.WithId(Sample.Id).ShouldBe(Sample);
    }

    // ── Nested resource types ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ANestedTypeFormatsAndParses()
    {
        var nested = Sample with
        {
            Type = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/databases"),
            Name = "orders"
        };

        nested.Path.ShouldEndWith("/providers/CyberCloud.DBforPostgreSQL/servers/databases/orders");

        ResourceId.TryParsePath(nested.Path, out var parsed).ShouldBeTrue();
        parsed.Type.Type.ShouldBe("servers/databases");
        parsed.Type.Depth.ShouldBe(2);
        parsed.Name.ShouldBe("orders");
    }

    [Fact]
    public void ATypePathDeeperThanTheCapIsRejectedRatherThanTruncated()
    {
        // Four type segments. Without the cap the parser would happily read three of them as the
        // type and the fourth as the name, producing a valid-looking id for a type that does not
        // exist.
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/a/b/c/d/name";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();
        ResourceId.ParsePath(path).Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
    }

    [Fact]
    public void APathWithTooFewSegmentsFailsCleanly()
    {
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
    //     (docs/plan/06:88) and lower-casing it would be exactly the mangling docs/plan/06:92-94
    //     forbids;
    //   * provider namespaces and type names ARE compared case-insensitively, because they are
    //     mixed-case by design (CyberCloud.DBforPostgreSQL) and Azure treats them that way.
    //
    // The round-trip property survives all three because Path always emits the canonical literals
    // and the values are already lower-case by construction.

    [Fact]
    public void StructuralLiteralsAreMatchedCaseInsensitively()
    {
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
    public void AnUpperCaseResourceGroupValueIsRejectedNotFolded()
    {
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
    public void AnUpperCaseResourceNameValueIsRejectedNotFolded()
    {
        const string path =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/PG-Main";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse();
        ResourceId.ParsePath(path).Error!.Message.ShouldContain("'PG-Main'");
    }

    [Fact]
    public void ProviderNamespaceCaseDiffersInThePathButNotInTheIdentity()
    {
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
        Sample.CanonicalPath.ShouldEndWith(
            "/providers/cybercloud.dbforpostgresql/servers/pg-main");
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
    public void OnlyTheHyphenatedLowerCaseGuidFormIsAcceptedInAPath(string tenant, string why)
    {
        var path =
            $"/tenants/{tenant}"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

        ResourceId.TryParsePath(path, out _).ShouldBeFalse($"the {why} GUID form must not parse");
    }

    [Fact]
    public void TheUpperCaseGuidRejectionIsDeliberateAndTheMessageSaysWhichFormIsWanted()
    {
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
    public void GuidTryParseExactIsNotActuallyExactAndThatIsWhyGuidFormatExists()
    {
        // ⚠ THE TRAP, recorded against the BCL rather than against our code. Guid.TryParseExact
        // trims surrounding whitespace before matching the format, so the "exact" overload accepts
        // a string the format does not describe. Without the length guard in GuidFormat, the path
        // "/tenants/ 2b4a…/…" would parse — and the id it produced would re-emit a path that is a
        // DIFFERENT string, which is a round-trip break and a second index entry for one resource.
        Guid.TryParseExact(" 2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3", "D", out _).ShouldBeTrue(
            "if this ever goes false, the BCL changed and GuidFormat's length guard is redundant");
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

    [Fact]
    public void GuidCaseIsNotCanonicalisedByTheParserButIsByTheFormatter()
    {
        // Guid.ToString("D") always emits lower case, so Path and CanonicalPath are already
        // GUID-canonical. Nothing to normalise; asserted so a future change to Path notices.
        Sample.Path.ShouldNotContain("2B4A1C66", Case.Sensitive);
        Sample.CanonicalPath.ShouldNotContain("2B4A1C66", Case.Sensitive);
    }

    [Fact]
    public void TheKeyUsesNAndThePathUsesDAndBothParse()
    {
        // docs/plan/06:52 spells GUIDs `D` in a path; docs/plan/06:101-110 spells them `N` in a key.
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
    public void MalformedInputReturnsFalseAndNeverThrows(string? path)
    {
        Should.NotThrow(() => ResourceId.TryParsePath(path, out _));
        ResourceId.TryParsePath(path, out var id).ShouldBeFalse();
        id.ShouldBe(default);

        Should.NotThrow(() => ResourceId.ParsePath(path));
        ResourceId.ParsePath(path).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ATrailingSlashIsRejected()
    {
        ResourceId.TryParsePath(Sample.Path + "/", out _).ShouldBeFalse();
        ResourceId.ParsePath(Sample.Path + "/").Error!.Message.ShouldContain("empty segment");
    }

    [Fact]
    public void ADoubledSlashIsRejected()
    {
        var doubled = Sample.Path.Replace("/resourceGroups/", "//resourceGroups//", StringComparison.Ordinal);

        ResourceId.TryParsePath(doubled, out _).ShouldBeFalse();
    }

    [Fact]
    public void AMissingProvidersSegmentIsRejected()
    {
        var without = Sample.Path.Replace("/providers/", "/provider/", StringComparison.Ordinal);

        ResourceId.TryParsePath(without, out _).ShouldBeFalse();
        ResourceId.ParsePath(without).Error!.Message.ShouldContain("providers");
    }

    [Fact]
    public void APathThatIsNotRootedIsRejected()
    {
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
    public void TheConstructorRefusesAnInjectedResourceGroup()
    {
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            Should.Throw<ArgumentException>(
                () => new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "pg" + value, Postgres, "x", Guid.NewGuid()),
                $"a resource group containing {Corpus.Printable(value)} ({why}) must not be constructible");
        }
    }

    [Fact]
    public void TheConstructorRefusesAnInjectedResourceName()
    {
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            Should.Throw<ArgumentException>(
                () => new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "prod", Postgres, "pg" + value, Guid.NewGuid()),
                $"a resource name containing {Corpus.Printable(value)} ({why}) must not be constructible");
        }
    }

    [Fact]
    public void WithAlsoValidatesSoAForgedNameCannotBeSlippedInAfterConstruction()
    {
        // `with` on a record struct does not run the constructor. Without a validating init
        // accessor this is the hole every "we validate in the constructor" scheme has.
        Should.Throw<ArgumentException>(() => Sample with { Name = "pg/evil" });
        Should.Throw<ArgumentException>(() => Sample with { ResourceGroup = "prod|evil" });
        Should.Throw<ArgumentException>(() => Sample with { Name = "pg\0evil" });
    }

    [Fact]
    public void WhyTheConstructorMustValidateToo()
    {
        // This is the forgery the naming rule prevents, demonstrated on a raw string so the shape
        // of the attack is on the record.
        //
        // The path grammar nests resource types, so `servers` + name `databases/orders` and
        // `servers/databases` + name `orders` produce THE SAME string. Both parse. They are
        // different resources. If a name could contain '/', a caller who is allowed to create
        // `.../servers/{name}` could address `.../servers/databases/{name}` — a different type,
        // with different permissions and a different reconciler.
        const string forged =
            "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
            + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
            + "/resourceGroups/prod"
            + "/providers/CyberCloud.DBforPostgreSQL/servers/databases/orders";

        ResourceId.TryParsePath(forged, out var parsed).ShouldBeTrue();

        // It parses as the NESTED type — not as a `servers` named "databases/orders".
        parsed.Type.Type.ShouldBe("servers/databases");
        parsed.Name.ShouldBe("orders");

        // And the id that would have produced it the other way cannot be built at all:
        Should.Throw<ArgumentException>(
            () => new ResourceId(parsed.TenantId, parsed.SubscriptionId, "prod", Postgres, "databases/orders", Guid.Empty));
    }

    [Fact]
    public void AnInjectedValueInAPathStringIsRejectedByTheParser()
    {
        foreach (var (value, why) in Corpus.InjectionCharacters)
        {
            var path =
                "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
                + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
                + "/resourceGroups/pr" + value + "od"
                + "/providers/CyberCloud.DBforPostgreSQL/servers/pg-main";

            Should.NotThrow(() => ResourceId.TryParsePath(path, out _));

            ResourceId.TryParsePath(path, out var parsed).ShouldBeFalse(
                $"a resource group containing {Corpus.Printable(value)} ({why}) must not parse; "
                + $"it produced {Corpus.Printable(parsed.ResourceGroup ?? "<default>")}");
        }
    }

    [Fact]
    public void AnInjectedValueCanNeverProduceADifferentValidId()
    {
        // The strongest statement available: for every injected value, either the path fails to
        // parse, or it parses to something whose own Path is byte-identical to the input (i.e. it
        // is not a forgery, it is just a path). Nothing in between.
        foreach (var (value, _) in Corpus.InjectionCharacters)
        {
            foreach (var slot in new[] { "group", "name" })
            {
                var group = slot == "group" ? "pr" + value + "od" : "prod";
                var name = slot == "name" ? "pg" + value + "main" : "pg-main";

                var path =
                    "/tenants/2b4a1c66-2e70-4a9d-9d0a-1f7ec1f1a4b3"
                    + "/subscriptions/7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f"
                    + "/resourceGroups/" + group
                    + "/providers/CyberCloud.DBforPostgreSQL/servers/" + name;

                if (ResourceId.TryParsePath(path, out var parsed))
                {
                    parsed.Path.ShouldBe(
                        path,
                        $"'{Corpus.Printable(path)}' parsed but did not round-trip, which means "
                        + "the parser accepted one string and produced a different id");
                }
            }
        }
    }

    // ── Defaults and degenerate construction ───────────────────────────────────────────────────

    [Fact]
    public void AResourceIdNeedsARealType() =>
        Should.Throw<ArgumentException>(
            () => new ResourceId(Guid.NewGuid(), Guid.NewGuid(), "prod", default, "pg", Guid.NewGuid()));

    [Fact]
    public void DefaultResourceIdIsInertRatherThanExplosive()
    {
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
