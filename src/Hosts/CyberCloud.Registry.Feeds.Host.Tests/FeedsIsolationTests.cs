using System.Reflection;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The feeds host's reference set, read off the compiled assembly — the same argument
///     <c>GatewayIsolationTests</c> makes for the gateway, for a second host that accepts the same
///     tokens.
/// </summary>
public sealed class FeedsIsolationTests {
    static Assembly Host => typeof(FeedsComposition).Assembly;

    [Fact]
    public void TheHostCannotNameTheAuthorizationEngine() {
        // ⚠ docs/plan/07 § The enforcement seam: exactly one place calls the engine, and it is the
        // resource manager. This host asks IResourceAuthorizer — the manager's own seam — and binds
        // no type from the assembly that declares ICheckGrain, so no AssemblyRef row exists for it
        // even though CyberCloud.ResourceManager is referenced and references it.
        Host.GetReferencedAssemblies()
            .Select(x => x.Name ?? "")
            .ShouldNotContain("CyberCloud.Authorization.Contracts");
    }

    [Fact]
    public void TheHostValidatesTokensAndMintsNone() {
        var referenced = Host.GetReferencedAssemblies().Select(x => x.Name ?? "").ToList();

        referenced.ShouldContain("CyberCloud.Identity.Validation");
        referenced.ShouldNotContain(x => x.StartsWith("OpenIddict.Server", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHostIsNoWritePathAndNoSilo() {
        var referenced = Host.GetReferencedAssemblies().Select(x => x.Name ?? "").ToList();

        referenced.ShouldNotContain(x => x.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        referenced.ShouldNotContain(x => x.StartsWith("Npgsql", StringComparison.Ordinal));
        referenced.ShouldNotContain("Microsoft.Orleans.Runtime", "a data-plane host activates no grain");
        referenced.ShouldNotContain("CyberCloud.Providers.ContainerRegistry", "the implementation assembly is the silo's; this host binds the module and the contracts");
    }

    [Fact]
    public void NoSourceFileCallsAnAuthorizationEngine() {
        // The same grep the gateway suite makes over its own source, for the same reason a
        // registration line is a line that changes when the engine changes.
        // Walks up to the solution file rather than counting `..` segments, as GatewayIsolationTests
        // does, so a change to the artifacts layout does not silently turn this into a test of nothing.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not find CyberCloud.slnx above " + AppContext.BaseDirectory);
        var root = Path.Combine(directory.FullName, "src", "Hosts", "CyberCloud.Registry.Feeds.Host");

        Directory.Exists(root).ShouldBeTrue(root);

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)) {
            // Comments legitimately discuss the seam at length, so they are stripped first.
            var code = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"^\s*(///|//).*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);

            // ⚠ IResourceAuthorizer is NOT on this list, unlike the gateway's. The gateway dispatches
            // to the resource manager and needs no authorization answer of its own; this host serves
            // a data plane and asks the manager's authorizer the question the manager would ask for a
            // PUT — with the type's own permission names — which is the one sanctioned way to reach a
            // decision without a second seam. What stays forbidden is the engine.
            foreach (var forbidden in new[] { "ICheckGrain", "CheckAsync(", "SubjectRef", "ObjectRef.Resource", "ReBacResourceAuthorizer" }) {
                if (code.Contains(forbidden, StringComparison.Ordinal)) {
                    offenders.Add($"{Path.GetFileName(file)} contains '{forbidden}'");
                }
            }
        }

        offenders.ShouldBeEmpty("docs/plan/07 § The enforcement seam: the engine is called from the resource manager and from nowhere in this host");
    }
}
