using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Agent.Host;

/// <summary>
///     Builds the agent host — the two health endpoints and the one service.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Composition lives here and not in <c>Program.cs</c></b>, for the reason every host
///         in this directory gives: top-level statements cannot be called from a test, and
///         <c>HostCompositionTests</c> composes this host to prove its options bind and its service
///         registers.
///     </para>
///     <para>
///         ⚠ <b>The in-cluster client is built lazily, at first use, not at composition.</b>
///         <c>KubernetesClientConfiguration.InClusterConfig()</c> throws outside a pod — no service
///         account token on disk — and a composition that called it eagerly could not be built in a
///         test. <see cref="IAgentEndpoints" /> is resolved by <see cref="AgentService" /> when it
///         starts, so the throw, when it comes, is in the log of a pod that has no service account
///         mounted, which is a chart problem the message names.
///     </para>
/// </remarks>
public static class AgentComposition {
    /// <summary>Composes the host. Nothing has started.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configure">What a test supplies instead of a pod — an <see cref="IAgentEndpoints" />.</param>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configure = null) {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddOptions<AgentOptions>().BindConfiguration(AgentOptions.SectionName);
        builder.Services.TryAddSingleton<IClock, SystemClock>();
        builder.Services.TryAddSingleton<IAgentEndpoints>(provider => {
                var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;

                // ⚠ The Secret name is the chart's, not a default: rbac.yaml scopes the agent's Role
                // to cluster.credentialSecretName, and a store writing under any other name could
                // create its Secret and never read it back.
                var (api, credentials, ns) = InClusterAgent.FromPod(
                    options.ParsedClusterId,
                    provider.GetRequiredService<IClock>(),
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger("CyberCloud.Agent.Host.Kubernetes"),
                    options.CredentialSecretName
                );

                provider.GetRequiredService<ILogger<AgentService>>()
                    .LogInformation(
                        "Serving the API server through the service account in namespace {Namespace}; the credential Secret is {Secret}.",
                        ns,
                        options.CredentialSecretName
                    );

                return new PodEndpoints(api, credentials);
            }
        );

        builder.Services.AddHostedService<AgentService>();

        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapDefaultEndpoints();
        return app;
    }
}
