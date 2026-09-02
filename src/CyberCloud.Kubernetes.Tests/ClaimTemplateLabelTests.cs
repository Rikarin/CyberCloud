using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;
using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     What a real API server does to a <c>StatefulSet</c>'s <c>spec.volumeClaimTemplates</c> — the
///     measurement that decided how many of ADR-013's labels a claim template may carry.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Only a real API server can run this, and the provider conformance lanes cannot — not
///         even the cluster-backed ones.</b> Both create a resource and converge it; neither applies
///         the same object twice at two different api-versions, which is the sequence the hazard
///         lives in. The Docker-free harness has no admission at all.
///     </para>
///     <para>
///         The finding, in one sentence: <c>spec.volumeClaimTemplates</c> is refused every change
///         once the set exists, so a claim template may carry only labels whose values are the same
///         on every reconcile — <see cref="KubeLabels.LifetimeStable" />, which is the six of the
///         seven that are not <c>cybercloud.io/api-version</c>.
///     </para>
/// </remarks>
[Collection(K3sSuite.Name)]
public sealed class ClaimTemplateLabelTests(K3sFixture k3s) {
    const string OurManager = "cybercloud/cybercloud.dbforpostgresql";
    const string ClaimTemplatePath = "spec/volumeClaimTemplates";

    static readonly GroupVersionKind StatefulSets =
        new() { Group = "apps", Version = "v1", Kind = "StatefulSet", Plural = "statefulsets" };

    [Fact]
    public async Task TheClaimTemplateCarriesTheSixLifetimeStableLabelsInTheCluster() {
        // ⚠ Read back off the API server rather than out of our own JSON — the point of this lane.
        // A claim template's ObjectMeta goes through the same admission as any other, and a label
        // key the platform got wrong would be rejected here and nowhere else.
        var token = TestContext.Current.CancellationToken;

        var command = Command("claims-labelled", "2026-08-01");
        (await k3s.Api.ApplyAsync(command, token)).GetValueOrThrow().Result.ShouldBe(ApplyResult.Created);

        var read = (await k3s.Api.GetAsync(command.Target, token)).GetValueOrThrow();

        var labels = JsonNode.Parse(read.Json)!["spec"]!["volumeClaimTemplates"]![0]!["metadata"]!
            ["labels"] as JsonObject;

        labels.ShouldNotBeNull();

        foreach (var key in KubeLabels.LifetimeStable) {
            labels[key]?.GetValue<string>().ShouldBe(
                command.Labels[key],
                $"the claim template in the cluster is missing or disagrees about {key}."
            );
        }

        // ⚠ The seventh is absent on the template and present on the object, which is the whole
        // shape of the decision in one assertion.
        labels.ContainsKey(KubeLabels.ApiVersion).ShouldBeFalse();

        (JsonNode.Parse(read.Json)!["metadata"]!["labels"] as JsonObject)
            .ShouldNotBeNull()
            .ContainsKey(KubeLabels.ApiVersion)
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ASecondApplyAtADifferentApiVersionIsAccepted() {
        // ⚠ THE REGRESSION GUARD FOR THE SIX-NOT-SEVEN DECISION. cybercloud.io/api-version is
        // stamped from the request that caused the reconcile, so this is the ordinary sequence — a
        // tenant calls at a newer api-version and the resource reconciles again. It passes only
        // because the claim template's labels did not move.
        var token = TestContext.Current.CancellationToken;

        (await k3s.Api.ApplyAsync(Command("claims-reapplied", "2026-08-01"), token))
            .GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        var second = await k3s.Api.ApplyAsync(Command("claims-reapplied", "2027-01-01"), token);

        second.IsSuccess.ShouldBeTrue(
            "a reconcile at a newer api-version must still apply. If this fails, something put a "
            + "per-request value into the claim template: a StatefulSet's spec.volumeClaimTemplates "
            + "is refused every change once the set exists, and a rejected apply does not heal — the "
            + "resource would never reconcile again. See KubeLabels.LifetimeStable."
        );

        // The object's OWN api-version label did move, which is what makes the assertion above a
        // statement about the template rather than about an apply that changed nothing.
        (await k3s.ReadAppsFieldAsync(
            "statefulsets",
            "claims-reapplied",
            "metadata",
            "labels",
            KubeLabels.ApiVersion
        )).ShouldBe("2027-01-01");
    }

    [Fact]
    public async Task AClaimTemplateThatMOVESIsRefusedByTheApiServer() {
        // ⚠ THE SABOTAGE ARM. The guard above has never been made to fire unless this one does: it
        // renders the api-version label INTO the claim template by hand, applies twice at two
        // versions, and asserts the API server refuses the second. Without this, "the six are
        // enough" would be a claim about a body nobody had contradicted.
        var token = TestContext.Current.CancellationToken;

        (await k3s.Api.ApplyAsync(Sabotaged("claims-moving", "2026-08-01"), token))
            .GetValueOrThrow()
            .Result.ShouldBe(ApplyResult.Created);

        var second = await k3s.Api.ApplyAsync(Sabotaged("claims-moving", "2027-01-01"), token);

        second.IsFailure.ShouldBeTrue(
            "a live StatefulSet's spec.volumeClaimTemplates is supposed to be refused every change. "
            + "If this passed, the rule changed and KubeLabels.LifetimeStable can be widened."
        );

        // ⚠ AND THE REFUSAL DOES NOT NAME THE OFFENDING FIELD. It lists the fields that MAY change
        // and says the rest are forbidden, so an operator reading this in a log is told a
        // StatefulSet apply was rejected and not which field did it. That is a second reason the
        // exclusion is enforced at the builder rather than left to be diagnosed: the failure this
        // prevents is one nobody can read.
        second.Error!.Message.ShouldContain("Forbidden", Case.Insensitive);
        second.Error.Message.ShouldContain("minReadySeconds");
        second.Error.Message.ShouldNotContain("volumeClaimTemplates");
    }

    KubeCommand Command(string name, string apiVersion) {
        var id = Resource(name);

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(StatefulSets)
            .InNamespace(K3sFixture.Namespace)
            .WithFieldManager(OurManager)
            .WithApiVersion(apiVersion)
            .WithTemplateLabels(ClaimTemplatePath)
            .ObjectJson(StatefulSetJson(name, claimLabels: null))
            .Build();
    }

    /// <summary>The same command with a per-request label written into the template by hand.</summary>
    /// <param name="name">The object's name.</param>
    /// <param name="apiVersion">The api-version to stamp, in the template as well as on the object.</param>
    KubeCommand Sabotaged(string name, string apiVersion) {
        var id = Resource(name);

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(StatefulSets)
            .InNamespace(K3sFixture.Namespace)
            .WithFieldManager(OurManager)
            .WithApiVersion(apiVersion)
            .ObjectJson(
                StatefulSetJson(name, $$"""{ "{{KubeLabels.ApiVersion}}": "{{apiVersion}}" }""")
            )
            .Build();
    }

    static ResourceId Resource(string name) =>
        new(
            Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            name,
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

    /// <summary>A minimal but real <c>StatefulSet</c> — one claim template, one pause container.</summary>
    /// <param name="name">The object's name, which is also its selector value and its service name.</param>
    /// <param name="claimLabels">A labels object to render into the template, or none.</param>
    static string StatefulSetJson(string name, string? claimLabels) =>
        $$"""
          {
            "metadata": { "name": "{{name}}" },
            "spec": {
              "replicas": 1,
              "serviceName": "{{name}}",
              "selector": { "matchLabels": { "app": "{{name}}" } },
              "template": {
                "metadata": { "labels": { "app": "{{name}}" } },
                "spec": {
                  "containers": [
                    {
                      "name": "main",
                      "image": "registry.k8s.io/pause:3.9",
                      "volumeMounts": [ { "name": "data", "mountPath": "/data" } ]
                    }
                  ]
                }
              },
              "volumeClaimTemplates": [
                {
                  "metadata": {
                    "name": "data"{{(claimLabels is null ? "" : ", \"labels\": " + claimLabels)}}
                  },
                  "spec": {
                    "accessModes": [ "ReadWriteOnce" ],
                    "resources": { "requests": { "storage": "1Gi" } }
                  }
                }
              ]
            }
          }
          """;
}
