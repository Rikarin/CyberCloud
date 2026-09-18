using CyberCloud.Agent.Host;
using CyberCloud.Core;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.Kubernetes.Tunnel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.Hosts.Tests;

/// <summary>
///     The agent host against a real WebSocket: <see cref="AgentService" /> started as production
///     starts it, dialling a Kestrel endpoint this test hosts, over
///     <c>WebSocketTunnelTransport</c> at both ends.
/// </summary>
/// <remarks>
///     <para>
///         What is real: the host's composition, its service, its dial, its credential fallback, the
///         WebSocket carrier on both sides, the agent's dispatcher and heartbeat, and the platform's
///         multiplexer. What is substituted: the gateway's pipeline and the tunnel grain — the
///         endpoint here admits by comparing the bearer value against what it expects and speaks the
///         platform side of the protocol directly, so this is the WebSocket half of what
///         <c>charts/agent/conformance.yaml § owed</c>'s <c>the-gateway-endpoint-is-not-driven-over-http</c>
///         still owes — the gateway's pipeline and the tunnel grain behind a real socket — and
///         not the NAT half of <c>no-suite-crosses-a-real-nat</c>.
///     </para>
/// </remarks>
public sealed class AgentServiceTests {
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAgentEnrollsWithTheTokenStoresTheCredentialAnswersRequestsAndRedialsWithTheCredential() {
        var clusterId = Guid.NewGuid();
        var enrollment = AgentCredentials.MintEnrollment();
        var credential = AgentCredentials.MintCredential();

        await using var platform = new FakePlatform(clusterId, enrollment.Plaintext, credential.Plaintext);
        await platform.StartAsync();

        var tokenFile = Path.Combine(Path.GetTempPath(), "cc-agent-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, enrollment.Plaintext + "\n", Ct);

        var endpoints = new FakeEndpoints();

        using var agent = AgentComposition.Build(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{AgentOptions.SectionName}:TunnelEndpoint={platform.TunnelUrl}",
                $"--{AgentOptions.SectionName}:ClusterId={clusterId:D}",
                $"--{AgentOptions.SectionName}:EnrollmentTokenFile={tokenFile}",
                $"--{AgentOptions.SectionName}:HeartbeatSeconds=1",
                $"--{AgentOptions.SectionName}:MaxReconnectSeconds=1"
            ],
            services => services.AddSingleton<IAgentEndpoints>(endpoints)
        );

        try {
            await agent.StartAsync(Ct);

            // ── The first dial: the enrollment token, and the credential comes back and is stored ──
            var first = await platform.NextSessionAsync();
            first.Bearer.ShouldBe(enrollment.Plaintext);
            first.ClusterHeader.ShouldBe(clusterId.ToString("D"));
            first.AgentVersion.ShouldNotBeEmpty();

            await endpoints.Credentials.StoredAsync();
            endpoints.Credentials.Value.ShouldBe(credential.Plaintext);

            // A request down the socket comes back answered by the agent's client.
            var ping = await first.Exchange.ExchangeAsync(TunnelFrame.Request(0, TunnelOperations.Ping, "{}"), Ct);
            ping.IsSuccess.ShouldBeTrue(ping.Error?.Message);
            TunnelOperations.Open<TunnelOperations.PingAnswer>(ping.GetValueOrThrow().Payload)
                .GetValueOrThrow()
                .Version.ShouldBe("v1.35.0-fake");

            // And a heartbeat arrives without being asked for.
            await first.FirstHeartbeatAsync();

            // ── The platform drops the socket: the agent redials with the CREDENTIAL, not the token ──
            await first.CloseAsync();

            var second = await platform.NextSessionAsync();
            second.Bearer.ShouldBe(credential.Plaintext);

            var again = await second.Exchange.ExchangeAsync(TunnelFrame.Request(0, TunnelOperations.Ping, "{}"), Ct);
            again.IsSuccess.ShouldBeTrue(again.Error?.Message);
        } finally {
            await agent.StopAsync(Ct);
            File.Delete(tokenFile);
        }
    }

    [Fact]
    public async Task ACredentialTheSecretRefusesIsPresentedFromMemoryAndStoredWhenTheWriteRecovers() {
        // ⚠ THE BRICKED-ENROLLMENT PATH. The welcome that carries the credential is the platform
        // saying the token is spent. A Secret write that fails at that moment — a 403 from the
        // Role, a transient API server error — used to escape the session, end the process, and
        // restart a pod holding neither a credential nor a working token. What is pinned: the
        // session survives the failed write, the redial presents the credential from memory and
        // not the spent token, and the write is retried until the Secret holds it.
        var clusterId = Guid.NewGuid();
        var enrollment = AgentCredentials.MintEnrollment();
        var credential = AgentCredentials.MintCredential();

        await using var platform = new FakePlatform(clusterId, enrollment.Plaintext, credential.Plaintext);
        await platform.StartAsync();

        var tokenFile = Path.Combine(Path.GetTempPath(), "cc-agent-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, enrollment.Plaintext, Ct);

        var endpoints = new FakeEndpoints();
        endpoints.Credentials.RefuseWrites(2);

        using var agent = AgentComposition.Build(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{AgentOptions.SectionName}:TunnelEndpoint={platform.TunnelUrl}",
                $"--{AgentOptions.SectionName}:ClusterId={clusterId:D}",
                $"--{AgentOptions.SectionName}:EnrollmentTokenFile={tokenFile}",
                $"--{AgentOptions.SectionName}:HeartbeatSeconds=1",
                $"--{AgentOptions.SectionName}:MaxReconnectSeconds=1",
                $"--{AgentOptions.SectionName}:CredentialStoreRetrySeconds=1"
            ],
            services => services.AddSingleton<IAgentEndpoints>(endpoints)
        );

        try {
            await agent.StartAsync(Ct);

            // The first dial enrolls; the Secret write is refused; the session is still up and
            // answering — the process did not die on the write.
            var first = await platform.NextSessionAsync();
            first.Bearer.ShouldBe(enrollment.Plaintext);
            await endpoints.Credentials.RefusedAsync(1);
            endpoints.Credentials.Value.ShouldBeNull("the first write was refused");

            var ping = await first.Exchange.ExchangeAsync(TunnelFrame.Request(0, TunnelOperations.Ping, "{}"), Ct);
            ping.IsSuccess.ShouldBeTrue(ping.Error?.Message);

            // The platform drops the socket before the write has recovered: the redial presents the
            // credential — from memory — and NOT the token, which is spent.
            await first.CloseAsync();

            var second = await platform.NextSessionAsync();
            second.Bearer.ShouldBe(credential.Plaintext);

            // And the retry lands once the store stops refusing.
            await endpoints.Credentials.StoredAsync();
            endpoints.Credentials.Value.ShouldBe(credential.Plaintext);
        } finally {
            await agent.StopAsync(Ct);
            File.Delete(tokenFile);
        }
    }

    [Fact]
    public async Task ARefusedAgentKeepsDiallingRatherThanExiting() {
        var clusterId = Guid.NewGuid();

        await using var platform =
            new FakePlatform(clusterId, "cca-enroll-somebody-else", "");
        await platform.StartAsync();

        var tokenFile = Path.Combine(Path.GetTempPath(), "cc-agent-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(tokenFile, AgentCredentials.MintEnrollment().Plaintext, Ct);

        using var agent = AgentComposition.Build(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{AgentOptions.SectionName}:TunnelEndpoint={platform.TunnelUrl}",
                $"--{AgentOptions.SectionName}:ClusterId={clusterId:D}",
                $"--{AgentOptions.SectionName}:EnrollmentTokenFile={tokenFile}",
                $"--{AgentOptions.SectionName}:MaxReconnectSeconds=1"
            ],
            static services => services.AddSingleton<IAgentEndpoints>(new FakeEndpoints())
        );

        try {
            await agent.StartAsync(Ct);

            // Two refusals in a row: the loop backed off and came back rather than dying on the 401.
            await platform.RefusalsAsync(2);
        } finally {
            await agent.StopAsync(Ct);
            File.Delete(tokenFile);
        }
    }

    // ── The platform, played by a Kestrel endpoint ─────────────────────────────────────────────

    /// <summary>One admitted socket, with the platform-side multiplexer on it.</summary>
    sealed class Session : IAsyncDisposable {
        readonly TaskCompletionSource heartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public required string Bearer { get; init; }

        public required string ClusterHeader { get; init; }

        public required string AgentVersion { get; init; }

        public required WebSocketTunnelTransport Transport { get; init; }

        public required TunnelExchange Exchange { get; init; }

        public Task FirstHeartbeatAsync() => heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        public Task Ended => ended.Task;

        public async Task RunAsync() {
            await TunnelPump.RunAsync(
                Transport,
                frame => {
                    if (frame.Kind == TunnelFrameKind.Response) {
                        Exchange.Complete(frame);
                    } else if (frame.Kind == TunnelFrameKind.Heartbeat) {
                        heartbeat.TrySetResult();
                    }

                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

            Exchange.FailAll("the socket closed");
            ended.TrySetResult();
        }

        public async Task CloseAsync() {
            await Transport.CloseAsync("the platform is closing this session", Ct);
            await Ended.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        }

        public async ValueTask DisposeAsync() {
            Exchange.Dispose();
            await Transport.DisposeAsync();
        }
    }

    sealed class FakePlatform(Guid clusterId, string expectedEnrollment, string credential) : IAsyncDisposable {
        readonly ConcurrentQueue<Session> sessions = new();
        readonly SemaphoreSlim arrived = new(0);
        readonly SemaphoreSlim refused = new(0);
        WebApplication? app;

        public string TunnelUrl { get; private set; } = string.Empty;

        public async Task StartAsync() {
            var builder = WebApplication.CreateBuilder(["--urls", "http://127.0.0.1:0"]);
            builder.Logging.ClearProviders();

            app = builder.Build();
            app.UseWebSockets();

            app.Map(
                TunnelCodec.TunnelPath,
                async (HttpContext http) => {
                    var bearer = http.Request.Headers.Authorization.ToString()
                        .Replace("Bearer ", "", StringComparison.Ordinal);
                    var isEnrollment = AgentCredentials.IsEnrollment(bearer);

                    var admitted = http.Request.Headers[TunnelCodec.ClusterHeader].ToString() == clusterId.ToString("D")
                        && (isEnrollment ? bearer == expectedEnrollment : bearer == credential);

                    if (!http.WebSockets.IsWebSocketRequest || !admitted) {
                        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        refused.Release();
                        return;
                    }

                    using var socket = await http.WebSockets.AcceptWebSocketAsync(TunnelCodec.SubProtocol);
                    var transport = new WebSocketTunnelTransport(socket);

                    var session = new Session {
                        Bearer = bearer,
                        ClusterHeader = http.Request.Headers[TunnelCodec.ClusterHeader].ToString(),
                        AgentVersion = http.Request.Headers[TunnelCodec.AgentVersionHeader].ToString(),
                        Transport = transport,
                        Exchange = new(transport.SendAsync, TimeSpan.FromSeconds(10))
                    };

                    await transport.SendAsync(
                        new() {
                            Kind = TunnelFrameKind.Welcome,
                            Payload = TunnelCodec.Serialize(
                                new WelcomeBody {
                                    SessionId = Guid.NewGuid(),
                                    HeartbeatSeconds = 1,
                                    Credential = isEnrollment ? credential : null
                                }
                            )
                        },
                        http.RequestAborted
                    );

                    sessions.Enqueue(session);
                    arrived.Release();

                    await session.RunAsync();
                }
            );

            await app.StartAsync(Ct);

            var address = app.Urls.First();
            TunnelUrl = address.Replace("http://", "ws://", StringComparison.Ordinal) + TunnelCodec.TunnelPath;
        }

        public async Task<Session> NextSessionAsync() {
            await arrived.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            sessions.TryDequeue(out var session).ShouldBeTrue();
            return session!;
        }

        public async Task RefusalsAsync(int count) {
            for (var i = 0; i < count; i++) {
                (await refused.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue(
                    $"refusal {i + 1} of {count} never came"
                );
            }
        }

        public async ValueTask DisposeAsync() {
            foreach (var session in sessions) {
                await session.DisposeAsync();
            }

            if (app is not null) {
                await app.StopAsync(CancellationToken.None);
                await app.DisposeAsync();
            }

            arrived.Dispose();
            refused.Dispose();
        }
    }

    // ── The pod, played by two fakes ───────────────────────────────────────────────────────────

    sealed class FakeEndpoints : IAgentEndpoints {
        public IKubeApiClient Api { get; } = new PingOnlyClient();

        public InMemoryCredentialStore Credentials { get; } = new();

        IAgentCredentialStore IAgentEndpoints.Credentials => Credentials;
    }

    sealed class InMemoryCredentialStore : IAgentCredentialStore {
        readonly TaskCompletionSource stored = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refusalsLeft;
        int refusals;

        public string? Value { get; private set; }

        public Task StoredAsync() => stored.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        /// <summary>Makes the next <paramref name="count" /> writes throw — the Role refusing the Secret.</summary>
        public void RefuseWrites(int count) => refusalsLeft = count;

        public async Task RefusedAsync(int count) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

            while (Volatile.Read(ref refusals) < count) {
                (DateTime.UtcNow < deadline).ShouldBeTrue($"refused write {count} never came");
                await Task.Delay(20, Ct);
            }
        }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);

        public Task WriteAsync(string credential, CancellationToken cancellationToken = default) {
            if (refusalsLeft > 0) {
                refusalsLeft--;
                Interlocked.Increment(ref refusals);
                throw new InvalidOperationException(
                    """secrets "cybercloud-agent-credential" is forbidden: the Role does not grant it"""
                );
            }

            Value = credential;
            stored.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>An API server that answers a ping and nothing else — the one call this suite makes.</summary>
    sealed class PingOnlyClient : IKubeApiClient {
        public Task<Result<string>> PingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string>.Success("v1.35.0-fake"));

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, "not here"));

        public Task<Result<ApplyOutcome>> ApplyAsync(
            KubeCommand command,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result<ApplyOutcome>.Failure(ErrorCode.InternalError, "not scripted"));

        public Task<Result> DeleteAsync(
            ObjectRef target,
            CascadePolicy policy,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result.Failure(ErrorCode.InternalError, "not scripted"));

        public Task<Result> SetOwnerAsync(
            ObjectRef target,
            OwnerRef? owner,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result.Failure(ErrorCode.InternalError, "not scripted"));

        public Task<Result<IReadOnlyList<GroupVersionKind>>> DiscoverNamespacedKindsAsync(
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result<IReadOnlyList<GroupVersionKind>>.Failure(ErrorCode.InternalError, "not scripted"));

        public Task<Result<ListPage>> ListAsync(
            GroupVersionKind kind,
            string ns,
            string labelSelector,
            string? resourceVersion = null,
            string? continueToken = null,
            int? limit = null,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(Result<ListPage>.Failure(ErrorCode.InternalError, "not scripted"));

        public async IAsyncEnumerable<KubeWatchEvent> WatchAsync(
            GroupVersionKind kind,
            string ns,
            string labelSelector,
            string resourceVersion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        ) {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose() { }
    }
}
