using CyberCloud.Authorization.Contracts;
using CyberCloud.Metering.Contracts;
using CyberCloud.ResourceManager.Conformance;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Providers.Billing.Tests;

/// <summary>
///     <see cref="BudgetStatusHandler" /> against the real budget grain and the real usage ledger —
///     the figures and fired thresholds the portal's budget list shows, issue #41.
/// </summary>
/// <remarks>
///     ⚠ Every answer is also checked against <see cref="Budgets.StatusResponse" />, the shape the
///     dispatcher checks it against and the generated client types it by: a handler that drifted from it
///     would fail every call in production with a message about the shape.
/// </remarks>
[Collection(BudgetSilo.Name)]
public sealed class BudgetStatusHandlerTests(BudgetSilo silo) {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");

    static string Service(Guid subscription) =>
        $"/tenants/{Tenant:D}/subscriptions/{subscription:D}/resourceGroups/prod/providers/CyberCloud.Communication/services/alerts";

    [Fact]
    public async Task ABudgetNoReconcileHasWrittenIsAConflictThatSaysSo() {
        var id = Budget(Guid.NewGuid());

        var answer = await StatusAsync(id);

        answer.Error!.Code.ShouldBe(ErrorCode.Conflict);
        answer.Error.Message.ShouldContain("not held yet");
    }

    [Fact]
    public async Task BeforeItsFirstEvaluationABudgetSaysItHasNoFigures() {
        var subscription = Guid.NewGuid();
        var id = Budget(subscription);
        await ReconcileAsync(id, Budgets.Body(Service(subscription), ["finance@example.com"], 1.50m, actual: [50m, 100m]));

        using var status = Parsed(await StatusAsync(id));
        var root = status.RootElement;

        root.GetProperty("evaluated").GetBoolean().ShouldBeFalse();
        root.GetProperty("amount").GetDecimal().ShouldBe(1.50m);
        root.TryGetProperty("lastEvaluatedAt", out _).ShouldBeFalse("a figure with no evaluation behind it would be a zero that looks real");
        root.GetProperty("firedActual").GetArrayLength().ShouldBe(0);
        root.GetProperty("alerts").GetArrayLength().ShouldBe(0);
    }

    /// <summary>⚠ The threshold state the portal shows: what has fired this period, and the history.</summary>
    [Fact]
    public async Task AfterAnEvaluationTheFiguresAndTheFiredThresholdsAreTheGrains() {
        var subscription = Guid.NewGuid();
        var id = Budget(subscription);
        await ReconcileAsync(id, Budgets.Body(Service(subscription), ["finance@example.com"], 1.50m, actual: [50m, 100m], forecast: [1000m]));

        // 40 vCPU-hours at 0.025 is 1.00 — past 50 % of 1.50 and short of 100 %.
        await UseAsync(subscription, 40m);
        var evaluated = (await silo.Plane.EvaluateAsync(Tenant, id.Id, TestContext.Current.CancellationToken)).GetValueOrThrow();
        evaluated.Fired.ShouldBe(1);

        using var status = Parsed(await StatusAsync(id));
        var root = status.RootElement;

        root.GetProperty("evaluated").GetBoolean().ShouldBeTrue();
        root.GetProperty("currency").GetString().ShouldBe("EUR");
        root.GetProperty("actual").GetDecimal().ShouldBe(1.00m);
        root.GetProperty("firedActual").EnumerateArray().Select(static x => x.GetDecimal()).ShouldBe([50m]);
        root.GetProperty("firedForecast").GetArrayLength().ShouldBe(0);
        root.GetProperty("periodStart").GetString().ShouldBe(Month().ToString("O", CultureInfo.InvariantCulture));

        var alert = root.GetProperty("alerts").EnumerateArray().ShouldHaveSingleItem().GetString() ?? string.Empty;
        alert.ShouldContain(" actual 50% at 1.00: ");
    }

    /// <summary>
    ///     ⚠ A subscription budget's figures are the subscription's spend. A reader of the budget's group,
    ///     whom the cost query answers with <c>filtered</c>, doesn't get them from the budget either; a
    ///     reader of the subscription does, and loses them at the revoke, not at the next evaluation.
    /// </summary>
    [Fact]
    public async Task ASubscriptionBudgetsFiguresAreShownOnlyToAReaderOfTheSubscription() {
        var subscription = Guid.NewGuid();
        var id = Budget(subscription);
        await ReconcileAsync(id, Budgets.Body(Service(subscription), ["finance@example.com"], 1.50m, actual: [50m], scope: "subscription"));

        var onTheSubscription = ObjectRef.Of(ObjectTypes.Subscription, subscription);
        await GrantAsync(onTheSubscription, SubjectRef.Of(ObjectTypes.Resource, id.Id));
        await GrantAsync(ObjectRef.Of(ObjectTypes.ResourceGroup, subscription.ToString("N", CultureInfo.InvariantCulture) + "-prod"), User("bob"));
        await GrantAsync(onTheSubscription, User("alice"));

        await UseAsync(subscription, 40m);
        (await silo.Plane.EvaluateAsync(Tenant, id.Id, TestContext.Current.CancellationToken)).GetValueOrThrow().Fired.ShouldBe(1);

        var groupReader = await StatusAsync(id, "bob");
        groupReader.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        groupReader.Error.Message.ShouldNotContain("1.00", Case.Sensitive, "the refusal carries no figure");

        (await StatusAsync(id)).Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed, "a call with no caller is nobody's, and nobody reads the subscription");

        using (var status = Parsed(await StatusAsync(id, "alice"))) {
            status.RootElement.GetProperty("actual").GetDecimal().ShouldBe(1.00m);
        }

        await RevokeAsync(onTheSubscription, User("alice"));
        (await StatusAsync(id, "alice")).Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed, "checked fully consistent, at the revoke");
    }

    /// <summary>A group budget's figures are the group's, and <c>read</c> on the budget is enough.</summary>
    [Fact]
    public async Task AGroupBudgetAsksNothingMoreThanTheManagerAsked() {
        var subscription = Guid.NewGuid();
        var id = Budget(subscription);
        await ReconcileAsync(id, Budgets.Body(Service(subscription), ["finance@example.com"], 1.50m, actual: [50m]));

        using var status = Parsed(await StatusAsync(id, "bob"));
        status.RootElement.GetProperty("amount").GetDecimal().ShouldBe(1.50m);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────────────────────────

    static ResourceId Budget(Guid subscription) =>
        new(Tenant, subscription, "prod", Budgets.Type, "b-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid());

    static DateTimeOffset Month() {
        var now = DateTimeOffset.UtcNow;
        return new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    async Task ReconcileAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        var outcome = await silo.Reconciler.ReconcileAsync(
            new(id, Budgets.V2026, desired.RootElement, null, string.Empty, null, new InMemorySecretVault(), new NullLog()),
            TestContext.Current.CancellationToken
        );
        outcome.IsConverged.ShouldBeTrue(outcome.ToString());
    }

    /// <summary>
    ///     One hour of usage in the group, in the month's first hour — this month's whenever the test
    ///     runs, the reason <c>BillingAcrossTheHostsTests</c> gives.
    /// </summary>
    async Task UseAsync(Guid subscription, decimal vcpuHours) {
        var month = Month();
        var appended = await silo.Grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IUsageLedgerGrain>(GrainKeys.Subscription(subscription))
            .AppendAsync(
                new() {
                    TenantId = Tenant,
                    ResourceId = Guid.NewGuid(),
                    ResourcePath = $"/tenants/{Tenant:D}/subscriptions/{subscription:D}/resourceGroups/prod/providers/CyberCloud.Compute/virtualMachines/web",
                    Meter = BillingMeter.VCpuHours,
                    Region = "eu-central",
                    WindowStart = month,
                    WindowEnd = month.AddHours(1),
                    Quantity = vcpuHours,
                    SampleCount = 12
                }
            );
        appended.IsSuccess.ShouldBeTrue(appended.Error?.Message);
    }

    Task<Result<string>> StatusAsync(ResourceId id, string user = "") {
        using var empty = JsonDocument.Parse("{}");
        return new BudgetStatusHandler(silo.Plane).InvokeAsync(
            new(id, Budgets.V2026, Budgets.StatusAction, empty.RootElement.Clone(), empty.RootElement.Clone(), string.Empty, null, new InMemorySecretVault()) {
                Caller = new() { TenantId = Tenant, SubjectType = "user", SubjectId = user }
            },
            TestContext.Current.CancellationToken
        );
    }

    static SubjectRef User(string id) => SubjectRef.Of(ObjectTypes.User, id);

    Task GrantAsync(ObjectRef on, SubjectRef subject) => WriteAsync(on, subject, grant: true);

    Task RevokeAsync(ObjectRef on, SubjectRef subject) => WriteAsync(on, subject, grant: false);

    async Task WriteAsync(ObjectRef on, SubjectRef subject, bool grant) {
        var tuple = RelationTuple.Create(on, Relations.Reader, subject).GetValueOrThrow();
        var store = silo.Grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture)).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant));

        var written = grant ? await store.WriteAsync(tuple) : await store.DeleteAsync(tuple);
        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
    }

    /// <summary>The answer, parsed, after the dispatcher's own check against the published shape.</summary>
    static JsonDocument Parsed(Result<string> answer) {
        answer.IsSuccess.ShouldBeTrue(answer.Error?.Message);

        var document = JsonDocument.Parse(answer.GetValueOrThrow());
        var validated = Budgets.StatusResponse.Validate(document.RootElement);
        validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);

        return document;
    }

    sealed class NullLog : IReconcileLog {
        public void Report(string phase, string detail) {
        }

        public void Report(string phase, string detail, int percentComplete) {
        }
    }
}
