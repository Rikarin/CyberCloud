namespace CyberCloud.Cluster.Conformance.Infrastructure;

/// <summary>
///     The class fixture the four lifecycle tests share: one Orleans cluster over one real API server
///     and one real PostgreSQL shard.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It never throws when Docker is absent, and it never quietly succeeds either.</b> A
///         fixture that threw would fail the class, which reads as "the provider is broken"; a fixture
///         that skipped would take the whole class out of the runner's output with one message. So the
///         failure is <i>remembered</i> and every test turns it into its own <c>Assert.Skip</c>,
///         naming the provider and the assertion that was not made. That is the behaviour the
///         loudly-skipped originals had, kept.
///     </para>
///     <para>
///         ⚠ <b>One cluster for four tests, and each test uses its own resource names.</b> A real API
///         server has no <c>Reset()</c>. Sharing is what keeps a k3s start out of every test.
///     </para>
/// </remarks>
/// <typeparam name="TSource">The provider under test.</typeparam>
public class ClusterConformanceFixture<TSource> : IAsyncLifetime
    where TSource : IProviderCaseSource {
    /// <summary>The Orleans service id — fixed so the durable rows are findable by hand.</summary>
    public const string ServiceId = "cluster-conformance";

    /// <summary>The base silo port. Kept clear of the silo-kill suite's.</summary>
    public const int BaseSiloPort = 22300;

    Exception? failure;

    /// <summary>The harness, or <see langword="null" /> when the infrastructure did not come up.</summary>
    public ClusterConformanceHarness<TSource>? Harness { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        var endpoints = await ClusterInfrastructure.TryStartAsync(token);

        if (endpoints is null) {
            return;
        }

        try {
            Harness = await ClusterConformanceHarness<TSource>.StartAsync(
                1,
                ServiceId + "-" + typeof(TSource).Name,
                BaseSiloPort,
                endpoints,
                token
            );
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            failure = ex;
        }
    }

    /// <summary>
    ///     The harness, or a loud skip naming what was not checked.
    /// </summary>
    /// <param name="wouldProve">What the calling test would have proved.</param>
    public ClusterConformanceHarness<TSource> Require(string wouldProve) {
        if (Harness is { } harness) {
            return harness;
        }

        Assert.Skip(
            ClusterInfrastructure.SkipMessage(TSource.ProviderCase.DisplayName, wouldProve, failure)
        );

        throw new InvalidOperationException("unreachable: Assert.Skip does not return.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (Harness is not null) {
            await Harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
