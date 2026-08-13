using Shouldly;
using Volo.Abp;
using Volo.Abp.Application;
using Volo.Abp.Application.Services;

namespace CyberCloud.Providers.Sample.Application.Tests;

/// <summary>
///     The sample provider's application layer: one ABP module, no application service, and a
///     reference set that has to stay that way.
/// </summary>
/// <remarks>
///     ⚠ <b>Three tests, on purpose.</b> docs/plan/25 § R1 makes this provider the instrument that
///     measures the platform, and docs/plan/24 § Phase 1 requires it to stay trivial. A suite that
///     reached a coverage number by calling things would be measuring itself; each test below names
///     the defect it exists for.
/// </remarks>
public sealed class SampleApplicationModuleTests {
    [Fact]
    public async Task TheModuleLoadsInAnAbpApplicationTheWayAHostLoadsIt() {
        // ⚠ THE ONLY TEST THAT CAN FAIL ON A BROKEN [DependsOn] GRAPH. A host writes
        // [DependsOn(typeof(SampleApplicationModule))] and finds out at startup whether the graph
        // resolves, whether every module in it is constructible, and whether the packages those
        // modules need are actually in the closure. None of that is a compile error, and the symptom
        // in a host is an AbpInitializationException out of Program.Main naming a module the
        // provider's author never wrote.
        //
        // Building the application is also what runs SampleApplicationModule's own constructor,
        // which is the whole of this assembly's executable code.
        using var application = await AbpApplicationFactory.CreateAsync<SampleApplicationModule>();

        var loaded = application.Modules.Select(x => x.Type).ToList();

        loaded.ShouldContain(typeof(SampleApplicationModule));

        // ⚠ docs/plan/03 § Providers puts ABP application services in this project. That is only true
        // if the application layer's own module is in the graph — dropping the [DependsOn] is a
        // one-line change that compiles, loads, and leaves the first application service anyone adds
        // with none of ABP's IApplicationService conventions registered under it.
        loaded.ShouldContain(typeof(AbpDddApplicationModule));

        // Initialising is the half that resolves services rather than only reading metadata: a
        // module whose ConfigureServices throws gets past the line above and dies here, which is
        // exactly where a host would die.
        await application.InitializeAsync();
        await application.ShutdownAsync();
    }

    [Fact]
    public void TheApplicationLayerIsStillEmptyAndThatIsTheMeasurementRatherThanAGap() {
        // ⚠ THE TRIPWIRE FOR docs/plan/25 § R1. The .csproj's own words: "A provider whose surface is
        // entirely generic legitimately has an empty application layer, and writing a service here to
        // fill the folder would be a service nothing calls." R1's leading indicator is how little a
        // provider has to write, so the day this assembly gains an application service is the day
        // either the platform stopped routing something generically or somebody filled a folder —
        // both decisions that deserve to be made rather than noticed later.
        //
        // It is trivially satisfiable: delete the service, or change this test and say in the diff
        // which of the two happened.
        var declared = typeof(SampleApplicationModule).Assembly.GetTypes();

        declared
            .Where(x => typeof(IApplicationService).IsAssignableFrom(x))
            .ShouldBeEmpty(
                "docs/plan/03 § Providers routes every widget operation as a generic resource-manager "
                + "verb from the provider registry (docs/plan/02 § ADR-012), so an application service "
                + "here is one nothing calls"
            );

        // The module and nothing else. Compiler-generated types are not public.
        declared.Where(x => x.IsPublic).ShouldBe([typeof(SampleApplicationModule)]);
    }

    [Fact]
    public void TheApplicationAssemblyBindsItsProviderAndStillNoKubernetesClient() {
        // ⚠ THIS TEST USED TO ASSERT THE OPPOSITE, AND THE CHANGE IS THE POINT RATHER THAN A
        // RELAXATION. It read TheApplicationAssemblyBindsNothingFromTheReconcilersHalfOfTheProvider,
        // and its argument was sound as far as it went: rule 2 reads as cross-provider, rule 5 only
        // inspects CyberCloud.Gateway.*, rule 4 constrains who may reference this assembly rather
        // than what it may reference — so Sample.Application → Sample was an edge no rule watched,
        // and binding the implementation would pull it into any host that loads this module.
        //
        // ⚠ WHAT IT MISSED IS THAT THE EDGE IS LOAD-BEARING AND ITS ABSENCE COST THE PLATFORM ITS
        // WHOLE API SURFACE. IProviderRegistry — the one description of what this platform serves,
        // and what stage 6 resolves every request path against — is built from IResourceProvider
        // INSTANCES in the process. The provider class lives in the implementation assembly, because
        // its Describe names the reconciler type. So a module that binds nothing from the
        // implementation is a module that cannot register a provider, and for as long as none of
        // them could, `AddCyberCloudProvider` had zero callers in the repository: no silo reconciled
        // anything and the gateway answered 404 to every resource and action path.
        //
        // ⚠ THE COUPLING THE OLD TEST FEARED IS REAL AND IS PAID DELIBERATELY. Both hosts load
        // provider implementations into their process — the gateway included, because ActionDispatcher
        // resolves a synchronous action's handler type there. What rule 5 forbids is the GATEWAY
        // naming an implementation type, which it still does not: it binds this module and nothing
        // else, and GatewayIsolationTests reads its AssemblyRef table for exactly that.
        //
        // ⚠ WHAT DOES NOT ARRIVE IS THE HALF THAT WOULD HAVE MATTERED. KubernetesClient is asserted
        // absent below and stays absent: rule 3 keeps k8s.Models inside CyberCloud.Kubernetes, so a
        // provider implementation binds the .Contracts seam and never the client. The old test's
        // three prohibitions were one real one and two that were carrying it.
        var bound = typeof(SampleApplicationModule).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name ?? string.Empty)
            .ToList();

        // Exact rather than StartsWith: CyberCloud.Providers.Sample.Contracts is a different
        // assembly and a prefix check could not tell the two apart.
        bound.ShouldContain(
            "CyberCloud.Providers.Sample",
            "the module registers `new SampleProvider()`, which is what puts this provider in the "
            + "registry both hosts route from"
        );

        bound.ShouldContain(
            "CyberCloud.ResourceManager",
            "AddCyberCloudProvider lives in the implementation assembly because what it registers is "
            + "what Describe declared — the reconciler and handler types"
        );

        // ⚠ THE ONE THAT STILL HAS TO HOLD, and rule 3 is what makes it hold rather than this line.
        bound.ShouldNotContain("KubernetesClient");
        bound.ShouldNotContain("CyberCloud.Kubernetes");
    }
}
