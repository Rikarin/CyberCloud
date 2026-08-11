using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Contracts.Serialization;
using Shouldly;
using System.Reflection;

namespace CyberCloud.ServiceDefaults.Tests;

/// <summary>
///     The assembly-graph rules of docs/plan/03:231-241, as far as they can be checked from here.
/// </summary>
/// <remarks>
///     <para>
///         These belong in <c>build/Build.Architecture.cs</c>, which docs/plan/03:233 says enforces
///         them and which does not implement them yet. Until it does, the three rules that bite this
///         part of the tree are asserted here, where they are at least run on every PR.
///     </para>
/// </remarks>
public sealed class AssemblyGraphTests {
    static readonly Assembly Core = typeof(Result).Assembly;
    static readonly Assembly Contracts = typeof(ResultSurrogate).Assembly;
    static readonly Assembly ServiceDefaults = typeof(OrleansApplication).Assembly;

    [Fact]
    public void CoreReferencesNothingButTheSharedFramework() {
        // Rule 1 (docs/plan/03:235). The package-level version of this is `dotnet list package`
        // reporting "No packages were found for this framework"; this is the assembly-level one,
        // which also catches a reference acquired through a ProjectReference.
        ReferencesOf(Core)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                && !string.Equals(name, "netstandard", StringComparison.Ordinal)
            )
            .ShouldBeEmpty(
                "CyberCloud.Core is contracts, identifiers, error codes and pure "
                + "functions. docs/plan/03:235."
            );
    }

    [Fact]
    public void ContractsReferencesOrleansSerializationAndNoHosting() {
        var references = ReferencesOf(Contracts).ToList();

        references.ShouldContain("Orleans.Serialization.Abstractions");
        references.ShouldNotContain("Orleans.Runtime");
        references.ShouldNotContain("Orleans.TestingHost");
    }

    [Fact]
    public void NothingHereReferencesTheKubernetesClient() {
        // Rule 3 (docs/plan/03:238) — "no assembly above CyberCloud.Kubernetes references
        // k8s.Models".
        //
        // ⚠ READ THE FAILURE CAREFULLY IF THIS EVER FIRES. The rule as written is about *using*
        // Kubernetes object types, and that is what is asserted: no CyberCloud assembly binds to
        // KubernetesClient. It is NOT a claim about the restore graph, and it cannot be: ADR-004
        // requires UseKubeMembership() and UseKubernetesHosting(), both of whose packages take a
        // hard dependency on KubernetesClient, so `dotnet list package --include-transitive` on
        // CyberCloud.ServiceDefaults shows KubernetesClient 19.0.2 and always will. The two
        // statements are compatible and the distinction is the whole content of this test: the
        // library is in the closure because Orleans' Kubernetes integration needs it; no code we
        // write touches it.
        foreach (var assembly in new[] { Core, Contracts, ServiceDefaults }) {
            ReferencesOf(assembly)
                .Where(name => name.StartsWith("k8s", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("KubernetesClient", StringComparison.OrdinalIgnoreCase)
                )
                .ShouldBeEmpty($"{assembly.GetName().Name} binds to the Kubernetes client.");
        }
    }

    [Fact]
    public void ServiceDefaultsIsTheFirstAssemblyWithOrleansHosting() {
        // The positive half: hosting IS allowed here, and the point of the rule is that it is
        // allowed nowhere below.
        ReferencesOf(ServiceDefaults).ShouldContain("Orleans.Runtime");
        ReferencesOf(Contracts).ShouldNotContain("Orleans.Runtime");
        ReferencesOf(Core).ShouldNotContain("Orleans.Core.Abstractions");
    }

    [Fact]
    public void TheStorageTierAndStreamProviderNamesAreWhereBothEndsCanSeeThem() {
        // A grain in a provider assembly and the silo host both name these, and the provider
        // assembly must not reference the host. Asserting the assembly, not just the value, is what
        // stops somebody "tidying" them into ServiceDefaults.
        typeof(StorageTiers).Assembly.ShouldBe(Contracts);
        typeof(StreamProviders).Assembly.ShouldBe(Contracts);

        StorageTiers.Hot.ShouldBe("Hot");
        StorageTiers.Durable.ShouldBe("Durable");
        StreamProviders.Events.ShouldBe("Events");
    }

    static IEnumerable<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(x => x.Name ?? string.Empty);
}
