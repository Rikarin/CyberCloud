namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>cyc … list</c> can reach the second page — issue #64.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every assertion here is about a request that leaves the process, never about the
///         verb tree alone.</b> The defect this suite closes was not that the tree lacked a member:
///         it was that a flag could be declared, accepted and parsed and then <i>not sent</i>,
///         because <c>CliFlag</c> bound a body pointer or a path placeholder and nothing else. A test
///         reading the tree would have been green throughout.
///     </para>
///     <para>
///         ⚠ <b>A gateway ignores a query parameter it does not recognise.</b> So <c>$skip-token</c>
///         where the platform reads <c>$skipToken</c> is not an error anywhere — it is a <c>200</c>
///         holding page one, and <c>--all</c> over it would page for ever. The exact spelling on the
///         wire is the property, which is why these read <c>Uri.Query</c> rather than a flag's value.
///     </para>
/// </remarks>
public sealed class PagingTests {
    [Fact]
    public async Task TheListVerbOffersTheDocumentsOwnQueryParameters() {
        var list = TestHost.Catalog().Select(null).Groups["sample"].Commands["widgets"].Verbs["list"];

        list.Paged.ShouldBeTrue();

        // ⚠ The wire names, sigil and all. The flag is `--skip-token` and the parameter is
        // `$skipToken`; a host deriving one from the other would be re-deriving a convention the
        // emitter owns.
        list.Flags.Single(x => x.Name == "--top").QueryParameter.ShouldBe("$top");
        list.Flags.Single(x => x.Name == "--skip-token").QueryParameter.ShouldBe("$skipToken");

        // ⚠ NOT `--api-version`. Every operation declares it, so reading the collection's parameters
        // emitted a required verb flag shadowing the global one of that name — the verb would have
        // refused every invocation that did not repeat a value the pipeline already sends.
        list.Flags.ShouldNotContain(x => x.Name == "--api-version");

        // `--all` is host behaviour and sends nothing, so it is named separately from the flags that
        // go on the wire.
        list.PageFlags.ShouldBe(["--all"]);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task PagingFlagsAreSentAsQueryParameters() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, """{"value":[]}"""));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "sample", "widgets", "list",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
            "--top", "3", "--skip-token", "a/b c", "--output", "none");

        code.ShouldBe((int)ExitCode.Ok);

        var query = transport.Requests[0].Uri.Query;

        query.ShouldContain("%24top=3");

        // ⚠ Percent-encoded. A continuation is a resource path, so it contains `/`, and an unescaped
        // one produces a URL whose query string the gateway re-parses into a different value than
        // the one it handed out.
        query.ShouldContain("%24skipToken=a%2Fb%20c");
    }

    [Fact]
    public async Task APagingFlagThatWasNotTypedIsNotSent() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, """{"value":[]}"""));
        using var host = TestHost.Create(transport);

        await host.RunAsync(
            "sample", "widgets", "list",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--output", "none");

        // The same rule the body builder follows: sending an untyped `$top` would replace the
        // platform's page size with whatever this build was generated against, and the platform
        // clamps rather than refuses — so nothing would say the number had changed.
        transport.Requests[0].Uri.Query.ShouldNotContain("top");
    }

    [Fact]
    public async Task OnePageWithMoreBehindItSaysSoOnStderr() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(
            HttpStatusCode.OK,
            """{"value":[{"name":"w1"}],"nextLink":"https://api.cybercloud.io/next?api-version=2026-08-01"}"""));

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "sample", "widgets", "list",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);
        transport.RequestCount.ShouldBe(1);

        // ⚠ The envelope has no `count` — a total would say how many resources exist that the caller
        // may not see — and `--output table` does not render `nextLink`. So without this line a
        // truncated listing looks exactly like a short one.
        host.Stderr.ShouldContain("--all");

        // ⚠ On stderr, not stdout. A script reading the JSON document must not find prose in it.
        host.Stdout.ShouldNotContain("--all");
    }

    [Fact]
    public async Task OneCompletePageSaysNothing() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, """{"value":[{"name":"w1"}]}"""));
        using var host = TestHost.Create(transport);

        await host.RunAsync(
            "sample", "widgets", "list",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--output", "none");

        host.Stderr.ShouldNotContain("--all");
    }

    [Fact]
    public async Task AllFollowsNextLinkToTheEndAndPrintsOneList() {
        var transport = new ScriptedTransport((_, index) => index switch {
            0 => Responses.Json(
                HttpStatusCode.OK,
                """{"value":[{"name":"w1"}],"nextLink":"https://api.cybercloud.io/p2?api-version=2026-08-01&$skipToken=w1"}"""),
            1 => Responses.Json(
                HttpStatusCode.OK,
                """{"value":[{"name":"w2"}],"nextLink":"https://api.cybercloud.io/p3?api-version=2026-08-01&$skipToken=w2"}"""),
            _ => Responses.Json(HttpStatusCode.OK, """{"value":[{"name":"w3"}]}"""),
        });

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "sample", "widgets", "list",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--all", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);
        transport.RequestCount.ShouldBe(3);

        // ⚠ Requested as it was handed out. docs/plan/10 makes nextLink an absolute URL precisely so
        // a client never has to know the endpoint's paging parameter to build the next request.
        transport.Requests[1].Uri.AbsolutePath.ShouldBe("/p2");
        transport.Requests[2].Uri.AbsolutePath.ShouldBe("/p3");

        using var printed = JsonDocument.Parse(host.Stdout);

        printed.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .ShouldBe(["w1", "w2", "w3"]);

        // ⚠ No nextLink on the result, because there is no next page. Echoing the last one would
        // hand a script a URL that returns nothing.
        printed.RootElement.TryGetProperty("nextLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AFailureOnALaterPageIsReportedRatherThanTruncatingTheList() {
        var transport = new ScriptedTransport((_, index) => index == 0
            ? Responses.Json(
                HttpStatusCode.OK,
                """{"value":[{"name":"w1"}],"nextLink":"https://api.cybercloud.io/p2?api-version=2026-08-01"}""")
            : Responses.Error(HttpStatusCode.Forbidden, "Forbidden", "The token no longer grants this."));

        using var host = TestHost.Create(transport);

        // ⚠ Exit non-zero rather than printing page one. A partial list that exits 0 is the failure
        // this whole issue is about, arrived at from the other direction.
        var code = await host.RunAsync(
            "sample", "widgets", "list",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--all", "--output", "json");

        code.ShouldNotBe((int)ExitCode.Ok);
        host.Stdout.ShouldNotContain("w1");
    }

    [Fact]
    public async Task ANestedTypeCanBeAddressedAtAll() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));
        using var host = TestHost.Create(transport);

        // ⚠ THE REGRESSION FOR A HARD-CODED TABLE OF FOUR. `ResourceVerb` filled placeholders from a
        // list of tenant/subscription/resourceGroup/resourceName, so a nested type's own ancestor
        // had no row and the command ended in "which this build of cyc does not know how to fill.
        // Upgrade cyc" — advice no newer build could have satisfied. Five of the twenty-two types
        // are nested.
        var code = await host.RunAsync(
            "network", "virtual-networks-subnets", "show",
            "--name", "web", "--virtual-networks-name", "vnet1",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--output", "none");

        code.ShouldBe((int)ExitCode.Ok);

        transport.Requests[0].Uri.AbsolutePath.ShouldBe(
            "/tenants/t/subscriptions/s/resourceGroups/prod/providers/CyberCloud.Network/virtualNetworks/vnet1/subnets/web");
    }

    [Fact]
    public async Task ANestedTypesListAddressesItsParentToo() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, """{"value":[]}"""));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "network", "virtual-networks-subnets", "list",
            "--virtual-networks-name", "vnet1",
            "--resource-group", "prod", "--subscription", "s", "--tenant", "t", "--output", "none");

        code.ShouldBe((int)ExitCode.Ok);

        // ⚠ Ends on the type rather than on a name, which is what makes it a collection address —
        // ResourceId.ParsePath refuses this and ResourceCollectionId.ParsePath requires it.
        transport.Requests[0].Uri.AbsolutePath.ShouldBe(
            "/tenants/t/subscriptions/s/resourceGroups/prod/providers/CyberCloud.Network/virtualNetworks/vnet1/subnets");
    }
}

/// <summary>
///     <c>cyc scope …</c> — the scope API on the CLI, issue #63.
/// </summary>
/// <remarks>
///     ⚠ <b>These are here rather than in the emitter's suite because the emitter's suite cannot
///     answer the question.</b> The verb tree could describe the scope group perfectly and the host
///     could still fail to build the URL or the body — which is exactly what happened to the five
///     nested resource types for as long as the host filled placeholders from a table of four. The
///     scope paths are the first this CLI has ever addressed that name no provider.
/// </remarks>
public sealed class ScopeCommandTests {
    [Fact]
    public async Task ASubscriptionIsCreatedAtTheAddressTheFlagNames() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(
            HttpStatusCode.Created,
            """{"id":"/tenants/t/subscriptions/s1","name":"Platform","type":"CyberCloud.Resources/subscriptions"}"""));

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "scope", "subscription", "create",
            "--name", "s1", "--display-name", "Platform", "--tenant", "t", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);

        var request = transport.Requests[0];

        request.Method.ShouldBe(HttpMethod.Put);
        request.Uri.AbsolutePath.ShouldBe("/tenants/t/subscriptions/s1");

        // ⚠ The property name the manager reads. ScopeBodyProperties is in the contracts assembly so
        // that the emitter and the service share one constant; this asserts the far end of that.
        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("displayName").GetString().ShouldBe("Platform");

        // ⚠ A 201 is a success. Every resource write in this CLI ends in a 202 and a poller, so the
        // one status a scope answers with is the one nothing else here produces.
        host.Stdout.ShouldContain("CyberCloud.Resources/subscriptions");
    }

    [Fact]
    public async Task AScopeCreateWithoutANameIsRefusedRatherThanTakingTheProfiles() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.Created, "{}"));

        using var host = TestHost.Create(
            transport,
            config: "[default]\ntenant = t\nsubscription = already-mine\n");

        // ⚠ THE WRITE NOBODY ASKED FOR. --subscription is profile-backed and optional on every
        // resource verb; if the scope's own segment reused it, this command line would have created
        // the subscription the profile points at. It is a usage error instead, and no request is
        // made at all.
        var code = await host.RunAsync(
            "scope", "subscription", "create", "--display-name", "Platform", "--output", "none");

        code.ShouldBe((int)ExitCode.Usage);
        transport.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task AResourceGroupIsAddressedThroughItsSubscription() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "scope", "resource-group", "create",
            "--name", "prod", "--location", "eu-central",
            "--subscription", "s", "--tenant", "t", "--output", "none");

        code.ShouldBe((int)ExitCode.Ok);
        transport.Requests[0].Uri.AbsolutePath.ShouldBe("/tenants/t/subscriptions/s/resourceGroups/prod");

        using var body = JsonDocument.Parse(transport.Requests[0].Body);
        body.RootElement.GetProperty("location").GetString().ShouldBe("eu-central");
    }

    [Fact]
    public async Task TheTenantCanBeReadFromTheProfileAndNotCreated() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));
        using var host = TestHost.Create(transport, config: "[default]\ntenant = t\n");

        (await host.RunAsync("scope", "tenant", "show", "--output", "none")).ShouldBe((int)ExitCode.Ok);
        transport.Requests[0].Uri.AbsolutePath.ShouldBe("/tenants/t");

        // ⚠ No create verb, and the reason is not that nobody wrote one: a request's tenant comes
        // from its token, so a call creating another tenant carries a token that is not that
        // tenant's and is refused before routing runs.
        (await host.RunAsync("scope", "tenant", "create", "--output", "none")).ShouldBe((int)ExitCode.Usage);
    }

    [Fact]
    public async Task AScopeVerbOffersNoWaitFlags() {
        using var host = TestHost.Create();

        // A scope converges before the call returns, so --wait would follow an operation URL that
        // answers 404. The tree marks the verb longRunning: false and the host declares the pair
        // only from waitFlags, so the flag is not merely ignored — it does not parse.
        (await host.RunAsync(
            "scope", "subscription", "create",
            "--name", "s1", "--display-name", "P", "--tenant", "t", "--wait")).ShouldBe((int)ExitCode.Usage);
    }
}
