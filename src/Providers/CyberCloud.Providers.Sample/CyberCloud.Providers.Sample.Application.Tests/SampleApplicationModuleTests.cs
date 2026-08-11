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
    public void TheApplicationAssemblyBindsNothingFromTheReconcilersHalfOfTheProvider() {
        // ⚠ AN EDGE NO ASSEMBLY-GRAPH RULE COVERS, and this is the finding rather than the test.
        // Rule 2 reads as cross-provider, so Sample.Application → Sample is same-provider and passes.
        // Rule 5 only inspects assemblies whose name starts with CyberCloud.Gateway. Rule 4
        // constrains who may reference THIS assembly, not what it may reference.
        //
        // A host loads this module for the application layer — rule 5 explicitly permits the gateway
        // to do so — and if this assembly bound the implementation it would pull the reconciler,
        // Orleans and KubernetesClient into that host's process. That is the coupling rules 3 and 5
        // exist to prevent, arriving through the one door neither watches.
        //
        // ⚠ Same semantics as the gates: an assembly reference exists only where a type was bound,
        // which is what build/ArchitectureFacts.cs reads off the AssemblyRef table.
        var bound = typeof(SampleApplicationModule).Assembly
            .GetReferencedAssemblies()
            .Select(x => x.Name ?? string.Empty)
            .ToList();

        // Exact: CyberCloud.Providers.Sample.Contracts is the reference this project is meant to
        // have, and a StartsWith check would call it a violation.
        bound.ShouldNotContain("CyberCloud.Providers.Sample");
        bound.ShouldNotContain("CyberCloud.ResourceManager");
        bound.ShouldNotContain("CyberCloud.Kubernetes");
        bound.ShouldNotContain("KubernetesClient");

        // ⚠ WHAT THIS ASSEMBLY ACTUALLY BINDS, measured rather than assumed, because two guesses
        // about it were wrong before this test settled:
        //
        //   System.Runtime, Orleans.Serialization.Abstractions, Orleans.Serialization,
        //   Volo.Abp.Ddd.Application, Volo.Abp.Core
        //
        // ⚠ Orleans is NOT prohibited above, and an earlier draft of this test was wrong to prohibit
        // it. Nothing anyone wrote here mentions Orleans: the source generator arrives transitively
        // with CyberCloud.Providers.Sample.Contracts and emits a type-manifest provider into every
        // assembly it runs in, including one whose only type is an empty module. Asserting its
        // absence would be asserting that a generator this repository depends on stops working.
        //
        // ⚠ CyberCloud.Providers.Sample.Contracts is NOT in the set either, despite the
        // ProjectReference, because SampleApplicationModule binds no type from it. That is the same
        // semantics build/ArchitectureFacts.cs reads, and it is why the six assembly-graph rules are
        // about bound types rather than about .csproj lines.
        //
        // The prohibitions above are asserted and the set is not: a package upgrade legitimately
        // changes the second and never the first.
    }
}
