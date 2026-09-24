using CyberCloud.Authorization;
using CyberCloud.Billing;
using CyberCloud.Communication;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Metering;
using CyberCloud.ResourceManager.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Text.Json;

namespace CyberCloud.Providers.Billing.Tests;

/// <summary>
///     <see cref="BudgetReconciler" /> against the real budget grain — the four clauses of docs/plan/08
///     § The reconcile loop, over grain state.
/// </summary>
[Collection(BudgetSilo.Name)]
public sealed class BudgetReconcilerTests(BudgetSilo silo) {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    static readonly string Service =
        $"/tenants/{Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/prod/providers/CyberCloud.Communication/services/alerts";

    [Fact]
    public async Task ABudgetConvergesArmedAndASecondPassWritesNothing() {
        var id = Budget();
        var body = Budgets.Body(Service, ["finance@example.com"], 250m);

        var first = await ReconcileAsync(id, body);
        var log = new Log();
        var second = await ReconcileAsync(id, body, log);

        first.IsConverged.ShouldBeTrue(first.ToString());
        second.IsConverged.ShouldBeTrue(second.ToString());
        log.Entries.ShouldContain(x => x.Contains("already carries the desired spec", StringComparison.Ordinal));

        var held = (await silo.Plane.GetAsync(Tenant, id.Id, TestContext.Current.CancellationToken)).GetValueOrThrow();
        held.Spec.Amount.ShouldBe(250m);
        (await silo.Plane.IsArmedAsync(Tenant, id.Id, TestContext.Current.CancellationToken)).GetValueOrThrow().ShouldBeTrue();
    }

    [Fact]
    public async Task AChangedBodyIsWrittenAndObservationSaysWhenTheGrainHasDrifted() {
        var id = Budget();
        (await ReconcileAsync(id, Budgets.Body(Service, ["finance@example.com"], 250m))).IsConverged.ShouldBeTrue();

        var changed = Budgets.Body(Service, ["finance@example.com"], 900m, period: "annually");

        (await ObserveAsync(id, changed)).Summary.ShouldBe("the budget has drifted from its body");
        (await ReconcileAsync(id, changed)).IsConverged.ShouldBeTrue();

        var observed = await ObserveAsync(id, changed);
        observed.Exists.ShouldBeTrue();
        observed.Summary.ShouldBe("the budget is held");
        observed.Json.ShouldContain("\"period\":\"annually\"");
    }

    [Fact]
    public async Task ABodyTheSchemaCannotRefuseFailsTheOperationByItsPointer() {
        var outcome = await ReconcileAsync(Budget(), Budgets.Body(Service, ["a@example.com"], actual: [], forecast: []));

        outcome.IsConverged.ShouldBeFalse();
        outcome.Error!.Target.ShouldBe("/properties/thresholds");
    }

    [Fact]
    public async Task DeleteRemovesTheBudgetAndItsReminder() {
        var id = Budget();
        var body = Budgets.Body(Service, ["finance@example.com"]);
        (await ReconcileAsync(id, body)).IsConverged.ShouldBeTrue();

        using var desired = JsonDocument.Parse(body);
        var deleted = await silo.Reconciler.DeleteAsync(Context(id, desired.RootElement), TestContext.Current.CancellationToken);

        deleted.IsConverged.ShouldBeTrue();
        (await silo.Plane.GetAsync(Tenant, id.Id, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        (await silo.Plane.IsArmedAsync(Tenant, id.Id, TestContext.Current.CancellationToken)).GetValueOrThrow().ShouldBeFalse();
        (await ObserveAsync(id, body)).Exists.ShouldBeFalse();
    }

    static ResourceId Budget() => new(Tenant, Subscription, "prod", Budgets.Type, "b-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid());

    async Task<ReconcileOutcome> ReconcileAsync(ResourceId id, string body, Log? log = null) {
        using var desired = JsonDocument.Parse(body);
        return await silo.Reconciler.ReconcileAsync(Context(id, desired.RootElement, log), TestContext.Current.CancellationToken);
    }

    async Task<ObservedState> ObserveAsync(ResourceId id, string body) {
        using var desired = JsonDocument.Parse(body);
        return await silo.Reconciler.ObserveAsync(new(id, Budgets.V2026, desired.RootElement, string.Empty, null), TestContext.Current.CancellationToken);
    }

    static ReconcileContext Context(ResourceId id, JsonElement desired, Log? log = null) =>
        new(id, Budgets.V2026, desired, null, string.Empty, null, new InMemorySecretVault(), log ?? new Log());

    sealed class Log : IReconcileLog {
        public List<string> Entries { get; } = [];

        public void Report(string phase, string detail) => Entries.Add(detail);

        public void Report(string phase, string detail, int percentComplete) => Entries.Add(detail);
    }
}

/// <summary>An in-process silo hosting the billing module as the silo host does.</summary>
public sealed class BudgetSilo : IAsyncLifetime {
    /// <summary>The collection's name.</summary>
    public const string Name = "budget silo";

    TestCluster cluster = null!;

    /// <summary>The control plane over the cluster client, as a reconciler's host holds it.</summary>
    public IBudgetControlPlane Plane { get; private set; } = null!;

    /// <summary>The reconciler under test, built over <see cref="Plane" />.</summary>
    public BudgetReconciler Reconciler { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        var services = new ServiceCollection().AddSingleton(cluster.GrainFactory).AddCyberCloudBillingClient().BuildServiceProvider();
        Plane = services.GetRequiredService<IBudgetControlPlane>();
        Reconciler = new(new SystemClock(), Plane);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();
            silo.AddCyberCloudMetering();
            silo.AddCyberCloudAuthorization();
            silo.AddCyberCloudCommunication();
            silo.AddCyberCloudBilling();
        }
    }
}

/// <summary>One silo for the collection.</summary>
[CollectionDefinition(BudgetSilo.Name)]
public sealed class BudgetSiloFixture : ICollectionFixture<BudgetSilo>;
