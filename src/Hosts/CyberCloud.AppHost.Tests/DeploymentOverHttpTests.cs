using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     A deployment over HTTP, through the nine stages, against two silo processes and a real k3s —
///     docs/plan/08 § Long-running operations, "Nested operations", as a tenant performs it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one place the new grain calls cross a process.</b> A child operation tells its
///         parent it has ended through a one-way call (<c>IOperationGrain.NotifyChildTerminalAsync</c>),
///         and the two may live on different silo processes; the gateway reads the parent's status, with
///         its new <c>parentOperationId</c> and its children, across the client boundary; and the what-if
///         runs in the gateway's process, reading resources on the silos. In-process
///         <c>TestCluster</c>s share one type manifest and hid exactly this class of omission once
///         already — <c>17313ed</c>, the shard map's confirmation — so these calls are exercised here
///         as well as there.
///     </para>
///     <para>
///         ⚠ <b>Nothing here drives an operation.</b> Every pass is a Redis reminder firing on
///         whichever silo process Orleans placed the grain on, and every child after the first is
///         written because the child before it told its parent it had ended — so a green run is also
///         the notification crossing a silo, or the reminder standing in for it.
///     </para>
///     <para>
///         ⚠ <b>The same arrangement as <see cref="TenantOverHttpTests" />, with its own tenant, and two
///         principals.</b> The owner deploys a two-widget template with a <c>dependsOn</c> and asks what a
///         second deployment would change. A service principal that is a contributor on one group only
///         — granted over HTTP, by the owner, through the role assignment route — deploys a template
///         whose second widget names a group it holds nothing on, and the deployment fails naming it.
///     </para>
/// </remarks>
/// <param name="topology">The running AppHost — two silo processes, Redis, PostgreSQL, k3s.</param>
[Collection(LocalTopologySuite.Name)]
public sealed class DeploymentOverHttpTests(LocalTopology topology) : IAsyncLifetime {
    /// <summary>
    ///     Two widgets in sequence, each a reminder period or so apart, plus the parent's own first pass.
    /// </summary>
    static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(8);

    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0030");
    static readonly Guid Subscription = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0031");
    static readonly Guid Cluster = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0032");

    /// <summary>The tenant's owner, who deploys the template that succeeds.</summary>
    static readonly Guid Owner = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0033");

    /// <summary>A contributor on <see cref="Home" /> only, who deploys the template that does not.</summary>
    static readonly Guid Contributor = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0034");

    const string Home = "deploy-app";
    const string Sealed = "deploy-sealed";
    const string Slug = "deployments-over-http";
    const string Version = "?api-version=" + Deployments.V2026;

    static readonly SecretRef OwnerSecretRef = new() { Path = "tenants/deployments-over-http/sp/owner", Field = "secret" };
    static readonly SecretRef ContributorSecretRef = new() { Path = "tenants/deployments-over-http/sp/contributor", Field = "secret" };

    const string OwnerSecret = "deployments-over-http-owner-secret-51c2";
    const string ContributorSecret = "deployments-over-http-contributor-secret-8d0e";

    WebApplication identity = null!;
    WebApplication gateway = null!;
    HttpClient http = null!;
    string ownerToken = null!;
    string contributorToken = null!;

    static ResourceId Widget(string name, string group) =>
        new(Tenant, Subscription, group, SampleWidgets.Type, name, Guid.Empty);

    static ResourceId Deployment(string name) =>
        new(Tenant, Subscription, Home, Deployments.Type, name, Guid.Empty);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var cancellationToken = TestContext.Current.CancellationToken;

        identity = await IdentityComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture),
                $"--{IdentityHostOptions.SectionName}:TenantId=" + Tenant.ToString("D", CultureInfo.InvariantCulture)
            ],
            static services => services.AddSingleton<IClientSecretSeam>(
                new TwoClientSecrets(
                    new(StringComparer.Ordinal) {
                        [OwnerSecretRef.Path] = OwnerSecret, [ContributorSecretRef.Path] = ContributorSecret
                    }
                )
            )
        );

        identity.MapIdentityHost();
        await identity.StartAsync(cancellationToken);

        gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture),
                "--CyberCloud:Gateway:Identity:Issuer=" + identity.Urls.First()
            ]
        );

        gateway.MapGateway();
        await gateway.StartAsync(cancellationToken);

        await BootstrapTenantAsync();
        await CreateServicePrincipalAsync(Owner, "the deployments owner", OwnerSecretRef);
        await CreateServicePrincipalAsync(Contributor, "a contributor on one group", ContributorSecretRef);
        await AttachClusterAsync();

        ownerToken = await TakeTokenAsync(Owner, OwnerSecret, cancellationToken);
        contributorToken = await TakeTokenAsync(Contributor, ContributorSecret, cancellationToken);

        http = new() { BaseAddress = new(gateway.Urls.First()) };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        http?.Dispose();

        if (gateway is not null) {
            await gateway.StopAsync(CancellationToken.None);
            await gateway.DisposeAsync();
        }

        if (identity is not null) {
            await identity.StopAsync(CancellationToken.None);
            await identity.DisposeAsync();
        }
    }

    [Fact]
    public async Task ATemplateDeploysInDependencyOrderAsItsCallerAndAChildTheCallerMayNotWriteFailsItNamingThatChild() {
        var cancellationToken = TestContext.Current.CancellationToken;

        // ── The scopes, and the contributor's one grant, all over HTTP as the owner ─────────────────
        (await SendAsync(HttpMethod.Put, ScopeId.Subscription(Tenant, Subscription).Path, """{"displayName":"deployments"}""", ownerToken, cancellationToken))
            .Status.ShouldBe(HttpStatusCode.Created);

        foreach (var group in (string[]) [Home, Sealed]) {
            (await SendAsync(HttpMethod.Put, ScopeId.Group(Tenant, Subscription, group).Path, """{"location":"eu-central"}""", ownerToken, cancellationToken))
                .Status.ShouldBe(HttpStatusCode.Created);
        }

        var grant = RoleAssignmentId.OnScope(
            ScopeId.Group(Tenant, Subscription, Home),
            new("contributor", SubjectTypes.ServicePrincipal, Contributor.ToString("N", CultureInfo.InvariantCulture))
        );

        var granted = await SendAsync(HttpMethod.Put, grant.Path, "{}", ownerToken, cancellationToken);
        granted.Status.ShouldBe(HttpStatusCode.Created, "the owner could not grant contributor on one group: " + granted.Body);

        // ── 1. The owner deploys two widgets, the one that depends on the other listed first ────────
        var rollout = Deployment("rollout");
        var accepted = await SendAsync(HttpMethod.Put, rollout.Path, Body(Template(Home)), ownerToken, cancellationToken);

        accepted.Status.ShouldBe(HttpStatusCode.Accepted, "the deployment's PUT was refused: " + accepted.Body);

        var terminal = await ConvergeAsync(OperationIdFrom(accepted), ownerToken, cancellationToken);
        var status = Json(terminal);

        status.GetProperty("status").GetString().ShouldBe("Succeeded", "the deployment did not succeed: " + terminal);

        var record = await SendAsync(HttpMethod.Get, rollout.Path, null, ownerToken, cancellationToken);
        record.Status.ShouldBe(HttpStatusCode.OK, record.Body);

        var properties = Json(record.Body).GetProperty("properties");

        // ⚠ The order is the dependency order, not the template's: the back end first.
        properties.GetProperty("outputResources").EnumerateArray().Select(static x => x.GetString()).ShouldBe(
            [Widget("back", Home).Path, Widget("front", Home).Path],
            "the history names the resources out of dependency order: " + record.Body
        );

        foreach (var name in (string[]) ["back", "front"]) {
            var widget = await SendAsync(HttpMethod.Get, Widget(name, Home).Path, null, ownerToken, cancellationToken);
            widget.Status.ShouldBe(HttpStatusCode.OK, widget.Body);
            Json(widget.Body).GetProperty("provisioningState").GetString().ShouldBe("Succeeded");
        }

        // ── 2. What would deploying it again change? Nothing — read over HTTP, as the owner ─────────
        var whatIf = await SendAsync(HttpMethod.Post, rollout.Path + "/whatIf", Body(Template(Home)), ownerToken, cancellationToken);
        whatIf.Status.ShouldBe(HttpStatusCode.OK, "the what-if was refused: " + whatIf.Body);

        Json(whatIf.Body).GetProperty("changes").EnumerateArray()
            .Select(static x => x.GetProperty("changeType").GetString())
            .ShouldBe([WhatIfChangeTypes.NoChange, WhatIfChangeTypes.NoChange], whatIf.Body);

        // ── 3. The contributor deploys a template whose second widget is in a group they hold nothing on
        var cross = Deployment("cross-group");
        var theirs = await SendAsync(HttpMethod.Put, cross.Path, Body(Template(Sealed, "c-")), contributorToken, cancellationToken);

        theirs.Status.ShouldBe(HttpStatusCode.Accepted, "the contributor's deployment PUT was refused at its own group: " + theirs.Body);

        var failed = Json(await ConvergeAsync(OperationIdFrom(theirs), contributorToken, cancellationToken));

        failed.GetProperty("status").GetString().ShouldBe("Failed", failed.GetRawText());

        var refused = Widget("c-back", Sealed).Path;
        var message = failed.GetProperty("error").GetProperty("message").GetString()!;
        message.ShouldContain(refused, Case.Sensitive, "the failure does not name the refused child.");
        message.ShouldContain("ResourceNotFound");

        // Nothing was created in the group the contributor may not write — as the owner can see.
        (await SendAsync(HttpMethod.Get, refused, null, ownerToken, cancellationToken)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, Widget("c-front", Home).Path, null, ownerToken, cancellationToken)).Status.ShouldBe(
            HttpStatusCode.NotFound,
            "the front end depends on the refused back end and was written anyway."
        );
    }

    /// <summary>
    ///     Two widgets: <c>front</c> in <see cref="Home" /> depending on <c>back</c>, which is placed in
    ///     <paramref name="backGroup" /> and listed second.
    /// </summary>
    static string Template(string backGroup, string prefix = "") {
        var back = Resource(prefix + "back");

        if (!string.Equals(backGroup, Home, StringComparison.Ordinal)) {
            back["resourceGroup"] = backGroup;
        }

        var front = Resource(prefix + "front");
        front["dependsOn"] = new JsonArray($"[resourceId('{backGroup}', '{SampleWidgets.Type}', '{prefix}back')]");

        return new JsonObject { ["resources"] = new JsonArray(front, back) }.ToJsonString();
    }

    static JsonObject Resource(string name) =>
        new() {
            ["type"] = SampleWidgets.Type.ToString(),
            ["apiVersion"] = SampleWidgets.V2026,
            ["name"] = name,
            ["location"] = "eu-central",
            ["properties"] = JsonNode.Parse(SampleWidgets.Body(Cluster, "deployed over http"))!["properties"]!.DeepClone()
        };

    static string Body(string template) =>
        new JsonObject { ["properties"] = new JsonObject { ["template"] = template } }.ToJsonString();

    async Task<string> ConvergeAsync(Guid operationId, string token, CancellationToken cancellationToken) {
        var clock = Stopwatch.StartNew();
        var last = "";

        while (clock.Elapsed < ConvergenceBudget) {
            var poll = await SendAsync(
                HttpMethod.Get,
                "/operations/" + operationId.ToString("D", CultureInfo.InvariantCulture),
                null,
                token,
                cancellationToken
            );

            poll.Status.ShouldBe(HttpStatusCode.OK, $"operation {operationId:D} became unreadable: " + poll.Body);
            last = poll.Body;

            if (Json(last).GetProperty("status").GetString() is "Succeeded" or "Failed" or "Canceled") {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"deployment operation {operationId:D} ended after {clock.Elapsed.TotalSeconds:F0} s: {last}"
                );

                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        return last;
    }

    readonly record struct Answer(HttpStatusCode Status, System.Net.Http.Headers.HttpResponseHeaders Headers, string Body);

    async Task<Answer> SendAsync(HttpMethod method, string path, string? body, string token, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(method, new Uri(path + Version, UriKind.Relative));
        request.Headers.Authorization = new("Bearer", token);

        if (body is not null) {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);

        return new(response.StatusCode, response.Headers, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    static Guid OperationIdFrom(Answer accepted) {
        accepted.Headers.TryGetValues("Azure-AsyncOperation", out var values).ShouldBeTrue("a 202 without Azure-AsyncOperation.");
        var last = values!.First().Split('?')[0].Split('/')[^1];

        return Guid.Parse(last, CultureInfo.InvariantCulture);
    }

    // ── Seeding — the shape TenantOverHttpTests' remarks explain, for this file's own tenant ─────────

    async Task BootstrapTenantAsync() {
        var tenant = topology.Client.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture));

        var created = await tenant.GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant)).CreateAsync(Slug, "Deployments over HTTP", "eu-central");
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var tuple = RelationTuple.Create(
            AuthObjectRef.Create(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow(),
            Relations.Owner,
            SubjectRef.Create(SubjectTypes.ServicePrincipal, Owner.ToString("N", CultureInfo.InvariantCulture)).GetValueOrThrow()
        )
            .GetValueOrThrow();

        (await tenant.GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant)).WriteAsync(tuple)).IsSuccess.ShouldBeTrue();

        (await topology.Client
                .GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
                .RegisterAsync(
                    new() { TenantId = Tenant, Slug = Slug, HomeRegion = "eu-central", Status = TenantStatus.Active }
                ))
            .IsSuccess.ShouldBeTrue();
    }

    async Task CreateServicePrincipalAsync(Guid id, string name, SecretRef credential) {
        var created = await topology.Client
            .ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(id))
            .CreateAsync(new() { DisplayName = name, Enabled = true, CredentialSecretRef = credential });

        created.IsSuccess.ShouldBeTrue($"{name}: {created.Error?.Code} — {created.Error?.Message}");
    }

    async Task AttachClusterAsync() {
        var attached = await topology.Client
            .GetGrain<IClusterConnectionGrain>(GrainKeys.ClusterConnection(Cluster))
            .AttachAsync(
                new() {
                    ClusterId = Cluster,
                    OwningTenantId = Tenant,
                    Kind = ClusterConnectionKind.Kubeconfig,
                    CredentialRef = new Uri(Path.Combine(TestPaths.AppHostDirectory, ".k3s", "kubeconfig.yaml")).AbsoluteUri,
                    Endpoint = $"https://127.0.0.1:{CyberCloudResources.K3sApiPort}",
                    DisplayName = "the AppHost's k3s, for deployments"
                }
            );

        attached.IsSuccess.ShouldBeTrue($"{attached.Error?.Code} — {attached.Error?.Message}");
    }

    async Task<string> TakeTokenAsync(Guid principal, string secret, CancellationToken cancellationToken) {
        using var client = new HttpClient { BaseAddress = new(identity.Urls.First()) };

        using var response = await client.PostAsync(
            new Uri(IdentityHostOpenIddict.TokenPath, UriKind.Relative),
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = principal.ToString("N", CultureInfo.InvariantCulture),
                    ["client_secret"] = secret,
                    ["scope"] = IdentityHostOpenIddict.Scopes.Api
                }
            ),
            cancellationToken
        );

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the identity host refused the grant: " + body);

        return Json(body).GetProperty("access_token").GetString()!;
    }

    /// <summary>
    ///     The vault seam a deployment registers over OpenBao, answering for exactly the two handles
    ///     this file creates — <c>TenantOverHttpTests.TestClientSecrets</c>' shape, for two principals.
    /// </summary>
    sealed class TwoClientSecrets(Dictionary<string, string> secrets) : IClientSecretSeam {
        public Task<Result<bool>> VerifyAsync(SecretRef reference, string presented, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<bool>.Success(
                    secrets.TryGetValue(reference.Path, out var secret)
                    && string.Equals(reference.Field, "secret", StringComparison.Ordinal)
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(secret))
                )
            );
    }
}
