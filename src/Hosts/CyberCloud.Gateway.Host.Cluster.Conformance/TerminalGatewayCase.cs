using CyberCloud.Conformance;
using CyberCloud.Core.Time;
using CyberCloud.Kubernetes;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Terminal;
using CyberCloud.Providers.Terminal.Conformance;
using CyberCloud.ResourceManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace CyberCloud.Gateway.Host.Cluster.Conformance;

/// <summary>
///     <see cref="CloudConsoleCase" />, with the silo reaching the cluster the way
///     <c>CyberCloud.Silo.Host</c> does: through the cluster connection grain.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why the terminal story can't use the harness's own connection.</b> Every other
///         cluster-backed suite hands the silo <c>RealClusterConnectionFactory</c>, a direct client, and
///         says why: what those suites assert lives in <c>KubeApiClient</c>. The terminal's attach
///         doesn't. In production it goes <c>GrainClusterConnection</c> → <c>ClusterAttachDialer</c> →
///         <c>IClusterConnectionGrain.AuthorizeAttachAsync</c> → <c>IKubeApiClientFactory</c> →
///         <c>KubeApiClient.AttachAsync</c>, and each hop is one the direct client skips — the
///         connection grain's tenancy check from a session grain's background task, and a client built
///         from the descriptor for the attach alone. The review of #22 found that path had never run
///         against a cluster.
///     </para>
///     <para>
///         ⚠ <b>So the silo composes the production path, and one substitute.</b> The kubeconfig is
///         resolved from <see cref="Kubeconfig" /> rather than from a vault, which is the same seam
///         <c>SiloComposition</c> fills from a local file. The connection grain is attached for
///         <see cref="ConformanceIds.Cluster" />, owned by <see cref="ConformanceIds.Tenant" />, by
///         <see cref="TerminalGatewayFixture" /> before the first test.
///     </para>
///     <para>
///         ⚠ <b>A case source of its own because the harness's state is per source.</b>
///         <c>ClusterConformanceState</c> is static per type argument, and <see cref="ConfigureSilo" />
///         is a static member, so this can't be a flag on <see cref="CloudConsoleCase" /> without
///         changing that provider's own cluster suite.
///     </para>
/// </remarks>
public sealed class TerminalGatewayCase : IProviderCaseSource {
    /// <summary>
    ///     The shell image the story runs: PostgreSQL's Alpine image, by digest — <c>bash</c>,
    ///     BusyBox's <c>stty</c> and <c>psql</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ A stand-in for docs/plan/19's image, which nothing in this repository builds —
    ///     <c>charts/managed/cloud-shell/conformance.yaml § owed</c>, <c>no-image-pipeline</c>. It is
    ///     handed to the platform exactly as the real one will be: as
    ///     <see cref="CloudShellImageOptions.Default" />, pinned, in the silo's configuration and the
    ///     gateway's.
    /// </remarks>
    public const string ShellImage =
        "docker.io/library/postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24";

    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase => CloudConsoleCase.ProviderCase;

    /// <summary>
    ///     The k3s kubeconfig the silo's client factory resolves every credential reference to. Set by
    ///     <see cref="TerminalGatewayFixture" /> before the silo starts.
    /// </summary>
    public static string Kubeconfig { get; set; } = string.Empty;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ Called after the harness's own registrations, so the connection factory here replaces
    ///     the harness's direct one: the last registration of a service is the one resolved.
    /// </remarks>
    public static void ConfigureSilo(ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        silo.ConfigureServices(static services => {
                // ⚠ The silo runs `connect` now — the gateway relays it — so the silo's handler needs
                // the image, as a deployment's silo configuration carries it.
                services.Configure<CloudShellImageOptions>(static images => images.Default = ShellImage);

                services.AddSingleton<IKubeApiClientFactory>(static provider =>
                    new KubeApiClientFactory(
                        provider.GetRequiredService<IClock>(),
                        provider.GetService<ILogger<KubeApiClientFactory>>(),
                        provider.GetRequiredService<IGrainFactory>()
                    ) {
                        ResolveKubeconfig = static (_, _) => Task.FromResult(Result<string>.Success(Kubeconfig))
                    }
                );

                services.AddSingleton<IClusterConnectionFactory, GrainClusterConnectionFactory>();
            }
        );

        // The connection grain, its tenancy filter and the attach dialer, as a silo host adds them.
        silo.AddCyberCloudKubernetes();
    }
}
