using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.Providers.Monitor.Contracts;
using CyberCloud.Providers.Monitor.Query;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The metrics explorer's and log search's three reads, typed at a real listener and answered
///     from a real VictoriaMetrics cluster and a real ClickHouse — issue #41, docs/plan/16 § Querying
///     a workspace.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="MonitorQueryFixture" /> says what is production code and what is not. The
///         assertions are grouped by the property they hold: that a workspace reads its own account and
///         database and no other, that reading needs <c>read</c> and nothing more, that the limits
///         refuse before the store is asked, and that the answers have the shape the portal parses.
///     </para>
///     <para>
///         ⚠ <b>The cross-tenant assertions run against data that genuinely exists</b>, for the reason
///         <c>CrossTenantVerbTests</c> gives: tenant A's series and rows are in the stores when tenant B
///         asks, so an empty answer to B is the boundary holding and not the store being empty.
///     </para>
/// </remarks>
public sealed class MonitorQueryOverHttpTests(MonitorQueryFixture stack) : IClassFixture<MonitorQueryFixture> {
    static readonly string[] ErrorsAndWorse = ["error", "fatal"];
    static readonly string[] GetRequests = ["http.method=GET"];
    static readonly string[] ProdBilling = ["deployment.environment=prod", "queue=billing"];
    static readonly string[] AWindowsPath = [@"file.path=C:\temp\new"];

    // ── A workspace reads its own tenancy and nothing else ────────────────────────────────────

    [Fact]
    public async Task ARangeQueryAnswersTheWorkspacesOwnSeriesPerMinute() {
        var (status, body) = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.QueryMetricsAction),
            new {
                query = "cc_requests_total",
                start = Stamp(stack.Origin),
                end = Stamp(stack.Origin.AddMinutes(60)),
                stepSeconds = 60
            }
        );

        status.ShouldBe(200, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("resultType").GetString().ShouldBe("matrix");
        root.GetProperty("stepSeconds").GetInt32().ShouldBe(60);
        root.GetProperty("truncated").GetBoolean().ShouldBeFalse();

        var series = root.GetProperty("series").EnumerateArray().ToList();
        series.Count.ShouldBe(2);
        series.Select(static x => x.GetProperty("labels").GetProperty("tenant").GetString()).ShouldAllBe(static x => x == "a");
        series.Select(static x => x.GetProperty("labels").GetProperty("route").GetString()).Order(StringComparer.Ordinal)
            .ShouldBe(["/api", "/health"]);

        // One point a minute, each value its minute's index — the seed's own arithmetic.
        var points = series[0].GetProperty("points").EnumerateArray().ToList();
        points.Count.ShouldBe(61);
        points[0][0].GetDouble().ShouldBe(stack.Origin.ToUnixTimeSeconds());
        points[0][1].GetDouble().ShouldBe(0);
        points[^1][1].GetDouble().ShouldBe(60);
    }

    [Fact]
    public async Task TheOtherTenantsWorkspaceOfTheSameNameReadsOnlyItsOwnAccount() {
        var path = MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantB, MonitorQueries.QueryMetricsAction);
        var window = new { start = Stamp(stack.Origin), end = Stamp(stack.Origin.AddMinutes(60)), stepSeconds = 300 };

        var (status, body) = await stack.PostAsync(
            MonitorQueryFixture.TenantB,
            path,
            new { query = "cc_requests_total", window.start, window.end, window.stepSeconds }
        );

        status.ShouldBe(200, body);
        Labels(body, "tenant").ShouldBe(["b"], "tenant B's workspace is the same name at a different address, and reads its own account");

        // ⚠ An expression that names A's account reads nothing: vm_account_id is a filter only on the
        // multitenant path, which the store never builds.
        var accountA = MonitorQueryFixture.AccountOf(stack.WorkspaceA, MonitorQueryFixture.TenantA, CyberCloud.Conformance.ConformanceIds.Subscription);
        var (named, namedBody) = await stack.PostAsync(
            MonitorQueryFixture.TenantB,
            path,
            new {
                query = $"cc_requests_total{{vm_account_id=\"{accountA.ToString(CultureInfo.InvariantCulture)}\"}}",
                window.start,
                window.end,
                window.stepSeconds
            }
        );

        named.ShouldBe(200, namedBody);
        Labels(namedBody, "tenant").ShouldBeEmpty();

        // And the label listing is the account's too: B's values of `tenant` are B's.
        var (listed, listedBody) = await stack.PostAsync(
            MonitorQueryFixture.TenantB,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantB, MonitorQueries.ListMetricLabelsAction),
            new { label = "tenant", start = Stamp(stack.Origin), end = Stamp(stack.Origin.AddMinutes(60)) }
        );

        listed.ShouldBe(200, listedBody);
        Values(listedBody).ShouldBe(["b"]);
    }

    [Fact]
    public async Task AWorkspaceThatFoldsOntoAnotherTenantsAccountFailsToCreateAndReadsNoneOfIt() {
        // ⚠⚠ #41's review: the accountID is the GUID folded to 32 bits, so a tenant creating
        // workspaces in a loop eventually lands one on a victim's account. The fixture stages exactly
        // that — tenant B's `collider` folds to an account the ledger says tenant A's workspace holds,
        // and tenant A's series are in it — so an answer here is the boundary and not an empty account.
        stack.CollidingCreate.State.ShouldBe(OperationState.Failed, "the reconciler converged a workspace onto a held account");
        stack.CollidingCreate.Error!.Code.ShouldBe(CyberCloud.Core.ErrorCode.Conflict);
        stack.CollidingCreate.Error.Message.ShouldContain("Delete this workspace and create it again");

        var window = new { start = Stamp(stack.Origin), end = Stamp(stack.Origin.AddMinutes(60)) };

        var (queried, queriedBody) = await stack.PostAsync(
            MonitorQueryFixture.TenantB,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantB, MonitorQueries.QueryMetricsAction, MonitorQueryFixture.CollidingWorkspace),
            new { query = "cc_requests_total", window.start, window.end, stepSeconds = 300 }
        );

        var (listed, listedBody) = await stack.PostAsync(
            MonitorQueryFixture.TenantB,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantB, MonitorQueries.ListMetricLabelsAction, MonitorQueryFixture.CollidingWorkspace),
            new { label = "route", window.start, window.end }
        );

        foreach (var (status, body) in new[] { (queried, queriedBody), (listed, listedBody) }) {
            status.ShouldBe(409, body);
            // No apostrophe in the phrase: the problem body's JSON may spell one as an escape.
            body.ShouldContain("hold its metrics account");
            body.ShouldNotContain("/collided", Case.Sensitive, "tenant A's series reached tenant B through a folded account");
        }
    }

    [Fact]
    public async Task AnotherTenantsPathIs404AndNoneOfItsDataComesBack() {
        // Tenant B's token at tenant A's address: stage 3 refuses the disagreement before routing,
        // with the canonical 404 and not a 403.
        foreach (var action in new[] { MonitorQueries.QueryMetricsAction, MonitorQueries.SearchLogsAction, MonitorQueries.ListMetricLabelsAction }) {
            var (status, body) = await stack.PostAsync(
                MonitorQueryFixture.TenantB,
                MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, action),
                action == MonitorQueries.SearchLogsAction
                    ? new { from = Stamp(stack.Origin), to = Stamp(stack.Origin.AddMinutes(60)) }
                    : action == MonitorQueries.QueryMetricsAction
                        ? new { query = "cc_requests_total" }
                        : (object)new { label = "tenant" }
            );

            status.ShouldBe(404, $"{action}: {body}");
            body.ShouldNotContain("\"a\"");
            body.ShouldNotContain("upstream timed out");
        }

        // Tenant B's search of its own workspace sees its own two rows, and none of A's eight.
        var (own, ownBody) = await stack.PostAsync(
            MonitorQueryFixture.TenantB,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantB, MonitorQueries.SearchLogsAction),
            new { from = Stamp(stack.Origin), to = Stamp(stack.Origin.AddMinutes(60)) }
        );

        own.ShouldBe(200, ownBody);
        Bodies(ownBody).ShouldBe(["tenant b served", "tenant b secret failure"]);
    }

    // ── Reading needs `read`, and nothing more ────────────────────────────────────────────────

    [Fact]
    public async Task ACallerWhoCannotReadTheWorkspaceGets404AndAReaderGetsTheAnswer() {
        var path = MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.QueryMetricsAction);

        try {
            stack.Cluster.Authorizer.Restricted = true;

            var (hidden, hiddenBody) = await stack.PostAsync(MonitorQueryFixture.TenantA, path, new { query = "cc_requests_total" });
            hidden.ShouldBe(404, "no permission at all is invisibility, not a 403 — " + hiddenBody);

            // ⚠ `read` and nothing else. If the action checked listKeys or write, this would be the
            // 403 the permissive double answers a caller who can read but not act.
            stack.Cluster.Authorizer.Granted["read"] = true;

            var (reader, readerBody) = await stack.PostAsync(MonitorQueryFixture.TenantA, path, new { query = "cc_requests_total" });
            reader.ShouldBe(200, readerBody);
        } finally {
            stack.Cluster.Authorizer.Reset();
        }
    }

    // ── The limits refuse before the store is asked ───────────────────────────────────────────

    [Fact]
    public async Task TheLimitsAre400sThatNameTheNumber() {
        var metrics = MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.QueryMetricsAction);

        var tooFine = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            metrics,
            new { query = "up", start = Stamp(stack.Origin.AddDays(-30)), end = Stamp(stack.Origin), stepSeconds = 1 }
        );
        tooFine.Status.ShouldBe(400, tooFine.Body);
        tooFine.Body.ShouldContain("11000");

        var tooLong = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            metrics,
            new { query = "up", start = Stamp(stack.Origin.AddDays(-401)), end = Stamp(stack.Origin) }
        );
        tooLong.Status.ShouldBe(400, tooLong.Body);
        tooLong.Body.ShouldContain("400");

        var half = await stack.PostAsync(MonitorQueryFixture.TenantA, metrics, new { query = "up", start = Stamp(stack.Origin) });
        half.Status.ShouldBe(400, half.Body);

        // A body member the schema does not name is refused before the handler, so nothing a caller
        // sends can become a store parameter — extra_label is VictoriaMetrics' own tenancy-narrowing knob.
        var smuggled = await stack.PostAsync(MonitorQueryFixture.TenantA, metrics, new { query = "up", extra_label = "tenant=b" });
        smuggled.Status.ShouldBe(400, smuggled.Body);

        // The store's own reason for a malformed expression is the caller's to read.
        var malformed = await stack.PostAsync(MonitorQueryFixture.TenantA, metrics, new { query = "sum(rate(cc_requests_total[5m]" });
        malformed.Status.ShouldBe(400, malformed.Body);
        malformed.Body.ShouldContain("The metrics store refused the query");

        var logs = MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.SearchLogsAction);

        var wide = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            logs,
            new { from = Stamp(stack.Origin.AddDays(-91)), to = Stamp(stack.Origin) }
        );
        wide.Status.ShouldBe(400, wide.Body);
        wide.Body.ShouldContain("90");

        var narrow = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            logs,
            new { from = Stamp(stack.Origin.AddDays(-30)), to = Stamp(stack.Origin), bucketSeconds = 60 }
        );
        narrow.Status.ShouldBe(400, narrow.Body);
        narrow.Body.ShouldContain("1000");
    }

    // ── The metric picker ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheLabelListingIsThePickersMetricNamesLabelNamesAndValues() {
        var path = MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.ListMetricLabelsAction);
        var window = new { start = Stamp(stack.Origin), end = Stamp(stack.Origin.AddMinutes(60)) };

        var names = await stack.PostAsync(MonitorQueryFixture.TenantA, path, new { label = "__name__", window.start, window.end });
        names.Status.ShouldBe(200, names.Body);
        Values(names.Body).ShouldBe(["cc_requests_total"]);

        var labels = await stack.PostAsync(MonitorQueryFixture.TenantA, path, new { match = "cc_requests_total", window.start, window.end });
        labels.Status.ShouldBe(200, labels.Body);
        Values(labels.Body).ShouldBe(["__name__", "route", "tenant"]);

        var routes = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            path,
            new { label = "route", match = "cc_requests_total", window.start, window.end }
        );
        routes.Status.ShouldBe(200, routes.Body);
        Values(routes.Body).ShouldBe(["/api", "/health"]);

        // A label name is a path segment on the store's side, so one that is not a label name never
        // reaches it.
        var traversal = await stack.PostAsync(MonitorQueryFixture.TenantA, path, new { label = "../../api/v1/admin" });
        traversal.Status.ShouldBe(400, traversal.Body);
    }

    // ── The log search ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASearchAnswersNewestFirstWithAHistogramThatCountsEverySeverity() {
        var (status, body) = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.SearchLogsAction),
            new { from = Stamp(stack.Origin), to = Stamp(stack.Origin.AddMinutes(60)), bucketSeconds = 600 }
        );

        status.ShouldBe(200, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var rows = root.GetProperty("rows").EnumerateArray().ToList();

        rows.Count.ShouldBe(8);
        rows[0].GetProperty("body").GetString().ShouldBe("tick");
        rows[0].GetProperty("severity").GetString().ShouldBe("unspecified");
        rows[^1].GetProperty("body").GetString().ShouldBe("request served");

        // ⚠ The row's time is the seed's, to the millisecond, although the store runs in
        // Europe/Prague — the one assertion a zoned comparison would fail.
        rows[^1].GetProperty("timestamp").GetString().ShouldBe(Stamp(stack.Origin.AddMinutes(2).AddMilliseconds(123)).Replace(".123Z", ".123000000Z", StringComparison.Ordinal));
        rows[^1].GetProperty("attributes").GetProperty("http.method").GetString().ShouldBe("GET");
        rows[^1].GetProperty("resource").GetProperty("deployment.environment").GetString().ShouldBe("prod");

        // Severity is the band of SeverityNumber, whatever the source spelled: "Error" is an error.
        rows.Single(static x => x.GetProperty("body").GetString()!.StartsWith("job failed", StringComparison.Ordinal))
            .GetProperty("severity").GetString().ShouldBe("error");

        root.GetProperty("truncated").GetBoolean().ShouldBeFalse();

        var histogram = root.GetProperty("histogram");
        histogram.GetProperty("bucketSeconds").GetInt32().ShouldBe(600);

        var buckets = histogram.GetProperty("buckets").EnumerateArray().ToList();
        buckets.Sum(static x => x.GetProperty("total").GetInt64()).ShouldBe(8);
        buckets[0].GetProperty("start").GetString().ShouldBe(Stamp(stack.Origin));
        buckets[0].GetProperty("bySeverity").GetProperty("info").GetInt64().ShouldBe(1);
        buckets[0].GetProperty("bySeverity").GetProperty("error").GetInt64().ShouldBe(1);

        root.GetProperty("statistics").GetProperty("rowsRead").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task EveryFilterNarrowsAndTheSearchTextIsAValueNeverSql() {
        var path = MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.SearchLogsAction);
        var from = Stamp(stack.Origin);
        var to = Stamp(stack.Origin.AddMinutes(60));

        (await Search(new { from, to, severities = ErrorsAndWorse }))
            .ShouldBe(["process exiting", "job failed: TIMED OUT waiting", "upstream timed out"]);

        (await Search(new { from, to, text = "timed out" }))
            .ShouldBe(["job failed: TIMED OUT waiting", "upstream timed out"], "the text is compared without regard to case");

        (await Search(new { from, to, service = "worker" })).Count.ShouldBe(2);

        (await Search(new { from, to, attributes = GetRequests })).Count.ShouldBe(3);

        (await Search(new { from, to, attributes = ProdBilling }))
            .ShouldBe(["job failed: TIMED OUT waiting", "queue is deep"], "a key matches the record's attributes or its resource's");

        (await Search(new { from, to, traceId = "0AF7651916CD43DD8448EB211C80319C" })).ShouldBe(["upstream timed out"]);

        // ⚠ The injection is a string the body is searched for: it matches the one row that carries
        // it, and the next search still sees all eight, which a statement that ran it would not.
        (await Search(new { from, to, text = "')) OR 1=1 --" })).ShouldBe(["user said ')) OR 1=1 -- and left"]);
        (await Search(new { from, to, service = "x' OR '1'='1" })).ShouldBeEmpty();
        (await Search(new { from, to })).Count.ShouldBe(8);

        // ⚠ ClickHouse reads a parameter in its escaped format, so each of these went wrong before the
        // store escaped them: "\t" in the path was a tab and found nothing, the lone backslash was a
        // CANNOT_PARSE_ESCAPE_SEQUENCE and the literal tab a BAD_QUERY_PARAMETER, both a 500.
        string[] copied = [MonitorQueryFixture.BackslashBody];
        (await Search(new { from, to, text = @"C:\temp\new" })).ShouldBe(copied);
        (await Search(new { from, to, text = @"\" })).ShouldBe(copied);
        (await Search(new { from, to, text = "to\tshare" })).ShouldBe(copied);
        (await Search(new { from, to, service = @"sync\agent" })).ShouldBe(copied);
        (await Search(new { from, to, attributes = AWindowsPath })).ShouldBe(copied);

        // top is honoured and a longer result says so.
        var (_, topBody) = await stack.PostAsync(MonitorQueryFixture.TenantA, path, new { from, to, top = 2 });
        using (var top = JsonDocument.Parse(topBody)) {
            top.RootElement.GetProperty("rows").GetArrayLength().ShouldBe(2);
            top.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        }

        async Task<List<string>> Search(object body) {
            var (status, text) = await stack.PostAsync(MonitorQueryFixture.TenantA, path, body);
            status.ShouldBe(200, text);
            return Bodies(text);
        }
    }

    [Fact]
    public async Task AnEstimateNamesTheRowsAndAWorkspaceWithNoLogsIsAnEmptyAnswerThatSaysWhy() {
        var (status, body) = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.SearchLogsAction),
            new { from = Stamp(stack.Origin), to = Stamp(stack.Origin.AddMinutes(60)), estimate = true }
        );

        status.ShouldBe(200, body);

        using (var document = JsonDocument.Parse(body)) {
            document.RootElement.TryGetProperty("rows", out _).ShouldBeFalse("an estimate runs nothing");
            document.RootElement.GetProperty("estimate").GetProperty("rows").GetInt64().ShouldBe(8);
        }

        var (quiet, quietBody) = await stack.PostAsync(
            MonitorQueryFixture.TenantA,
            MonitorQueryFixture.ActionPath(MonitorQueryFixture.TenantA, MonitorQueries.SearchLogsAction, MonitorQueryFixture.EmptyWorkspace),
            new { from = Stamp(stack.Origin), to = Stamp(stack.Origin.AddMinutes(60)) }
        );

        quiet.ShouldBe(200, quietBody);
        Bodies(quietBody).ShouldBeEmpty();
        JsonDocument.Parse(quietBody).RootElement.GetProperty("note").GetString().ShouldBe(ClickHouseLogStore.NoTableNote);
    }

    // ── The user a search runs as ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheExplorerUserMayReadTheWorkspaceDatabasesAndNotWriteThem() {
        // Every search above ran as MonitorQueryFixture.ExplorerUser; this is the "nothing else" half.
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, stack.ClickHouseUri);
        request.Headers.Add("X-ClickHouse-User", MonitorQueryFixture.ExplorerUser);
        request.Headers.Add("X-ClickHouse-Key", MonitorQueryFixture.ExplorerPassword);
        request.Content = new StringContent(
            $"INSERT INTO `{stack.DatabaseA}`.`{MonitorLogsTable.Name}` (Body) VALUES ('forged')",
            System.Text.Encoding.UTF8,
            "text/plain"
        );

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse();
        response.Headers.GetValues("X-ClickHouse-Exception-Code").ShouldBe(["497"], "ACCESS_DENIED: the grant is SELECT only");
    }

    [Fact]
    public async Task AReadonlyOneUserCanRunNoSearchBecauseTheStoreSetsItsBudgetPerStatement() {
        // ⚠ The conventional read-only profile is the wrong one. MonitorQueryOptions.LogsUser's remarks.
        var store = new ClickHouseLogStore(
            new HttpClient(),
            new MonitorQueryOptions {
                LogsEndpoint = stack.ClickHouseUri.ToString(),
                LogsUser = MonitorQueryFixture.ReadonlyOneUser,
                LogsPassword = MonitorQueryFixture.ExplorerPassword,
                AllowInsecureTransport = true
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClickHouseLogStore>.Instance
        );

        using var body = JsonDocument.Parse(
            JsonSerializer.Serialize(new { from = Stamp(stack.Origin), to = Stamp(stack.Origin.AddMinutes(60)) })
        );
        var search = MonitorWorkspaceSearchLogsHandler.ReadSearch(body.RootElement).GetValueOrThrow();

        var answered = await store.SearchAsync(stack.DatabaseA, search, TestContext.Current.CancellationToken);

        answered.TryGetError(out var error).ShouldBeTrue("a readonly = 1 user may not set readonly, max_execution_time or max_rows_to_read");
        error.Message.ShouldBe(ClickHouseLogStore.FailedSentence);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static string Stamp(DateTimeOffset instant) => MonitorQueries.Stamp(instant);

    static List<string> Labels(string body, string label) {
        using var document = JsonDocument.Parse(body);

        return [
            .. document.RootElement.GetProperty("series")
                .EnumerateArray()
                .Select(x => x.GetProperty("labels").TryGetProperty(label, out var value) ? value.GetString()! : "")
                .Order(StringComparer.Ordinal)
        ];
    }

    static List<string> Values(string body) {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("values").EnumerateArray().Select(static x => x.GetString()!)];
    }

    static List<string> Bodies(string body) {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(static x => x.GetProperty("body").GetString()!)];
    }
}
