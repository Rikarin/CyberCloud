using CyberCloud.Kubernetes.Apply;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The projection behind <see cref="ApplyResult.Updated" />'s contract — "the object existed and
///     at least one field <b>we own</b> changed".
/// </summary>
/// <remarks>
///     <para>
///         The bodies below are shaped like what a real API server returns, and the
///         <c>managedFields</c> entries are the ones a real k3s wrote: our <c>Apply</c> entry, and
///         the deployment controller's <c>Update</c> entry with <c>"subresource": "status"</c>
///         covering <c>f:status</c> plus the <c>deployment.kubernetes.io/revision</c> annotation.
///         That second entry is the whole reason this class exists — it moves
///         <c>metadata.resourceVersion</c>, which is what the client used to compare.
///     </para>
///     <para>
///         The end-to-end proof against a real cluster is in <c>ServerSideApplyTests</c>; these are
///         the cases that are quicker to pin here than to provoke there.
///     </para>
/// </remarks>
public sealed class OwnedFieldSnapshotTests {
    const string Ours = "cybercloud/cybercloud.dbforpostgresql";

    // ── The defect ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AControllersStatusWriteDoesNotCountAsOurChange() {
        // ⚠ THE BUG, in one assertion. Everything here that differs between the two bodies was
        // written by the k3s deployment controller inside the window between our read and our write:
        // the whole of .status, the revision annotation, metadata.generation, and resourceVersion.
        // None of it is ours, so the snapshots must be equal and the apply must report Unchanged.
        Capture(Deployment(replicas: 1, settled: false)).ShouldBe(Capture(Deployment(replicas: 1, settled: true)));
    }

    [Fact]
    public void ChangingAValueWeOwnDoesCount() {
        Capture(Deployment(replicas: 1)).ShouldNotBe(Capture(Deployment(replicas: 3)));
    }

    [Fact]
    public void ChangingALabelWeOwnCountsEvenThoughGenerationWouldNotMove() {
        // ⚠ The trap in the cheapest alternative fix. metadata.generation ignores status writes,
        // which is the half that looks right — but it does not move for a metadata-only change
        // either, so a relabel we genuinely made would report Unchanged.
        var before = Deployment(replicas: 1);
        var after = Deployment(replicas: 1).Replace(@"""app"": ""web""", @"""app"": ""api""", StringComparison.Ordinal);

        Capture(before).ShouldNotBe(Capture(after));
    }

    [Fact]
    public void AnAnnotationAnotherManagerAddsDoesNotCount() {
        // ⚠ The trap in the second-cheapest fix — diffing the bodies while ignoring .status. The
        // deployment controller also writes deployment.kubernetes.io/revision, which lives under
        // metadata, so that diff still reports a phantom update. We own two annotations by name;
        // this is not one of them.
        var before = Deployment(replicas: 1);
        var after = Deployment(replicas: 1).Replace(
            @"""deployment.kubernetes.io/revision"": ""1""",
            @"""deployment.kubernetes.io/revision"": ""2""",
            StringComparison.Ordinal
        );

        Capture(before).ShouldBe(Capture(after));
    }

    // ── Ownership ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OwningNothingYetIsADifferentAnswerFromOwningTheSameThings() {
        // An object we have never applied to. TryCapture succeeds — field management is readable —
        // and reports an empty projection, which differs from any projection with fields in it. So
        // the first apply against someone else's object is an Updated, correctly.
        OwnedFieldSnapshot.TryCapture(Deployment(replicas: 1), "nobody-by-that-name", out var none)
            .ShouldBeTrue();

        none.ShouldNotBe(Capture(Deployment(replicas: 1)));
    }

    [Fact]
    public void AnEntryOfOursAgainstASubresourceIsSkipped() {
        // Our manager name, our fields — but written to .status, which is never what an apply of the
        // main resource compares. Rewriting the controller's entry to carry our name must change
        // nothing.
        var mine = Deployment(replicas: 1).Replace(@"""manager"": ""k3s""", $@"""manager"": ""{Ours}""", StringComparison.Ordinal);

        Capture(mine).ShouldBe(Capture(Deployment(replicas: 1)));
    }

    [Fact]
    public void AContainerFieldWeOwnIsComparedThroughItsListMapKey() {
        // f:containers is keyed by k:{"name":"c"}, so resolving it means matching the element by its
        // key fields rather than by position.
        var before = Deployment(replicas: 1);
        var after = Deployment(replicas: 1).Replace("pause:3.10", "pause:3.9", StringComparison.Ordinal);

        Capture(before).ShouldNotBe(Capture(after));
    }

    [Fact]
    public void ADefaultedFieldInsideAContainerWeOwnDoesNotCount() {
        // We own the container element and, by name, its image and name. The API server defaults
        // imagePullPolicy and terminationMessagePath into the same element; those are not ours.
        var defaulted = Deployment(replicas: 1).Replace(
            @"""imagePullPolicy"": ""IfNotPresent""",
            @"""imagePullPolicy"": ""Always""",
            StringComparison.Ordinal
        );

        Capture(defaulted).ShouldBe(Capture(Deployment(replicas: 1)));
    }

    [Fact]
    public void KeyOrderInTheBodyIsNotAChange() {
        // The snapshot is compared as a string, so it sorts. The API server's own serialization is
        // deterministic; this pins that the comparison does not quietly depend on that.
        const string plain = """
                             { "metadata": { "name": "x", "labels": { "a": "1", "b": "2" },
                                 "managedFields": [ { "manager": "m", "operation": "Apply",
                                   "apiVersion": "v1", "fieldsV1": { "f:metadata": { "f:labels": {} } } } ] } }
                             """;

        const string reordered = """
                                 { "metadata": { "labels": { "b": "2", "a": "1" }, "name": "x",
                                     "managedFields": [ { "operation": "Apply", "manager": "m",
                                       "apiVersion": "v1", "fieldsV1": { "f:metadata": { "f:labels": {} } } } ] } }
                                 """;

        OwnedFieldSnapshot.TryCapture(plain, "m", out var left).ShouldBeTrue();
        OwnedFieldSnapshot.TryCapture(reordered, "m", out var right).ShouldBeTrue();

        left.ShouldBe(right);
    }

    // ── When ownership cannot be read ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{ "metadata": { "name": "x" } }""")]
    [InlineData("""{ "metadata": { "managedFields": "not-an-array" } }""")]
    public void ABodyWithNoReadableFieldManagementIsReportedAsSuch(string? json) {
        // ⚠ false is "cannot tell", not "owns nothing" — KubeApiClient falls back to the
        // resourceVersion comparison on it, which over-reports Updated rather than under-reporting.
        // Collapsing the two would make an API server that strips managedFields report every apply
        // as Unchanged, which is the failure that hides a real change.
        OwnedFieldSnapshot.TryCapture(json, Ours, out var snapshot).ShouldBeFalse();
        snapshot.ShouldBeEmpty();
    }

    static string Capture(string json) {
        OwnedFieldSnapshot.TryCapture(json, Ours, out var snapshot).ShouldBeTrue();
        return snapshot;
    }

    /// <summary>
    ///     A <c>Deployment</c> as the API server returns it, with both managers' entries.
    /// </summary>
    /// <param name="replicas">The replica count, which we own.</param>
    /// <param name="settled">
    ///     <c>true</c> for the body after the deployment controller has run: a populated
    ///     <c>.status</c>, a moved <c>generation</c>, and a later <c>resourceVersion</c>. Everything
    ///     it toggles belongs to the controller, not to us.
    /// </param>
    static string Deployment(int replicas, bool settled = false) =>
        $$"""
          {
            "apiVersion": "apps/v1",
            "kind": "Deployment",
            "metadata": {
              "name": "ssa-noop",
              "namespace": "cc-fabric",
              "generation": {{(settled ? 2 : 1)}},
              "resourceVersion": "{{(settled ? 900 : 812)}}",
              "labels": { "app": "web", "cybercloud.io/managed-by": "cybercloud" },
              "annotations": {
                "cybercloud.io/resource-path": "/tenants/t/providers/CyberCloud.DBforPostgreSQL/servers/main",
                "cybercloud.io/reconcile-hash": "sha256:abc",
                "deployment.kubernetes.io/revision": "1"
              },
              "managedFields": [
                {
                  "manager": "{{Ours}}",
                  "operation": "Apply",
                  "apiVersion": "apps/v1",
                  "fieldsV1": {
                    "f:metadata": {
                      "f:annotations": {
                        "f:cybercloud.io/resource-path": {},
                        "f:cybercloud.io/reconcile-hash": {}
                      },
                      "f:labels": {
                        "f:app": {},
                        "f:cybercloud.io/managed-by": {}
                      }
                    },
                    "f:spec": {
                      "f:replicas": {},
                      "f:selector": {},
                      "f:template": {
                        "f:metadata": { "f:labels": { "f:app": {} } },
                        "f:spec": {
                          "f:containers": {
                            "k:{\"name\":\"c\"}": { ".": {}, "f:image": {}, "f:name": {} }
                          }
                        }
                      }
                    }
                  }
                },
                {
                  "manager": "k3s",
                  "operation": "Update",
                  "apiVersion": "apps/v1",
                  "subresource": "status",
                  "fieldsV1": {
                    "f:metadata": { "f:annotations": { "f:deployment.kubernetes.io/revision": {} } },
                    "f:status": {
                      "f:observedGeneration": {},
                      "f:replicas": {},
                      "f:conditions": {}
                    }
                  }
                }
              ]
            },
            "spec": {
              "replicas": {{replicas}},
              "selector": { "matchLabels": { "app": "web" } },
              "template": {
                "metadata": { "labels": { "app": "web" } },
                "spec": {
                  "containers": [
                    {
                      "name": "c",
                      "image": "registry.k8s.io/pause:3.10",
                      "imagePullPolicy": "IfNotPresent",
                      "terminationMessagePath": "/dev/termination-log"
                    }
                  ]
                }
              }
            },
            "status": {{(settled
                ? """{ "observedGeneration": 2, "replicas": 1, "conditions": [ { "type": "Available", "status": "True" } ] }"""
                : "{}")}}
          }
          """;
}
