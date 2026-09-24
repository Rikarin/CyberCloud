using CyberCloud.Authorization;
using CyberCloud.Billing;
using CyberCloud.Billing.Contracts;
using CyberCloud.Communication;
using CyberCloud.Conformance;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Metering;
using CyberCloud.Providers.Billing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Billing.Conformance;

/// <summary>
///     <c>CyberCloud.Billing/budgets</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     ⚠ <b>What a green run proves, and what it does not.</b> It proves the write path, the verb
///     grammar, the four reconciler clauses, the cross-tenant 404 and the delete-read-back over the
///     budget grain's state, read around the reconciler by <see cref="BudgetModule" />. It proves
///     nothing about an alert — <c>BudgetTests</c> in <c>CyberCloud.Billing.Tests</c> drives the
///     evaluation against the real ledger and the real carrier — and nothing about the silo-kill
///     criterion, which has no clusterless harness (<c>charts/bundle/bundle.yaml § owed</c>).
/// </remarks>
public sealed class BudgetCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Billing/budgets",
            CreateProvider = static () => new BillingProvider(),
            ReconcilerType = typeof(BudgetReconciler),
            // The module's seam, read when the factory runs — after the harness attached a cluster.
            CreateReconciler = static clock => new BudgetReconciler(clock, BudgetModule.Instance.Plane),
            Type = Budgets.Type,
            ApiVersion = Budgets.V2026,
            Body = static _ => Budgets.Body(HarnessService, ["finance@example.com"], 500m),
            // Changes the amount, which the grain holds and every evaluation compares against.
            ChangedBody = static _ => Budgets.Body(HarnessService, ["finance@example.com"], 750m),
            // A service that is not a resource id path — refused by the schema's format, at the pointer.
            InvalidBody = static _ => WithService(Budgets.Body(HarnessService, ["finance@example.com"]), "not-a-path"),
            InvalidBodyTarget = "/properties/notification/service",
            // No action: a budget's figures are its status, read on GET — stated rather than defaulted,
            // for the reason ProviderConformanceCase.ActionName gives.
            ActionName = string.Empty,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            DataPlane = null,
            StoragePrefix = null,
            // Never asked of a clusterless case, and false so a harness bug that did ask reads as one.
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => BudgetModule.Instance;

    static string HarnessService =>
        new ResourceId(
            ConformanceIds.Tenant,
            ConformanceIds.Subscription,
            ConformanceIds.ResourceGroup,
            new(Budgets.ServiceNamespace, Budgets.ServiceType),
            "alerts",
            Guid.Empty
        ).Path;

    static string WithService(string body, string service) {
        var node = JsonNode.Parse(body)!.AsObject();
        node["properties"]!["notification"]!["service"] = service;
        return node.ToJsonString();
    }
}

/// <summary>
///     The budget grain as the suite's world: hosted the way the silo host hosts it, read and
///     corrupted around the reconciler through <see cref="IBudgetControlPlane" />.
/// </summary>
public sealed class BudgetModule : IConvergedModule {
    IGrainFactory? grains;

    /// <summary>The one instance the case and the harness share.</summary>
    public static BudgetModule Instance { get; } = new();

    /// <summary>The control plane over the harness's client.</summary>
    /// <exception cref="InvalidOperationException">Read before the harness attached a cluster — a harness bug.</exception>
    public IBudgetControlPlane Plane =>
        new GrainBudgetControlPlane(
            grains
            ?? throw new InvalidOperationException(
                "The harness has not attached a cluster yet. BudgetModule.Plane is read by the case's "
                + "CreateReconciler, which the suite calls only after ProviderTestCluster.InitializeAsync."
            )
        );

    /// <inheritdoc />
    public void ConfigureSilo(ISiloBuilder silo) {
        // What CyberCloud.Silo.Host composes for a budget grain: the ledger it rates, the ReBAC engine a
        // subscription budget asks, the sending module it alerts through, and billing itself.
        silo.AddCyberCloudMetering();
        silo.AddCyberCloudAuthorization();
        silo.AddCyberCloudCommunication();
        silo.AddCyberCloudBilling();
    }

    /// <inheritdoc />
    public void ConfigureHandlers(IServiceCollection services, IGrainFactory grains) {
        services.AddSingleton(grains);
        services.AddCyberCloudBillingClient();
    }

    /// <inheritdoc />
    public void Attach(IGrainFactory grains) => this.grains = grains;

    /// <inheritdoc />
    /// <remarks>Nothing to reset: every budget is its own grain, and no two tests share one.</remarks>
    public void Reset() { }

    /// <inheritdoc />
    public async Task<bool> HoldsAsync(ResourceId id, CancellationToken cancellationToken) =>
        (await Plane.GetAsync(id.TenantId, id.Id, cancellationToken)).IsSuccess;

    /// <inheritdoc />
    public async Task<bool> MatchesAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        var held = await Plane.GetAsync(id.TenantId, id.Id, cancellationToken);
        if (!held.TryGetValue(out var snapshot)) {
            return false;
        }

        using var desired = JsonDocument.Parse(desiredJson);
        return Budgets.Matches(snapshot, id, desired.RootElement);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) =>
        Throwing(await Plane.RemoveAsync(id.TenantId, id.Id, cancellationToken));

    /// <inheritdoc />
    public async Task CorruptAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        // An amount no body in the suite asks for, on the spec as held — the hand edit.
        var held = await Plane.GetAsync(id.TenantId, id.Id, cancellationToken);
        if (!held.TryGetValue(out var snapshot)) {
            throw new InvalidOperationException($"There is no budget {id.Id:D} to corrupt: {held.Error!.Message}");
        }

        var written = await Plane.UpsertAsync(id.TenantId, snapshot.Spec with { Amount = snapshot.Spec.Amount + 7919m }, cancellationToken);
        if (written.TryGetError(out var error)) {
            throw new InvalidOperationException($"The budget grain refused the suite's hand edit: {error.Message}");
        }
    }

    static void Throwing(Result result) {
        if (result.TryGetError(out var error)) {
            throw new InvalidOperationException($"The budget grain refused a write the suite made behind the reconciler's back: {error.Message}");
        }
    }
}

/// <summary>The suite, over <see cref="BudgetCase" />.</summary>
public sealed class BudgetConformance(ProviderTestCluster<BudgetCase> cluster)
    : ProviderConformanceTests<BudgetCase>(cluster), IClassFixture<ProviderTestCluster<BudgetCase>>;
