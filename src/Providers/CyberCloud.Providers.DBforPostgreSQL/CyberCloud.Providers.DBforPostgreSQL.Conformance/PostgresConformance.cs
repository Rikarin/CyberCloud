using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.DBforPostgreSQL.Contracts;
using CyberCloud.ResourceManager.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Conformance;

/// <summary>
///     <c>CyberCloud.DBforPostgreSQL/servers</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         This file is the entire cost of putting the second provider under conformance, and that
///         is the claim being tested rather than restated.
///     </b> docs/plan/03 § Providers:
///     <i>
///         "It is one
///         xUnit theory that every provider must pass … A provider is not registered in the platform
///         bundle until it passes."
///     </i> Nothing in <c>test/CyberCloud.Conformance</c> changed to
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
            // ⚠ AND THE CLAIMS, OWNED BY THE CLUSTER — issue #69. CloudNativePG creates each
            // instance's PersistentVolumeClaims itself and stamps a controller reference on every one,
            // so the shared suite's fake garbage-collects them with the Cluster unless the teardown
            // detached them first. Serials 1 and 3 rather than 1 and 2, because a failover replaced
            // instance 2 and the serial only ever moves forward — a claim named off the replica count
            // would miss serial 3, and the suite's follow-through would then pass over a claim the
            // reconciler never touched. The owner's uid is left empty: the harness fills in the one
            // the fake issued for the applied Cluster, which is what SetAsOwnedBy reads.
            OperatorWritten = (id, ns) => [
                (KubeSecret.Ref(ns, PostgresServers.CredentialSecretName(id.Name)),
                    OperatorSecret.Json(
                        KubeSecret.Ref(ns, PostgresServers.CredentialSecretName(id.Name)),
                        [
                            (PostgresServers.UsernameKey, "app"),
                            (PostgresServers.PasswordKey, "a-generated-password")
                        ]
                    )),
                Claim(ns, id.Name, serial: 1, wal: false),
                Claim(ns, id.Name, serial: 1, wal: true),
                Claim(ns, id.Name, serial: 3, wal: false),
                Claim(ns, id.Name, serial: 3, wal: true)
            ],
            ObjectMatchesDesired = match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);
                return PostgresServers.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <summary>
    ///     One claim as CloudNativePG v1.30.0 builds it — <c>pkg/reconciler/persistentvolumeclaim/build.go</c>:
    ///     the calculator's labels, the serial and status annotations, the cluster label
    ///     <c>SetInheritedData</c> adds, and a controller reference to the <c>Cluster</c> whose uid
    ///     the harness resolves.
    /// </summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="cluster">The <c>Cluster</c>'s name, which is the resource's.</param>
    /// <param name="serial">The instance serial the operator assigned.</param>
    /// <param name="wal">Whether this is the <c>-wal</c> claim rather than the data claim.</param>
    static (ObjectRef Target, string Json) Claim(string ns, string cluster, int serial, bool wal) {
        var instance = $"{cluster}-{serial.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var name = wal ? instance + "-wal" : instance;

        var metadata = new JsonObject {
            ["name"] = name,
            ["namespace"] = ns,
            ["labels"] = new JsonObject {
                [PostgresServers.ClaimLabel] = cluster,
                ["cnpg.io/instanceName"] = instance,
                ["cnpg.io/pvcRole"] = wal ? "PG_WAL" : "PG_DATA",
                ["app.kubernetes.io/managed-by"] = "cloudnative-pg",
                ["app.kubernetes.io/name"] = "cloudnative-pg",
                ["app.kubernetes.io/component"] = "database"
            },
            ["annotations"] = new JsonObject {
                ["cnpg.io/nodeSerial"] = serial.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["cnpg.io/pvcStatus"] = "ready"
            },
            ["ownerReferences"] = new JsonArray(
                KubeJson.OwnerReference(PostgresServers.ClusterOwner(cluster, uid: string.Empty))
            )
        };

        var target = new ObjectRef { Kind = RetainedVolume.ClaimKind, Namespace = ns, Name = name };

        return (target,
            new JsonObject {
                ["apiVersion"] = "v1",
                ["kind"] = "PersistentVolumeClaim",
                ["metadata"] = metadata,
                ["spec"] = new JsonObject {
                    ["accessModes"] = new JsonArray("ReadWriteOnce"),
                    ["resources"] = new JsonObject { ["requests"] = new JsonObject { ["storage"] = "20Gi" } }
                }
            }.ToJsonString());
    }

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
