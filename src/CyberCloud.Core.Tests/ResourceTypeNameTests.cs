using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary><see cref="ResourceTypeName" /> — docs/plan/08 § The provider registry.</summary>
public class ResourceTypeNameTests {
    [Fact]
    public void TheWorkedExampleFromTheDocumentParses() {
        var type = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers");

        type.Namespace.ShouldBe("CyberCloud.DBforPostgreSQL");
        type.Type.ShouldBe("servers");
        type.Depth.ShouldBe(1);
        type.ToString().ShouldBe("CyberCloud.DBforPostgreSQL/servers");
    }

    [Fact]
    public void TheNestedWorkedExampleFromTheDocumentParses() {
        // docs/plan/08:134 — `.ResourceType("servers/databases")`.
        var type = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/databases");

        type.Depth.ShouldBe(2);
        type.ToString().ShouldBe("CyberCloud.DBforPostgreSQL/servers/databases");

        ResourceTypeName.TryParse(type.ToString(), out var parsed).ShouldBeTrue();
        parsed.ShouldBe(type);
        parsed.Type.ShouldBe("servers/databases");
    }

    [Fact]
    public void EveryGeneratedTypeRoundTrips() {
        var count = 0;
        using var namespaces = Corpus.ValidNamespaces(2_000, 11).GetEnumerator();
        using var typePaths = Corpus.ValidTypePaths(2_000, 12).GetEnumerator();

        while (namespaces.MoveNext() && typePaths.MoveNext()) {
            count++;
            var type = new ResourceTypeName(namespaces.Current, typePaths.Current);

            ResourceTypeName.TryParse(type.ToString(), out var parsed).ShouldBeTrue($"'{type}' should parse");
            parsed.ShouldBe(type);
            parsed.Namespace.ShouldBe(type.Namespace);
            parsed.Type.ShouldBe(type.Type);
        }

        count.ShouldBe(2_000);
    }

    // ── Case ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TypesCompareCaseInsensitivelyButRenderAsWritten() {
        var written = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers");
        var shouted = new ResourceTypeName("CYBERCLOUD.DBFORPOSTGRESQL", "SERVERS");

        written.ShouldBe(shouted);
        written.GetHashCode().ShouldBe(shouted.GetHashCode());
        written.ToString().ShouldNotBe(shouted.ToString());
    }

    [Fact]
    public void CanonicalIsAsciiLowerCased() {
        var canonical = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers/Databases").Canonical;

        canonical.Namespace.ShouldBe("cybercloud.dbforpostgresql");
        canonical.Type.ShouldBe("servers/databases");
    }

    // ── Malformed ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "servers")]
    [InlineData("", "servers")]
    [InlineData("CyberCloud", "servers")] // one segment — no dot
    [InlineData("CyberCloud.", "servers")] // trailing dot
    [InlineData(".CyberCloud", "servers")] // leading dot
    [InlineData("CyberCloud..Data", "servers")] // doubled dot
    [InlineData("Cyber Cloud.Data", "servers")] // space
    [InlineData("Cyber-Cloud.Data", "servers")] // hyphen is not allowed in a namespace
    [InlineData("Cyber/Cloud.Data", "servers")] // slash — the separator
    [InlineData("Cyber|Cloud.Data", "servers")] // pipe — the OTHER separator
    [InlineData("1CyberCloud.Data", "servers")] // must start with a letter
    [InlineData("CyberCloud.Data", null)]
    [InlineData("CyberCloud.Data", "")]
    [InlineData("CyberCloud.Data", "/servers")]
    [InlineData("CyberCloud.Data", "servers/")]
    [InlineData("CyberCloud.Data", "servers//databases")]
    [InlineData("CyberCloud.Data", "servers/1databases")]
    [InlineData("CyberCloud.Data", "a/b/c/d")] // deeper than MaxDepth
    [InlineData("CyberCloud.Data", "ser vers")]
    [InlineData("CyberCloud.Data", "servers\0")]
    public void MalformedPartsAreRejected(string? providerNamespace, string? typePath) {
        ResourceTypeName.Create(providerNamespace, typePath).IsFailure.ShouldBeTrue();

        Should.Throw<ArgumentException>(() => new ResourceTypeName(providerNamespace!, typePath!));
    }

    [Fact]
    public void ASegmentLongerThanSixtyThreeCharactersIsRejected() {
        var tooLong = new string('a', 64);

        ResourceTypeName.Create("CyberCloud." + tooLong, "servers").IsFailure.ShouldBeTrue();
        ResourceTypeName.Create("CyberCloud.Data", tooLong).IsFailure.ShouldBeTrue();
        ResourceTypeName.Create("CyberCloud.Data", new('a', 63)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("noslash")]
    [InlineData("/servers")]
    [InlineData("CyberCloud.Data/")]
    public void TryParseReturnsFalseAndNeverThrows(string? value) {
        Should.NotThrow(() => ResourceTypeName.TryParse(value, out _));
        ResourceTypeName.TryParse(value, out var type).ShouldBeFalse();
        type.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void TheFailureMessageNamesTheOffendingValueAndTheRule() {
        var error = ResourceTypeName.Create("CyberCloud", "servers").Error!;

        error.Code.ShouldBe(ErrorCode.InvalidResourceType);
        error.Message.ShouldContain("'CyberCloud'");
        error.Message.ShouldContain("at least two");
        error.Message.ShouldContain("CyberCloud.DBforPostgreSQL");
    }

    [Fact]
    public void TheDepthCapFailureExplainsWhyTheCapExists() {
        var error = ResourceTypeName.Create("CyberCloud.Data", "a/b/c/d").Error!;

        error.Message.ShouldContain("nests 4 levels deep");
        error.Message.ShouldContain("limit is 3");
        error.Message.ShouldContain("ambiguous");
    }

    [Fact]
    public void DefaultIsEmptyAndInert() {
        var type = default(ResourceTypeName);

        type.IsEmpty.ShouldBeTrue();
        type.Namespace.ShouldBe(string.Empty);
        type.Type.ShouldBe(string.Empty);
        type.Depth.ShouldBe(0);
        type.ToString().ShouldBe(string.Empty);
        Should.NotThrow(() => type.GetHashCode());
        type.Canonical.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AsciiLowerDoesNotFoldUnicodeLookAlikes() {
        // ToLowerInvariant would map some of these onto ASCII and produce an identifier collision.
        // AsciiLower deliberately leaves anything outside A-Z alone.
        ResourceTypeName.AsciiLower("ABCxyz123-.").ShouldBe("abcxyz123-.");
        ResourceTypeName.AsciiLower("K").ShouldBe("K"); // KELVIN SIGN, not 'k'
        ResourceTypeName.AsciiLower("İ").ShouldBe("İ"); // I WITH DOT ABOVE, not 'i'
    }
}
