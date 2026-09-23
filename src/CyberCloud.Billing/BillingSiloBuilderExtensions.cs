using CyberCloud.Billing.Pricing;
using CyberCloud.Billing.Tax;
using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Billing;

/// <summary>Wires the billing module into a silo, and its client half into a host that is not one.</summary>
public static class BillingSiloBuilderExtensions {
    /// <summary>
    ///     Registers what the billing grains take: the committed price sheet, the EU VAT service, the
    ///     shared rating path, the budget control plane and the issuer.
    /// </summary>
    /// <param name="silo">The silo being built.</param>
    /// <param name="options">
    ///     The issuer, from <see cref="BillingOptions.Bind" />. <see langword="null" /> registers an
    ///     empty one, which serves cost queries and budgets and refuses to compute an invoice.
    /// </param>
    /// <remarks>
    ///     ⚠ <b><c>TryAdd</c> throughout</b>, so a test or a host that registers a price sheet, a tax
    ///     service or a clock first keeps its own — the contract every module's registration here
    ///     follows. What is <i>not</i> registered is <c>IMessageSender</c>, which the budget grain
    ///     holds: that is the sending module's (<c>AddCyberCloudCommunication</c>), and choosing it here
    ///     would be this module deciding how the platform sends.
    /// </remarks>
    public static ISiloBuilder AddCyberCloudBilling(this ISiloBuilder silo, BillingOptions? options = null) {
        ArgumentNullException.ThrowIfNull(silo);

        return silo.ConfigureServices(services => {
                services.TryAddSingleton<IClock, SystemClock>();
                services.TryAddSingleton(options ?? new BillingOptions());
                services.TryAddSingleton(static _ => PriceSheet.Committed);
                services.TryAddSingleton<ITaxService>(static _ => EuVatTaxService.Committed);
                services.TryAddSingleton<UsagePricing>();
                services.AddCyberCloudBillingClient();
            }
        );
    }

    /// <summary>
    ///     Registers the two client-side seams over a host's grain factory — <see cref="ICostQuery" />
    ///     for the gateway's dispatch stage, <see cref="IBudgetControlPlane" /> for the budget
    ///     reconciler, which a synchronous path in the gateway builds as well as the silo.
    /// </summary>
    /// <param name="services">The container. Must already hold an <c>IGrainFactory</c>.</param>
    public static IServiceCollection AddCyberCloudBillingClient(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICostQuery, GrainCostQuery>();
        services.TryAddSingleton<IBudgetControlPlane, GrainBudgetControlPlane>();

        return services;
    }
}
