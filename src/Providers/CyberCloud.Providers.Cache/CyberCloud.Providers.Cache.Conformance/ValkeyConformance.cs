using CyberCloud.Conformance;
using CyberCloud.Conformance.Harness;
using CyberCloud.Providers.Cache.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Conformance;

/// <summary>
///     <c>CyberCloud.Cache/redis</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>This file is the entire cost of putting the third provider under conformance.</b>
///     docs/plan/03 § Providers: <i>"It is one xUnit theory that every provider must pass … A provider
///     is not registered in the platform bundle until it passes."</i> Nothing in
///     <c>test/CyberCloud.Conformance</c> changed for a type that renders one object where the last
///     one rendered two, or for an <see cref="ProviderConformanceCase.ObjectMatchesDesired" /> that is
///     a containment test rather than a field comparison.
/// </remarks>
public sealed class ValkeyCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Cache/redis",
            CreateProvider = () => new ValkeyCacheProvider(),
            ReconcilerType = typeof(ValkeyCacheReconciler),
            CreateReconciler = clock => new ValkeyCacheReconciler(clock),
            Type = ValkeyCaches.Type,
            ApiVersion = ValkeyCaches.V2026,
            Body = cluster => ValkeyCaches.Body(cluster),
            // ⚠ Changes `replicas`, which the reconciler renders into `spec.redis.replicas` and
            // ValkeyCaches.Matches reads back. A body that differed only where the reconciler ignores
            // it would pass the update test while proving the update never left the grain.
            ChangedBody = cluster => ValkeyCaches.Body(cluster, replicas: 5),
            // Drops the required `/properties/version`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and then
            // tests "invalid for the wrong reason" while still going green.
            InvalidBody = cluster => WithoutVersion(ValkeyCaches.Body(cluster)),
            InvalidBodyTarget = "/properties/version",
            ActionName = ValkeyCaches.ListKeysAction,
            // ⚠ ONE OBJECT, and the count is a fact about the operator rather than about this suite. A
            // RedisFailover expands into a StatefulSet, a Deployment, three Services and two
            // ConfigMaps; none of them is applied by this provider, so none of them belongs here. A
            // case listing an object the provider does not apply fails every world-facing assertion for
            // the case's reason rather than the provider's.
            Objects = (id, ns) => [ValkeyCaches.FailoverRef(ns, id.Name)],
            ObjectMatchesDesired = (objectJson, desiredJson) => {
                using var desired = JsonDocument.Parse(desiredJson);
                return ValkeyCaches.Matches(objectJson, desired.RootElement);
            },
            // ⚠ THE FIRST NON-EMPTY ONE IN THE TREE, AND IT IS WHY THE MEMBER EXISTS. See below.
            RequiredCrds = [RedisFailoverCrd]
        };

    /// <summary>
    ///     The <c>RedisFailover</c> CRD, for the cluster-backed suite only.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A STUB, AND IT SAYS SO RATHER THAN LOOKING LIKE THE REAL ONE.</b> spotahome's own
    ///         CRD carries a full pod-spec schema thousands of lines long. What
    ///         <c>ClusterConformanceTests</c> asserts needs none of it: that the plural addresses a
    ///         real REST path, that server-side apply behaves as ADR-013 assumes, that the seven labels
    ///         survive admission, and that the stored object carries the desired shape. All four need
    ///         the kind to be <i>served</i>; none needs the spec to be <i>validated</i>. So the spec is
    ///         <c>x-kubernetes-preserve-unknown-fields</c> and the assertions this file supports are
    ///         about addressing and admission, <b>not</b> about whether
    ///         <c>ValkeyCaches.RedisFailoverJson</c> satisfies the operator's schema. Nothing in this
    ///         repository checks that today; <c>charts/managed/valkey/SOURCE</c> records the review
    ///         date, which is the weaker claim, and labels it as one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not the CRD the platform installs.</b> That is <c>charts/bundle/</c>'s, along
    ///         with the operator, and this chart deliberately ships neither — see
    ///         <c>charts/managed/valkey/templates/redisfailover.yaml</c>. A test fixture and a bundle
    ///         artifact would be two files claiming to be the same thing, so this one is scoped to the
    ///         suite by living here and being named for what it is.
    ///     </para>
    ///     <para>
    ///         The group, version, kind, plural, singular, short name and scope are the upstream CRD's,
    ///         taken from <c>manifests/databases.spotahome.com_redisfailovers.yaml</c> — those seven
    ///         <b>are</b> the contract this fixture has to get right, because
    ///         <c>ValkeyCaches.FailoverKind</c> addresses a REST path built from four of them.
    ///     </para>
    /// </remarks>
    public const string RedisFailoverCrd =
        """
        apiVersion: apiextensions.k8s.io/v1
        kind: CustomResourceDefinition
        metadata:
          name: redisfailovers.databases.spotahome.com
        spec:
          group: databases.spotahome.com
          scope: Namespaced
          names:
            kind: RedisFailover
            listKind: RedisFailoverList
            plural: redisfailovers
            singular: redisfailover
            shortNames:
              - rf
          versions:
            - name: v1
              served: true
              storage: true
              schema:
                openAPIV3Schema:
                  type: object
                  properties:
                    spec:
                      type: object
                      x-kubernetes-preserve-unknown-fields: true
                    status:
                      type: object
                      x-kubernetes-preserve-unknown-fields: true
        """;

    /// <summary>A valid body with the required major version removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutVersion(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!.AsObject().Remove("version");
        return node.ToJsonString();
    }
}

/// <summary>The shared suite, run against the managed-Valkey provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class ValkeyCacheConformance(ProviderTestCluster<ValkeyCase> cluster)
    : ProviderConformanceTests<ValkeyCase>(cluster), IClassFixture<ProviderTestCluster<ValkeyCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-Valkey provider.</summary>
/// <remarks>
///     ⚠ <b>Still declared, even though this provider has a real <c>*.Cluster.Conformance</c> project
///     that makes the same assertions against a real API server.</b> The skips are not redundant with
///     it: they run on a machine with no Docker daemon and say, by name, which criteria were not
///     checked. Deleting them here would make "conformance: green" readable as "the cluster-backed
///     criteria were met" on exactly the machines where they were not — which is the reading
///     <c>ClusterBackedConformanceTests</c> exists to prevent.
/// </remarks>
public sealed class ValkeyCacheClusterBackedConformance() : ClusterBackedConformanceTests(ValkeyCase.ProviderCase);
