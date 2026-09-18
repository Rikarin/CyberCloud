using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     That <c>dotnet run</c> on the AppHost brings up the whole platform — the three hosts a user
///     reaches and the two Angular apps — and that the pieces which have to agree on a port do.
/// </summary>
/// <remarks>
///     <para>
///         <b>Model-level, and deliberately so.</b> Every other file in this suite starts the
///         topology, which is a k3s and a two-silo cluster and a minute of wall clock;
///         <see cref="LocalTopology" /> holds the machine lock for it. What is asserted here is what
///         <c>Program.cs</c> <i>declares</i> — which resources exist, what environment they are
///         handed, what they wait on — and Aspire answers that from the built model without
///         starting anything. The one thing this cannot see is whether a host actually comes up on
///         the port it was given; <c>TenantOverHttpTests</c> drives the same composition roots over
///         HTTP, and a run of the AppHost is the rest.
///     </para>
///     <para>
///         ⚠ <b>The proxy files are read back from disk, and that is the point of the class.</b>
///         The portal calls the platform on its own origin at <c>/api</c> and the Angular dev server
///         forwards it — <c>apps/portal/proxy.conf.json</c> names the gateway's port, and
///         <c>apps/identity/proxy.conf.json</c> the identity host's. Those are JSON files no
///         compiler reads against <see cref="CyberCloudResources" />, so a port moved in one place
///         is a portal that loads, renders, and cannot call anything — with the only symptom a
///         <c>ECONNREFUSED</c> in the dev server's console. This is the compiler.
///     </para>
/// </remarks>
public sealed class AppHostTopologyTests {
    /// <summary>The repository root, found the way <see cref="LocalTopology" /> finds it: by walking up to the solution file.</summary>
    static readonly string RepositoryRoot = FindRepositoryRoot();

    static string PortalRoot => Path.Combine(RepositoryRoot, "portal");

    static string FindRepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("CyberCloud.slnx is above no ancestor of " + AppContext.BaseDirectory);
    }

    /// <summary>The built model and the context its environment is resolved in — nothing started.</summary>
    sealed record Built(DistributedApplicationModel Model) {
        public IResource Resource(string name) => Model.Resources.Single(x => x.Name == name);

        public IEnumerable<string> Names => Model.Resources.Select(x => x.Name);

        /// <summary>A resource's environment as the AppHost declares it.</summary>
        /// <remarks>
        ///     ⚠ Resolved in the <b>Publish</b> operation, not Run. In Run, a value that references
        ///     another resource's endpoint — the silos' NATS connection string, say — is resolved
        ///     by waiting for that endpoint to be allocated, which happens when the application
        ///     starts, which it never does here: the first version of this method hung for ten
        ///     minutes on silo-1. Under Publish a reference renders as its manifest expression and
        ///     a literal renders as itself, and every value this class asserts on is a literal.
        /// </remarks>
        public async Task<IReadOnlyDictionary<string, string>> EnvironmentOf(string name) {
            var resolved = await ExecutionConfigurationBuilder
                .Create(Resource(name))
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                    NullLogger.Instance,
                    TestContext.Current.CancellationToken
                );

            resolved.Exception.ShouldBeNull($"{name}'s environment could not be resolved");

            return resolved.EnvironmentVariables.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     The model <c>Program.cs</c> would run, built and not started.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>DistributedApplicationTestingBuilder</c>.</b> That builder runs the AppHost's
    ///     entry point, whose last line is <c>RunAsync</c> — so its <c>BuildAsync</c> is a started
    ///     platform, and the first version of this class left three sets of Redis, PostgreSQL, NATS
    ///     and SeaweedFS containers running on the machine while asserting that a dictionary held a
    ///     string. <see cref="CyberCloudTopology.Compose" /> is the same declarations over a plain
    ///     builder, and <c>Build()</c> on that starts nothing until somebody calls <c>Start</c>,
    ///     which nobody here does.
    /// </remarks>
    static Built Model(params string[] args) => ModelOver(AppHostDirectory, args);

    /// <summary>Where <c>Program.cs</c> lives — what <see cref="Model" /> composes over.</summary>
    static string AppHostDirectory => Path.Combine(RepositoryRoot, "src", "Hosts", "CyberCloud.AppHost");

    /// <summary>
    ///     <see cref="Model" /> over a chosen AppHost directory — the one thing about the
    ///     composition a test can move, and only <see cref="TheModelBuildsWhenManyComposeAtOnceOnAFreshCheckout" />
    ///     moves it.
    /// </summary>
    static Built ModelOver(string appHostDirectory, params string[] args) {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions {
                Args = args,
                // Where Program.cs lives, so that AppHostDirectory — which the topology uses for the
                // k3s kubeconfig and the SeaweedFS identity file — is the same directory it is under
                // `dotnet run`, and the portal is found two levels above it.
                ProjectDirectory = appHostDirectory,
                DisableDashboard = true
            }
        );

        CyberCloudTopology.Compose(builder);

        var application = builder.Build();

        return new(application.Services.GetRequiredService<DistributedApplicationModel>());
    }

    [Fact]
    public async Task TheThreeHostsAndTheTwoAppsAreInTheModel() {
        var built = Model();
        var names = built.Names.ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[] {
                     CyberCloudResources.Gateway, CyberCloudResources.Identity, CyberCloudResources.Feeds,
                     CyberCloudResources.Portal, CyberCloudResources.IdentityApp,
                     CyberCloudResources.ObjectStore, CyberCloudResources.ObjectStoreBucketInit,
                     CyberCloudResources.Mailpit
                 }) {
            names.ShouldContain(
                expected,
                "src/Hosts/README.md § What exists today lists four hosts; a `dotnet run` that started fewer "
                + "would be a platform a test can drive and nobody can open"
            );
        }

        built.Model.Resources.Where(x => x.Name is CyberCloudResources.Gateway or CyberCloudResources.Identity or CyberCloudResources.Feeds)
            .ShouldAllBe(x => x is ProjectResource, "the hosts are the processes their own Program.cs files start, not containers of a build");
    }

    [Fact]
    public async Task TheFrontendsCanBeLeftOutAndNothingElseCan() {
        var built = Model($"--{CyberCloudResources.FrontendsKey}=false");
        var names = built.Names.ToHashSet(StringComparer.Ordinal);

        names.ShouldNotContain(CyberCloudResources.Portal);
        names.ShouldNotContain(CyberCloudResources.IdentityApp);

        foreach (var kept in new[] { CyberCloudResources.Gateway, CyberCloudResources.Identity, CyberCloudResources.Feeds, CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo }) {
            names.ShouldContain(kept, $"{CyberCloudResources.FrontendsKey} is the one switch this AppHost has, and it turns off the two dev servers only");
        }
    }

    [Fact]
    public async Task TheIssuerIsOneStringOnAllThreeSides() {
        var built = Model();

        var gateway = await built.EnvironmentOf(CyberCloudResources.Gateway);
        var feeds = await built.EnvironmentOf(CyberCloudResources.Feeds);
        var identity = await built.EnvironmentOf(CyberCloudResources.Identity);

        gateway["CyberCloud__Gateway__Identity__Issuer"].ShouldBe(CyberCloudResources.IdentityIssuer);
        feeds["CyberCloud__Feeds__Identity__Issuer"].ShouldBe(CyberCloudResources.IdentityIssuer);

        // ⚠ The identity host is handed the issuer rather than inferring it, and the string has to
        // name the port it listens on. Inference was the first shape, and it held until /authorize
        // was proxied through the identity app's dev server: a request resumed through 4201 minted
        // a code under `http://localhost:4201/`, and /token on 5101 refused it (OpenIddict ID2088)
        // with every host healthy — CyberCloudTopology's issuer note. An Issuer that DIFFERED from
        // the port would be a discovery document the gateway refuses, just as quietly.
        identity["CyberCloud__Identity__Issuer"].ShouldBe(CyberCloudResources.IdentityIssuer, "one issuer, whichever origin the request arrived on");
        new Uri(CyberCloudResources.IdentityIssuer).Port.ShouldBe(CyberCloudResources.IdentityPort);

        // ⚠ The person's path, pinned on the identity host's side: where an unauthenticated
        // /authorize sends the person (the identity app's dev server, which proxies the resumed
        // request back), where the portal's code may be sent (which is also the CORS origin for
        // /token), and where the keys persist so a restart does not sign everybody out.
        identity["CyberCloud__Identity__SignInPageBaseUri"].ShouldBe($"http://localhost:{CyberCloudResources.IdentityAppPort}");
        identity["CyberCloud__Identity__Clients__Portal__RedirectUris__0"].ShouldBe($"http://localhost:{CyberCloudResources.PortalPort}/auth/callback");
        identity["CyberCloud__Identity__Clients__Portal__PostLogoutRedirectUris__0"].ShouldBe($"http://localhost:{CyberCloudResources.PortalPort}/");
        identity["CyberCloud__Identity__DevelopmentKeyDirectory"].ShouldBe(Path.Combine(RepositoryRoot, "src", "Hosts", "CyberCloud.AppHost", ".identity"));

        foreach (var (name, environment) in new[] { (CyberCloudResources.Gateway, gateway), (CyberCloudResources.Feeds, feeds), (CyberCloudResources.Identity, identity) }) {
            environment["CyberCloud__Cluster__LocalhostGatewayPort"]
                .ShouldBe(CyberCloudResources.SiloOneGatewayPort.ToString(), $"{name} is an Orleans client of silo 1 — AsOrleansClient");
            environment["DOTNET_ENVIRONMENT"].ShouldBe("Development", $"{name} would otherwise choose Kubernetes membership under Aspire.Hosting.Testing — WithOrleansPorts' remarks");
        }
    }

    [Fact]
    public async Task SelfServeSignUpIsOneDecisionOnAllThreeSides() {
        var built = Model();

        // ⚠ Three processes read CyberCloud:Identity:SelfServeSignUp and they have to agree: the
        // silos' PlatformBootstrapTask writes the platform:root#operator grant sign-up creates
        // tenants under only when it is on, and the identity host opens /api/signup/* only when it
        // is on. A host with it on beside silos with it off refuses every completion with
        // "something went wrong" — IdentityHostOptions.SelfServeSignUp.
        foreach (var name in new[] { CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo, CyberCloudResources.Identity }) {
            var environment = await built.EnvironmentOf(name);
            environment[CyberCloudTopology.SelfServeSignUpVariable].ShouldBe("true", $"{name} is one of the three");
        }

        // And the region a signed-up tenant is homed to, without which the first create step refuses.
        var identity = await built.EnvironmentOf(CyberCloudResources.Identity);
        identity["CyberCloud__Identity__DefaultRegion"].ShouldBe(CyberCloudResources.DefaultRegion);
    }

    [Fact]
    public async Task TheGatewayAnnouncesItselfAndNotProduction() {
        var built = Model();
        var gateway = await built.EnvironmentOf(CyberCloudResources.Gateway);

        gateway["CyberCloud__Gateway__PublicBaseUri"].ShouldBe(
            $"http://localhost:{CyberCloudResources.GatewayPort}",
            "the shipped default is https://api.cybercloud.io, and a connected cluster's install command (#36) would carry it"
        );
    }

    [Fact]
    public async Task TheObjectStoreReachesTheSilosAndTheFeedsHost() {
        var built = Model();

        foreach (var name in new[] { CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo, CyberCloudResources.Feeds }) {
            var environment = await built.EnvironmentOf(name);

            environment["CyberCloud__ObjectStorage__Endpoint"].ShouldBe($"http://localhost:{CyberCloudResources.ObjectStoreS3Port}");
            environment["CyberCloud__ObjectStorage__Bucket"].ShouldBe(CyberCloudResources.ObjectStoreBucket);
            environment["CyberCloud__ObjectStorage__AllowInsecureTransport"].ShouldBe("true", $"{name} speaks plain http to a container on the laptop, and the option exists so production cannot");
        }

        // The bucket is made by the init container, and everything that writes into it waits for
        // that to have finished — a store pointed at a bucket that does not exist answers
        // NoSuchBucket, which reads like a signing bug.
        foreach (var name in new[] { CyberCloudResources.SiloOne, CyberCloudResources.Feeds }) {
            built.Resource(name).Annotations.OfType<WaitAnnotation>()
                .ShouldContain(
                    x => x.Resource.Name == CyberCloudResources.ObjectStoreBucketInit && x.WaitType == WaitType.WaitForCompletion,
                    $"{name} writes into {CyberCloudResources.ObjectStoreBucket} and must wait for the container that creates it"
                );
        }
    }

    [Fact]
    public async Task TheMailRelayIsInTheModelAndBothSilosSendThroughIt() {
        var built = Model();

        // ⚠ The development carrier (#93): Mailpit, the image the carrier's own suite runs against,
        // SMTP on 1025 and the inbox on 8025, both published unproxied so the literal addresses the
        // silos and the README carry are the addresses.
        var mailpit = built.Resource(CyberCloudResources.Mailpit).ShouldBeOfType<ContainerResource>();

        var image = mailpit.Annotations.OfType<ContainerImageAnnotation>().ShouldHaveSingleItem();
        image.Image.ShouldBe("axllent/mailpit");
        image.Tag.ShouldBe(
            "v1.31.1",
            "SmtpChannelProviderTests.Image runs the carrier against this exact tag; the dialect proven is the dialect this run speaks"
        );

        var endpoints = mailpit.Annotations.OfType<EndpointAnnotation>().ToDictionary(x => x.Name, StringComparer.Ordinal);
        endpoints["smtp"].Port.ShouldBe(CyberCloudResources.MailpitSmtpPort);
        endpoints["smtp"].TargetPort.ShouldBe(CyberCloudResources.MailpitSmtpPort);
        endpoints["smtp"].IsProxied.ShouldBeFalse("the silos are handed localhost:1025 as a literal");
        endpoints["http"].Port.ShouldBe(CyberCloudResources.MailpitHttpPort);
        endpoints["http"].IsProxied.ShouldBeFalse("a person is told http://localhost:8025");

        // Both silos, because the grain that mints a code lives on either — and the section is the
        // one switch that registers the carrier, writes the platform's service and mails the codes.
        foreach (var name in new[] { CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo }) {
            var environment = await built.EnvironmentOf(name);

            environment["CyberCloud__Communication__Smtp__Host"].ShouldBe("localhost", $"{name} reaches Mailpit on the published port");
            environment["CyberCloud__Communication__Smtp__Port"].ShouldBe(CyberCloudResources.MailpitSmtpPort.ToString());
            environment["CyberCloud__Communication__Smtp__Security"].ShouldBe("None", "Mailpit speaks no TLS, and there is no password to protect");
            environment["CyberCloud__Communication__Smtp__From"].ShouldBe(CyberCloudResources.PlatformSender);
            environment["CyberCloud__Communication__Smtp__UnsubscribeMailbox"].ShouldBe(CyberCloudResources.PlatformUnsubscribeMailbox, "List-Unsubscribe is sent, and its mailbox lands in the same inbox");
            environment.ShouldNotContainKey("CyberCloud__Communication__Smtp__Password", "no credential on a relay that trusts the laptop");

            // ⚠ No explicit route: the silo is in Development, so the unset section is what makes
            // DevelopmentOtpDelivery log the code AND mail it through the platform's own service.
            environment.ShouldNotContainKey("CyberCloud__Identity__OtpDelivery__ServiceId");
            environment["DOTNET_ENVIRONMENT"].ShouldBe("Development");
        }

        // Nothing waits on the relay, for the reason nothing waits on k3s — CyberCloudTopology.
        built.Resource(CyberCloudResources.SiloOne).Annotations.OfType<WaitAnnotation>()
            .ShouldNotContain(x => x.Resource.Name == CyberCloudResources.Mailpit, "a carrier is a data plane the control plane refuses honestly without");
    }
    [Fact]
    public async Task TheResourceChangedStreamReachesTheGatewayAndTheProjectionReachesTheSilos() {
        // docs/plan/08 § The resource-graph projection, #54. The gateway PUBLISHES — step 11 runs in
        // its process — so it needs NATS and nothing else; the silos publish their half and PROJECT,
        // so they need NATS and ClickHouse. A gateway without the NATS reference is the shape the
        // platform had before #54: the write path works and the stream carries only the silo's
        // transitions, with nothing in any log to say the creates are missing.
        var built = Model();

        foreach (var name in new[] { CyberCloudResources.Gateway, CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo }) {
            var environment = await built.EnvironmentOf(name);

            // Under Publish a reference renders as its manifest expression rather than an address —
            // see EnvironmentOf — so what is asserted is that the key ResourceGraphOptions.Bind reads
            // is there and names the NATS resource.
            environment.ShouldContainKey("ConnectionStrings__nats", $"{name} has no NATS connection string, so it keeps the logging sink and publishes nothing");
            environment["ConnectionStrings__nats"].ShouldContain(CyberCloudResources.Nats);
        }

        // ⚠ The gateway is in this list since the query half of #54: it answers
        // POST …/providers/CyberCloud.ResourceGraph/resources from the same ClickHouse, and without
        // the endpoint every query is a 500 naming the section. It still runs no projector —
        // GatewayComposition calls AddResourceGraphQuery, never AddResourceGraphProjector, and
        // HostCompositionTests holds that line.
        foreach (var name in new[] { CyberCloudResources.SiloOne, CyberCloudResources.SiloTwo, CyberCloudResources.Gateway }) {
            var environment = await built.EnvironmentOf(name);

            environment["CyberCloud__ResourceGraph__ClickHouseEndpoint"].ShouldBe($"http://localhost:{CyberCloudResources.ClickHouseHttpPort}");
            environment["CyberCloud__ResourceGraph__ClickHouseUser"].ShouldBe(CyberCloudResources.ClickHouseUser);
            environment["CyberCloud__ResourceGraph__AllowInsecureTransport"].ShouldBe("true", $"{name} speaks plain http to a container on the laptop, and the option exists so production cannot");
        }

        built.Resource(CyberCloudResources.Gateway).Annotations.OfType<WaitAnnotation>()
            .ShouldContain(x => x.Resource.Name == CyberCloudResources.ClickHouse, "the gateway's first query would otherwise race the container's start");

        var clickHouse = built.Resource(CyberCloudResources.ClickHouse);
        clickHouse.ShouldBeAssignableTo<ContainerResource>();
        clickHouse.Annotations.OfType<EndpointAnnotation>()
            .ShouldContain(x => x.Port == CyberCloudResources.ClickHouseHttpPort && x.TargetPort == CyberCloudResources.ClickHouseHttpPort && !x.IsProxied);
    }

    [Fact]
    public void ThePortalProxyForwardsApiToTheGatewayOnItsPinnedPort() {
        var proxy = ReadProxy(Path.Combine("apps", "portal", "proxy.conf.json"));

        proxy.ShouldContainKey("/api", "API_BASE_PATH in portal/apps/portal/src/app/api/http-transport.ts is /api");
        TargetPortOf(proxy["/api"]).ShouldBe(CyberCloudResources.GatewayPort, "the portal's /api is the gateway — CyberCloudResources.GatewayPort");
        proxy["/api"].GetProperty("pathRewrite").GetProperty("^/api").GetString()
            .ShouldBe("", "the gateway serves its routes at the root, so /api has to come off");

        // ⚠ The terminal's socket is `ws://localhost:4200/api/hubs/terminal?ticket=…`, on this same
        // entry — and Vite forwards an Upgrade only for an entry that says `ws: true` (or whose
        // target is `ws:`), which @angular/build's proxy loader never adds. Without it every HTTP
        // request reaches the gateway and the one WebSocket does not, with the symptom a pane that
        // reconnects five times and gives up. The entry has to say it.
        proxy["/api"].TryGetProperty("ws", out var ws).ShouldBeTrue("the /api entry has no `ws`, so the dev server drops the terminal hub's Upgrade");
        ws.GetBoolean().ShouldBeTrue("`ws` is false, so the dev server drops the terminal hub's Upgrade");

        ServePortOf("portal").ShouldBe(CyberCloudResources.PortalPort);
    }

    [Fact]
    public void TheIdentityAppProxyForwardsToTheIdentityHostOnItsPinnedPort() {
        var proxy = ReadProxy(Path.Combine("apps", "identity", "proxy.conf.json"));

        // ⚠ /authorize and /logout, and no /connect: the sign-in page resumes the OIDC request by
        // navigating to the relative /authorize its returnUrl carries, and only a proxy entry makes
        // that reach the host on 5101 with the cookie. /connect was an entry for a path that never
        // existed — the endpoints are at the root, IdentityHostOpenIddict says where.
        foreach (var path in new[] { "/api", "/authorize", "/logout", "/.well-known" }) {
            proxy.ShouldContainKey(path);
            TargetPortOf(proxy[path]).ShouldBe(CyberCloudResources.IdentityPort, $"the identity app's {path} is the identity host — CyberCloudResources.IdentityPort");
        }

        ServePortOf("identity").ShouldBe(CyberCloudResources.IdentityAppPort);
    }

    /// <summary>
    ///     The model still builds while a container from another run holds the SeaweedFS identity
    ///     file open.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Measured, not imagined — 2026-09-17, this suite run whole.</b> The LocalTopology
    ///     collection fixture starts the real SeaweedFS in this same process, Docker Desktop for
    ///     Windows holds the bind-mounted <c>.seaweedfs/s3.json</c> open while it runs, and
    ///     <c>SelfServeSignUpIsOneDecisionOnAllThreeSides</c> reached
    ///     <see cref="CyberCloudTopology.Compose" />'s "written only when it differs" guard at that
    ///     moment: the guard's own <c>File.ReadAllText</c> threw "being used by another process", and a
    ///     model-only test failed with a message about a container it never started. The guard now
    ///     treats a file it cannot read as one it wrote — the only writer is that line and the content
    ///     is a constant — and this test holds the file the way the container does, so the repair is
    ///     exercised on a machine with no Docker at all rather than only when the fixture's timing
    ///     lines up.
    /// </remarks>
    [Fact]
    public void TheModelBuildsWhileAnotherProcessHoldsTheObjectStoreIdentityFile() {
        var file = Path.Combine(AppHostDirectory, ".seaweedfs", "s3.json");

        // The first Model() writes the file if it is not there; every later one compares and leaves it.
        Model();
        File.Exists(file).ShouldBeTrue("Compose did not write the SeaweedFS identity file it mounts");

        // ⚠ FileShare.None is the lock the bind mount takes: no reader, no writer, until this handle
        // closes. Held across the whole Compose, which is the shape the fixture's timing produced.
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) {
            var built = Model();

            built.Names.ShouldContain(
                CyberCloudResources.ObjectStore,
                "Compose ran with the identity file held open and lost the object store on the way"
            );
        }
    }

    /// <summary>
    ///     The model builds when several Compose calls start at once over an AppHost directory that
    ///     has never run — the fresh checkout every CI run is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The half the test above does not reach, found by the review of it.</b>
    ///         <see cref="TheModelBuildsWhileAnotherProcessHoldsTheObjectStoreIdentityFile" /> holds a
    ///         file that exists, so it exercises the guard's <i>read</i>; when
    ///         <c>.seaweedfs/s3.json</c> does not exist yet the guard reads nothing and every Compose
    ///         writes, and <c>File.WriteAllText</c> opens with <c>FileShare.Read</c> — so the fixture's
    ///         Compose and this class's, started by xUnit at the same moment, failed
    ///         <c>TheIssuerIsOneStringOnAllThreeSides</c> 53 ms in with "being used by another process"
    ///         out of <c>File.WriteAllText</c>, 2 of 2 times on a fresh worktree and never with the file
    ///         present, which is the only state the first fix was measured in (the file is gitignored).
    ///     </para>
    ///     <para>
    ///         So this test makes its own fresh checkout — a temporary AppHost directory with no
    ///         <c>.seaweedfs</c> under it — and composes eight models over it at once. Without the
    ///         frontends, because the portal is found relative to the AppHost directory and there is
    ///         none above a temporary one; the object store is what is under test and it stays. Sabotage
    ///         checked: with the lock and the catch removed from
    ///         <c>CyberCloudTopology.EnsureObjectStoreIdentityFile</c> this fails on the first run
    ///         with the measured message.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task TheModelBuildsWhenManyComposeAtOnceOnAFreshCheckout() {
        var appHostDirectory = Path.Combine(Path.GetTempPath(), "cybercloud-apphost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appHostDirectory);

        try {
            var file = Path.Combine(appHostDirectory, ".seaweedfs", "s3.json");
            File.Exists(file).ShouldBeFalse("a directory made a moment ago already holds the identity file, and the race this test runs needs it absent");

            var composed = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => Task.Run(() => ModelOver(appHostDirectory, $"--{CyberCloudResources.FrontendsKey}=false"), TestContext.Current.CancellationToken))
            );

            foreach (var built in composed) {
                built.Names.ShouldContain(CyberCloudResources.ObjectStore, "a Compose that raced another on the identity file lost the object store on the way");
            }

            File.Exists(file).ShouldBeTrue("eight Compose calls over an empty AppHost directory and none of them wrote the identity file");
        } finally {
            Directory.Delete(appHostDirectory, recursive: true);
        }
    }

    /// <summary>The proxy file's entries, keyed by the path each forwards.</summary>
    static Dictionary<string, JsonElement> ReadProxy(string relative) {
        var path = Path.Combine(PortalRoot, relative);
        File.Exists(path).ShouldBeTrue($"{path} is the file `ng serve` reads through angular.json's proxyConfig, and it is gone");

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
    }

    static int TargetPortOf(JsonElement entry) => new Uri(entry.GetProperty("target").GetString()!).Port;

    /// <summary>The <c>port</c> angular.json's serve target pins for a project.</summary>
    static int ServePortOf(string project) {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(PortalRoot, "angular.json")));

        return document.RootElement
            .GetProperty("projects").GetProperty(project)
            .GetProperty("architect").GetProperty("serve").GetProperty("options").GetProperty("port")
            .GetInt32();
    }
}
