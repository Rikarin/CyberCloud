using CyberCloud.Cluster.Conformance.Infrastructure;
using System.Reflection;

namespace CyberCloud.Cluster.Conformance;

/// <summary>
///     That the containers stop inside the test run rather than at process exit.
/// </summary>
/// <remarks>
///     ⚠ <b>Daemon-free on purpose</b>, like <c>SkipConventionTests</c>: it reads an attribute, so
///     it runs, and can fail, on a machine with no Docker. Without the attribute nothing fails at
///     all: the containers are left to the resource reaper, and each suite sits beside a k3s
///     nobody stopped until the reaper notices the process is gone. See
///     <see cref="ClusterInfrastructureTeardown" /> for why the stop can't move back to
///     <c>AppDomain.ProcessExit</c>.
/// </remarks>
public sealed class ClusterInfrastructureTeardownTests {
    [Fact]
    public void ThisAssemblyStopsItsContainersBeforeTheRunnerReturns() =>
        typeof(ClusterInfrastructureTeardownTests).Assembly
            .GetCustomAttributes<AssemblyFixtureAttribute>()
            .Select(static x => x.AssemblyFixtureType)
            .ShouldContain(
                typeof(ClusterInfrastructureTeardown),
                "Directory.Build.targets § Cluster harness teardown did not generate "
                + "[assembly: AssemblyFixture(typeof(ClusterInfrastructureTeardown))] for this "
                + "assembly, and every .Cluster.Conformance assembly gets it by the same condition. "
                + "Without it nothing stops the process-wide k3s, PostgreSQL and Redis before the "
                + "runner returns."
            );
}
