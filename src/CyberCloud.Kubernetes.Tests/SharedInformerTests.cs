using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Informers;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The informer-cache-loss failure class of docs/plan/09 § Observing: resume from the last
///     <c>resourceVersion</c>, fall back when the API server has compacted it, and stagger.
/// </summary>
public sealed class SharedInformerTests {
    static readonly Guid ClusterId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");

    static readonly GroupVersionKind Deployments =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    // ── The mandatory selector ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryListCarriesTheManagedBySelector() {
        // docs/plan/09 § Observing: informers are "filtered by
        // cybercloud.io/managed-by=cybercloud". A cluster we manage may also be written to by the
        // tenant (ADR-013 says so), so an informer without this term would stream the tenant's own
        // objects into our resource grains.
        var api = new RecordingApiClient();
        var informer = Informer(api);

        (await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        api.Lists.ShouldHaveSingleItem();
        api.Lists[0].Selector.ShouldBe("cybercloud.io/managed-by=cybercloud");
        informer.LabelSelector.ShouldBe(KubeLabels.ManagedBySelector);
    }

    [Fact]
    public async Task ACallerSelectorIsAndedOnAndCannotDisplaceTheMandatoryTerm() {
        var api = new RecordingApiClient();
        var informer = Informer(api, "app=postgres");

        await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken);

        api.Lists[0].Selector.ShouldBe("cybercloud.io/managed-by=cybercloud,app=postgres");
    }

    [Fact]
    public async Task ACallerCannotRemoveTheMandatoryTermByPassingAContradictoryOne() {
        // The term is prepended, so a caller passing `managed-by=theirs` gets a selector that
        // matches nothing rather than one that matches everything. Failing closed.
        var api = new RecordingApiClient();
        var informer = Informer(api, "cybercloud.io/managed-by=theirs");

        await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken);

        api.Lists[0].Selector.ShouldStartWith("cybercloud.io/managed-by=cybercloud,");
    }

    // ── Resume ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AColdInformerListsInFull() {
        var api = new RecordingApiClient {
            DefaultPage = Result<ListPage>.Success(new(["{}", "{}"], "rv-100", string.Empty))
        };

        var informer = Informer(api);
        var outcome = (await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken))
            .GetValueOrThrow();

        outcome.Resumed.ShouldBeFalse();
        outcome.FellBackToFullList.ShouldBeFalse();
        outcome.ItemCount.ShouldBe(2);
        api.Lists[0].ResourceVersion.ShouldBeNull("a cold list must not send a resourceVersion.");
        informer.ResourceVersion.ShouldBe("rv-100");
    }

    [Fact]
    public async Task AnInformerWithACursorResumesFromItRatherThanRelisting() {
        // ⚠ THE MITIGATION. docs/plan/09 § Observing: "resume from the last resourceVersion where
        // the API server still has it". This is the assertion that the cursor is actually sent.
        var api = new RecordingApiClient { DefaultPage = Result<ListPage>.Success(new([], "rv-205", string.Empty)) };

        var informer = Informer(api);
        informer.ResumeFrom("rv-200");

        var outcome = (await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken))
            .GetValueOrThrow();

        outcome.Resumed.ShouldBeTrue();
        outcome.FellBackToFullList.ShouldBeFalse();
        outcome.ItemCount.ShouldBe(0, "a resume that finds nothing new is the cheap case.");

        api.Lists.ShouldHaveSingleItem();
        api.Lists[0].ResourceVersion.ShouldBe("rv-200");
        informer.ResourceVersion.ShouldBe("rv-205");
    }

    [Fact]
    public async Task A410GoneFallsBackToAFullListAndSaysSo() {
        // ⚠ The bounded case. The cursor is older than etcd's retained history, so the delta we
        // asked for does not exist. Resuming from a compacted cursor would silently skip every
        // change in between — worse than the burst of load a full list costs.
        var api = new RecordingApiClient();
        api.Pages.Enqueue(Result<ListPage>.Failure(ErrorCode.PreconditionFailed, "resourceVersion is too old"));
        api.Pages.Enqueue(Result<ListPage>.Success(new(["{}", "{}", "{}"], "rv-900", string.Empty)));

        var informer = Informer(api);
        informer.ResumeFrom("rv-1");

        var outcome = (await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken))
            .GetValueOrThrow();

        outcome.Resumed.ShouldBeTrue();
        outcome.FellBackToFullList.ShouldBeTrue();
        outcome.ItemCount.ShouldBe(3);

        api.Lists.Count.ShouldBe(2);
        api.Lists[0].ResourceVersion.ShouldBe("rv-1", "the resume was attempted…");
        api.Lists[1].ResourceVersion.ShouldBeNull("…and the fallback dropped the cursor entirely.");

        informer.ResourceVersion.ShouldBe("rv-900");
    }

    [Fact]
    public async Task AnErrorThatIsNotA410IsNotSilentlyTurnedIntoAFullList() {
        // A transport failure must not be mistaken for compaction: re-listing in full on every
        // network blip is the stampede, not the mitigation.
        var api = new RecordingApiClient();
        api.Pages.Enqueue(Result<ListPage>.Failure(ErrorCode.InternalError, "connection refused"));

        var informer = Informer(api);
        informer.ResumeFrom("rv-1");

        var outcome = await informer.EstablishAsync(cancellationToken: TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        api.Lists.Count.ShouldBe(1, "no fallback list may be attempted.");
        informer.ResourceVersion.ShouldBe("rv-1", "and the cursor must survive to be retried.");
    }

    [Fact]
    public async Task APagedListWalksEveryPageAndKeepsTheFinalCursor() {
        var api = new RecordingApiClient();
        api.Pages.Enqueue(Result<ListPage>.Success(new(["{}", "{}"], "rv-500", "page-2")));
        api.Pages.Enqueue(Result<ListPage>.Success(new(["{}"], "rv-500", string.Empty)));

        var outcome = (await Informer(api).EstablishAsync(cancellationToken: TestContext.Current.CancellationToken))
            .GetValueOrThrow();

        outcome.Pages.ShouldBe(2);
        outcome.ItemCount.ShouldBe(3);
        outcome.ResourceVersion.ShouldBe("rv-500");
        api.Lists[1].Continue.ShouldBe("page-2");
    }

    // ── Stagger ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EstablishmentWaitsTheClustersStaggerSlotBeforeListing() {
        // ⚠ Asserted through an injected delay rather than by sleeping, so the test measures the
        // VALUE the informer chose rather than the wall clock. A test that actually waited 30
        // seconds is a test that gets deleted.
        var api = new RecordingApiClient();
        var window = TimeSpan.FromSeconds(30);
        var informer = Informer(api, window: window);

        var waited = new List<TimeSpan>();

        var outcome = (await informer.EstablishAsync(
            (d, _) => {
                // The delay must be taken BEFORE the list, or it staggers nothing.
                api.Lists.ShouldBeEmpty();
                waited.Add(d);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow();

        waited.ShouldHaveSingleItem();
        waited[0].ShouldBe(InformerStagger.DelayFor(ClusterId, window));
        waited[0].ShouldBeGreaterThan(TimeSpan.Zero);
        waited[0].ShouldBeLessThan(window);

        outcome.StaggerDelay.ShouldBe(waited[0]);
        informer.LastStaggerDelay.ShouldBe(waited[0]);
        informer.Lease.StaggerDelay.ShouldBe(waited[0]);
    }

    [Fact]
    public async Task ThirtyClustersReEstablishingTogetherDoNotAllWaitTheSameTime() {
        // ⚠ THE ROLLING-DEPLOY CASE, end to end through the informer rather than through the
        // stagger function alone. docs/plan/09 § Observing: "a 30-silo rolling deploy without
        // staggering is a synchronized list storm."
        var window = TimeSpan.FromSeconds(30);
        var delays = new List<TimeSpan>();
        var bytes = new byte[16];

        for (var i = 0; i < 30; i++) {
            Array.Clear(bytes);
            BitConverter.TryWriteBytes(bytes, i);
            bytes[15] = 0xD0;

            var informer = new SharedInformer(
                new(bytes),
                Deployments,
                "ns",
                string.Empty,
                new RecordingApiClient(),
                NullLogger.Instance,
                window
            );

            await informer.EstablishAsync(
                (d, _) => {
                    delays.Add(d);
                    return Task.CompletedTask;
                },
                TestContext.Current.CancellationToken
            );
        }

        delays.Count.ShouldBe(30);
        delays.Distinct().Count().ShouldBeGreaterThan(25);
        (delays.Max() - delays.Min()).ShouldBeGreaterThan(
            TimeSpan.FromSeconds(20),
            "the 30 clusters must be spread across most of the window, not bunched."
        );
    }

    // ── Sharing ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheInformerIsSharedAndCountsItsHolders() {
        // docs/plan/09 § Observing: "One watch per kind is O(kinds)" — not O(resources).
        var informer = Informer(new());

        informer.Subscribers.ShouldBe(0);
        informer.Subscribe().ShouldBe(1);
        informer.Subscribe().ShouldBe(2);
        informer.Unsubscribe().ShouldBe(1);
        informer.Unsubscribe().ShouldBe(0);
        informer.Unsubscribe().ShouldBe(0, "the count must not go negative.");
    }

    // ── The pump ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheCursorAdvancesOnEveryEventIncludingBookmarks() {
        var api = new RecordingApiClient();
        api.WatchEvents.AddRange(
            [
                new(KubeWatchEventKind.Added, "{}", "rv-11"),
                new(KubeWatchEventKind.Bookmark, "{}", "rv-12"),
                new(KubeWatchEventKind.Modified, "{}", "rv-13")
            ]
        );

        var informer = Informer(api);
        var seen = new List<KubeWatchEventKind>();

        (await informer.PumpAsync(
            e => {
                seen.Add(e.Kind);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        // ⚠ A bookmark advances the cursor and is NOT delivered — that is what it is for. Without
        // bookmarks a quiet kind holds an ever-older cursor and is the most likely to be compacted
        // out by the time the silo restarts.
        seen.ShouldBe([KubeWatchEventKind.Added, KubeWatchEventKind.Modified]);
        informer.ResourceVersion.ShouldBe("rv-13");
    }

    [Fact]
    public async Task AnErrorFrameDropsTheCursorSoTheNextEstablishmentListsInFull() {
        var api = new RecordingApiClient();
        api.WatchEvents.Add(new(KubeWatchEventKind.Added, "{}", "rv-20"));
        api.WatchEvents.Add(new(KubeWatchEventKind.Error, "{}", string.Empty));

        var informer = Informer(api);
        var outcome = await informer.PumpAsync(_ => Task.CompletedTask, TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.PreconditionFailed);
        informer.ResourceVersion.ShouldBe(
            string.Empty,
            "an ERROR frame is nearly always a 410 in disguise; keeping the cursor would re-ask for "
            + "a delta the server has already refused."
        );
    }

    static SharedInformer Informer(
        RecordingApiClient api,
        string extraSelector = "",
        TimeSpan? window = null
    ) =>
        new(
            ClusterId,
            Deployments,
            "tenant-space",
            extraSelector,
            api,
            NullLogger.Instance,
            window ?? TimeSpan.Zero
        );
}
