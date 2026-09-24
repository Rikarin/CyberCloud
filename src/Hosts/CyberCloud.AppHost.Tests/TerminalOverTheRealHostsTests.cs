using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Terminal.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using k8s;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     A cloud shell driven from the gateway's process into a shell pod scheduled by a silo process,
///     and its output delivered back across the same boundary.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why a second process at all.</b> Issue #22 added three calls that cross from the
///         gateway to a silo — <see cref="IClusterActionGrain" /> (<c>connect</c> and <c>terminate</c>,
///         relayed), <see cref="ITerminalSessionGrain" /> (the hub's attach, keystrokes, resizes and
///         status) and <see cref="ITerminalViewer" />, which the silo calls back into the gateway with
///         the shell's output. Every other suite that ran them shares one type manifest between the
///         gateway and the silo, and that's how #39's refused type reached master. The second review
///         of #22 found none of these calls had crossed a process boundary, and one wire type,
///         <see cref="TerminalSessionPhase" />, had already shipped without its alias.
///     </para>
///     <para>
///         ⚠ <b>The gateway is the one <c>Program.cs</c> builds, in this process; the silos are the
///         AppHost's two processes.</b> Nothing below is substituted: the console is created through
///         the real write path, converged by the reminder on a silo, and connected to through the
///         gateway's own <see cref="IResourceManager" />, which relays the action. The attach is made
///         the way <c>TerminalHub</c> makes it — an observer reference from the gateway's Orleans
///         client — rather than over the hub's socket, because the socket needs a token from an
///         identity host and the socket is not what crosses. <c>TerminalOverTheGatewayTests</c> runs
///         the socket.
///     </para>
///     <para>
///         ⚠ <b>Two things the topology needed and did not have.</b> Its silos had no shell image, so a
///         connect started a pod on a placeholder digest that never pulled, and its k3s node tainted
///         itself <c>DiskPressure</c> on a Docker disk past 85%. A widget is a ConfigMap, so nothing
///         noticed until a pod was asked for. See <see cref="CyberCloudResources.ShellImage" /> and the
///         k3s arguments in <c>CyberCloudTopology</c>.
///     </para>
///     <para>
///         ⚠ <b>And its first run found the platform refusing every shell.</b> The gateway's
///         <c>connect</c> answered the console's owner <c>404</c>: <c>CyberCloudSchema</c> declared no
///         <c>connect</c>, an undeclared permission evaluates false, and every terminal suite before
///         this one ran against a doubled authorizer. It is <c>Rel(contributor)</c> now —
///         <c>RoleAssignmentTests.AContributorMayOpenAConsolesShellAndAReaderMayNot</c> in
///         <c>CyberCloud.Isolation</c>.
///     </para>
/// </remarks>
/// <param name="topology">The running AppHost — two silo processes, Redis, PostgreSQL, k3s.</param>
[Collection(LocalTopologySuite.Name)]
public sealed class TerminalOverTheRealHostsTests(LocalTopology topology) : IAsyncLifetime {
    /// <summary>How long the console's create has to converge — reminder-driven, a minute per pass.</summary>
    static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(5);

    /// <summary>How long the shell has to answer: an image pull, a pod start and an attach.</summary>
    static readonly TimeSpan ShellBudget = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for k3s, which nothing in the AppHost waits on.</summary>
    static readonly TimeSpan ClusterBudget = TimeSpan.FromMinutes(3);

    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0220");
    static readonly Guid Subscription = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0221");

    /// <summary>The cluster the console's body names, and the connection grain's key.</summary>
    static readonly Guid Cluster = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0222");

    const string ResourceGroup = "cloud-shell";
    const string ConsoleName = "ops";

    static CallerContext Alice { get; } = new() { TenantId = Tenant, SubjectType = SubjectTypes.User, SubjectId = "shell-alice" };

    static CallerContext Bob { get; } = Alice with { SubjectId = "shell-bob" };

    static ResourceId Address { get; } = new(Tenant, Subscription, ResourceGroup, CloudConsoles.Type, ConsoleName, Guid.Empty);

    static string Namespace { get; } = Subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + ResourceGroup;

    WebApplication gateway = null!;
    IKubernetes raw = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        raw = await ConnectToK3sAsync(token);

        gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture),
                // Never fetched: nothing here sends an HTTP request. ReconcileThroughTheRealHostTests says why it's named.
                "--CyberCloud:Gateway:Identity:Issuer=http://127.0.0.1:1"
            ]
        );

        await gateway.StartAsync(token);

        await BootstrapTenantAsync();
        await CreateScopesAsync(token);
        await AttachClusterAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (gateway is not null) {
            await gateway.StopAsync(CancellationToken.None);
            await gateway.DisposeAsync();
        }

        raw?.Dispose();
    }

    [Fact]
    public async Task AShellIsConnectedTypedIntoAndTerminatedAcrossTheGatewaySiloBoundary() {
        var token = TestContext.Current.CancellationToken;
        var manager = gateway.Services.GetRequiredService<IResourceManager>();

        // ── The console, created and converged by the platform ──────────────────────────────────
        var accepted = await manager.WriteAsync(
            new() {
                Path = Address.Path,
                ApiVersion = CloudConsoles.V2026,
                Verb = WriteVerb.Put,
                Body = CloudConsoles.Body(Cluster),
                Caller = Alice
            },
            token
        );

        accepted.IsSuccess.ShouldBeTrue($"{accepted.Error?.Code} — {accepted.Error?.Message}");

        var created = await ConvergeAsync(manager, accepted.GetValueOrThrow().OperationId);
        created.State.ShouldBe(OperationState.Succeeded, $"{created.Error?.Code} — {created.Error?.Message}");

        var console = Address.WithId(accepted.GetValueOrThrow().Resource.Id);

        // ── connect, relayed from this process to a silo's IClusterActionGrain ───────────────────
        var connected = await ActAsync(manager, CloudConsoles.ConnectAction, Alice);

        connected.IsSuccess.ShouldBeTrue(
            "connect through the gateway's manager failed. An InternalError naming "
            + "TypeManifestOptions.AllowedTypes or an unknown alias is the silo process refusing a wire "
            + $"type the relay sent: {connected.Error?.Code} — {connected.Error?.Message}"
        );

        string sessionId;

        using (var answer = JsonDocument.Parse(connected.GetValueOrThrow().ActionResponse)) {
            sessionId = answer.RootElement.GetProperty(CloudConsoles.SessionIdField).GetString()!;
        }

        TerminalSessionKeys.IsSessionId(sessionId).ShouldBeTrue(sessionId);

        // A colleague with connect on the console, relayed the same way: the silo's refusal comes back.
        var taken = await ActAsync(manager, CloudConsoles.ConnectAction, Bob);
        taken.IsFailure.ShouldBeTrue("a second person was given the first person's shell");
        taken.Error!.Code.ShouldBe(ErrorCode.Conflict, taken.Error.Message);

        // ── The session grain, from the gateway's own Orleans client, as TerminalHub reaches it ─────
        var grains = gateway.Services.GetRequiredService<IGrainFactory>();
        var session = grains
            .ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITerminalSessionGrain>(TerminalSessionKeys.Session(sessionId));

        // TerminalSessionSpec across the boundary, and the owner the silo recorded refusing it.
        var registered = await session.OpenAsync(
            new() {
                Resource = console,
                ApiVersion = CloudConsoles.V2026,
                ClusterId = Cluster,
                Pod = CloudConsoles.PodRef(Namespace, ConsoleName),
                Container = CloudConsoles.ShellContainer,
                PodUid = sessionId,
                IdleTimeoutSeconds = 1200,
                Permission = CloudConsoles.ConnectPermission,
                ReadPermission = "read",
                OwnerAnnotation = CloudConsoles.OwnerAnnotation
            },
            Bob
        );

        registered.IsFailure.ShouldBeTrue("a spec that crossed the boundary bound somebody else");
        registered.Error!.Code.ShouldBe(ErrorCode.Conflict, registered.Error.Message);

        var pane = new RecordingPane();
        var viewer = grains.CreateObjectReference<ITerminalViewer>(pane);

        try {
            var attached = await session.AttachAsync(Alice, viewer, 100, 30);
            attached.IsSuccess.ShouldBeTrue($"{attached.Error?.Code} — {attached.Error?.Message}");

            (await session.AttachAsync(Bob, viewer, 80, 24)).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

            // Held until the stream opens, then sent; the output comes back through ITerminalViewer.
            (await session.SendAsync(Alice, "echo cross-$((40+2)); stty size\r"u8.ToArray())).IsSuccess.ShouldBeTrue();

            await UntilAsync(
                () => pane.Screen.Contains("cross-42", StringComparison.Ordinal)
                    && pane.Screen.Contains("30 100", StringComparison.Ordinal),
                ShellBudget,
                async () => $"the shell's output never came back. Pane: '{pane.Screen}'. Pod: {await PodStateAsync(token)}"
            );

            var status = await session.StatusAsync();
            status.Phase.ShouldBe(TerminalSessionPhase.Open);
            status.Owner.ShouldBe(TerminalSessionKeys.OwnerStamp(Alice));
            status.Viewers.ShouldBe(1);

            // ── terminate, relayed; the end comes back through the observer ─────────────────────
            var terminated = await ActAsync(manager, CloudConsoles.TerminateAction, Alice);
            terminated.IsSuccess.ShouldBeTrue($"{terminated.Error?.Code} — {terminated.Error?.Message}");

            await UntilAsync(
                () => pane.Ended is not null,
                TimeSpan.FromMinutes(1),
                () => Task.FromResult("the pane was never told the shell ended")
            );
        } finally {
            await session.DetachAsync(viewer);
            grains.DeleteObjectReference<ITerminalViewer>(viewer);
        }
    }

    Task<Result<WriteAccepted>> ActAsync(IResourceManager manager, string action, CallerContext caller) =>
        manager.ActionAsync(
            new() {
                Path = Address.Path,
                ApiVersion = CloudConsoles.V2026,
                Verb = WriteVerb.Post,
                Action = action,
                Body = "{}",
                Caller = caller
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>What the pod says, for a failure message: its phase, its scheduling, and why its container waits.</summary>
    async Task<string> PodStateAsync(CancellationToken cancellationToken) {
        var pods = await raw.CoreV1.ListNamespacedPodAsync(
            Namespace,
            fieldSelector: "metadata.name=" + CloudConsoles.ShellName(ConsoleName),
            cancellationToken: cancellationToken
        );

        if (pods.Items.Count == 0) {
            return "no pod";
        }

        var pod = pods.Items[0];
        var conditions = string.Join(
            "; ",
            pod.Status?.Conditions?.Select(static x => $"{x.Type}={x.Status} {x.Reason} {x.Message}") ?? []
        );
        var waiting = string.Join(
            "; ",
            pod.Status?.ContainerStatuses?.Select(static x => $"{x.State?.Waiting?.Reason} {x.State?.Waiting?.Message}") ?? []
        );

        return $"{pod.Status?.Phase}; conditions: {conditions}; waiting: {waiting}";
    }

    static async Task<OperationStatus> ConvergeAsync(IResourceManager manager, Guid operationId) {
        var clock = Stopwatch.StartNew();
        OperationStatus? last = null;

        while (clock.Elapsed < ConvergenceBudget) {
            var read = await manager.GetOperationAsync(operationId, Alice, TestContext.Current.CancellationToken);
            read.IsSuccess.ShouldBeTrue(read.Error?.Message);

            last = read.GetValueOrThrow();

            if (last.IsTerminal) {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        return last!;
    }

    static async Task UntilAsync(Func<bool> condition, TimeSpan budget, Func<Task<string>> because) {
        var clock = Stopwatch.StartNew();

        while (!condition()) {
            if (clock.Elapsed > budget) {
                throw new TimeoutException($"{await because()} (after {budget.TotalSeconds:F0} s)");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }
    }

    // ── Seeding, as ReconcileThroughTheRealHostTests does it and for its reasons ──────────────────

    /// <summary>The tenant record, and both people as owners of the tenant — so each holds connect.</summary>
    async Task BootstrapTenantAsync() {
        var tenant = topology.Client.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture));

        var record = await tenant
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant))
            .CreateAsync("cloud-shell-tenant", "Cloud shell", "eu-central");

        record.IsSuccess.ShouldBeTrue(record.Error?.Message);

        foreach (var person in new[] { Alice, Bob }) {
            var tuple = RelationTuple.Create(
                AuthObjectRef.Create(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow(),
                Relations.Owner,
                SubjectRef.Create(SubjectTypes.User, person.SubjectId).GetValueOrThrow()
            )
                .GetValueOrThrow();

            var written = await tenant.GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant)).WriteAsync(tuple);
            written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        }
    }

    async Task CreateScopesAsync(CancellationToken cancellationToken) {
        var scopes = gateway.Services.GetRequiredService<IScopeManager>();

        var subscription = await scopes.CreateAsync(
            new() { Path = ScopeId.Subscription(Tenant, Subscription).Path, Body = """{"displayName":"cloud shell"}""", Caller = Alice },
            cancellationToken
        );

        subscription.IsSuccess.ShouldBeTrue(subscription.Error?.Message);

        var group = await scopes.CreateAsync(
            new() { Path = ScopeId.Group(Tenant, Subscription, ResourceGroup).Path, Body = """{"location":"eu-central"}""", Caller = Alice },
            cancellationToken
        );

        group.IsSuccess.ShouldBeTrue(group.Error?.Message);
    }

    async Task AttachClusterAsync() {
        var attached = await topology.Client
            .GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(Cluster))
            .AttachAsync(
                new() {
                    ClusterId = Cluster,
                    OwningTenantId = Tenant,
                    Kind = ClusterConnectionKind.Kubeconfig,
                    CredentialRef = new Uri(KubeconfigPath).AbsoluteUri,
                    Endpoint = $"https://127.0.0.1:{CyberCloudResources.K3sApiPort}",
                    DisplayName = "the AppHost's k3s, for the cloud shell"
                }
            );

        attached.IsSuccess.ShouldBeTrue($"{attached.Error?.Code} — {attached.Error?.Message}");
    }

    static string KubeconfigPath { get; } = Path.Combine(TestPaths.AppHostDirectory, ".k3s", "kubeconfig.yaml");

    static async Task<IKubernetes> ConnectToK3sAsync(CancellationToken cancellationToken) {
        var clock = Stopwatch.StartNew();
        Exception? last = null;

        while (clock.Elapsed < ClusterBudget) {
            if (File.Exists(KubeconfigPath)) {
                try {
                    var client = new k8s.Kubernetes(
                        await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(new FileInfo(KubeconfigPath))
                    );

                    _ = await client.CoreV1.ListNamespaceAsync(limit: 1, cancellationToken: cancellationToken);
                    return client;
                } catch (Exception notYet) when (notYet is not OperationCanceledException) {
                    last = notYet;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new InvalidOperationException($"The AppHost's k3s did not answer within {ClusterBudget}: {last?.Message}", last);
    }

    /// <summary>A pane in the gateway's process, as the silo sees one: what it was shown and whether it was told the end.</summary>
    sealed class RecordingPane : ITerminalViewer {
        readonly StringBuilder screen = new();

        public string Screen {
            get {
                lock (screen) {
                    return screen.ToString();
                }
            }
        }

        public string? Ended { get; private set; }

        public Task OutputAsync(byte[] data) {
            lock (screen) {
                screen.Append(Encoding.UTF8.GetString(data));
            }

            return Task.CompletedTask;
        }

        public Task EndedAsync(string reason) {
            Ended = reason;
            return Task.CompletedTask;
        }
    }
}
