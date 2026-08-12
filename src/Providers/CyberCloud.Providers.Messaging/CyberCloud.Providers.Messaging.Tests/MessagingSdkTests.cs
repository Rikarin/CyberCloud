using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Failure class (d): two resource types in one provider namespace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>CyberCloud.Messaging</c> is the first provider namespace in the platform with two
///         types in it, and the disambiguation ladders every generated surface uses were built for a
///         world where a namespace had one.</b> <c>SdkEmitter</c>'s first tier resolves a colliding
///         display name by prefixing the provider namespace's last segment — which cannot separate
///         two colliding types in the <i>same</i> namespace, because the prefix is identical. Its
///         second tier falls back to the type path and its third throws. <c>CliEmitter.CommandOf</c>
///         kebabs the type path and throws on a duplicate command name.
///     </para>
///     <para>
///         ⚠ <b>None of those ladders is entered today, and that is exactly why this file exists.</b>
///         <c>Kafka cluster</c> and <c>NATS cluster</c> are distinct display names and
///         <c>kafka</c>/<c>nats</c> are distinct short names, so nothing collides and the interesting
///         code never runs. What can be asserted is the <i>outcome</i>: both types reach both
///         surfaces, under distinct names, with neither swallowing the other. That is the claim a
///         third type in this namespace — <c>rabbitmqClusters</c> is next in docs/plan/12 — can
///         break, and it would break silently: <c>CliEmitter</c>'s <c>JsonObject</c> indexer
///         <i>replaces</i>, so one verb tree entry can overwrite another.
///     </para>
/// </remarks>
public sealed class MessagingSdkTests {
    [Fact]
    public void BothTypesReachTheSdkUnderDistinctModelNames() {
        var sdk = SdkEmitter.Emit(Document());

        foreach (var expected in new[] { "KafkaClusterData", "NATSClusterData" }) {
            sdk.ShouldContain(
                "class " + expected,
                Case.Sensitive,
                $"'{expected}' is not in the emitted SDK. SdkEmitter's collision ladder prefixes the "
                + "PROVIDER NAMESPACE first, which is identical for these two types — so a collision "
                + "here falls through to the type-path tier or throws, and either way one of the two "
                + "models is not what a caller expects."
            );
        }
    }

    [Fact]
    public void BothTypesReachTheCliUnderDistinctCommandsAndDistinctAliases() {
        var cli = CliEmitter.Emit(Document());

        // ⚠ `groups` AND `commands` ARE BOTH JSON OBJECTS KEYED BY NAME, NOT ARRAYS — which is the
        // whole reason this test is worth writing. A JsonObject indexer REPLACES on a duplicate key,
        // so two types that kebabbed to one command name would leave the tree with one entry, no
        // error, and a verb silently missing from `cyc`.
        var commands = cli["groups"]!["messaging"]!["commands"]!.AsObject()
            .Select(x => x.Key)
            .ToArray();

        // ⚠ Count first, then membership. A test that only asserted membership would pass on a tree
        // that had silently lost one verb to the other, because the surviving one is still there.
        commands.Length.ShouldBe(
            2,
            "the messaging group carries " + commands.Length + " command(s): "
            + string.Join(", ", commands)
        );

        commands.ShouldContain("kafka-clusters");
        commands.ShouldContain("nats-clusters");
    }

    [Fact]
    public void TheTwoShortNamesAreDistinctAndTheRegistryRefusesThemOtherwise() {
        // ⚠ THE COLLISION THE REGISTRY ITSELF CATCHES, AND THE ONLY ONE OF THE THREE THAT FAILS AT
        // SILO START RATHER THAN QUIETLY. docs/plan/21 § Grammar's alias table is declared next to
        // each type precisely so that a duplicate is a startup failure; two types in one namespace is
        // the first shape where a duplicate is plausible, because their display names rhyme.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);

        registry.TryGetType(KafkaClusters.Type, out var kafka).ShouldBeTrue();
        registry.TryGetType(NatsClusters.Type, out var nats).ShouldBeTrue();

        kafka.Display.Alias.ShouldNotBeNullOrEmpty();
        nats.Display.Alias.ShouldNotBeNullOrEmpty();
        nats.Display.Alias.ShouldNotBe(kafka.Display.Alias);
        nats.Display.Name.ShouldNotBe(kafka.Display.Name);
    }

    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
    }
}
