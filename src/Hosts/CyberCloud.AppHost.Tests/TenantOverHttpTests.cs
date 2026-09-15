using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Host;
using CyberCloud.Kubernetes.Contracts;
using CyberCloud.Providers.Sample.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthObjectRef = CyberCloud.Authorization.Contracts.ObjectRef;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     docs/plan/24 § Phase 1's exit story as a tenant performs it — over HTTP, through the nine
///     stages, against the real resource manager.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE JOIN NOTHING COVERED.</b> <c>CyberCloud.Gateway.Host.Tests</c> runs the nine
///         stages against a <c>DefaultHttpContext</c> with a <b>substituted</b>
///         <c>IResourceManager</c>; <c>CyberCloud.ResourceManager.Tests</c>,
///         <c>test/CyberCloud.Isolation</c> and <see cref="ReconcileThroughTheRealHostTests" /> drive
///         the <b>real</b> manager, real grains and the real authorizer, by resolving it out of a
///         container and calling it. The two halves met at an interface and neither covered the
///         join, so every route added since inherited a proof about its routing and none about its
///         behaviour. This file is the join: an <c>HttpClient</c> at one end, the real
///         <c>ResourceManagerService</c> and a real k3s at the other, and nothing substituted in
///         between.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The token comes from the real identity host, and the gateway validates it the way
///             the deployed one does.
///         </b> This file starts <c>IdentityComposition.BuildAsync</c> — the object graph
///         <c>CyberCloud.Identity.Host</c>'s own <c>Program.cs</c> builds — beside the gateway, and
///         tells the gateway to trust it through the same <c>CyberCloud:Gateway:Identity:Issuer</c>
///         key a deployment sets. A service principal takes a client-credentials token from
///         <c>/token</c>; the gateway fetches the discovery document and the key set from
///         <c>/.well-known/*</c>, checks the signature, the issuer, the audience and the expiry, and
///         reads <c>tid</c> and <c>sub_typ</c> off what survived. Nothing in the identity seam is
///         substituted, and https://github.com/Rikarin/CyberCloud/issues/68 — a deployed gateway
///         with no resolver at all, invisible to a suite that supplied one through <c>configure</c>
///         — is what this arrangement closes. The one thing supplied here that a deployment
///         supplies differently is the vault: <see cref="TestClientSecrets" /> is the
///         <c>IClientSecretSeam</c> a deployment registers over OpenBao, and it verifies exactly one
///         secret.
///     </para>
///     <para>
///         ⚠ <b>Why a service principal and not a user.</b> Client credentials is the one grant in
///         docs/plan/11 § Protocol's table that completes with no browser, no cookie and no page —
///         which makes it the one a test can drive over HTTP and the one the identity host serves
///         first. <c>TokenApi</c>'s remarks say what each of the other grants is waiting on. The
///         subject type therefore rides the whole path as <c>servicePrincipal</c>: the tenant's
///         owner tuple names one, the token's <c>sub_typ</c> says so, and the resource manager's
///         checks resolve against it — which is the property <c>AccessTokenClaims.SubjectType</c>
///         exists for, exercised end to end for a subject that is not a user.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The request path itself had to become callable, and that is a production fix rather
///             than a test affordance.
///         </b> The one <c>app.Use</c> that runs the pipeline lived in
///         <c>Program.cs</c>, whose own header explains that top-level statements cannot be called
///         from a test — an argument it made about composition while holding the request path. It is
///         <c>GatewayComposition.MapGateway</c> now, and <c>Program.cs</c> calls it.
///     </para>
///     <para>
///         ⚠ <b>Its own tenant, subscription, resource group, widget and cluster.</b> Nothing here is
///         shared with <see cref="ReconcileThroughTheRealHostTests" />, which runs in the same
///         collection against the same topology. A shared subscription would make each class's
///         subject depend on which ran first — the failure this repository has already shipped once,
///         where a claim assertion listed a shared namespace and saw five sibling resources.
///     </para>
///     <para>
///         ⚠ <b>What is still supplied by this file rather than by the platform.</b> The service
///         principal is created by reaching for <c>IServicePrincipalGrain</c> directly, because
///         nothing over HTTP creates one yet; and its secret is verified by
///         <see cref="TestClientSecrets" /> rather than by a vault, because no host wires one.
///         Both are stated so they can be paid rather than forgotten. Neither is the identity seam:
///         the token is minted, signed, published and validated by the two real hosts.
///     </para>
///     <para>
///         ⚠ <b>One test, not a sweep.</b> Every additional case here costs a topology cycle and
///         a five-minute convergence budget, and the sweep already exists one layer down:
///         <c>CyberCloud.Gateway.Host.Tests</c> covers the stages exhaustively and cheaply. What only
///         this file can say is that the two layers are joined, and it says it once, for the story
///         docs/plan/24 § Phase 1 is defined by.
///     </para>
/// </remarks>
/// <param name="topology">The running AppHost — two silo processes, Redis, PostgreSQL, k3s.</param>
[Collection(LocalTopologySuite.Name)]
public sealed class TenantOverHttpTests(LocalTopology topology) : IAsyncLifetime {
    /// <summary>How long the widget is given to converge. Same reasoning as the sibling suite's.</summary>
    static readonly TimeSpan ConvergenceBudget = TimeSpan.FromMinutes(5);

    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0020");
    static readonly Guid Subscription = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0021");
    static readonly Guid Cluster = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0022");

    /// <summary>
    ///     The service principal the story is performed as. Its id is its <c>client_id</c> —
    ///     <c>TokenApi</c>'s remarks say why — and the ReBAC subject the tenant's owner tuple names.
    /// </summary>
    static readonly Guid ServicePrincipal = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0023");

    const string ResourceGroup = "over-http";
    const string Widget = "http-widget";
    const string Slug = "phase-1-over-http";

    /// <summary>Where the principal's secret would live in a vault; the handle its descriptor carries.</summary>
    static readonly SecretRef CredentialRef = new() { Path = "tenants/phase-1-over-http/sp/ci", Field = "secret" };

    /// <summary>
    ///     The secret itself. ⚠ Held by the test and by <see cref="TestClientSecrets" />, and by
    ///     nothing else — it is never written to a grain, which is the rule <c>CC1005</c> enforces on
    ///     the platform side.
    /// </summary>
    const string ClientSecret = "phase-1-over-http-client-secret-9f3c";

    /// <summary>The query every non-hub route requires — docs/plan/10 § Versioning.</summary>
    const string Version = "?api-version=" + SampleWidgets.V2026;

    WebApplication identity = null!;
    WebApplication gateway = null!;
    HttpClient http = null!;
    string token = null!;

    static ResourceId Address { get; } =
        new(Tenant, Subscription, ResourceGroup, SampleWidgets.Type, Widget, Guid.Empty);

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var cancellationToken = TestContext.Current.CancellationToken;

        // ⚠ The identity host first, because the gateway is configured with its address, and the
        // address is not known until Kestrel has bound a port.
        identity = await BuildIdentityHostAsync();
        identity.MapIdentityHost();
        await identity.StartAsync(cancellationToken);

        gateway = await BuildGatewayAsync(identity.Urls.First());
        gateway.MapGateway();
        await gateway.StartAsync(cancellationToken);

        await BootstrapTenantAsync(cancellationToken);
        await CreateServicePrincipalAsync();
        await AttachClusterAsync();

        token = await TakeTokenAsync(cancellationToken);

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

    // ── The criterion ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Sign in, create a subscription and a resource group, create a resource, read it back and
    ///     list it — all over HTTP, as a tenant would.
    /// </summary>
    [Fact]
    public async Task ATenantCreatesAResourceOverHttpAndSeesItConverge() {
        var cancellationToken = TestContext.Current.CancellationToken;

        // ── Step 0: the pipeline is actually in front of this. ──────────────────────────────────
        //
        // ⚠ WITHOUT THIS, EVERY ASSERTION BELOW WOULD ALSO PASS AGAINST A GATEWAY WITH NO
        // AUTHENTICATE STAGE AT ALL. That is the exact failure this whole file exists to close, one
        // level up: a check that answers a narrower question than it appears to. A 401 here is the
        // cheapest available proof that the stages are running and that the token below is doing
        // something.
        using var anonymous = await http.GetAsync(
            new Uri(Address.Path + Version, UriKind.Relative),
            cancellationToken
        );

        anonymous.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a request with no bearer token reached past stage 2. AuthenticateStage is what refuses "
            + "it, and a 404 or a 500 here means the pipeline is not in front of this route."
        );

        anonymous.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");

        // ⚠ AND A TOKEN THIS PLATFORM DID NOT SIGN IS REFUSED THE SAME WAY. The real token below has
        // the same shape as this one — three base64url segments — and differs only in whose key
        // signed it. A 401 here is what says the gateway checked the signature against the key set
        // the identity host published, rather than reading tid off whatever it was handed.
        using var forged = new HttpRequestMessage(HttpMethod.Get, new Uri(Address.Path + Version, UriKind.Relative));
        forged.Headers.Authorization = new("Bearer", Forge(token));

        using var refused = await http.SendAsync(forged, cancellationToken);

        refused.StatusCode.ShouldBe(
            HttpStatusCode.Unauthorized,
            "a token with a valid body and an invalid signature was accepted. Stage 2 is not "
            + "validating against the identity host's key set."
        );

        // ── Step 1: the subscription. 201, and nothing in this test wrote a role tuple for it. ──
        //
        // ⚠ The only grant this file makes is on the TENANT — CyberCloudSchema gives `tenant` no
        // `parent`, so nothing above it can grant on it. Everything below is authorized by rewrite:
        // the subscription's parent edge is what carries the tenant owner's rights down, and the
        // resource group's carries them down again.
        var subscription = await PutAsync(
            ScopeId.Subscription(Tenant, Subscription).Path,
            """{"displayName":"over http"}""",
            cancellationToken
        );

        subscription.Status.ShouldBe(
            HttpStatusCode.Created,
            "the scope route refused a subscription the tenant's owner is entitled to create: "
            + subscription.Body
        );

        Json(subscription.Body).GetProperty("id")
            .GetString()
            .ShouldBe(ScopeId.Subscription(Tenant, Subscription).Path);

        // ── Step 2: the resource group. ─────────────────────────────────────────────────────────
        var group = await PutAsync(
            ScopeId.Group(Tenant, Subscription, ResourceGroup).Path,
            """{"location":"eu-central"}""",
            cancellationToken
        );

        group.Status.ShouldBe(
            HttpStatusCode.Created,
            "the scope route refused a resource group in a subscription the same caller had just "
            + "created over the same connection: "
            + group.Body
        );

        // ── Step 3: the resource. 202, with both headers docs/plan/10 requires. ─────────────────
        var accepted = await PutAsync(Address.Path, SampleWidgets.Body(Cluster), cancellationToken);

        accepted.Status.ShouldBe(
            HttpStatusCode.Accepted,
            "the write path refused a create the caller is entitled to make: "
            + accepted.Body
            + " ⚠ A 404 here is the enforcement seam answering for a check that could not be made "
            + "rather than for a resource that does not exist — see the silo's "
            + "CyberCloud.Authorization reference."
        );

        accepted.Headers.Contains("Azure-AsyncOperation")
            .ShouldBeTrue(
                "a 202 without Azure-AsyncOperation is a 202 every client has to special-case — "
                + "docs/plan/10 § Long-running operations."
            );

        accepted.Headers.Contains("Retry-After").ShouldBeTrue();

        var created = Json(accepted.Body);

        created.GetProperty("provisioningState").GetString().ShouldBe("Creating");
        created.GetProperty("id").GetString().ShouldBe(Address.Path);

        var operationId = OperationIdFrom(accepted.Headers.GetValues("Azure-AsyncOperation").First());

        // ── Step 4: convergence, polled over HTTP and driven by nothing in this process. ────────
        //
        // ⚠ Nothing here calls DriveAsync. The only thing that can move this operation is the
        // reminder OperationGrain registers, fired by the Redis reminder table on whichever silo
        // PROCESS Orleans placed the grain on.
        var terminal = await ConvergeAsync(operationId, cancellationToken);

        Json(terminal).GetProperty("status")
            .GetString()
            .ShouldBe(
                "Succeeded",
                "the operation did not succeed. Its body, which carries the progress array and the "
                + "error if there is one, is: "
                + terminal
            );

        // ── Step 5: read it back through the same front door. ───────────────────────────────────
        var read = await GetAsync(Address.Path, cancellationToken);

        read.Status.ShouldBe(HttpStatusCode.OK, "the created resource is not readable: " + read.Body);

        var resource = Json(read.Body);

        resource.GetProperty("provisioningState")
            .GetString()
            .ShouldBe(
                "Succeeded",
                "the operation reported Succeeded and the resource did not follow it — a caller polling "
                + "the operation and a caller reading the resource get different answers."
            );

        resource.GetProperty("name").GetString().ShouldBe(Widget);

        // ⚠⚠ THE SHAPE BELOW IS THE ONE THE PLATFORM SERVES AND IT IS ALMOST CERTAINLY WRONG.
        // ⚠⚠ https://github.com/Rikarin/CyberCloud/issues/69 — do not "tidy" this assertion.
        //
        // The rendered body nests `properties` inside `properties` and repeats `location`:
        //
        //   { "id":…, "name":…, "location":"eu-central", "provisioningState":"Succeeded",
        //     "properties": { "location":"eu-central",
        //                     "properties": { "clusterId":…, "message":"hello", … } } }
        //
        // ResourceGrain.Project writes each declared pointer at its FULL path, so a type declaring
        // `/location` and `/properties/message` projects a whole document; ResponseBodies
        // .WriteResource then writes that document raw under a `properties` member, on the stated
        // assumption that it is already the inner slice. Both halves are self-consistent and they
        // disagree, which is docs/plan/08's projection and docs/plan/10's Azure shape meeting at
        // ResourceSnapshot.Properties with nothing covering the join.
        //
        // ⚠ WHY NOTHING SAW IT, AND IT IS THIS FILE'S OWN THESIS ARRIVING ONE LAYER DOWN.
        // CyberCloud.Gateway.Host.Tests renders this body constantly — against a SUBSTITUTED
        // IResourceManager whose snapshots carry a hand-written inner object, so the substitute's
        // Properties and the real grain's Properties are different shapes with one name and every
        // assertion about the rendered body was made against the wrong one. The first HTTP request
        // ever driven to the real manager found it, which is the whole argument for this file.
        //
        // ⚠ NOT FIXED HERE ON PURPOSE. The wire shape is the API contract: changing it moves the
        // published OpenAPI document, the generated SDK, the cyc verb tree and the portal forms, all
        // four of which are byte-compared by the Generated surfaces gate, and the OpenAPI
        // compatibility gate diffs the published version against its predecessor. That is an owner's
        // decision, not a passing repair. Asserted as-served so the suite is honest about what ships;
        // when #72 lands this assertion fails and this comment says why.
        resource.GetProperty("properties")
            .GetProperty("properties")
            .GetProperty("message")
            .GetString()
            .ShouldBe("hello");

        // ── Step 6: list it. ────────────────────────────────────────────────────────────────────
        //
        // ⚠ A COLLECTION ROUTE IS NOT THE RESOURCE ROUTE MINUS A SEGMENT. It resolves to a different
        // RouteKind, dispatches to ListAsync rather than ReadAsync, and its page is filtered by what
        // the caller may read — so a list that answered from the same code as the GET above would be
        // this repository's signature defect in the one place it is also an enumeration oracle.
        var list = await GetAsync(ResourceCollectionId.Of(Address).Path, cancellationToken);

        list.Status.ShouldBe(HttpStatusCode.OK, "the collection route is not readable: " + list.Body);

        var value = Json(list.Body).GetProperty("value").EnumerateArray().ToList();

        value.Count.ShouldBe(
            1,
            "the resource group holds exactly one widget and the listing did not return it. "
            + "Body: " + list.Body
        );

        value[0].GetProperty("id").GetString().ShouldBe(Address.Path);
        value[0].GetProperty("provisioningState").GetString().ShouldBe("Succeeded");
    }

    // ── Polling ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Polls <c>/operations/{id}</c> until the body reports a terminal status.</summary>
    /// <param name="operationId">The operation the <c>202</c> named.</param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The last body read.</returns>
    async Task<string> ConvergeAsync(Guid operationId, CancellationToken cancellationToken) {
        var clock = Stopwatch.StartNew();
        var last = "";

        while (clock.Elapsed < ConvergenceBudget) {
            var poll = await GetAsync(
                "/operations/" + operationId.ToString("D", CultureInfo.InvariantCulture),
                cancellationToken
            );

            poll.Status.ShouldBe(
                HttpStatusCode.OK,
                $"operation {operationId:D} became unreadable while it was running: " + poll.Body
            );

            last = poll.Body;

            var status = Json(last).GetProperty("status").GetString();

            if (status is "Succeeded" or "Failed" or "Canceled") {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"operation {operationId:D} reached {status} after "
                    + $"{clock.Elapsed.TotalSeconds:F0} s."
                );

                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return last;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One answer, in the three parts an assertion needs.</summary>
    /// <param name="Status">The status line.</param>
    /// <param name="Headers">The response headers.</param>
    /// <param name="Body">The body, read to a string.</param>
    readonly record struct Answer(HttpStatusCode Status, HttpResponseHeaders Headers, string Body);

    async Task<Answer> PutAsync(string path, string body, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri(path + Version, UriKind.Relative)
        ) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        return await SendAsync(request, cancellationToken);
    }

    async Task<Answer> GetAsync(string path, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(path + Version, UriKind.Relative)
        );

        return await SendAsync(request, cancellationToken);
    }

    async Task<Answer> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        request.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);

        return new(
            response.StatusCode,
            response.Headers,
            await response.Content.ReadAsStringAsync(cancellationToken)
        );
    }

    static JsonElement Json(string body) => JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>The operation id out of the <c>Azure-AsyncOperation</c> URL the gateway wrote.</summary>
    /// <param name="header">The header value.</param>
    /// <remarks>
    ///     ⚠ Taken from the HEADER rather than from the body, because the header is the contract: it
    ///     is what <c>Operation&lt;T&gt;</c> in a generated SDK and <c>--wait</c> in the CLI follow,
    ///     and a header naming an operation the platform cannot answer for would be invisible to a
    ///     test that polled an id it read somewhere else.
    /// </remarks>
    static Guid OperationIdFrom(string header) {
        var last = header.Split('?')[0].Split('/')[^1];

        return Guid.TryParse(last, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Azure-AsyncOperation was '{header}', whose last path segment '{last}' is not an "
                + "operation id. GatewayRouterPaths.AsyncOperation is what builds it."
            );
    }

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The tenant record, the one direct grant, and the directory entry that makes a tenant
    ///     reachable at all.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The directory entry is the part <see cref="ReconcileThroughTheRealHostTests" />
    ///             does not need and this file cannot do without, and that difference is itself the
    ///             point.
    ///         </b> Stage 3 resolves a token's tenant through <c>TenantDirectoryCache</c> and
    ///         answers <c>404</c> on a miss, so a tenant with a record, a shard and an owner is still
    ///         unreachable over HTTP until it is in the directory. A suite that calls
    ///         <c>IResourceManager</c> directly never meets that rule — which is one more thing the
    ///         join was hiding.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The owner tuple is written BEFORE the directory entry</b>, which is the ordering
    ///         <c>ScopeManagerService.CreateTenantAsync</c> keeps and the reason it gives: there must
    ///         be no window in which a tenant is reachable and owned by nobody.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is still owed.</b> This does not call <c>CreateTenantAsync</c> itself, which
    ///         would drive the shard assignment and this ordering through the platform rather than
    ///         beside it; that needs a <c>platform:root#operator</c> grant in the platform tenant and
    ///         a shard map this topology does not seed.
    ///     </para>
    /// </remarks>
    async Task BootstrapTenantAsync(CancellationToken cancellationToken) {
        var tenant = topology.Client.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture));

        var record = await tenant
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant))
            .CreateAsync(Slug, "Phase 1 over HTTP", "eu-central");

        record.IsSuccess.ShouldBeTrue(record.Error?.Message);

        var tuple = RelationTuple.Create(
            AuthObjectRef
                .Create(ObjectTypes.Tenant, Tenant.ToString("N", CultureInfo.InvariantCulture))
                .GetValueOrThrow(),
            Relations.Owner,
            SubjectRef.Create(SubjectTypes.ServicePrincipal, ServicePrincipal.ToString("N", CultureInfo.InvariantCulture))
                .GetValueOrThrow()
        )
            .GetValueOrThrow();

        var granted = await tenant
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(Tenant))
            .WriteAsync(tuple);

        granted.IsSuccess.ShouldBeTrue(
            $"the ReBAC grant could not be written: {granted.Error?.Code} — {granted.Error?.Message}"
        );

        var registered = await topology.Client
            .GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .RegisterAsync(
                new() { TenantId = Tenant, Slug = Slug, HomeRegion = "eu-central", Status = TenantStatus.Active }
            );

        registered.IsSuccess.ShouldBeTrue(
            $"the tenant could not be put in the directory: {registered.Error?.Code} — "
            + $"{registered.Error?.Message}. Until it is there, stage 3 answers 404 for every "
            + "request this tenant makes."
        );

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Attaches the AppHost's k3s as the cluster this tenant's widget names.</summary>
    /// <remarks>
    ///     ⚠ Its own cluster id, because a <c>ClusterConnectionDescriptor</c> carries one owning
    ///     tenant and <c>ClusterConnectionGrain</c> checks it on every call. Sharing the sibling
    ///     suite's would make one of the two suites reach another tenant's cluster.
    /// </remarks>
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
                    DisplayName = "the AppHost's k3s, over HTTP"
                }
            );

        attached.IsSuccess.ShouldBeTrue(
            $"the cluster could not be attached: {attached.Error?.Code} — {attached.Error?.Message}"
        );
    }

    /// <summary>Where the AppHost's k3s wrote its kubeconfig.</summary>
    static string KubeconfigPath { get; } =
        Path.Combine(TestPaths.AppHostDirectory, ".k3s", "kubeconfig.yaml");

    /// <summary>
    ///     Creates the service principal the story is performed as, with a credential handle that
    ///     <see cref="TestClientSecrets" /> knows how to answer for.
    /// </summary>
    /// <remarks>
    ///     ⚠ Reached for directly, like the tenant record above, because nothing over HTTP creates
    ///     a service principal yet — that is an identity-object API the portal's "app registrations"
    ///     page will need and docs/plan/11 § The object model describes. The descriptor holds a
    ///     <see cref="SecretRef" /> and never the secret, which is the difference between this and
    ///     the "client secret in a Kubernetes Secret" the same document calls the bad answer.
    /// </remarks>
    async Task CreateServicePrincipalAsync() {
        var created = await topology.Client
            .ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IServicePrincipalGrain>(GrainKeys.ServicePrincipal(ServicePrincipal))
            .CreateAsync(
                new() {
                    DisplayName = "Phase 1 over HTTP, as CI would",
                    Enabled = true,
                    CredentialSecretRef = CredentialRef
                }
            );

        created.IsSuccess.ShouldBeTrue(
            $"the service principal could not be created: {created.Error?.Code} — {created.Error?.Message}"
        );
    }

    /// <summary>
    ///     Takes a client-credentials token from the identity host, as a CI job would.
    /// </summary>
    /// <param name="cancellationToken">The test's token.</param>
    /// <returns>The access token, verbatim.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The response is asserted on beyond <c>access_token</c>, because the wire token is
    ///         the contract.</b> <c>NoRolesInTokenTests</c> asserts the principal the factory builds;
    ///         nothing until here asserted what OpenIddict serialized from it, and the two differ in
    ///         exactly the way that bit: a claim without an access-token destination is dropped
    ///         silently. So the payload is decoded and checked for the four claims the gateway cannot
    ///         work without, and for the absence of every claim the contract forbids.
    ///     </para>
    ///     <para>
    ///         Decoded without verifying — this is the test reading its own token, and verification
    ///         is what the gateway is about to do with it against the published key set.
    ///     </para>
    /// </remarks>
    async Task<string> TakeTokenAsync(CancellationToken cancellationToken) {
        using var client = new HttpClient { BaseAddress = new(identity.Urls.First()) };

        using var response = await client.PostAsync(
            new Uri(IdentityHostOpenIddict.TokenPath, UriKind.Relative),
            new FormUrlEncodedContent(
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = ServicePrincipal.ToString("N", CultureInfo.InvariantCulture),
                    ["client_secret"] = ClientSecret,
                    ["scope"] = IdentityHostOpenIddict.Scopes.Api
                }
            ),
            cancellationToken
        );

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            "the identity host refused the client-credentials grant: " + body
        );

        var issued = Json(body);

        issued.GetProperty("token_type").GetString().ShouldBe("Bearer");

        var accessToken = issued.GetProperty("access_token").GetString();
        accessToken.ShouldNotBeNullOrEmpty();

        var payload = Payload(accessToken);

        TestContext.Current.TestOutputHelper?.WriteLine("access token payload: " + payload.GetRawText());

        payload.GetProperty(AccessTokenClaims.TenantId).GetString().ShouldBe(Tenant.ToString("N"));
        payload.GetProperty(AccessTokenClaims.SubjectType).GetString().ShouldBe(SubjectTypes.ServicePrincipal);
        payload.GetProperty(AccessTokenClaims.Subject).GetString().ShouldBe(ServicePrincipal.ToString("N"));
        payload.GetProperty(AccessTokenClaims.Audience).GetString().ShouldBe(AccessTokenPolicy.Audience);

        foreach (var claim in payload.EnumerateObject()) {
            AccessTokenClaims.ForbiddenClaims.ShouldNotContain(
                claim.Name,
                $"the issued token carries '{claim.Name}', which docs/plan/11 § Protocol forbids and "
                + "the gateway refuses."
            );
        }

        return accessToken;
    }

    /// <summary>The token's payload, decoded and not verified.</summary>
    static JsonElement Payload(string jwt) {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');

        return Json(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    /// <summary>
    ///     The same token with its signature replaced — a body the platform wrote under a signature
    ///     it did not.
    /// </summary>
    static string Forge(string jwt) {
        var parts = jwt.Split('.');
        var bogus = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{parts[0]}.{parts[1]}.{bogus}";
    }

    /// <summary>
    ///     Builds the <b>real</b> identity host, bound to this test's tenant and given the one
    ///     vault seam its token endpoint needs.
    /// </summary>
    /// <remarks>
    ///     <c>IdentityComposition.BuildAsync</c> is the method <c>CyberCloud.Identity.Host</c>'s own
    ///     <c>Program.cs</c> calls: the same OpenIddict server in the same degraded mode with the same
    ///     handlers, the same ephemeral ES256 key published at the same <c>/.well-known/jwks</c>, the
    ///     same <c>TokenApi</c> over the same grains. <c>configure</c> supplies what a deployment
    ///     supplies — the vault adapter — and nothing else.
    /// </remarks>
    static Task<WebApplication> BuildIdentityHostAsync() =>
        IdentityComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture),
                $"--{IdentityHostOptions.SectionName}:TenantId=" + Tenant.ToString("D", CultureInfo.InvariantCulture)
            ],
            services => services.AddSingleton<IClientSecretSeam>(new TestClientSecrets(CredentialRef, ClientSecret))
        );

    /// <summary>
    ///     Builds the <b>real</b> gateway, told which identity host to trust.
    /// </summary>
    /// <param name="issuer">Where the identity host is listening.</param>
    /// <remarks>
    ///     ⚠ <b>No <c>configure</c>.</b> Everything about the host built here is the object graph
    ///     <c>CyberCloud.Gateway.Host</c>'s own <c>Program.cs</c> builds, and stage 2's resolver
    ///     arrives the way it arrives in production: from <c>CyberCloud:Gateway:Identity:Issuer</c>.
    ///     This file used to register an in-process token table through <c>configure</c>, and its
    ///     first paragraph warned that a green run therefore said nothing about production; the
    ///     paragraph is gone because the cause is.
    /// </remarks>
    static Task<WebApplication> BuildGatewayAsync(string issuer) =>
        GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture),
                "--CyberCloud:Gateway:Identity:Issuer=" + issuer
            ]
        );

    /// <summary>
    ///     The vault seam a deployment registers over OpenBao, answering for exactly one handle.
    /// </summary>
    /// <remarks>
    ///     ⚠ A real implementation of a real seam, doing the real thing for one value: the handle
    ///     has to match, and the comparison is constant-time, as <see cref="IClientSecretSeam" />
    ///     requires of every implementation. It is not a bypass — a verifier that answered
    ///     <c>true</c> to everything would pass this suite and prove nothing about the endpoint.
    /// </remarks>
    /// <param name="known">The one handle this vault holds.</param>
    /// <param name="secret">What is behind it.</param>
    sealed class TestClientSecrets(SecretRef known, string secret) : IClientSecretSeam {
        /// <inheritdoc />
        public Task<Result<bool>> VerifyAsync(
            SecretRef reference,
            string presented,
            CancellationToken cancellationToken = default
        ) =>
            Task.FromResult(
                Result<bool>.Success(
                    reference == known
                    && CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(presented),
                        Encoding.UTF8.GetBytes(secret)
                    )
                )
            );
    }
}
