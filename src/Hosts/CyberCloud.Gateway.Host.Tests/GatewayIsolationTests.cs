using CyberCloud.Gateway.Host.Pipeline;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     What the gateway must never do, asserted against the compiled assembly and its own source.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two instruments, because they catch different mistakes.</b> The metadata check reads
///         the <c>AssemblyRef</c> table, which Roslyn writes a row into only for an assembly a type
///         was actually <i>bound</i> from — so it answers "does the gateway name any type from
///         there?", which is the rule, while staying silent about the restore closure, which is not.
///         The source check catches a call made through a type that <i>is</i> legitimately
///         referenced. Neither alone is enough.
///     </para>
///     <para>
///         docs/plan/10 § What the gateway must never do is the list; the rows this file can check
///         mechanically are <i>perform authorization itself</i> and <i>query a database directly</i>.
///     </para>
/// </remarks>
public sealed class GatewayIsolationTests {
    static Assembly Gateway => typeof(GatewayPipeline).Assembly;

    /// <summary>
    ///     The gateway's source directory, found by walking up to the file that marks the repository.
    /// </summary>
    /// <remarks>
    ///     ⚠ A fixed number of <c>..</c> segments from <see cref="AppContext.BaseDirectory" /> is
    ///     what this was first written as, and it was wrong: <c>artifacts/bin/{name}/{configuration}</c>
    ///     is four levels, not five, and the miscount produced a directory that does not exist rather
    ///     than a failing assertion — so the test reported a missing path instead of a clean tree.
    ///     Walking up to <c>CyberCloud.slnx</c> cannot be off by one.
    /// </remarks>
    static string SourceRoot {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
                directory = directory.Parent;
            }

            directory.ShouldNotBeNull("could not find CyberCloud.slnx above " + AppContext.BaseDirectory);

            return Path.Combine(directory.FullName, "src", "Hosts", "CyberCloud.Gateway.Host");
        }
    }

    /// <summary>
    ///     ⚠ The mechanical form of docs/plan/07 § The enforcement seam. The gateway cannot even
    ///     <i>name</i> <c>ICheckGrain</c>: the authorization assembly is in its restore closure
    ///     (through <c>CyberCloud.ResourceManager</c>) and there is no <c>AssemblyRef</c> row for it,
    ///     which means no type from it is bound anywhere in this assembly.
    /// </summary>
    [Fact]
    public void TheGatewayBindsNoTypeFromTheAuthorizationAssemblies() {
        var referenced = Gateway.GetReferencedAssemblies().Select(x => x.Name ?? "").ToList();

        referenced.ShouldNotContain("CyberCloud.Authorization.Contracts");
        referenced.ShouldNotContain("CyberCloud.Authorization");
    }

    /// <summary>
    ///     docs/plan/10 § What the gateway must never do: <i>"A gateway with a <c>DbContext</c> is a
    ///     second write path within a year."</i>
    /// </summary>
    [Fact]
    public void TheGatewayBindsNoDataAccessAssembly() {
        var referenced = Gateway.GetReferencedAssemblies().Select(x => x.Name ?? "").ToList();

        foreach (var forbidden in new[] {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.Relational",
            "Npgsql",
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            "Volo.Abp.EntityFrameworkCore"
        }) {
            referenced.ShouldNotContain(forbidden);
        }
    }

    /// <summary>
    ///     docs/plan/10 § SignalR: <i>"No SignalR backplane product."</i> The fan-out is a connection
    ///     grain plus Orleans streams, which is <c>O(interested)</c> rather than <c>O(pods)</c>.
    /// </summary>
    [Fact]
    public void TheGatewayBindsNoSignalRBackplane() {
        Gateway.GetReferencedAssemblies()
            .Select(x => x.Name ?? "")
            .ShouldNotContain("Microsoft.AspNetCore.SignalR.StackExchangeRedis");
    }

    /// <summary>
    ///     ⚠ <b>A test that fails the day a <c>Check</c> call appears in gateway code.</b> The
    ///     metadata test above would not catch a check made through a type the gateway legitimately
    ///     references, so this one reads the source.
    /// </summary>
    [Fact]
    public void NoGatewaySourceFileCallsAnAuthorizationEngine() {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)) {
            var text = File.ReadAllText(file);

            // Comments legitimately discuss the seam at length, so they are stripped first —
            // otherwise this test would forbid explaining why the rule exists.
            var code = Regex.Replace(text, @"^\s*(///|//).*$", "", RegexOptions.Multiline);

            foreach (var forbidden in new[] {
                "ICheckGrain",
                "CheckAsync(",
                "IResourceAuthorizer",
                "SubjectRef",
                "ObjectRef.Resource"
            }) {
                if (code.Contains(forbidden, StringComparison.Ordinal)) {
                    offenders.Add($"{Path.GetFileName(file)} contains '{forbidden}'");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "docs/plan/07 § The enforcement seam: authorization lives in exactly one place, inside "
            + "the resource manager. A check here would not be a duplicate — it would be a SECOND "
            + "seam, and the one that gets updated when a rule changes is whichever one the author "
            + "remembered."
        );
    }

    /// <summary>
    ///     The gateway keeps no per-request state across pods, and holds no store handle.
    /// </summary>
    [Fact]
    public void NoGatewaySourceFileNamesADbContextOrAConnection() {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)) {
            var code = Regex.Replace(
                File.ReadAllText(file),
                @"^\s*(///|//).*$",
                "",
                RegexOptions.Multiline
            );

            foreach (var forbidden in new[] { "DbContext", "NpgsqlConnection", "SqlConnection", "DbSet<" }) {
                if (code.Contains(forbidden, StringComparison.Ordinal)) {
                    offenders.Add($"{Path.GetFileName(file)} contains '{forbidden}'");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "docs/plan/10 § What the gateway must never do: every read is a grain call or the "
            + "resource-graph projection, and a gateway with a DbContext is a second write path "
            + "within a year."
        );
    }

    /// <summary>
    ///     ⚠ <c>CC1006</c> only runs on projects that reference the analyzer, and docs/plan/00 § The
    ///     tenant-separation row, corrected says adding it here <i>"is what makes this row true for
    ///     the caller it is actually about"</i>. The <c>Analyzer coverage</c> architecture gate
    ///     enforces it too; this asserts it from the test side so a local build says so as well.
    /// </summary>
    [Fact]
    public void TheGatewayProjectReferencesTheAnalyzers() {
        var project = Path.Combine(SourceRoot, "CyberCloud.Gateway.Host.csproj");

        File.Exists(project).ShouldBeTrue($"expected the gateway project at {project}");

        var text = File.ReadAllText(project);
        text.ShouldContain("CyberCloud.Analyzers.csproj");
        text.ShouldContain("OutputItemType=\"Analyzer\"");
    }

    /// <summary>
    ///     ⚠ <b>The gateway declares no grain — neither an implementation nor a contract.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         docs/plan/03 § Hosts and docs/plan/10 § Shape: the gateway is an Orleans <b>client</b>
    ///         (<c>CreateClient</c>), <i>"not a silo"</i>. A client's <c>IGrainFactory</c> hands out
    ///         references to grains a silo activates, so a <c>Grain</c> subclass declared in this
    ///         assembly is a grain <b>no silo can load</b>: the type is simply not in the silo's
    ///         application-parts, and the first call to it fails at runtime in a deployment rather
    ///         than in a build.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is not hypothetical — it is what <c>ConnectionGrain</c> was.</b>
    ///         docs/plan/10 § SignalR names the connection grain and never says where it runs, so the
    ///         interface and the implementation shipped here, and the arrangement survived because the
    ///         tests constructed the class with <c>new</c> — which worked precisely because the grain
    ///         used no grain infrastructure, a constraint the file recorded as if it were a design.
    ///         Both now live in <c>CyberCloud.ResourceManager(.Contracts)</c>, where a silo composes
    ///         them and the interest set's authorization seam already is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The interface half matters as much as the implementation half.</b> A grain
    ///         <i>contract</i> declared in a host is a contract no other component can name without
    ///         referencing a host — inverting docs/plan/00 § Layer discipline — and
    ///         docs/plan/03 § src puts grain interfaces in <c>*.Contracts</c> for exactly that reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheGatewayDeclaresNoGrainInterfaceAndNoGrainImplementation() {
        var offenders = Gateway.GetTypes()
            .Where(type => typeof(IGrain).IsAssignableFrom(type) || typeof(Grain).IsAssignableFrom(type))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "docs/plan/10 § Shape: the gateway is an Orleans CLIENT, not a silo. A grain declared "
            + "here is one no silo can activate — grain contracts belong in a *.Contracts assembly "
            + "and grain implementations in the module assembly a silo composes."
        );
    }

    /// <summary>
    ///     ⚠ Raw <c>IGrainFactory.GetGrain</c> is banned here. <c>CC1006</c> is the real gate and it
    ///     fails the build; this reads the source so the rule is also stated where a reader looks.
    /// </summary>
    [Fact]
    public void NoGatewaySourceFileTakesAGrainReferenceWithoutForTenant() {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot, "*.cs", SearchOption.AllDirectories)) {
            var code = Regex.Replace(
                File.ReadAllText(file),
                @"^\s*(///|//).*$",
                "",
                RegexOptions.Multiline
            );

            foreach (Match match in Regex.Matches(code, @"\.GetGrain<")) {
                var before = code[..match.Index];

                if (!before.EndsWith("ForTenant(caller.TenantId.ToString(\"D\", CultureInfo.InvariantCulture))", StringComparison.Ordinal)
                    && !before.Contains("ForTenant(", StringComparison.Ordinal)) {
                    offenders.Add($"{Path.GetFileName(file)} calls GetGrain without ForTenant");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }
}
