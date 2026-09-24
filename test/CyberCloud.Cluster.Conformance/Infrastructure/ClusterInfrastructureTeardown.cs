namespace CyberCloud.Cluster.Conformance.Infrastructure;

/// <summary>
///     Stops the containers <see cref="ClusterInfrastructure.TryStartAsync" /> started, as an
///     assembly fixture, so the stop happens while the test runner is still waiting for it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A stop in <c>AppDomain.ProcessExit</c> races a watchdog that turns a green suite
///             into exit code 1.
///         </b> xunit.v3 3.2.2's Microsoft.Testing.Platform entry point returns from
///         <c>RunAsync</c> and queues a timer: a second later it prints
///         <i>"Waiting 10 seconds for foreground threads to exit..."</i>, and ten seconds after that
///         <i>"[FATAL ERROR] Foreground threads were left running, forcing process exit"</i>, then
///         calls <c>Environment.Exit(1)</c>. <c>ProcessExit</c> handlers run after <c>RunAsync</c>
///         has returned, so they spend the watchdog's time. The harness used to stop k3s,
///         PostgreSQL and Redis there one after another: every cluster-backed run printed the
///         warning, and a <c>CyberCloud.Providers.Network.Cluster.Conformance</c> run with 0
///         failures once exited 1, which <c>build/Build.Test.cs</c> reads as a failed suite because
///         it goes by the exit code. An assembly fixture is disposed inside <c>RunAsync</c>, after
///         every class fixture, so none of the stop is on the watchdog's clock.
///     </para>
///     <para>
///         ⚠
///         <b>
///             No suite declares it: <c>Directory.Build.targets</c> § Cluster harness teardown
///             adds the attribute.
///         </b> Every project whose name ends in <c>.Cluster.Conformance</c> gets
///         <c>[assembly: AssemblyFixture(typeof(ClusterInfrastructureTeardown))]</c> generated, by
///         the same suffix rule <c>Directory.Build.props</c> § Project role detection uses, so a
///         new provider suite still needs only its class declarations. The bundle suite never calls
///         <see cref="ClusterInfrastructure.TryStartAsync" /> and disposes a fixture with nothing to
///         stop. <c>ClusterInfrastructureTeardownTests.ThisAssemblyStopsItsContainersBeforeTheRunnerReturns</c>
///         pins the generated attribute on this assembly, which the same condition reaches.
///     </para>
/// </remarks>
public sealed class ClusterInfrastructureTeardown : IAsyncDisposable {
    /// <inheritdoc />
    public ValueTask DisposeAsync() => ClusterInfrastructure.StopAsync();
}
