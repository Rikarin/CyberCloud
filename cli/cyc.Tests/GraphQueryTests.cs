namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>cyc graph query</c> — the resource graph's KQL from the terminal, docs/plan/08 § The
///     resource-graph projection and docs/plan/21 § Grammar.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion is about a request that leaves the process</b>, as <c>PagingTests</c>
///     puts it: the address, the verb, the body's three members and the way a <c>nextLink</c> is
///     followed are what the gateway reads, and a command that declared them and did not send them
///     would pass a test of its help text.
/// </remarks>
public sealed class GraphQueryTests {
    const string Tenant = "aaaaaaaa-0000-0000-0000-000000000001";
    const string Kql = "resources | where type =~ 'cybercloud.dbforpostgresql/servers' | project name, location";
    const string Address = "/tenants/" + Tenant + "/providers/CyberCloud.ResourceGraph/resources";

    const string OnePage = """
                           {"columns":[{"name":"name","type":"string"},{"name":"location","type":"string"}],
                            "value":[{"name":"pg-main","location":"eu-central"}]}
                           """;

    [Fact]
    public async Task TheQueryIsPostedAsTheBodyToTheTenantsGraphAddress() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Json(HttpStatusCode.OK, OnePage));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync("graph", "query", Kql, "--tenant", Tenant, "--top", "10", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok, host.Stderr);

        var request = transport.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsolutePath.ShouldBe(Address);
        request.Uri.Query.ShouldContain("api-version=");

        using var body = JsonDocument.Parse(request.Body);
        body.RootElement.GetProperty("query").GetString().ShouldBe(Kql);
        body.RootElement.GetProperty("$top").GetInt32().ShouldBe(10);
        body.RootElement.TryGetProperty("$skipToken", out _).ShouldBeFalse("no token was typed, so none is sent");

        // The rows reach stdout as the gateway rendered them, columns included.
        using var output = JsonDocument.Parse(host.Stdout);
        output.RootElement.GetProperty("value")[0].GetProperty("name").GetString().ShouldBe("pg-main");
        output.RootElement.GetProperty("columns")[1].GetProperty("name").GetString().ShouldBe("location");
    }

    [Fact]
    public async Task TheTenantComesFromTheProfileWhenNoFlagIsTyped() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Json(HttpStatusCode.OK, OnePage));
        using var host = TestHost.Create(transport, config: $"[default]\ntenant = {Tenant}\n");

        var code = await host.RunAsync("graph", "query", Kql, "--output", "none");

        code.ShouldBe((int)ExitCode.Ok, host.Stderr);
        transport.Requests[0].Uri.AbsolutePath.ShouldBe(Address);
    }

    [Fact]
    public async Task NoTenantAnywhereIsAUsageErrorThatNamesTheThreeWaysToSupplyOne() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Json(HttpStatusCode.OK, OnePage));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync("graph", "query", Kql, "--output", "none");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("--tenant");
        host.Stderr.ShouldContain("CYC_TENANT");
        host.Stderr.ShouldContain("~/.cyc/config");
        transport.RequestCount.ShouldBe(0, "nothing leaves the process without an address");
    }

    [Fact]
    public async Task AllFollowsTheNextLinkByPostingTheSameQueryWithItsSkipToken() {
        var transport = new ScriptedTransport(static (_, index) => index switch {
                0 => Responses.Json(
                    HttpStatusCode.OK,
                    """{"columns":[{"name":"name","type":"string"}],"value":[{"name":"a"}],"nextLink":"https://api.cybercloud.io"""
                    + Address
                    + """?api-version=2026-08-01&$top=1&$skipToken=1.0123456789abcdef"}"""
                ),
                1 => Responses.Json(
                    HttpStatusCode.OK,
                    """{"columns":[{"name":"name","type":"string"}],"value":[{"name":"b"}],"nextLink":"https://api.cybercloud.io"""
                    + Address
                    + """?api-version=2026-08-01&$top=1&$skipToken=2.0123456789abcdef"}"""
                ),
                _ => Responses.Json(
                    HttpStatusCode.OK,
                    """{"columns":[{"name":"name","type":"string"}],"value":[{"name":"c"}]}"""
                )
            }
        );

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "graph",
            "query",
            Kql,
            "--tenant",
            Tenant,
            "--top",
            "1",
            "--all",
            "--output",
            "json"
        );

        code.ShouldBe((int)ExitCode.Ok, host.Stderr);
        transport.RequestCount.ShouldBe(3);

        // ⚠ Every page is a POST of the same query to the same address — never a GET of the link —
        // with the token the previous page's nextLink carried and the caller's own $top.
        foreach (var request in transport.Requests) {
            request.Method.ShouldBe(HttpMethod.Post);
            request.Uri.AbsolutePath.ShouldBe(Address);
            JsonDocument.Parse(request.Body).RootElement.GetProperty("query").GetString().ShouldBe(Kql);
            JsonDocument.Parse(request.Body).RootElement.GetProperty("$top").GetInt32().ShouldBe(1);
        }

        JsonDocument.Parse(transport.Requests[0].Body).RootElement.TryGetProperty("$skipToken", out _).ShouldBeFalse();
        JsonDocument.Parse(transport.Requests[1].Body)
            .RootElement.GetProperty("$skipToken")
            .GetString()
            .ShouldBe("1.0123456789abcdef");
        JsonDocument.Parse(transport.Requests[2].Body)
            .RootElement.GetProperty("$skipToken")
            .GetString()
            .ShouldBe("2.0123456789abcdef");

        using var output = JsonDocument.Parse(host.Stdout);
        output.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(static x => x.GetProperty("name").GetString())
            .ShouldBe(["a", "b", "c"]);
        output.RootElement.GetProperty("columns").GetArrayLength().ShouldBe(1);
        output.RootElement.TryGetProperty("nextLink", out _).ShouldBeFalse("there is no next page after the last");
    }

    [Fact]
    public async Task ASkipTokenTypedByHandIsSentInTheBody() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Json(HttpStatusCode.OK, OnePage));
        using var host = TestHost.Create(transport);

        await host.RunAsync(
            "graph",
            "query",
            Kql,
            "--tenant",
            Tenant,
            "--skip-token",
            "50.0123456789abcdef",
            "--output",
            "none"
        );

        JsonDocument.Parse(transport.Requests[0].Body)
            .RootElement.GetProperty("$skipToken")
            .GetString()
            .ShouldBe("50.0123456789abcdef");
    }

    [Fact]
    public async Task ARefusedQueryIsTheGatewaysSentenceAndExitOne() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Error(
                HttpStatusCode.BadRequest,
                "InvalidRequestBody",
                "'mv-expand' is not in the resource graph's KQL subset (an operator it does not translate)."
            )
        );

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "graph",
            "query",
            "resources | mv-expand tags",
            "--tenant",
            Tenant,
            "--output",
            "json"
        );

        code.ShouldBe((int)ExitCode.ClientError, host.Stderr);
        host.Stderr.ShouldContain("mv-expand");
        host.Stderr.ShouldContain("InvalidRequestBody");
    }

    [Fact]
    public async Task OnePageWithMoreBehindItSaysSoOnStderrAndNotOnStdout() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Json(
                HttpStatusCode.OK,
                """{"columns":[],"value":[{"name":"a"}],"nextLink":"https://api.cybercloud.io/x?api-version=2026-08-01&$skipToken=1.0"}"""
            )
        );

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync("graph", "query", Kql, "--tenant", Tenant, "--output", "json");

        code.ShouldBe((int)ExitCode.Ok, host.Stderr);
        host.Stderr.ShouldContain("--all");
        host.Stderr.ShouldContain("--skip-token");
        JsonDocument.Parse(host.Stdout).RootElement.GetProperty("nextLink").GetString().ShouldNotBeNull();
    }

    [Fact]
    public void TheGroupIsReservedSoNoProviderCanShadowIt() {
        // The generated tree's groups come from provider namespaces; a provider called
        // CyberCloud.Graph would otherwise mean two things by `cyc graph`.
        using var test = TestHost.Create();

        Should.Throw<CycUsageException>(() => CommandTree.Build(
                test.Host,
                GlobalOptions.For(test.Host.Catalog),
                ReservedGroupTests.TreeWith("graph")
            )
        )
            .Message.ShouldContain("'graph'");
    }
}
