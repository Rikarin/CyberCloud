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
///         Every case here is a value <c>ValkeyCaches.QuantityPattern</c> accepts, because a value the
///         pattern refuses never reaches this code.
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

    [Theory]
    [InlineData("")]
    [InlineData("Gi")]
    [InlineData("four gigabytes")]
    // ⚠ A suffix apimachinery does not have. `Gb` is not a Kubernetes quantity and never was; the
    // pattern refuses it, and if it ever reached here the honest answer is "not a quantity" rather than
    // a guess about which of GB, Gb and Gi the author meant.
    [InlineData("4Gb")]
    public void SomethingThatIsNotAQuantityIsNotGuessedAt(string quantity) {
        ValkeyCaches.QuantityBytes(quantity).ShouldBeNull();
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

    [Fact]
    public void NoCeilingIsRenderedWhenThereIsNoMemoryToDeriveOneFrom() {
        // ⚠ A `maxmemory 0` would mean UNLIMITED to Valkey, which is the opposite of what a caller with
        // no memory figure should get. The line is omitted instead, and `maxmemory-policy` is still
        // written — a policy with no ceiling is inert, which is the honest state, and the alternative
        // is a number this provider invented.
        var body = JsonNode.Parse(ValkeyCaches.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["sizing"] = new JsonObject { ["memory"] = "500m", ["cpu"] = "1" };

        using var desired = JsonDocument.Parse(body.ToJsonString());

        ValkeyCaches.MaxMemoryBytes(desired.RootElement).ShouldBeNull();

        var config = ValkeyCaches.CustomConfig(desired.RootElement);

        config.ShouldNotContain(x => x.StartsWith("maxmemory ", StringComparison.Ordinal));
        config.ShouldContain("maxmemory-policy noeviction");
    }

    [Fact]
    public void TheCeilingLeavesRoomForABackgroundSaveRatherThanFillingTheContainer() {
        // The fraction is a decision, not a rounding. Valkey's own accounting covers the dataset; a
        // fork for a background save copies pages on write, the replication backlog and client output
        // buffers sit outside maxmemory, and the exporter sidecar shares the pod's limit. Pinning it
        // means a change to it is a change somebody had to make on purpose.
        ValkeyCaches.MaxMemoryFraction.ShouldBe(0.75);

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
