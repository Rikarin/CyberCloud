using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;
using k8s;
using k8s.Models;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     A namespace listing against a real API server that advertises a group it can't serve — the
///     refusal <c>KubeApiClient.DiscoverNamespacedKindsAsync</c> makes, provoked on purpose.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This used to be proved by accident, and the accident is what #96 removed.
///         </b>
///         <c>ClusterConformanceTests.ARealNamespaceHoldsWhatKubernetesPutsThereAndTheReclaimSeesIt</c>
///         reached the refusal whenever a young k3s's metrics-server hadn't come up yet, so
///         <c>metrics.k8s.io/v1beta1</c> answered 503 — and reached the listing instead when it
///         had. Which half of that test ran was a race, and the recipe now switches metrics-server
///         off (<c>ClusterInfrastructure.DisableMetricsServer</c>). The property the race used to
///         exercise still matters: a group whose discovery fails must fail the whole enumeration and
///         name itself, because a kind missing from the answer reads as a kind the namespace doesn't
///         hold, and that absence is what authorizes a recursive delete. So it's provoked here,
///         deterministically, with an <c>APIService</c> whose backing service doesn't exist.
///     </para>
///     <para>
///         ⚠ <b>A real API server, because the 503 is the aggregator's and not ours.</b>
///         <c>NamespaceContentsTests</c> fails discovery wholesale through a recording client; what
///         it can't show is that a real <c>/apis</c> advertises a group it then can't describe, which
///         is the shape <c>KubeApiClient</c> reads one group at a time.
///     </para>
///     <para>
///         ⚠ <b>The <c>APIService</c> is removed in a <c>finally</c>, and the removal is asserted.</b>
///         Every other class in <see cref="K3sSuite" /> shares this API server; an unanswering group
///         left behind would turn their discovery into this test's refusal.
///     </para>
/// </remarks>
/// <param name="k3s">The shared API server.</param>
[Collection(K3sSuite.Name)]
public sealed class NamespaceDiscoveryRefusalTests(K3sFixture k3s) {
    /// <summary>A group nothing serves. The <c>.test</c> TLD is reserved, so no real API can own it.</summary>
    const string Group = "refusal.cybercloud.test";

    /// <summary>The group's one version.</summary>
    const string Version = "v1alpha1";

    /// <summary>An <c>APIService</c> is named <c>{version}.{group}</c>; the API server refuses any other name.</summary>
    const string ApiServiceName = Version + "." + Group;

    [Fact]
    public async Task AGroupTheApiServerAdvertisesAndCannotDescribeRefusesTheWholeListingAndNamesItself() {
        var token = TestContext.Current.CancellationToken;

        // ── Before: a complete listing, so the refusal below is this test's and nobody else's ────
        //
        // ⚠ This is also the check that K3sFixture.DisableMetricsServer took effect. A stock k3s's
        // metrics-server answers 503 until its pod is up, and on a young cluster this line would
        // refuse over `metrics.k8s.io/v1beta1` instead.
        var before = await NamespaceContents.ListAsync(k3s.Api, K3sFixture.ClusterId, K3sFixture.Namespace, token);

        before.IsSuccess.ShouldBeTrue(
            "the listing refused before this test registered anything, so some other group on this "
            + $"k3s already fails discovery: {before.Error?.Message}"
        );

        await k3s.Raw.ApiregistrationV1.CreateAPIServiceAsync(
            new V1APIService {
                Metadata = new() { Name = ApiServiceName },
                Spec = new() {
                    Group = Group,
                    Version = Version,
                    GroupPriorityMinimum = 1000,
                    VersionPriority = 15,
                    InsecureSkipTLSVerify = true,
                    Service = new() { NamespaceProperty = "default", Name = "nobody-serves-this", Port = 443 }
                }
            },
            cancellationToken: token
        );

        try {
            await WaitUntilAsync(advertised: true, token);

            var listed = await NamespaceContents.ListAsync(
                k3s.Api,
                K3sFixture.ClusterId,
                K3sFixture.Namespace,
                token
            );

            // ⚠ A refusal, not a shorter answer. A listing that skipped the group would come back
            // Success with every kind but this one's, and nothing about it would look wrong.
            listed.IsSuccess.ShouldBeFalse(
                $"the API server advertises '{Group}/{Version}' and answers 503 for it, and the "
                + $"listing came back with {(listed.TryGetValue(out var found) ? found.Count : 0)} "
                + "object(s) anyway — a listing with a hole in it, reported as complete."
            );

            // ⚠ And it names what to fix: the namespace that couldn't be enumerated, and the group
            // that wouldn't answer. A refusal naming neither tells an operator nothing.
            listed.Error!.Message.ShouldContain(K3sFixture.Namespace);
            listed.Error.Message.ShouldContain(Group + "/" + Version);
        } finally {
            await k3s.Raw.ApiregistrationV1.DeleteAPIServiceAsync(ApiServiceName, cancellationToken: CancellationToken.None);
        }

        // ── After: the condition clears on its own once the group is gone ────────────────────────
        await WaitUntilAsync(advertised: false, token);

        var after = await NamespaceContents.ListAsync(k3s.Api, K3sFixture.ClusterId, K3sFixture.Namespace, token);

        after.IsSuccess.ShouldBeTrue(
            "the unanswering group was removed and the listing still refuses, so this test has left "
            + $"the shared API server worse than it found it: {after.Error?.Message}"
        );
    }

    /// <summary>
    ///     Waits for the aggregator to add the group to <c>/apis</c>, or to drop it.
    /// </summary>
    /// <param name="advertised">Whether to wait for the group to appear rather than disappear.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ The aggregator picks up an <c>APIService</c> from a watch, so the create and the
    ///     advertisement are two moments. Listing in between would pass for the wrong reason.
    /// </remarks>
    async Task WaitUntilAsync(bool advertised, CancellationToken cancellationToken) {
        for (var attempt = 0; attempt < 60; attempt++) {
            var groups = await k3s.Raw.Apis.GetAPIVersionsAsync(cancellationToken);

            if (groups.Groups.Any(static x => x.Name == Group) == advertised) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        Assert.Fail(
            $"'{Group}' was {(advertised ? "never advertised" : "still advertised")} in /apis after "
            + "thirty seconds."
        );
    }
}
