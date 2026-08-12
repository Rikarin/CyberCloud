using CyberCloud.Core.Time;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CyberCloud.Vault.Tests.Infrastructure;

/// <summary>
///     A real OpenBao — docs/plan/18, the vault the platform reads.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Real, and not a stubbed handler, because every property this suite asserts belongs
///         to OpenBao rather than to us.</b> That a missing <c>kv-v2</c> path answers <c>404</c> with
///         an <i>empty</i> error list while a refused token answers <c>403</c> with
///         <c>["permission denied"]</c>; that a <c>?version=</c> which never existed is
///         indistinguishable from a path that never existed; that a revoked token starts being
///         refused immediately. Each of those is a belief the client is built on, and a stub would
///         assert the belief rather than check it.
///     </para>
///     <para>
///         ⚠ <b>Dev mode, in memory, unsealed, with a known root token — and none of that weakens
///         what is under test.</b> Dev mode changes how OpenBao stores and unseals, not how it
///         answers an HTTP request; the status codes, the JSON envelopes and the token lifecycle are
///         the production paths. What it does mean is that this suite proves nothing about
///         docs/plan/18 § Shape's Raft topology or its transit-engine auto-unseal, which are
///         deployment properties and belong to <c>deploy/</c>.
///     </para>
///     <para>
///         ⚠ <b>The root token is never what the client under test uses.</b> Every test goes through
///         a token minted by <see cref="IssueTokenAsync" /> against a real policy, so a read that
///         should be refused actually is — a suite driven on the root token would pass every
///         permission assertion by accident.
///     </para>
/// </remarks>
public sealed class OpenBaoFixture : IAsyncLifetime {
    /// <summary>
    ///     The OpenBao image, pinned.
    /// </summary>
    /// <remarks>
    ///     ⚠ Pinned to a digest-stable tag rather than <c>latest</c>, and to a 2.x release rather
    ///     than 1.x, because namespaces — docs/plan/18 § Shape's "namespace per tenant" — arrived in
    ///     the open-source fork at 2.3.1. A suite pinned below that would be testing a client against
    ///     a server that cannot do the thing the topology depends on.
    /// </remarks>
    public const string Image = "openbao/openbao:2.4.1";

    /// <summary>The dev-mode root token, used only to set the vault up.</summary>
    public const string RootToken = "cyber-cloud-test-root";

    /// <summary>The <c>kv-v2</c> mount every test reads through.</summary>
    public const string KvMount = "secret";

    IContainer container = null!;

    /// <summary>The base address, as <see cref="VaultOptions.Address" /> wants it.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>A client with no token on it, for building requests by hand.</summary>
    public HttpClient Raw { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        container = new ContainerBuilder(Image)
            .WithPortBinding(8200, true)
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", RootToken)
            .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
            // ⚠ Waits on the container's own health endpoint rather than on a log line. OpenBao
            // prints its banner before the listener is accepting, so a log-line strategy hands back
            // a container the first request fails against — which is the flake this suite would
            // otherwise contribute to the shared run.
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(x => x.ForPort(8200).ForPath("/v1/sys/health"))
            )
            .Build();

        await container.StartAsync(token);

        Address = $"http://{container.Hostname}:{container.GetMappedPublicPort(8200)}";
        Raw = new() { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Raw?.Dispose();

        if (container is not null) {
            await container.DisposeAsync();
        }
    }

    /// <summary>Writes a secret at a <c>kv-v2</c> path.</summary>
    /// <param name="path">The path, relative to the mount.</param>
    /// <param name="fields">The fields to write.</param>
    public Task WriteSecretAsync(string path, IReadOnlyDictionary<string, string> fields) =>
        RootAsync(HttpMethod.Post, $"/v1/{KvMount}/data/{path}", new { data = fields });

    /// <summary>Writes a policy.</summary>
    /// <param name="name">The policy name.</param>
    /// <param name="rules">The HCL body.</param>
    public Task WritePolicyAsync(string name, string rules) =>
        RootAsync(HttpMethod.Put, $"/v1/sys/policies/acl/{name}", new { policy = rules });

    /// <summary>
    ///     Mints a real OpenBao token carrying the named policies.
    /// </summary>
    /// <param name="policies">The policies to attach.</param>
    /// <param name="ttl">How long it lives.</param>
    /// <returns>The token and its accessor, so a test can revoke it.</returns>
    /// <remarks>
    ///     ⚠ <b>This is how the suite gets a real token without a Kubernetes cluster, and it is the
    ///     honest substitute rather than an equivalent one.</b> A token from
    ///     <c>auth/token/create</c> and a token from <c>auth/kubernetes/login</c> are the same thing
    ///     to every path this suite exercises — the same <c>X-Vault-Token</c>, the same policy
    ///     evaluation, the same revocation. What differs is how it was obtained, and that is exactly
    ///     the one step a container cannot reproduce: OpenBao's Kubernetes login calls the cluster's
    ///     <c>TokenReview</c> API unconditionally.
    /// </remarks>
    public async Task<(string Token, string Accessor)> IssueTokenAsync(string[] policies, string ttl = "1h") {
        using var document = await RootAsync(
            HttpMethod.Post,
            "/v1/auth/token/create",
            new { policies, ttl, no_parent = true, renewable = false }
        );

        var auth = document!.RootElement.GetProperty("auth");

        return (auth.GetProperty("client_token").GetString()!, auth.GetProperty("accessor").GetString()!);
    }

    /// <summary>Revokes a token by its accessor, the way an operator responding to a compromise would.</summary>
    /// <param name="accessor">The accessor from <see cref="IssueTokenAsync" />.</param>
    public Task RevokeAsync(string accessor) =>
        RootAsync(HttpMethod.Post, "/v1/auth/token/revoke-accessor", new { accessor });

    /// <summary>Enables the Kubernetes auth method, with no cluster behind it.</summary>
    /// <remarks>
    ///     ⚠ Enabled precisely so a login can be attempted and <i>fail the way it does in
    ///     production when the cluster is unreachable</i>. Nothing here can make it succeed.
    ///     <para>
    ///         ⚠ Idempotent, because the fixture is shared and a second enable answers <c>400</c>
    ///         with "path is already in use". The first version of this threw a fixture fault on
    ///         whichever test happened to run second, which is the ordering dependence a shared
    ///         container invites.
    ///     </para>
    /// </remarks>
    public async Task EnableKubernetesAuthAsync() {
        using var request = new HttpRequestMessage(HttpMethod.Post, Address + "/v1/sys/auth/kubernetes") {
            Content = JsonContent.Create(new { type = "kubernetes" }),
        };

        request.Headers.Add(VaultHeaders.Token, RootToken);

        using var response = await Raw.SendAsync(request, TestContext.Current.CancellationToken);

        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest) {
            return;
        }

        throw new InvalidOperationException(
            $"Enabling the kubernetes auth method answered {(int)response.StatusCode} — a fixture "
            + "fault rather than a test failure."
        );
    }

    /// <summary>Builds the options a test points at this container.</summary>
    /// <param name="role">The role name the token source would log in as.</param>
    public VaultOptions Options(string role = "cc-silo") =>
        new() {
            Address = Address,
            Role = role,
            KvMountPath = KvMount,
            // ⚠ A container has no certificate anybody trusts. VaultOptions' own remarks say why this
            // flag exists rather than an HttpMessageHandler that skips validation.
            AllowInsecureTransport = true,
            RequestTimeout = TimeSpan.FromSeconds(10),
        };

    /// <summary>A resolver pointed at this container, reading with a fixed token.</summary>
    /// <param name="token">The token to present.</param>
    /// <param name="options">The options, or this container's defaults.</param>
    /// <param name="logger">A logger to capture the operator half of a refusal.</param>
    public OpenBaoSecretResolver Resolver(
        string token,
        VaultOptions? options = null,
        Microsoft.Extensions.Logging.ILogger<OpenBaoSecretResolver>? logger = null
    ) {
        var effective = options ?? Options();

        return new(
            new() { Timeout = effective.RequestTimeout },
            new FixedTokenSource(token),
            effective,
            logger
        );
    }

    async Task<JsonDocument?> RootAsync(HttpMethod method, string path, object body) {
        using var request = new HttpRequestMessage(method, Address + path) {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add(VaultHeaders.Token, RootToken);

        using var response = await Raw.SendAsync(request, TestContext.Current.CancellationToken);

        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException(
                $"Setting up the vault failed: {method} {path} answered {(int)response.StatusCode} "
                + "— a fixture fault rather than a test failure."
            );
        }

        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);

        await using (stream) {
            return stream.Length == 0
                ? null
                : await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>
///     An <see cref="IVaultTokenSource" /> that hands out one token and counts how often it is asked.
/// </summary>
/// <remarks>
///     ⚠ The counters are what make the retry assertable. A resolve that hits a <c>403</c> must
///     invalidate once and ask once more — not loop — and the only way to see that from outside is to
///     count the asks.
/// </remarks>
/// <param name="token">The token to hand out.</param>
public sealed class FixedTokenSource(string token) : IVaultTokenSource {
    /// <summary>How many times a token was asked for.</summary>
    public int Asked { get; private set; }

    /// <summary>How many times a token was invalidated.</summary>
    public int Invalidated { get; private set; }

    /// <inheritdoc />
    public ValueTask<Result<VaultToken>> GetAsync(CancellationToken cancellationToken = default) {
        Asked++;

        return ValueTask.FromResult(Result<VaultToken>.Success(new(token, DateTimeOffset.MaxValue)));
    }

    /// <inheritdoc />
    public void Invalidate(VaultToken stale) => Invalidated++;
}

/// <summary>An <see cref="IClock" /> a test moves by hand.</summary>
/// <param name="now">Where it starts.</param>
public sealed class MovableClock(DateTimeOffset now) : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Binds <see cref="OpenBaoFixture" /> to the classes that share it.</summary>
/// <remarks>
///     ⚠ One container for the whole suite. Starting one per class is where a container-backed suite
///     starts losing host ports under a parallel run — build/README.md § failed to bind host port.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class OpenBaoSuite : ICollectionFixture<OpenBaoFixture> {
    /// <summary>The collection name.</summary>
    public const string Name = "openbao";
}
