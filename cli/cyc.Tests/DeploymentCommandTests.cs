namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>cyc deployment create</c> and <c>cyc deployment what-if</c> — a template file deployed to a
///     resource group, and its dry run. docs/plan/08 § Long-running operations.
/// </summary>
/// <remarks>
///     ⚠ <b>Every assertion is about what leaves the process</b>, as <c>GraphQueryTests</c> puts it: the
///     address, the verb, and the body the files became. The gateway reads those; a command that
///     declared <c>--template-file</c> and sent something else would pass a test of its help text.
/// </remarks>
public sealed class DeploymentCommandTests : IDisposable {
    const string Address =
        "/tenants/t/subscriptions/s/resourceGroups/prod/providers/CyberCloud.Resources/deployments/rollout";

    const string Template = """
                            {
                              "parameters": { "prefix": { "type": "string" } },
                              "resources": [
                                { "type": "CyberCloud.Sample/widgets", "apiVersion": "2026-08-01",
                                  "name": "[concat(parameters('prefix'), '-a')]", "properties": { "message": "hi" } }
                              ]
                            }
                            """;

    const string Parameters = """{ "prefix": { "value": "shop" } }""";

    readonly string directory = Path.Combine(Path.GetTempPath(), "cyc-deployment-tests", Guid.NewGuid().ToString("N"));

    public DeploymentCommandTests() => Directory.CreateDirectory(directory);

    public void Dispose() {
        try {
            Directory.Delete(directory, true);
        } catch (IOException) {
            // A test host still holding the file is not a test failure.
        }
    }

    string Write(string name, string text) {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task CreatePutsTheFilesAsTheBodysTextWaitsAndPrintsTheDeployment() {
        var transport = new ScriptedTransport(static (_, index) => index switch {
                0 => Responses.Accepted("https://api.cybercloud.io/operations/op-1"),
                1 => Responses.Json(
                    HttpStatusCode.OK,
                    """{"status":"Running","percentComplete":50,"progress":[{"at":"2026-09-23T10:00:00Z","step":"deployed","message":"step 1 of 2 succeeded","percentComplete":50}]}"""
                ),
                2 => Responses.Json(HttpStatusCode.OK, """{"status":"Succeeded"}"""),
                _ => Responses.Json(HttpStatusCode.OK, """{"name":"rollout","properties":{"outputResources":["a"]}}""")
            }
        );

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "deployment",
            "create",
            "--name",
            "rollout",
            "--resource-group",
            "prod",
            "--subscription",
            "s",
            "--tenant",
            "t",
            "--template-file",
            Write("template.json", Template),
            "--parameters-file",
            Write("parameters.json", Parameters),
            "--output",
            "json"
        );

        code.ShouldBe((int)ExitCode.Ok, host.Stderr);

        var put = transport.Requests[0];
        put.Method.ShouldBe(HttpMethod.Put);
        put.Uri.AbsolutePath.ShouldBe(Address);
        put.Uri.Query.ShouldContain("api-version=");

        // ⚠ The files travel as TEXT inside the body — the registry declares both as strings — and
        // byte for byte, so a template's own formatting and comments-free JSON reach the server as typed.
        using (var body = JsonDocument.Parse(put.Body)) {
            var properties = body.RootElement.GetProperty("properties");
            properties.GetProperty("template").GetString().ShouldBe(Template);
            properties.GetProperty("parameters").GetString().ShouldBe(Parameters);
        }

        host.Stderr.ShouldContain("step 1 of 2 succeeded", Case.Sensitive, "the deployment's progress was not streamed.");

        using var output = JsonDocument.Parse(host.Stdout);
        output.RootElement.GetProperty("properties").GetProperty("outputResources")[0].GetString().ShouldBe("a");
    }

    [Fact]
    public async Task WhatIfPostsTheSameBodyToTheActionAndPrintsTheChanges() {
        const string Answer =
            """{"status":"Succeeded","changes":[{"resourceId":"/x/a","resourceType":"CyberCloud.Sample/widgets","changeType":"Create","delta":[]}]}""";

        var transport = new ScriptedTransport(static (_, _) => Responses.Json(HttpStatusCode.OK, Answer));
        using var host = TestHost.Create(
            transport,
            config: "[default]\ntenant = t\nsubscription = s\nresource-group = prod\n"
        );

        var code = await host.RunAsync(
            "deployment",
            "what-if",
            "--name",
            "rollout",
            "--template-file",
            Write("template.json", Template),
            "--output",
            "json"
        );

        code.ShouldBe((int)ExitCode.Ok, host.Stderr);

        var request = transport.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsolutePath.ShouldBe(Address + "/whatIf");

        using (var body = JsonDocument.Parse(request.Body)) {
            body.RootElement.GetProperty("properties").GetProperty("template").GetString().ShouldBe(Template);
            body.RootElement.GetProperty("properties").TryGetProperty("parameters", out _).ShouldBeFalse(
                "no parameters file was given, so none is sent"
            );
        }

        using var output = JsonDocument.Parse(host.Stdout);
        output.RootElement.GetProperty("changes")[0].GetProperty("changeType").GetString().ShouldBe("Create");
    }

    [Fact]
    public async Task ATemplateFileThatIsNotJsonIsAUsageErrorBeforeAnythingIsSent() {
        var transport = new ScriptedTransport(static (_, _) => throw new ShouldAssertException("a request was sent."));
        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "deployment",
            "what-if",
            "--name",
            "rollout",
            "--resource-group",
            "prod",
            "--subscription",
            "s",
            "--tenant",
            "t",
            "--template-file",
            Write("broken.json", "{ \"resources\": [ ")
        );

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("--template-file");
        host.Stderr.ShouldContain("is not JSON");
        transport.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task ARefusalPointingIntoTheTemplateNamesTheTemplateFile() {
        var transport = new ScriptedTransport(static (_, _) => Responses.Error(
                HttpStatusCode.BadRequest,
                "InvalidRequestBody",
                "The template calls 'uniqueString()', which is not supported.",
                "/properties/template"
            )
        );

        using var host = TestHost.Create(transport);

        var code = await host.RunAsync(
            "deployment",
            "create",
            "--name",
            "rollout",
            "--resource-group",
            "prod",
            "--subscription",
            "s",
            "--tenant",
            "t",
            "--template-file",
            Write("template.json", Template)
        );

        code.ShouldBe((int)ExitCode.ClientError);
        host.Stderr.ShouldContain("uniqueString()");
        host.Stderr.ShouldContain("--template-file");
    }
}
