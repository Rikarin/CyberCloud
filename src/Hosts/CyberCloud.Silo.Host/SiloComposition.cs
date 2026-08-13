using CyberCloud.Communication;
using CyberCloud.ResourceManager;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CyberCloud.Silo.Host;

/// <summary>
///     Builds the silo, module graph and all. docs/plan/04 § Silo composition.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists so a test can compose the real host rather than a lookalike, and the reason
///         is the defect it was written to close.</b> Every conformance and resource-manager suite in
///         the repository builds its own <c>TestCluster</c>, registers the resource manager and
///         registers a provider — so all of them stayed green through the whole period in which
///         <c>CyberCloud.Silo.Host</c> referenced no provider module, composed no resource manager, and
///         could not have reconciled anything. A green conformance run said nothing about this process.
///         <c>CyberCloud.Silo.Host.Tests</c> calls <see cref="BuildAsync" /> and asserts against what it
///         produced, so the thing under test is the thing that ships.
///     </para>
///     <para>
///         <c>Program.cs</c> keeps what is not composition: the one-shot <c>--apply-durable-schema</c>
///         mode, ABP's initialization and <c>RunAsync</c>.
///     </para>
/// </remarks>
public static class SiloComposition {
    /// <summary>
    ///     Composes the silo and returns the built host, ready to initialize and run.
    /// </summary>
    /// <param name="args">The process arguments, passed through to configuration.</param>
    /// <returns>The built host. Nothing has started.</returns>
    public static async Task<WebApplication> BuildAsync(string[] args) {
        ArgumentNullException.ThrowIfNull(args);

        var builder = OrleansApplication.CreateSilo(
            args,
            configureCluster: ConfigureCluster,
            configureStorage: (silo, storage) => silo.AddCyberCloudTenancy(storage)
        );

        // ⚠ Required, and not optional. CreateSilo calls builder.Host.UseAutofac(), and ABP's
        // service-provider factory resolves IModuleContainer during Build(). Without a module the host
        // dies with "Could not find singleton service: Volo.Abp.Modularity.IModuleContainer" — a
        // message naming neither UseAutofac nor the missing call.
        await builder.Services.AddApplicationAsync<SiloHostModule>();

        return builder.Build();
    }

    /// <summary>
    ///     The cluster wiring: the sending domain, identity and the resource manager.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>AddCyberCloudCommunication</c> adds seven grain types and no new start-up
    ///         requirement</b> — docs/plan/17. It registers services only: the clock, the five refusing
    ///         channel providers, the provider registry, the client-side sender and the webhook router.
    ///         It configures no reminder service, no stream provider and no storage, because its grains
    ///         bind <c>StorageTiers.Hot</c> and <c>StorageTiers.Durable</c>, which are the two
    ///         <c>AddCyberCloudTenancy</c> already wires. With no carrier configured every send fails
    ///         with a sentence saying so, which is the designed state of a silo with no Twilio client
    ///         rather than a defect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>AddSiloIdentity</c> goes beside it and not instead of it</b> — docs/plan/11. The
    ///         <c>ProjectReference</c> in the .csproj is what puts the identity grains in the silo; this
    ///         call is what registers the services their constructors ask for, including the
    ///         <c>IOtpDeliverySeam</c> that <c>UserGrain.IssueOtpAsync</c> reaches. The seam adapter
    ///         resolves <c>IMessageSender</c>, which the call above is what provides.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>AddCyberCloudResourceManager</c> is the call that had no caller anywhere</b> —
    ///         docs/plan/08, docs/plan/04 § Silo composition. The providers themselves arrive through
    ///         <see cref="SiloHostModule" />'s <c>[DependsOn]</c> list, whose <c>ConfigureServices</c>
    ///         runs later than anything here, and the ordering does not matter:
    ///         <c>IProviderRegistry</c> is a factory registration, built at first resolve from whatever
    ///         providers the container ended up holding. A host that composed the manager and
    ///         registered no provider used to get an empty registry and answer <c>404</c> to every
    ///         resource path; <c>ProviderRegistry.Build</c> now refuses to build one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reminders are not here, and that moved on purpose.</b>
    ///         <c>OperationGrain</c> is <c>IRemindable</c>, so the reconcile loop needs a reminder
    ///         service — and it is configured from the hot tier's connection string, so it belongs
    ///         beside the tiers in <c>OrleansApplication.CreateSilo</c> rather than in a host that would
    ///         have to re-read the same configuration section to find it.
    ///     </para>
    /// </remarks>
    static void ConfigureCluster(ISiloBuilder silo) =>
        silo.AddCyberCloudCommunication()
            .AddSiloIdentity()
            .AddCyberCloudResourceManager();
}
