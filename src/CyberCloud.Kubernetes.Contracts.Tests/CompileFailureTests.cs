using CyberCloud.Core.Resources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using System.Collections.Immutable;
using System.Reflection;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     ADR-013's central claim, proved as the compile-time claim it is.
/// </summary>
/// <remarks>
///     <para>
///         ADR-013 (docs/plan/02):
///         <i>
///             "<c>Build()</c> and <c>Apply()</c> exist only on the
///             fully-qualified interface, so <b>an unlabelled object does not compile</b>."
///         </i>
///         docs/plan/09 § The command builder says the same:
///         <i>
///             "The requirement from the brief —
///             every deployment marked with proper labels — is met by making the unlabelled case not
///             compile."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>Why this is a Roslyn test and not a <c>Should.Throw</c>.</b> "Does not compile" and
///         "throws at run time" are different guarantees, and only the first one is what the design
///         bought. A runtime check can be reached — in a code path nobody exercised, at 3 a.m., in
///         the one provider that forgot — and produces an unlabelled object that then fails admission
///         with an unrelated message. A compile error cannot be reached at all. Asserting an
///         exception here would quietly downgrade the claim to the weaker one and nobody would
///         notice, because the test would still be green.
///     </para>
///     <para>
///         So the source below is compiled, against the <b>real</b>
///         <c>CyberCloud.Kubernetes.Contracts</c> assembly this test project references, and the
///         diagnostics are asserted. The positive control compiles the legal chain and requires zero
///         errors — without it, a broken harness (a missing reference, say) would make every negative
///         case "pass" for the wrong reason.
///     </para>
/// </remarks>
public sealed class CompileFailureTests {
    /// <summary>
    ///     The references a snippet needs: the shared framework, plus every assembly this test
    ///     project already binds to.
    /// </summary>
    static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    // ── The positive control ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFullyQualifiedChainCompiles() {
        // ⚠ Without this, every negative case below would "pass" if the harness were broken.
        var errors = Errors(
            """
                    var command = KubeCommand.For(connection)
                        .WithTenantId(tenantId)
                        .WithResourceId(id)
                        .WithKind(new GroupVersionKind { Version = "v1", Kind = "ConfigMap", Plural = "configmaps" })
                        .InNamespace("ns")
                        .ObjectJson("{}")
                        .Build();
                    GC.KeepAlive(command);
            """
        );

        errors.ShouldBeEmpty(
            "the legal chain must compile, or the negative cases below prove nothing. "
            + string.Join("; ", errors.Select(x => x.ToString()))
        );
    }

    [Fact]
    public void TheFullyQualifiedChainCanAlsoApplyAndDelete() {
        Errors(
                """
                        _ = KubeCommand.For(connection).WithTenantId(tenantId).WithResourceId(id).ApplyAsync();
                        _ = KubeCommand.For(connection).WithTenantId(tenantId).WithResourceId(id).DeleteAsync(CascadePolicy.Foreground);
                """
            )
            .ShouldBeEmpty();
    }

    // ── The claim: the unlabelled cases do not compile ─────────────────────────────────────────

    [Fact]
    public void BuildDoesNotExistBeforeTheTenantIsNamed() {
        // KubeCommand.For(...) → IKubeCommandNeedsTenant, whose only member is WithTenantId.
        var errors = Errors(
            """
                    var command = KubeCommand.For(connection).Build();
                    GC.KeepAlive(command);
            """
        );

        errors.ShouldNotBeEmpty("ADR-013: an unlabelled object must not compile.");
        errors.ShouldContain(
            x => x.Id == "CS1061",
            "the failure must be 'IKubeCommandNeedsTenant does not contain a definition for Build' "
            + "— a member-resolution error — and not some incidental error. Got: "
            + string.Join("; ", errors.Select(x => x.Id))
        );

        errors[0].GetMessage().ShouldContain("IKubeCommandNeedsTenant");
        errors[0].GetMessage().ShouldContain("Build");
    }

    [Fact]
    public void ApplyAsyncDoesNotExistBeforeTheTenantIsNamed() {
        var errors = Errors(
            """
                    _ = KubeCommand.For(connection).ApplyAsync();
            """
        );

        errors.ShouldContain(x => x.Id == "CS1061");
        errors[0].GetMessage().ShouldContain("IKubeCommandNeedsTenant");
        errors[0].GetMessage().ShouldContain("ApplyAsync");
    }

    [Fact]
    public void BuildDoesNotExistBeforeTheResourceIsNamed() {
        // ⚠ THE CASE THAT MATTERS MOST. The tenant is named — so a developer has done *some* of the
        // labelling and is most likely to believe they are finished. The resource id is what carries
        // resource-id, subscription-id, resource-group and resource-type: four of the seven.
        var errors = Errors(
            """
                    var command = KubeCommand.For(connection).WithTenantId(tenantId).Build();
                    GC.KeepAlive(command);
            """
        );

        errors.ShouldContain(x => x.Id == "CS1061");
        errors[0].GetMessage().ShouldContain("IKubeCommandNeedsResource");
        errors[0].GetMessage().ShouldContain("Build");
    }

    [Fact]
    public void ApplyAsyncDoesNotExistBeforeTheResourceIsNamed() {
        var errors = Errors(
            """
                    _ = KubeCommand.For(connection).WithTenantId(tenantId).ApplyAsync();
            """
        );

        errors.ShouldContain(x => x.Id == "CS1061");
        errors[0].GetMessage().ShouldContain("IKubeCommandNeedsResource");
    }

    [Fact]
    public void DeleteAsyncDoesNotExistBeforeTheResourceIsNamed() {
        var errors = Errors(
            """
                    _ = KubeCommand.For(connection).WithTenantId(tenantId).DeleteAsync(CascadePolicy.Background);
            """
        );

        errors.ShouldContain(x => x.Id == "CS1061");
        errors[0].GetMessage().ShouldContain("IKubeCommandNeedsResource");
    }

    [Fact]
    public void TheStagesDoNotOfferTheBuilderMethodsEither() {
        // The intermediate interfaces have ONE member each, on purpose. If InNamespace or WithLabels
        // appeared on them, some unlabelled command would become expressible even though Build()
        // stayed put — and the chain would be decorative.
        Errors(
                """
                        _ = KubeCommand.For(connection).InNamespace("ns");
                """
            )
            .ShouldContain(x => x.Id == "CS1061");

        Errors(
                """
                        _ = KubeCommand.For(connection).WithTenantId(tenantId).WithLabels(("a", "b"));
                """
            )
            .ShouldContain(x => x.Id == "CS1061");
    }

    [Fact]
    public void TheOrderIsFixedAndCannotBeReversed() {
        // WithResourceId is not on stage 1, so the two calls cannot be swapped.
        Errors(
                """
                        _ = KubeCommand.For(connection).WithResourceId(id);
                """
            )
            .ShouldContain(x => x.Id == "CS1061");
    }

    [Fact]
    public void AKubeCommandCannotBeConstructedDirectly() {
        // ⚠ The escape hatch that would make the whole chain pointless. KubeCommand's constructor is
        // internal, so a caller cannot bypass the builder with an object initializer and hand an
        // unlabelled command straight to the grain.
        var errors = Errors(
            """
                    var command = new KubeCommand { Body = "{}" };
                    GC.KeepAlive(command);
            """
        );

        errors.ShouldNotBeEmpty("KubeCommand's constructor must not be accessible.");
        errors.ShouldContain(x => x.Id == "CS0122" || x.Id == "CS1729" || x.Id == "CS0143");
    }

    [Fact]
    public void TheBuilderImplementationCannotBeNamedSoTheChainCannotBeCastAway() {
        // ⚠ One class implements all three stages. If it were public, this would compile and the
        // type-state would be a suggestion:
        //     ((KubeCommandBuilder)KubeCommand.For(connection)).Build()
        var errors = Errors(
            """
                    _ = ((KubeCommandBuilder)KubeCommand.For(connection)).Build();
            """
        );

        errors.ShouldNotBeEmpty("KubeCommandBuilder must not be public.");
        errors.ShouldContain(x => x.Id == "CS0246" || x.Id == "CS0122");
    }

    [Fact]
    public void CastingAStageToTheBuilderInterfaceIsNotAWayIn() {
        // A cast to IKubeCommandBuilder compiles (the compiler cannot know the runtime type does not
        // implement it) — but it is an explicit, greppable act rather than an accident, and the
        // three interfaces being unrelated means no IMPLICIT conversion exists. That is the property
        // asserted: you cannot arrive at IKubeCommandBuilder by assignment.
        var errors = Errors(
            """
                    IKubeCommandBuilder builder = KubeCommand.For(connection);
                    GC.KeepAlive(builder);
            """
        );

        errors.ShouldNotBeEmpty("there must be no implicit conversion from stage 1 to the builder.");
        errors.ShouldContain(x => x.Id == "CS0266");
    }

    // ── The harness itself ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheHarnessCompilesAgainstTheRealContractsAssembly() {
        // Guards the failure mode that would make every assertion above vacuous: a References set
        // that does not actually contain the assemblies the probe names. If one were missing, the
        // negative cases would fail with CS0246 ("type or namespace not found") rather than CS1061,
        // which the assertions above already distinguish — this makes the reason explicit.
        // ⚠ Both assemblies, not just the one under test: CyberCloud.Core is the one that actually
        // went missing when the reference set was taken from the load context, and it goes missing
        // as the `using CyberCloud.Core;` in every snippet, not as a member-resolution failure.
        foreach (var assembly in (Assembly[]) [typeof(KubeLabels).Assembly, typeof(ResourceId).Assembly]) {
            References.ShouldContain(
                x => x.Display != null
                    && x.Display.EndsWith(Path.GetFileName(assembly.Location), StringComparison.OrdinalIgnoreCase),
                $"the probe must reference {assembly.GetName().Name} itself."
            );
        }

        Errors(
                """
                        _ = KubeLabels.ManagedByValue;
                """
            )
            .ShouldBeEmpty();
    }

    static ImmutableArray<MetadataReference> BuildReferences() {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string location) {
            if (!string.IsNullOrEmpty(location) && seen.Add(location)) {
                builder.Add(MetadataReference.CreateFromFile(location));
            }
        }

        // Everything the host resolved for this process: the shared framework plus every assembly
        // deployed beside the test — CyberCloud.Core, CyberCloud.Kubernetes.Contracts, Orleans'
        // abstractions. Same intent as before, that the snippet compiles against exactly what the
        // production code binds to, but taken from the host's list rather than the load context.
        //
        // ⚠ NOT AppDomain.CurrentDomain.GetAssemblies(). That is whatever the runtime has happened
        // to load by the time this initialiser runs, and this is a `beforefieldinit` class, so the
        // JIT may run the initialiser at any point before the first read of References — including
        // before the "touch a contracts type" line that used to sit at the top of Compile. Which
        // assemblies were loaded then depended on the order xunit ran the tests in the process: run
        // this class on its own and CyberCloud.Core was absent, every snippet failed with CS0246
        // "the type or namespace name 'Core' does not exist", and every negative case "passed" for
        // the wrong reason. The trusted-platform list is fixed at process start and cannot drift.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted) {
            foreach (var path in trusted.Split(Path.PathSeparator)) {
                Add(path);
            }
        }

        // Belt and braces, and the documentation of what the probe source actually needs: the two
        // assemblies it names, anchored on types so the compiler — not a string — guarantees they
        // resolve. Deduplicated against the list above, which normally already has them.
        Add(typeof(ResourceId).Assembly.Location);   // CyberCloud.Core
        Add(typeof(KubeLabels).Assembly.Location);   // CyberCloud.Kubernetes.Contracts
        Add(typeof(object).Assembly.Location);       // System.Private.CoreLib

        return builder.ToImmutable();
    }

    static ImmutableArray<Diagnostic> Compile(string body) {
        var source = $$"""
                       using System;
                       using CyberCloud.Core;
                       using CyberCloud.Core.Resources;
                       using CyberCloud.Kubernetes.Contracts;

                       public static class Probe
                       {
                           public static void Run(IKubeClusterConnection connection, Guid tenantId, ResourceId id)
                           {
                       {{body}}
                           }
                       }
                       """;

        var compilation = CSharpCompilation.Create(
            "CompileFailureProbe",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new(OutputKind.DynamicallyLinkedLibrary)
        );

        return compilation.GetDiagnostics()
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    static ImmutableArray<Diagnostic> Errors(string body) => Compile(body);
}
