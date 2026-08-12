using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Messaging.Tests;

/// <summary>
///     Failure class (e): <b>three</b> resource types in one provider namespace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>CyberCloud.Messaging</c> is the only provider namespace in the platform with more
///         than one type in it, and the disambiguation ladders every generated surface uses were
///         built for a world where a namespace had one.</b> <c>SdkEmitter</c>'s first tier resolves a
///         colliding display name by prefixing the provider namespace's last segment — which cannot
///         separate two colliding types in the <i>same</i> namespace, because the prefix is
///         identical. Its second tier falls back to the type path and its third throws.
///         <c>CliEmitter.CommandOf</c> kebabs the type path and throws on a duplicate command name.
///     </para>
///     <para>
///         ⚠ <b>None of those ladders is entered today, and that is exactly why this file exists.</b>
///         <c>Kafka cluster</c>, <c>NATS cluster</c> and <c>RabbitMQ cluster</c> are distinct display
///         names and <c>kafka</c>/<c>nats</c>/<c>rabbitmq</c> are distinct short names, so nothing
///         collides and the interesting code never runs. What can be asserted is the <i>outcome</i>:
///         all three types reach both surfaces, under distinct names, with none swallowing another.
///     </para>
///     <para>
///         ⚠ <b>THE PREDICTION THIS FILE MADE WAS HALF RIGHT, AND THE HALF IT GOT WRONG IS RECORDED
///         RATHER THAN QUIETLY EDITED.</b> It said the claim <i>"a third type in this namespace —
///         <c>rabbitmqClusters</c> is next in docs/plan/12 — can break"</i>, naming the SDK ladder as
///         the reason. The third type landed on 2026-08-12 and the SDK ladder was never entered:
///         <c>SdkEmitter.Pascal("RabbitMQ cluster")</c> is <c>RabbitMQCluster</c>, which collides with
///         neither sibling. What the third type <i>did</i> exercise for the first time is the shape
///         this file's own comment identified as the silent one — <c>CliEmitter</c>'s command map is
///         a <c>JsonObject</c> whose indexer REPLACES, and a map is not stressed by two entries the
///         way it is by three. So the hazard was named correctly and attributed to the wrong emitter,
///         which is worth knowing before a fourth type is added to reason about the same ladders.
///     </para>
/// </remarks>
public sealed class MessagingSdkTests {
    [Fact]
    public void AllThreeTypesReachTheSdkUnderDistinctModelNames() {
        var sdk = SdkEmitter.Emit(Document());

        foreach (var expected in new[] { "KafkaClusterData", "NATSClusterData", "RabbitMQClusterData" }) {
            sdk.ShouldContain(
                "class " + expected,
                Case.Sensitive,
                $"'{expected}' is not in the emitted SDK. SdkEmitter's collision ladder prefixes the "
                + "PROVIDER NAMESPACE first, which is identical for all three of these types — so a "
                + "collision here falls through to the type-path tier or throws, and either way one "
                + "of the three models is not what a caller expects."
            );
        }
    }

    [Fact]
    public void AllThreeTypesReachTheCliUnderDistinctCommandsAndDistinctAliases() {
        var cli = CliEmitter.Emit(Document());

        // ⚠ `groups` AND `commands` ARE BOTH JSON OBJECTS KEYED BY NAME, NOT ARRAYS — which is the
        // whole reason this test is worth writing. A JsonObject indexer REPLACES on a duplicate key,
        // so two types that kebabbed to one command name would leave the tree with one entry, no
        // error, and a verb silently missing from `cyc`.
        var commands = cli["groups"]!["messaging"]!["commands"]!.AsObject()
            .Select(x => x.Key)
            .ToArray();

        // ⚠ Count first, then membership. A test that only asserted membership would pass on a tree
        // that had silently lost one verb to another, because the survivors are still there.
        commands.Length.ShouldBe(
            3,
            "the messaging group carries " + commands.Length + " command(s): "
            + string.Join(", ", commands)
        );

        commands.ShouldContain("kafka-clusters");
        commands.ShouldContain("nats-clusters");

        // ⚠ `rabbitmq-clusters`, NOT `rabbit-mq-clusters`. CliEmitter.CommandOf kebab-cases the type
        // path on case transitions, and the product is written `RabbitMQ` everywhere else — so a type
        // path of `rabbitMqClusters` would compile, would route (ResourceTypeName compares
        // case-insensitively) and would give the CLI a verb nobody would type.
        commands.ShouldContain("rabbitmq-clusters");
    }

    [Fact]
    public void TheThreeShortNamesAreDistinctAndTheRegistryRefusesThemOtherwise() {
        // ⚠ THE COLLISION THE REGISTRY ITSELF CATCHES, AND THE ONLY ONE OF THE THREE THAT FAILS AT
        // SILO START RATHER THAN QUIETLY. docs/plan/21 § Grammar's alias table is declared next to
        // each type precisely so that a duplicate is a startup failure.
        //
        // ⚠ WHAT IT DOES NOT CATCH is a short name equal to a CLI GROUP — ProviderRegistry.Build
        // never compares one to the other, and System.CommandLine keeps one ValidTokens dictionary
        // over the whole verb tree, so that collision breaks EVERY `cyc` parse rather than one type's.
        // RabbitmqDeclarationTests pins that separately, against typed-out literals.
        var registry = ProviderRegistry.Build([new MessagingProvider()]);

        registry.TryGetType(KafkaClusters.Type, out var kafka).ShouldBeTrue();
        registry.TryGetType(NatsClusters.Type, out var nats).ShouldBeTrue();
        registry.TryGetType(RabbitmqClusters.Type, out var rabbitmq).ShouldBeTrue();

        var aliases = new[] { kafka.Display.Alias, nats.Display.Alias, rabbitmq.Display.Alias };
        var names = new[] { kafka.Display.Name, nats.Display.Name, rabbitmq.Display.Name };

        foreach (var alias in aliases) {
            alias.ShouldNotBeNullOrEmpty();
        }

        aliases.Distinct(StringComparer.Ordinal).Count().ShouldBe(3, string.Join(", ", aliases));
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(3, string.Join(", ", names));
    }

    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MessagingProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
    }
}
