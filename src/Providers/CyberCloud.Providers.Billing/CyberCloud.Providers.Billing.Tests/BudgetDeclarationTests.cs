using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.Billing.Tests;

/// <summary>
///     <c>CyberCloud.Billing/budgets</c> as the registry reads it, and the body as <see cref="Budgets.ToSpec" />
///     reads it — the refusals the schema cannot make, each by its pointer.
/// </summary>
public sealed class BudgetDeclarationTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    static readonly ResourceId Budget = new(Tenant, Subscription, "prod", Budgets.Type, "monthly", Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001"));

    static readonly string Service =
        $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod/providers/CyberCloud.Communication/services/alerts";

    [Fact]
    public void OneClusterlessTypeIsDeclaredUnderTheBillingNamespace() {
        var registry = ProviderRegistry.Build([new BillingProvider()]);

        registry.Namespaces.ShouldBe(["CyberCloud.Billing"]);
        var type = registry.Types.ShouldHaveSingleItem();

        type.Type.ToString().ShouldBe("CyberCloud.Billing/budgets");
        type.RequiresCluster.ShouldBeFalse("a budget converges a grain and renders nothing");
        type.Chart.ShouldBeEmpty();
        type.ReconcilerType.ShouldBe(typeof(BudgetReconciler));
        type.SupportsTags.ShouldBeTrue();
        type.ApiVersions.Select(static x => x.Version.Value).ShouldBe([Budgets.V2026]);
    }

    [Fact]
    public void TheSchemaAcceptsTheBodyTheTestsWriteAndRefusesAnUnknownPeriod() {
        var registry = ProviderRegistry.Build([new BillingProvider()]);
        registry.TryGetType(Budgets.Type, out var type).ShouldBeTrue();
        var schema = type.ApiVersions.Single().Schema;

        using var good = JsonDocument.Parse(Budgets.Body(Service, ["finance@example.com"]));
        schema.Validate(good.RootElement).IsSuccess.ShouldBeTrue();

        using var bad = JsonDocument.Parse(Budgets.Body(Service, ["finance@example.com"], period: "weekly"));
        schema.Validate(bad.RootElement).IsFailure.ShouldBeTrue("weekly is not a calendar period the evaluator has");
    }

    [Fact]
    public void TheBodyBecomesTheSpecTheGrainHolds() {
        var spec = ToSpec(Budgets.Body(Service, ["finance@example.com"], 500m, [80m, 50m, 100m], [100m], "quarterly", "subscription"))
            .GetValueOrThrow();

        spec.BudgetId.ShouldBe(Budget.Id);
        spec.SubscriptionId.ShouldBe(Subscription);
        spec.ResourceGroup.ShouldBe("prod");
        spec.Scope.ShouldBe(BudgetScope.Subscription);
        spec.Period.ShouldBe(BudgetPeriod.Quarterly);
        spec.Amount.ShouldBe(500m);
        spec.Thresholds.Select(static x => (x.Kind, x.Percent))
            .ShouldBe([(ThresholdKind.Actual, 50m), (ThresholdKind.Actual, 80m), (ThresholdKind.Actual, 100m), (ThresholdKind.Forecast, 100m)]);
        spec.Notification.ServicePath.ShouldBe(Service);
        spec.Notification.Recipients.ShouldBe(["finance@example.com"]);
    }

    [Fact]
    public void AServiceInAnotherTenantIsRefusedByItsPointer() {
        var foreign = Service.Replace(Tenant.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);

        var refused = ToSpec(Budgets.Body(foreign, ["a@example.com"]));

        refused.Error!.Target.ShouldBe("/properties/notification/service");
        refused.Error.Message.ShouldContain("its own tenant's services only");
    }

    /// <summary>
    ///     ⚠ The review's finding: a writer in one group made another group's service send, to recipients
    ///     they chose, on that service's limits. The group is the line — <see cref="Budgets.InSameGroup" />.
    /// </summary>
    [Theory]
    [InlineData("resourceGroups/prod/", "resourceGroups/finance/")]
    [InlineData("subscriptions/33333333-3333-4333-8333-333333333333/", "subscriptions/44444444-4444-4444-8444-444444444444/")]
    public void AServiceInAnotherGroupOfTheSameTenantIsRefusedByItsPointer(string from, string to) {
        var elsewhere = Service.Replace(from, to, StringComparison.Ordinal);

        var refused = ToSpec(Budgets.Body(elsewhere, ["a@example.com"]));

        refused.Error!.Target.ShouldBe("/properties/notification/service");
        refused.Error.Message.ShouldContain("in its own resource group only");
    }

    [Fact]
    public void AServiceThatIsNotASendingServiceIsRefused() {
        var widget = $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod/providers/CyberCloud.Sample/widgets/a";

        ToSpec(Budgets.Body(widget, ["a@example.com"])).Error!.Message.ShouldContain("CyberCloud.Communication/services");
    }

    [Fact]
    public void ABudgetWithNoThresholdIsRefused() =>
        ToSpec(Budgets.Body(Service, ["a@example.com"], actual: [], forecast: [])).Error!.Target.ShouldBe("/properties/thresholds");

    [Fact]
    public void ABudgetWithElevenThresholdsIsRefused() =>
        ToSpec(Budgets.Body(Service, ["a@example.com"], actual: [.. Enumerable.Range(1, 11).Select(static x => (decimal)x * 10)], forecast: []))
            .Error!.Target.ShouldBe("/properties/thresholds");

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void ARecipientCountOutsideOneToTwentyIsRefused(int count) =>
        ToSpec(Budgets.Body(Service, [.. Enumerable.Range(0, count).Select(static x => $"r{x}@example.com")]))
            .Error!.Target.ShouldBe("/properties/notification/recipients");

    static Result<BudgetSpec> ToSpec(string body) {
        using var document = JsonDocument.Parse(body);
        return Budgets.ToSpec(Budget, document.RootElement);
    }
}
