using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.DBforPostgreSQL.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Conformance;

/// <summary>
///     <c>CyberCloud.DBforPostgreSQL/servers</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>This file is the entire cost of putting the second provider under conformance, and that
///     is the claim being tested rather than restated.</b> docs/plan/03 § Providers: <i>"It is one
///     xUnit theory that every provider must pass … A provider is not registered in the platform
///     bundle until it passes."</i> Nothing in <c>test/CyberCloud.Conformance</c> changed to
///     accommodate a type that renders two objects instead of one — <c>Objects</c> was already a
///     list and <c>ObjectMatchesDesired</c> was already called per object.
/// </remarks>
public sealed class PostgresCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.DBforPostgreSQL/servers",
            CreateProvider = () => new PostgresProvider(),
            ReconcilerType = typeof(PostgresServerReconciler),
            CreateReconciler = clock => new PostgresServerReconciler(clock),
            Type = PostgresServers.Type,
            ApiVersion = PostgresServers.V2026,
            Body = cluster => PostgresServers.Body(cluster),
            // ⚠ Changes `replicas`, which the reconciler renders into `spec.instances` and
            // PostgresServers.Matches reads back. A body that differed only where the reconciler
            // ignores it would pass the update test while proving the update never left the grain.
            ChangedBody = cluster => PostgresServers.Body(cluster, replicas: 3),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written:
            // a hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutStorageSize(PostgresServers.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            ActionName = PostgresServers.ListKeysAction,
            // ⚠ TWO OBJECTS, AND THE POOLER IS IN THE LIST BECAUSE `Body` LEAVES POOLING ON. The case
            // supplies one body, so the object set is a function of that body — a case whose Body
            // turned pooling off and whose Objects still listed the Pooler would fail every
            // world-facing assertion for a reason that is the case's rather than the provider's.
            Objects = (id, ns) => [
                PostgresServers.ClusterRef(ns, id.Name),
                PostgresServers.PoolerRef(ns, id.Name)
            ],
            // ⚠ CloudNativePG's, not this reconciler's — ClusterJson deliberately renders no
            // `bootstrap.initdb.secret`, so the operator generates the owner's password itself. It is
            // not in `Objects` above because that member is what the reconciler is judged on having
            // APPLIED, and applying this one would be the defect rather than the fixture.
            OperatorWritten = (id, ns) => [
                (KubeSecret.Ref(ns, PostgresServers.CredentialSecretName(id.Name)),
                    OperatorSecret.Json(
                        KubeSecret.Ref(ns, PostgresServers.CredentialSecretName(id.Name)),
                        [
                            (PostgresServers.UsernameKey, "app"),
                            (PostgresServers.PasswordKey, "a-generated-password")
                        ]
                    ))
            ],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return PostgresServers.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>A valid body with the required data-volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-PostgreSQL provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class PostgresServerConformance(ProviderTestCluster<PostgresCase> cluster)
    : ProviderConformanceTests<PostgresCase>(cluster), IClassFixture<ProviderTestCluster<PostgresCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-PostgreSQL provider.</summary>
public sealed class PostgresServerClusterConformance() : ClusterBackedConformanceTests(PostgresCase.ProviderCase);
