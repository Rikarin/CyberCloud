using CyberCloud.Kubernetes.Apply;
using Shouldly;
using System.Reflection;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     Rule 3 of docs/plan/03 § Assembly graph rules, from the side that owns it.
/// </summary>
/// <remarks>
///     <para>
///         <i>"No assembly above <c>CyberCloud.Kubernetes</c> references <c>k8s.Models</c>."</i>
///         <c>CyberCloud.ServiceDefaults.Tests.AssemblyGraphTests</c> asserts the rule for the
///         assemblies below; this asserts it for the boundary itself, which is the half that can
///         actually be broken by accident. The tempting mistake is a convenience overload —
///         <c>Object&lt;T&gt;(T obj) where T : IKubernetesObject&lt;V1ObjectMeta&gt;</c>, exactly as
///         docs/plan/09 § The command builder writes it — which would put <c>k8s.Models</c> in the
///         compile-time closure of every provider in the platform at once.
///     </para>
///     <para>
///         ⚠ These belong in <c>build/Build.Architecture.cs</c>, which docs/plan/03 says enforces the
///         six rules and which does not implement them yet. Until it does, the rule that bites this
///         part of the tree is asserted here, where it runs on every PR.
///     </para>
/// </remarks>
public sealed class AssemblyGraphTests {
    static readonly Assembly Contracts = typeof(KubeLabels).Assembly;
    static readonly Assembly Kubernetes = typeof(KubeApiClient).Assembly;

    [Fact]
    public void TheContractsAssemblyDoesNotBindToTheKubernetesClient() {
        // ⚠ THE RULE THAT MATTERS MOST HERE. Every provider reconciler references this assembly to
        // build a KubeCommand. One k8s.Models type in its public surface breaks rule 3 for the whole
        // provider tree, transitively and invisibly.
        ReferencesOf(Contracts)
            .Where(IsKubernetesClient)
            .ShouldBeEmpty(
                "CyberCloud.Kubernetes.Contracts is what providers reference; it must be "
                + "JSON-and-GUIDs. docs/plan/03 § Assembly graph rules, rule 3."
            );
    }

    [Fact]
    public void NoPublicMemberOfTheContractsAssemblyMentionsAKubernetesType() {
        // The reference check above catches a binding; this catches the shape. A signature naming a
        // k8s type would fail to compile without the reference, so in practice these agree — but the
        // assertion states the intent, and it is the one a reviewer reads.
        var offenders = new List<string>();

        foreach (var type in Contracts.GetExportedTypes()) {
            foreach (var member in type.GetMembers(
                         BindingFlags.Public
                         | BindingFlags.Instance
                         | BindingFlags.Static
                         | BindingFlags.DeclaredOnly
                     )) {
                var types = member switch {
                    MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
                    PropertyInfo p => [p.PropertyType],
                    FieldInfo f => new[] { f.FieldType },
                    ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
                    _ => []
                };

                foreach (var referenced in types) {
                    var ns = referenced.Namespace ?? string.Empty;
                    if (ns.StartsWith("k8s", StringComparison.OrdinalIgnoreCase)) {
                        offenders.Add($"{type.Name}.{member.Name} -> {referenced.FullName}");
                    }
                }
            }
        }

        offenders.ShouldBeEmpty(
            "docs/plan/03 § Assembly graph rules, rule 3. ⚠ docs/plan/09 § The command builder's "
            + "`Object<T>(T obj) where T : IKubernetesObject<V1ObjectMeta>` and § Cluster "
            + "connections' `GetAsync<T>(ObjectRef) where T : IKubernetesObject` both violate this "
            + "rule as written; the repairs are documented on IKubeCommandBuilder.Object and "
            + "KubeObject."
        );
    }

    [Fact]
    public void TheImplementationAssemblyIsAllowedToBindToTheKubernetesClient() {
        // The positive half: this is the boundary, and the point of the rule is that nothing above
        // it may do the same. A rule with no assembly on the allowed side is a ban, not a boundary.
        ReferencesOf(Kubernetes).ShouldContain("KubernetesClient");
    }

    [Fact]
    public void OnlyTheApplyLayerNamesKubernetesTypes() {
        // ⚠ Inside CyberCloud.Kubernetes the client is confined to one namespace, behind
        // IKubeApiClient. That is what lets the grain, the informers and the health tracker be
        // tested without an API server — and what would make swapping the client library a change
        // to one file rather than to the assembly.
        var offenders = Kubernetes.GetTypes()
            .Where(x => x.Namespace is not null
                && !x.Namespace.StartsWith("CyberCloud.Kubernetes.Apply", StringComparison.Ordinal)
            )
            .SelectMany(type => type
                .GetMembers(
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
                )
                .OfType<MethodInfo>()
                .Where(m => (m.ReturnType.Namespace ?? string.Empty).StartsWith(
                        "k8s",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || m.GetParameters()
                        .Any(p =>
                            (p.ParameterType.Namespace ?? string.Empty).StartsWith(
                                "k8s",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                )
                .Select(m => $"{type.FullName}.{m.Name}")
            )
            .Where(x => !x.Contains('<'))
            .ToList();

        offenders.ShouldBeEmpty("k8s types outside CyberCloud.Kubernetes.Apply: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheGrainSurfaceIsExpressedInJsonAndGuidsOnly() {
        // The contract docs/plan/09 § Cluster connections sketches with IKubernetesObject, restated
        // in the vocabulary rule 3 permits.
        foreach (var method in typeof(IClusterConnectionGrain).GetMethods()) {
            method.IsGenericMethod.ShouldBeFalse(
                $"{method.Name} is generic; a generic grain method here would be the route by which "
                + "a k8s constraint reappeared."
            );

            foreach (var parameter in method.GetParameters()) {
                (parameter.ParameterType.Namespace ?? string.Empty)
                    .ShouldNotStartWith("k8s");
            }
        }
    }

    static IEnumerable<string> ReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(x => x.Name ?? string.Empty);

    static bool IsKubernetesClient(string name) =>
        name.StartsWith("k8s", StringComparison.OrdinalIgnoreCase)
        || name.Contains("KubernetesClient", StringComparison.OrdinalIgnoreCase);
}
