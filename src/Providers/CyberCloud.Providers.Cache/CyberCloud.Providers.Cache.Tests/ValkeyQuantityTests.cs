using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Tests;

/// <summary>
///     The quantity arithmetic, which is the one piece of real computation this provider does.
/// </summary>
/// <remarks>
///     ⚠ <b>It exists because <c>maxmemory-policy</c> does nothing without <c>maxmemory</c>.</b> Valkey
///     applies an eviction policy only when a ceiling is set; without one the process grows until the
///     kernel's OOM killer takes the pod, and the tenant's chosen policy has never been consulted. So
///     the provider derives the ceiling from the container's own memory quantity — and a derivation is
///     a thing that can be wrong quietly, which is what the rest of this provider's rendering is not.
///     <para>
///         ⚠ <b>The parsing half of that derivation is no longer this provider's.</b>
///         <c>ValkeyCaches.QuantityBytes</c> used to walk the digits and the suffix table itself, in
///         <see langword="double" />; it now floors what <see cref="KubeQuantity.TryParse" /> returns.
///         So the cases below split in two: what a <i>quantity</i> means, which the platform decides,
///         and what a <i>ceiling</i> is, which this provider decides. The tests that pin literal
///         preset ceilings are the ones that had to stay green across that change.
///     </para>
///     <para>
///         Every case here is a value <see cref="ValkeyCaches.QuantityPattern" /> accepts, because a
///         value the pattern refuses never reaches this code.
///     </para>
/// </remarks>
public sealed class ValkeyQuantityTests {
    [Theory]
    // The binary suffixes, which is what every preset uses.
    [InlineData("1Gi", 1073741824L)]
    [InlineData("4Gi", 4294967296L)]
    [InlineData("128Gi", 137438953472L)]
    [InlineData("512Mi", 536870912L)]
    [InlineData("1Ki", 1024L)]
    // The decimal ones, which a tenant who has read the Kubernetes docs will write.
    [InlineData("1k", 1000L)]
    [InlineData("2M", 2000000L)]
    [InlineData("3G", 3000000000L)]
    // No suffix is bytes.
    [InlineData("4096", 4096L)]
    // ⚠ `m` IS MILLI, AND THIS IS THE ONE EVERYBODY GETS WRONG. `512m` is half a byte, not 512
    // mebibytes. It is converted rather than refused because QuantityPattern permits it: a caller who
    // meant mebibytes gets a ceiling of zero, which MaxMemoryBytes turns into no ceiling at all —
    // visible — rather than one a thousand times too large, which would OOM the pod under load.
    [InlineData("512m", 0L)]
    [InlineData("2000m", 2L)]
    // A fractional quantity is legal apimachinery and legal here.
    [InlineData("1.5Gi", 1610612736L)]
    public void AKubernetesQuantityBecomesTheBytesItMeans(string quantity, long expected) {
        ValkeyCaches.QuantityBytes(quantity).ShouldBe(expected);
    }

    /// <summary>
    ///     A fractional mantissa against a large suffix lands on the byte it means, not one beside it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>These are the cases that made a second parser a defect rather than a duplicate.</b> The
    ///     old <see langword="double" /> implementation agreed with <see cref="KubeQuantity" /> on every
    ///     accept/reject verdict and disagreed on the number: <c>8.7T</c> came back as 8699999999999,
    ///     one byte short of the terabyte a tenant asked for, because 8.7 has no exact binary
    ///     expansion. Every suffix is a power of ten or a power of two, both of which terminate inside
    ///     <see langword="decimal" />, so the exact answer was always available — the provider just was
    ///     not asking for it.
    /// </remarks>
    [Theory]
    [InlineData("8.7T", 8700000000000L)]
    [InlineData("8.7P", 8700000000000000L)]
    [InlineData("8.7E", 8700000000000000000L)]
    // 7.7 × 2^50 is 8669429282688204.8, so the floor is …204. `double` rounded up to …205 first.
    [InlineData("7.7Pi", 8669429282688204L)]
    // 0.001 × 2^60 is 1152921504606846.976.
    [InlineData("0.001Ei", 1152921504606846L)]
    // Above 2^53 a `double` cannot hold consecutive integers at all, so this one lost its last digit.
    [InlineData("9007199254740993", 9007199254740993L)]
    public void AQuantityLandsOnTheByteItMeansRatherThanTheNearestOneADoubleCanHold(
        string quantity,
        long expected
    ) {
        ValkeyCaches.QuantityBytes(quantity).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Gi")]
    [InlineData("four gigabytes")]
    // ⚠ A suffix apimachinery does not have. `Gb` is not a Kubernetes quantity and never was; the
    // pattern refuses it, and if it ever reached here the honest answer is "not a quantity" rather than
    // a guess about which of GB, Gb and Gi the author meant.
    [InlineData("4Gb")]
    // The forms the platform's grammar deliberately drops, each read as "not a quantity" rather than
    // as zero: no sign, no exponent, no wrong case, no whitespace anywhere.
    [InlineData("-1")]
    [InlineData("-1Gi")]
    [InlineData("1e3")]
    [InlineData("4gi")]
    [InlineData("4GB")]
    [InlineData("500 m")]
    [InlineData(" 500m")]
    [InlineData("500m ")]
    [InlineData("1.2.3")]
    public void SomethingThatIsNotAQuantityIsNotGuessedAt(string quantity) {
        // ⚠ BOTH HALVES, because "one parser" is the claim under test. The provider refusing a string
        // the platform accepts, or accepting one it refuses, is the divergence this consolidation
        // removed — and asserting only the provider's side would let it come back.
        KubeQuantity.TryParse(quantity, out _).ShouldBeFalse(quantity);
        ValkeyCaches.QuantityBytes(quantity).ShouldBeNull(quantity);
    }

    /// <summary>The ceiling is the platform's answer floored, and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>Whole bytes is this provider's rounding rule, not the platform's.</b>
    ///     <c>maxmemory</c> is a byte count, so the exact <see langword="decimal" /> gets floored here;
    ///     a quota meter reserving <c>0.5</c> vCPU floors nothing. Pinning the relationship rather than
    ///     the numbers is what keeps the two from being confused for one rule again.
    /// </remarks>
    [Theory]
    [InlineData("4Gi")]
    [InlineData("1.5Gi")]
    [InlineData("512m")]
    [InlineData("2000m")]
    [InlineData("8.7T")]
    [InlineData("4096")]
    public void TheProvidersAnswerIsThePlatformsAnswerFloored(string quantity) {
        KubeQuantity.TryParse(quantity, out var exact).ShouldBeTrue(quantity);
        ValkeyCaches.QuantityBytes(quantity).ShouldBe((long)exact, quantity);
    }

    /// <summary>A quantity past <see cref="long.MaxValue" /> is refused rather than clamped to it.</summary>
    /// <remarks>
    ///     ⚠ <b>The old parser returned <see cref="long.MaxValue" /> for these and called it a
    ///     success.</b> Its range check was <c>bytes > long.MaxValue</c> on a <see langword="double" />,
    ///     and <c>(double)long.MaxValue</c> rounds up to 2^63 — so <c>8Ei</c>, which is exactly 2^63,
    ///     compared equal, passed the check, and saturated on the cast. A cache would have been told its
    ///     ceiling was 8 exbibytes of a pod that has nothing like it. Refusing is the honest answer: the
    ///     line is then omitted and the operator sees no ceiling rather than an invented one.
    /// </remarks>
    [Theory]
    [InlineData("8Ei")]
    [InlineData("9007199254740993Ki")]
    [InlineData("99999999999999999999999999999999999999")]
    public void AQuantityTooLargeForAByteCountIsRefusedRatherThanSaturated(string quantity) {
        ValkeyCaches.QuantityBytes(quantity).ShouldBeNull(quantity);
    }

    /// <summary>Every preset's ceiling is the number it was before the parsers were consolidated.</summary>
    /// <remarks>
    ///     ⚠ <b>Written to be green on both sides, which is the opposite of every other test here.</b>
    ///     A cache whose <c>maxmemory</c> moved because the platform changed how it reads <c>4Gi</c> is
    ///     a cache that starts evicting at a size nobody chose, so these eight numbers were read off the
    ///     old implementation first and pinned as literals — not re-derived from the parser, which would
    ///     have agreed with whatever the parser had become.
    /// </remarks>
    [Theory]
    [InlineData("m1.nano", 805306368L)]
    [InlineData("m1.micro", 1610612736L)]
    [InlineData("m1.small", 3221225472L)]
    [InlineData("m1.medium", 6442450944L)]
    [InlineData("m1.large", 12884901888L)]
    [InlineData("m1.xlarge", 25769803776L)]
    [InlineData("m1.2xlarge", 51539607552L)]
    [InlineData("m1.4xlarge", 103079215104L)]
    public void EveryPresetsCeilingIsTheNumberItWasBeforeTheParsersWereConsolidated(
        string preset,
        long expected
    ) {
        using var desired = JsonDocument.Parse(BodyWithPreset(preset));

        ValkeyCaches.MaxMemoryBytes(desired.RootElement).ShouldBe(expected, preset);
    }

    [Fact]
    public void EveryPresetsMemoryParsesAndItsCeilingIsThreeQuartersOfIt() {
        // ⚠ THE WHOLE TABLE, because a preset whose memory did not parse would render no `maxmemory`
        // at all — silently, for that one size. A per-preset failure is exactly the shape that survives
        // a test written against one example.
        foreach (var (preset, quantities) in ValkeyCaches.Presets) {
            var bytes = ValkeyCaches.QuantityBytes(quantities.Memory);

            bytes.ShouldNotBeNull($"'{preset}' declares the memory quantity '{quantities.Memory}'");
            bytes.Value.ShouldBeGreaterThan(0, preset);

            using var desired = JsonDocument.Parse(BodyWithPreset(preset));

            ValkeyCaches.MaxMemoryBytes(desired.RootElement).ShouldBe(
                (long)(bytes.Value * ValkeyCaches.MaxMemoryFraction),
                preset
            );
        }
    }

    [Fact]
    public void AnExplicitMemoryOverridesThePresetForTheCeilingAsWellAsForTheLimit() {
        // ⚠ The two must move together or the cache evicts at a size unrelated to the pod's limit. A
        // maxmemory derived from the preset while the container got an override is a cache that OOMs
        // at 75 % of a number nobody set.
        var body = JsonNode.Parse(ValkeyCaches.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["sizing"] =
            new JsonObject { ["preset"] = "m1.4xlarge", ["memory"] = "2Gi", ["cpu"] = "1" };

        using var desired = JsonDocument.Parse(body.ToJsonString());

        ValkeyCaches.Resources(desired.RootElement).Memory.ShouldBe("2Gi");
        ValkeyCaches.MaxMemoryBytes(desired.RootElement).ShouldBe(1610612736L);
    }

    [Theory]
    // Half a byte: the `512m`-means-mebibytes mistake, which floors to nothing.
    [InlineData("500m")]
    // ⚠ AND A WHOLE BYTE, WHICH USED TO RENDER `maxmemory 0` — the exact line the case above exists to
    // prevent. The guard tested the parsed byte count, and 1 passes `> 0`; three quarters of it floors
    // to 0, and 0 means UNLIMITED to Valkey. Moving the guard onto the ceiling closed it. No preset is
    // anywhere near one byte, which is why nothing caught this until the parser was touched.
    [InlineData("1")]
    [InlineData("1500m")]
    [InlineData("1.9")]
    public void NoCeilingIsRenderedWhenThereIsNoMemoryToDeriveOneFrom(string memory) {
        // ⚠ A `maxmemory 0` would mean UNLIMITED to Valkey, which is the opposite of what a caller with
        // no memory figure should get. The line is omitted instead, and `maxmemory-policy` is still
        // written — a policy with no ceiling is inert, which is the honest state, and the alternative
        // is a number this provider invented.
        var body = JsonNode.Parse(ValkeyCaches.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["sizing"] = new JsonObject { ["memory"] = memory, ["cpu"] = "1" };

        using var desired = JsonDocument.Parse(body.ToJsonString());

        ValkeyCaches.MaxMemoryBytes(desired.RootElement).ShouldBeNull(memory);

        var config = ValkeyCaches.CustomConfig(desired.RootElement);

        config.ShouldNotContain(x => x.StartsWith("maxmemory ", StringComparison.Ordinal), memory);
        config.ShouldContain("maxmemory-policy noeviction");
    }

    [Fact]
    public void TheCeilingLeavesRoomForABackgroundSaveRatherThanFillingTheContainer() {
        // The fraction is a decision, not a rounding. Valkey's own accounting covers the dataset; a
        // fork for a background save copies pages on write, the replication backlog and client output
        // buffers sit outside maxmemory, and the exporter sidecar shares the pod's limit. Pinning it
        // means a change to it is a change somebody had to make on purpose.
        //
        // ⚠ `decimal`, so the fraction cannot put the ceiling a byte off the way `double` put `8.7T`
        // a byte off. It is only 0.75 today, which is exact either way — the type is what keeps that
        // from mattering when somebody picks 0.8.
        ValkeyCaches.MaxMemoryFraction.ShouldBe(0.75m);

        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        // The default preset is m1.small — 4Gi.
        ValkeyCaches.MaxMemoryBytes(desired.RootElement).ShouldBe(3221225472L);
    }

    [Fact]
    public void TheCeilingIsWrittenAsAPlainByteCountRatherThanAQuantity() {
        // ⚠ `redis.conf` is not Kubernetes. `maxmemory 4Gi` is a parse error there — its own suffixes
        // are `gb` and `gbi` — so the line carries a decimal integer, which every version of both
        // engines accepts.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        var line = ValkeyCaches.CustomConfig(desired.RootElement)
            .Single(x => x.StartsWith("maxmemory ", StringComparison.Ordinal));

        var argument = line["maxmemory ".Length..];

        long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out _).ShouldBeTrue(
            $"'{line}' is not `maxmemory <bytes>`"
        );
    }

    static string BodyWithPreset(string preset) {
        var body = JsonNode.Parse(ValkeyCaches.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["sizing"] = new JsonObject { ["preset"] = preset };
        return body.ToJsonString();
    }
}
