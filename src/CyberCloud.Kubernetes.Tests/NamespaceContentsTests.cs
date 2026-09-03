using CyberCloud.Kubernetes.Apply;
using System.Text.Json.Nodes;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The enumeration a namespace delete is decided on: everything, of every kind, or nothing at
///     all.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every test here is about a failure mode whose symptom is silence.</b> A namespace
///         enumeration that comes back short does not look wrong — it looks like an emptier
///         namespace, and an empty namespace is the one answer that authorises a recursive delete of
///         a tenant's live data. So the assertions are mostly of the form "this refuses" rather than
///         "this returns the right thing", and that is the shape the subject deserves.
///     </para>
///     <para>
///         ⚠ <b>What only <c>CyberCloud.Cluster.Conformance</c> can show.</b>
///         <see cref="RecordingApiClient" /> is scripted, so discovery here is whatever a test says
///         it is. That a real API server advertises what it serves, that <c>bindings</c> really
///         answers <c>405</c> to a list, and that a fresh namespace really contains
///         <c>ServiceAccount/default</c> are properties of Kubernetes, and a fake would only assert
///         our belief about them.
///     </para>
/// </remarks>
public sealed class NamespaceContentsTests {
    static readonly Guid ClusterId = Guid.Parse("6f2b91d4-0000-4000-8000-00000000000a");

    const string Namespace = "9d1c2b3a-prod";

    static GroupVersionKind Secrets { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    static GroupVersionKind Claims { get; } =
        new() { Group = "", Version = "v1", Kind = "PersistentVolumeClaim", Plural = "persistentvolumeclaims" };

    // ── The refusals ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoveryThatFailsIsARefusalAndNotAnEmptyNamespace() {
        // ⚠ THE HEADLINE FAILURE. If discovery answers "no kinds" the loop below it runs zero times
        // and the result is an empty list — a namespace that reads as empty without a single object
        // having been looked for.
        var api = new RecordingApiClient {
            Discovery = Result<IReadOnlyList<GroupVersionKind>>.Failure(
                ErrorCode.InternalError,
                "the metrics apiserver is unavailable"
            )
        };

        var listed = await NamespaceContents.ListAsync(
            api,
            ClusterId,
            Namespace,
            TestContext.Current.CancellationToken
        );

        listed.TryGetError(out var error).ShouldBeTrue(
            "a cluster that cannot say what it serves cannot be asked what a namespace holds."
        );

        error.Message.ShouldContain(Namespace);
        api.Lists.ShouldBeEmpty("nothing may be listed once discovery has failed.");
    }

    [Fact]
    public async Task OneKindThatCannotBeListedFailsTheWholeEnumeration() {
        // ⚠ A partial listing is not a smaller true answer. The kinds that were read would be
        // reported and the kind that was not would read as absent, which is precisely how a
        // namespace holding a tenant's Secrets reports as holding none.
        var api = new RecordingApiClient { Discovery = Result<IReadOnlyList<GroupVersionKind>>.Success([Claims, Secrets]) };

        api.Pages.Enqueue(Result<ListPage>.Success(new([Object("data-main-0")], "rv-1", string.Empty)));
        api.Pages.Enqueue(Result<ListPage>.Failure(ErrorCode.AuthorizationFailed, "secrets is forbidden"));

        var listed = await NamespaceContents.ListAsync(
            api,
            ClusterId,
            Namespace,
            TestContext.Current.CancellationToken
        );

        listed.TryGetError(out var error).ShouldBeTrue();
        error.Code.ShouldBe(ErrorCode.AuthorizationFailed, "the cluster's own code is what says whether another pass could differ.");
        error.Message.ShouldContain("Secret");
    }

    [Fact]
    public async Task ANamespaceThatIsReallyEmptyIsReportedAsEmpty() {
        // The other half: refusing is only correct when there is a reason to. A complete listing
        // that found nothing is evidence, and it has to be reportable or the verdict is unreachable
        // for a different reason than the one this suite exists to close.
        var api = new RecordingApiClient { Discovery = Result<IReadOnlyList<GroupVersionKind>>.Success([Claims]) };

        var listed = await NamespaceContents.ListAsync(
            api,
            ClusterId,
            Namespace,
            TestContext.Current.CancellationToken
        );

        listed.GetValueOrThrow().ShouldBeEmpty();
        api.Lists.ShouldHaveSingleItem();
    }

    // ── The selector ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoLabelSelectorIsSentAtAll() {
        // ⚠ THE DISTINCTION THE WHOLE SEAM EXISTS FOR. docs/plan/09 § Observing filters every
        // informer by managed-by, and reusing that here would produce a listing that is empty
        // precisely when the delete is most dangerous — the tenant's own PVC, the operator's Secret
        // and the unregistered chart are all objects that selector hides.
        var api = new RecordingApiClient { Discovery = Result<IReadOnlyList<GroupVersionKind>>.Success([Claims]) };

        await NamespaceContents.ListAsync(api, ClusterId, Namespace, TestContext.Current.CancellationToken);

        api.Lists[0].Selector.ShouldBeEmpty();
        api.Lists[0].Namespace.ShouldBe(Namespace);
    }

    // ── Paging ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryPageIsFollowedBeforeTheKindIsConsideredRead() {
        // ⚠ A kind whose first page is followed and whose second is not is the same defect as a kind
        // that was never listed, one page in — and the objects it drops are the ones alphabetically
        // last, which is not a set anybody would notice missing.
        var api = new RecordingApiClient { Discovery = Result<IReadOnlyList<GroupVersionKind>>.Success([Claims]) };

        api.Pages.Enqueue(Result<ListPage>.Success(new([Object("data-main-0")], "rv-1", "cursor-2")));
        api.Pages.Enqueue(Result<ListPage>.Success(new([Object("data-main-1")], "rv-1", string.Empty)));

        var listed = await NamespaceContents.ListAsync(
            api,
            ClusterId,
            Namespace,
            TestContext.Current.CancellationToken
        );

        listed.GetValueOrThrow().Count.ShouldBe(2);
        api.Lists.Count.ShouldBe(2);
        api.Lists[0].Continue.ShouldBeNull();
        api.Lists[1].Continue.ShouldBe("cursor-2");
    }

    // ── What comes back ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LabelsComeOffTheStoredObjectAndAnObjectWithNoneIsNotOurs() {
        // ⚠ ABSENT IS NOT EMPTY-BUT-OURS. An object with no metadata.labels carries no managed-by,
        // which is the conservative reading and the only one that cannot turn into a delete.
        var api = new RecordingApiClient { Discovery = Result<IReadOnlyList<GroupVersionKind>>.Success([Claims]) };

        api.Pages.Enqueue(
            Result<ListPage>.Success(
                new(
                    [
                        """{"metadata":{"name":"ours","labels":{"cybercloud.io/managed-by":"cybercloud"}}}""",
                        """{"metadata":{"name":"theirs"}}"""
                    ],
                    "rv-1",
                    string.Empty
                )
            )
        );

        var found = (await NamespaceContents.ListAsync(
            api,
            ClusterId,
            Namespace,
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        found.Count.ShouldBe(2);

        found.Single(x => x.Name == "ours").Labels[KubeLabels.ManagedBy].ShouldBe(KubeLabels.ManagedByValue);
        found.Single(x => x.Name == "theirs").Labels.ShouldBeEmpty();

        found[0].Kind.Kind.ShouldBe("PersistentVolumeClaim");
        found[0].Namespace.ShouldBe(Namespace);
    }

    static string Object(string name) =>
        new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = Namespace }
        }.ToJsonString();
}
