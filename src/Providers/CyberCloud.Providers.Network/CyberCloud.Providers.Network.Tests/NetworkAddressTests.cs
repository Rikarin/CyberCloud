using System.Text.Json;

namespace CyberCloud.Providers.Network.Tests;

/// <summary>
///     The address-space machinery — the part of this family that <c>ResourceSchema</c> cannot express
///     and that therefore has nothing but this file holding it up.
/// </summary>
/// <remarks>
///     ⚠ <b>THIS IS THE LARGEST TEST FILE IN THE FAMILY AND THAT IS PROPORTIONATE RATHER THAN
///     THOROUGH-FOR-ITS-OWN-SAKE.</b> Every other constraint this provider declares is enforced by
///     <c>ResourceSchema.Validate</c> at the API, which is code the platform owns and the platform
///     tests. The reserved-range rule is enforced by <see cref="NetworkAddressing" /> alone, called
///     from two reconcilers, after the write path has already answered <c>202</c> — so a bug here is
///     a bug that reaches a tenant's fabric. The argument for why it lives there rather than at the
///     API is on <see cref="NetworkAddressing" />.
/// </remarks>
public sealed class NetworkAddressTests {
    // ── The prefix type ──────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.20.0.0/16")]
    [InlineData("192.168.1.0/24")]
    [InlineData("0.0.0.0/0")]
    [InlineData("255.255.255.255/32")]
    [InlineData("fd00::/8")]
    [InlineData("fd00:20::/48")]
    [InlineData("::/0")]
    public void AWellFormedPrefixParses(string text) => Cidr.TryParse(text, out _).ShouldBeTrue(text);

    [Theory]
    [InlineData("")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.0/")]
    [InlineData("/8")]
    [InlineData("hello/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fd00::/129")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("999.0.0.1/8")]
    // ⚠ The four below are why the length is parsed with NumberStyles.None. `int.Parse` with the
    // default styles accepts a leading sign and surrounding whitespace, so "/ 8" and "/+8" would
    // become /8 — a prefix the tenant did not write, silently accepted.
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/0x8")]
    [InlineData("10.0.0.0/8/8")]
    public void AMalformedPrefixDoesNot(string text) => Cidr.TryParse(text, out _).ShouldBeFalse(text);

    [Fact]
    public void HostBitsAreClearedSoTwoSpellingsOfOneNetworkAreOneValue() {
        // ⚠ THE PROPERTY THE WHOLE FAMILY LEANS ON, AND THE ONE THE KUBE-OVN CONTROLLER FORCES.
        // pkg/controller/subnet.go's formatCIDR runs every element of spec.cidrBlock through
        // net.ParseCIDR and writes back ipNet.String(), so a tenant who sends 10.20.5.7/24 has
        // 10.20.5.0/24 stored on the object by the controller. Without this, NetworkSubnets.Matches
        // reports drift on a converged subnet forever and the reconciler never leaves InProgress.
        Cidr.TryParse("10.20.5.7/24", out var sloppy).ShouldBeTrue();
        Cidr.TryParse("10.20.5.0/24", out var canonical).ShouldBeTrue();

        sloppy.ShouldBe(canonical);
        sloppy.Canonical.ShouldBe("10.20.5.0/24");
    }

    [Fact]
    public void OverlapIsSymmetricAndContainmentIsNot() {
        Cidr.TryParse("10.20.0.0/16", out var big).ShouldBeTrue();
        Cidr.TryParse("10.20.5.0/24", out var small).ShouldBeTrue();

        big.Overlaps(small).ShouldBeTrue();
        small.Overlaps(big).ShouldBeTrue("overlap is a symmetric relation and must be computed as one");

        big.Contains(small).ShouldBeTrue();
        small.Contains(big).ShouldBeFalse("containment is not symmetric");
    }

    [Fact]
    public void TwoDisjointPrefixesDoNotOverlap() {
        Cidr.TryParse("10.20.0.0/16", out var a).ShouldBeTrue();
        Cidr.TryParse("10.21.0.0/16", out var b).ShouldBeTrue();

        a.Overlaps(b).ShouldBeFalse();
        b.Overlaps(a).ShouldBeFalse();
    }

    [Fact]
    public void APrefixOverlapsItself() {
        Cidr.TryParse("10.20.0.0/16", out var a).ShouldBeTrue();

        a.Overlaps(a).ShouldBeTrue(
            "an identical range is the most important overlap to catch and the easiest to lose to an "
            + "off-by-one in the mask"
        );
    }

    [Fact]
    public void TwoFamiliesNeverOverlapEachOther() {
        // ⚠ NOT A SPECIAL CASE BOLTED ON. An IPv4 and an IPv6 prefix describe disjoint address
        // spaces, and this is the line that stops a dual-stack subnet reporting a conflict between
        // its own two halves. Without it, Mask() would compare a 4-byte array against a 16-byte one.
        Cidr.TryParse("0.0.0.0/0", out var everyV4).ShouldBeTrue();
        Cidr.TryParse("::/0", out var everyV6).ShouldBeTrue();

        everyV4.Overlaps(everyV6).ShouldBeFalse();
        everyV6.Overlaps(everyV4).ShouldBeFalse();
    }

    [Fact]
    public void ADefaultRouteOverlapsEverythingInItsOwnFamily() {
        Cidr.TryParse("0.0.0.0/0", out var everything).ShouldBeTrue();

        foreach (var reserved in NetworkAddressing.ReservedRanges.Where(x => !x.Cidr.IsV6)) {
            everything.Overlaps(reserved.Cidr).ShouldBeTrue(reserved.Id);
        }
    }

    // ── The reserved table ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryReservedRangeParsesAndIsCanonical() {
        // ⚠ The ReservedRange constructor throws on a malformed prefix, so reaching this test at all
        // proves the first half. What it adds is the SECOND half: a row written as 10.96.0.1/12 would
        // construct fine and then silently reserve 10.96.0.0/12, which is the right answer for the
        // wrong reason and would mislead the next person to read the table.
        foreach (var reserved in NetworkAddressing.ReservedRanges) {
            reserved.Cidr.Canonical.ShouldBe(
                reserved.Prefix,
                $"the reserved range '{reserved.Id}' is not written in its canonical form, so the "
                + "table says something slightly different from what it enforces"
            );
        }
    }

    [Fact]
    public void EveryReservedRangeHasADistinctIdAndAReasonAWholeSentenceLong() {
        var ids = NetworkAddressing.ReservedRanges.Select(x => x.Id).ToList();

        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            ids.Count,
            "two rows share an id, so a refusal naming one of them is ambiguous"
        );

        foreach (var reserved in NetworkAddressing.ReservedRanges) {
            // docs/plan/08 § Errors requires an actionable message. A row whose `Because` is a
            // fragment produces a refusal that names a range and does not say why it is reserved,
            // which leaves the tenant guessing at the one moment they most need not to be.
            reserved.Because.Length.ShouldBeGreaterThan(
                40,
                $"the reserved range '{reserved.Id}' has no real explanation, and it is quoted "
                + "verbatim into the refusal a tenant reads"
            );
        }
    }

    [Fact]
    public void AGlobalRowAppliesInEveryRegionAndARegionalOneDoesNot() {
        var global = NetworkAddressing.ReservedRanges.First(x => x.Region.Length == 0);
        var regional = NetworkAddressing.ReservedRanges.First(x => x.Region.Length > 0);

        global.AppliesIn("eu-central").ShouldBeTrue();
        global.AppliesIn("us-east").ShouldBeTrue();
        global.AppliesIn("").ShouldBeTrue();

        regional.AppliesIn(regional.Region).ShouldBeTrue();

        regional.AppliesIn("somewhere-else").ShouldBeFalse(
            "a per-region row leaking into another region would refuse an address space that is "
            + "perfectly legal there, and docs/plan/14 makes the reserved list per-region precisely "
            + "because an underlay differs per datacentre"
        );
    }

    [Fact]
    public void TheTableContainsAtLeastOneRegionalRowSoTheColumnIsExercised() {
        // ⚠ An unexercised column is the column that rots. The row is an EXAMPLE rather than a fact
        // about any real datacentre — NetworkAddressing.ReservedRanges says so — and it is here so
        // that the first operator to add a real underlay row finds the shape already filled in once.
        NetworkAddressing.ReservedRanges.ShouldContain(x => x.Region.Length > 0);
    }

    // ── The refusal ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("10.96.0.0/12", "kubernetes-services")]
    [InlineData("10.96.5.0/24", "kubernetes-services")]
    [InlineData("10.16.0.0/16", "kube-ovn-default-subnet")]
    [InlineData("100.64.0.0/16", "kube-ovn-join-subnet")]
    [InlineData("127.0.0.0/8", "loopback")]
    [InlineData("169.254.0.0/16", "link-local")]
    [InlineData("224.0.0.0/4", "multicast")]
    [InlineData("fe80::/10", "ipv6-link-local")]
    // ⚠ A SUPERNET OF A RESERVED RANGE IS ALSO REFUSED, which is the case a containment-only check
    // would miss. 10.0.0.0/8 does not sit inside 10.96.0.0/12 — it CONTAINS it — and handing a
    // tenant 10.0.0.0/8 would swallow the Kubernetes service CIDR whole. ⚠ The expected id is the
    // FIRST row of the table it conflicts with, because ProblemWith reports one conflict and stops;
    // ConflictsWithWalksTheWholeTableRatherThanStoppingAtTheFirstRow is what asserts the other three.
    [InlineData("10.0.0.0/8", "kubernetes-services")]
    [InlineData("0.0.0.0/0", "kubernetes-services")]
    public void AnOverlappingPrefixIsRefusedAndTheConflictingRangeIsNamed(string prefix, string expected) {
        var problem = NetworkAddressing.ProblemWith(prefix, "eu-central", "/properties/addressSpace/v4");

        problem.ShouldNotBeNull($"'{prefix}' overlaps '{expected}' and must be refused");

        // ⚠ docs/plan/14 requires the API to "reject with the conflicting range NAMED". A refusal
        // that says only "not allowed" leaves the tenant with nothing to do but guess, and
        // docs/plan/08 § Errors requires a message that names the actual values.
        problem.ShouldContain(expected, Case.Sensitive);
        problem.ShouldContain(prefix, Case.Sensitive);
        problem.ShouldContain("/properties/addressSpace/v4", Case.Sensitive);
    }

    [Theory]
    [InlineData("10.20.0.0/16")]
    [InlineData("10.21.0.0/16")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.168.0.0/16")]
    [InlineData("fd00:20::/48")]
    public void ALegalPrefixIsAccepted(string prefix) =>
        NetworkAddressing.ProblemWith(prefix, "eu-central", "/x").ShouldBeNull(prefix);

    [Fact]
    public void OverlappingAnotherTenantsRangeIsNotRefusedAndThatIsThePointOfAVpc() {
        // ⚠ THE MOST IMPORTANT NEGATIVE ASSERTION IN THE FAMILY. docs/plan/14: "Overlapping CIDRs
        // between a tenant's VPCs is fine; overlapping with the platform's underlay is not." A
        // reserved list that also refused tenant-to-tenant overlap would make 10.20.0.0/16
        // allocatable exactly once across the whole platform, which is not a cloud — it is a
        // spreadsheet. Kube-OVN's per-VPC routing tables are what make the reuse safe.
        const string Same = "10.20.0.0/16";

        NetworkAddressing.ProblemWith(Same, "eu-central", "/x").ShouldBeNull();
        NetworkAddressing.ProblemWith(Same, "eu-central", "/x").ShouldBeNull();

        NetworkAddressing.ProblemWith(Same, "us-east", "/x").ShouldBeNull(
            "the same range in a different region is also fine — nothing about a tenant's private "
            + "address space is globally unique"
        );
    }

    [Fact]
    public void ARegionalRowRefusesOnlyInItsOwnRegion() {
        var regional = NetworkAddressing.ReservedRanges.First(x => x.Region.Length > 0);

        NetworkAddressing.ProblemWith(regional.Prefix, regional.Region, "/x").ShouldNotBeNull();

        NetworkAddressing.ProblemWith(regional.Prefix, "somewhere-else", "/x").ShouldBeNull(
            "a region's underlay is not reserved in another region, and refusing it there would deny "
            + "a tenant an address space that works"
        );
    }

    [Fact]
    public void AMalformedPrefixIsRefusedBeforeAnyOverlapIsConsidered() {
        var problem = NetworkAddressing.ProblemWith("not-a-cidr", "eu-central", "/properties/x");

        problem.ShouldNotBeNull();
        problem.ShouldContain("/properties/x", Case.Sensitive);
        problem.ShouldContain("not a CIDR prefix", Case.Sensitive);
    }

    [Fact]
    public void ConflictsWithWalksTheWholeTableRatherThanStoppingAtTheFirstRow() {
        // ⚠ ProblemWith reports the FIRST conflict, which is right for a message. This is the
        // assertion that the rule is right about every row rather than about row one: a /8 that
        // swallows two reserved ranges must report both, or a table reordering would change what a
        // future author believes the rule does.
        Cidr.TryParse("10.0.0.0/8", out var wide).ShouldBeTrue();

        var conflicts = NetworkAddressing.ConflictsWith(wide, "eu-central");

        conflicts.ShouldContain("kubernetes-services");
        conflicts.ShouldContain("kube-ovn-default-subnet");
        conflicts.ShouldContain("eu-central-underlay");

        conflicts.ShouldNotContain("loopback");
        conflicts.ShouldNotContain("ipv6-link-local");
    }

    // ── The defaults every other test in the family depends on ───────────────────────────────────

    [Fact]
    public void TheDefaultBodiesOfBothTypesAreAcceptedInEveryRegionTheTableKnows() {
        // ⚠ THE TEST THAT STOPS THE WHOLE FAMILY FAILING FOR THE WRONG REASON. Every conformance
        // assertion in both suites creates a resource from these default bodies, and the reconciler
        // refuses an overlapping address space TERMINALLY. A default that conflicted with a reserved
        // range would turn all 28 assertions in each suite red with a message about CIDRs, which is a
        // debugging afternoon nobody should have to spend.
        var cluster = Guid.NewGuid();

        var regions = NetworkAddressing.ReservedRanges
            .Select(x => x.Region)
            .Where(x => x.Length > 0)
            .Append("eu-central")
            .Distinct(StringComparer.Ordinal);

        foreach (var region in regions) {
            using var network = JsonDocument.Parse(VirtualNetworks.Body(cluster, location: region));

            VirtualNetworks.AddressProblem(network.RootElement).ShouldBeNull(
                $"VirtualNetworks.Body's default address space is refused in '{region}'"
            );

            using var subnet = JsonDocument.Parse(NetworkSubnets.Body(cluster, location: region));

            NetworkSubnets.AddressProblem(subnet.RootElement).ShouldBeNull(
                $"NetworkSubnets.Body's default prefix is refused in '{region}'"
            );
        }
    }

    [Fact]
    public void TheDefaultSubnetSitsInsideTheDefaultNetworksAddressSpace() {
        // ⚠ NOTHING ENFORCES THIS AND IT IS ASSERTED ANYWAY. The relation between a subnet's prefix
        // and its parent's address space is a relation between two RESOURCES' bodies, which
        // ResourceSchema cannot see — see § owed, `subnets-are-not-checked-against-the-address-space`.
        // The fixtures are made consistent so that the family's examples read as a coherent network
        // rather than as an illustration of the gap, and this is what keeps them that way.
        var cluster = Guid.NewGuid();

        using var network = JsonDocument.Parse(VirtualNetworks.Body(cluster));
        using var subnet = JsonDocument.Parse(NetworkSubnets.Body(cluster));

        Cidr.TryParse(VirtualNetworks.AddressSpaceV4(network.RootElement), out var space).ShouldBeTrue();
        Cidr.TryParse(NetworkSubnets.PrefixV4(subnet.RootElement), out var prefix).ShouldBeTrue();

        space.Contains(prefix).ShouldBeTrue(
            "the example subnet is outside the example network, which would teach every reader of "
            + "this provider's fixtures the wrong shape"
        );
    }

    [Fact]
    public void EverySchemaExampleInTheFamilyIsItselfALegalPrefix() {
        // ⚠ A schema's ExampleJson reaches the OpenAPI document, the CLI help and the portal's
        // placeholder text. An example the platform would refuse is a copy-paste that fails, which is
        // the worst possible first experience of a resource type.
        string[] examples = [
            "10.20.0.0/16", "fd00:20::/48", "10.20.1.0/24", "fd00:20:1::/64"
        ];

        foreach (var example in examples) {
            NetworkAddressing.ProblemWith(example, "eu-central", "/x").ShouldBeNull(example);
        }
    }

    // ── The pattern the schema actually enforces, at the API, before the 202 ─────────────────────

    [Fact]
    public void TheSchemaRefusesAMalformedPrefixBeforeTheWritePathAnswers() {
        // ⚠ THE HALF OF docs/plan/14'S REQUIREMENT THAT **IS** MET AT THE API, AND IT IS MET BECAUSE
        // THE ADDRESS SPACE IS A STRING RATHER THAN docs/plan/14'S ARRAY. ADR-012's fifth surface
        // refuses `@pattern` on an array — charts/managed/kafka records the same gap as
        // `cidr-shape-is-unenforced`, whose cost is a body that "may send 999.0.0.1/99 and be
        // accepted". Two typed properties instead of one list is what keeps this family out of that.
        var property = VirtualNetworks.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/addressSpace/v4");

        property.Pattern.ShouldBe(Cidr.V4Pattern);
        property.Kind.ShouldBe(SchemaKind.Text);

        property.ElementKind.ShouldBe(
            SchemaKind.Unknown,
            "the moment this becomes an array its Pattern stops reaching ./build.sh Charts and the "
            + "CIDR shape is enforced by nothing"
        );

        using var body = JsonDocument.Parse(
            VirtualNetworks.Body(Guid.NewGuid(), addressSpaceV4: "not-a-cidr")
        );

        VirtualNetworks.Schema2026.Validate(body.RootElement, allowTags: true)
            .TryGetError(out var error)
            .ShouldBeTrue("a malformed prefix must be refused at the API, not after the 202");

        error.Target.ShouldBe("/properties/addressSpace/v4");
    }

    [Fact]
    public void TheSubnetsPrefixIsPatternedToo() {
        var property = NetworkSubnets.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/addressPrefix/v4");

        property.Pattern.ShouldBe(Cidr.V4Pattern);

        using var body = JsonDocument.Parse(
            NetworkSubnets.Body(Guid.NewGuid(), prefixV4: "10.0.0.0")
        );

        NetworkSubnets.Schema2026.Validate(body.RootElement, allowTags: true)
            .TryGetError(out var error)
            .ShouldBeTrue();

        error.Target.ShouldBe("/properties/addressPrefix/v4");
    }

    [Fact]
    public void ThePatternAndTheParserDisagreeOnlyInTheDirectionThatIsSafe() {
        // ⚠ THE DIVISION OF LABOUR, PINNED. The Pattern is a SHAPE check that runs on the request
        // path with a 100 ms budget, so it is deliberately linear and loose; TryParse decides
        // meaning. What must never happen is the reverse — a value the pattern REFUSES that TryParse
        // would have accepted — because that is a legal address space the API rejects for no reason
        // the tenant can see.
        const string PatternAcceptsParserRefuses = "999.0.0.1/8";

        System.Text.RegularExpressions.Regex
            .IsMatch(PatternAcceptsParserRefuses, "^(?:" + Cidr.V4Pattern + ")$")
            .ShouldBeTrue("the loose pattern is expected to admit this");

        Cidr.TryParse(PatternAcceptsParserRefuses, out _).ShouldBeFalse(
            "and the parser is what actually refuses it — the reconciler's message names the pointer"
        );

        // The safe direction, over every example and every reserved range the family declares.
        foreach (var legal in NetworkAddressing.ReservedRanges.Select(x => x.Prefix)) {
            var pattern = legal.Contains(':', StringComparison.Ordinal)
                ? Cidr.V6Pattern
                : Cidr.V4Pattern;

            System.Text.RegularExpressions.Regex
                .IsMatch(legal, "^(?:" + pattern + ")$")
                .ShouldBeTrue(
                    $"'{legal}' parses as a CIDR and the schema pattern refuses it, so a tenant could "
                    + "not enter a value the platform itself uses"
                );
        }
    }
}
