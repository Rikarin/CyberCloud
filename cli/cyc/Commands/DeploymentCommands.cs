using CyberCloud.Cli.Execution;
using CyberCloud.Cli.VerbTree;
using System.CommandLine;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc deployment create</c> and <c>cyc deployment what-if</c> — a template file deployed to a
///     resource group, and the dry run of it. docs/plan/08 § Long-running operations and docs/plan/21
///     § Grammar.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Hand-written beside the generated <c>cyc resources deployments</c>, and for one reason:
///         the files.</b> The registry describes a deployment's template and parameters as strings
///         (<c>Deployments.TemplatePointer</c> — the schema vocabulary has no free-form object), so the
///         generated verb takes <c>--template '{…}'</c>, which is a whole JSON document on a command
///         line. <c>--template-file</c> reads it from disk instead, the way <c>az deployment group
///         create</c> does, and checks it is JSON before a socket is opened. Everything after the body
///         — the address, the pipeline, the wait and its progress — is the generated verbs' own code
///         (<see cref="ResourceVerb.WaitForResourceAsync" />), so the two commands cannot drift apart
///         on anything but how the body is assembled.
///     </para>
///     <para>
///         ⚠ <b>The what-if prints the server's answer as it is.</b> Azure's <c>what-if</c> renders a
///         coloured diff; <c>--output table</c> and <c>--query</c> already turn the JSON into whatever
///         a person or a script wants, and a second renderer here would be a second opinion on what
///         the platform said.
///     </para>
/// </remarks>
static class DeploymentCommands {
    /// <summary>The group's name — reserved in <c>CommandTree.ReservedGroups</c>.</summary>
    public const string GroupName = "deployment";

    const string ProviderPath = "providers/CyberCloud.Resources/deployments";

    /// <summary>Builds the group.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree, for the api-version the requests carry.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        return new(
            GroupName,
            "Deploy a template of resources to a resource group, in dependency order and as you, or see what "
            + "deploying it would change."
        ) {
            Create(host, globals, tree),
            WhatIf(host, globals, tree)
        };
    }

    static Command Create(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var flags = new Flags();

        var noWait = new Option<bool>("--no-wait") {
            Description = "Return as soon as the deployment is accepted, printing its operation id."
        };

        var command = new Command(
            "create",
            "Create or re-run a deployment from a template file. Waits for every resource, streaming progress "
            + "to stderr; a failure stops at the resource that failed and leaves what was created."
        );

        flags.AddTo(command);
        command.Options.Add(noWait);

        command.SetAction(async (parse, cancellationToken) => {
                var invocation = CycRunner.Bind(host, globals, tree, parse);
                var request = flags.Read(invocation, parse);

                using var client = invocation.CreateClient(request.Tenant);
                var context = client.Context;
                var uri = new Uri(context.Endpoint, request.Path + "?api-version=" + invocation.ApiVersion);

                var response = await SendAsync(invocation, context, HttpMethod.Put, uri, request.Body, cancellationToken)
                    .ConfigureAwait(false);

                if (response.Status != 202) {
                    using var done = ResponseBody.Parse(response);
                    invocation.Render(done.Value);
                    return (int)ExitCode.Ok;
                }

                if (parse.GetValue(noWait)) {
                    invocation.Render(ResourceVerb.AcceptedPayload(response));
                    return (int)ExitCode.Ok;
                }

                return await ResourceVerb.WaitForResourceAsync(
                    invocation,
                    $"deployment create {uri.AbsolutePath}",
                    context,
                    uri,
                    response,
                    cancellationToken
                ).ConfigureAwait(false);
            }
        );

        return command;
    }

    static Command WhatIf(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var flags = new Flags();

        var command = new Command(
            "what-if",
            "Say what deploying a template would do — per resource: Create, Modify or NoChange, with the property "
            + "diff — reading each one as you. Writes nothing; the deployment need not exist."
        );

        flags.AddTo(command);

        command.SetAction(async (parse, cancellationToken) => {
                var invocation = CycRunner.Bind(host, globals, tree, parse);
                var request = flags.Read(invocation, parse);

                using var client = invocation.CreateClient(request.Tenant);
                var context = client.Context;
                var uri = new Uri(context.Endpoint, request.Path + "/whatIf?api-version=" + invocation.ApiVersion);

                var response = await SendAsync(invocation, context, HttpMethod.Post, uri, request.Body, cancellationToken)
                    .ConfigureAwait(false);

                using var answer = ResponseBody.Parse(response);
                invocation.Render(answer.Value);

                return (int)ExitCode.Ok;
            }
        );

        return command;
    }

    static async Task<Response> SendAsync(
        CycInvocation invocation,
        CyberCloudClientContext context,
        HttpMethod method,
        Uri uri,
        byte[] body,
        CancellationToken cancellationToken
    ) {
        using var request = context.CreateRequest(method, uri);
        CyberCloudClientContext.SetJsonBody(request, body);

        invocation.Trace($"{method} {Redaction.Url(uri)}");

        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        invocation.Trace(
            $"{response.Status} {response.ReasonPhrase} (request id {response.ServiceRequestId ?? "none"})"
        );

        if (response.IsError) {
            // A refusal that points into the template or the parameters names the file it came from,
            // which is what the person has open.
            var target = CyberCloudError.TryParse(response.Content)?.Target;

            throw CycRequestException.From(
                response,
                target switch {
                    "/properties/template" => "--template-file",
                    "/properties/parameters" => "--parameters-file",
                    _ => null
                }
            );
        }

        return response;
    }

    /// <summary>The address and the body, as both commands read them.</summary>
    sealed record DeploymentRequest(string Tenant, string Path, byte[] Body);

    /// <summary>The flags both commands take.</summary>
    sealed class Flags {
        readonly Option<string> name = new("--name") {
            Description = "The deployment's name within its resource group. Re-using a name re-runs that deployment.",
            Required = true
        };

        readonly Option<string> resourceGroup = new("--resource-group") {
            Description = "The resource group the deployment belongs to. Defaults to the profile's resource-group."
        };

        readonly Option<string> subscription = new("--subscription") {
            Description = "The subscription. Defaults to the profile's."
        };

        readonly Option<string> tenant = new("--tenant") {
            Description = "The tenant. Defaults to the profile's."
        };

        readonly Option<FileInfo> template = new("--template-file") {
            Description = "The template: parameters, variables and resources with type, name, apiVersion, "
                + "properties and dependsOn. Expressions are parameters(), variables(), resourceId() and concat().",
            Required = true
        };

        readonly Option<FileInfo> parameters = new("--parameters-file") {
            Description = "The parameter values: { \"name\": { \"value\": … } }, or a parameters file with "
                + "$schema and parameters."
        };

        public void AddTo(Command command) {
            command.Options.Add(name);
            command.Options.Add(resourceGroup);
            command.Options.Add(subscription);
            command.Options.Add(tenant);
            command.Options.Add(template);
            command.Options.Add(parameters);
        }

        public DeploymentRequest Read(CycInvocation invocation, ParseResult parse) {
            var tenantId = Setting(invocation, parse.GetValue(tenant), "tenant", "--tenant");
            var subscriptionId = Setting(invocation, parse.GetValue(subscription), "subscription", "--subscription");
            var group = Setting(invocation, parse.GetValue(resourceGroup), "resource-group", "--resource-group");

            var templateText = ReadJson(parse.GetRequiredValue(template), "--template-file");
            var parametersText = parse.GetValue(parameters) is { } file ? ReadJson(file, "--parameters-file") : null;

            var path = $"/tenants/{Uri.EscapeDataString(tenantId)}/subscriptions/{Uri.EscapeDataString(subscriptionId)}"
                + $"/resourceGroups/{Uri.EscapeDataString(group)}/{ProviderPath}/{Uri.EscapeDataString(parse.GetRequiredValue(name))}";

            return new(tenantId, path, Body(templateText, parametersText));
        }

        /// <summary>
        ///     <c>{ "properties": { "template": "…", "parameters": "…" } }</c> — the files' text as
        ///     strings, which is how the registry declares them.
        /// </summary>
        /// <remarks>
        ///     Written by hand rather than serialised: this binary is NativeAOT and the reflection
        ///     serialiser is IL2026/IL3050 here — <c>GraphCommands</c> does the same.
        /// </remarks>
        static byte[] Body(string template, string? values) {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(template.Length + 64);

            using (var writer = new Utf8JsonWriter(buffer)) {
                writer.WriteStartObject();
                writer.WriteStartObject("properties");
                writer.WriteString("template", template);

                if (values is not null) {
                    writer.WriteString("parameters", values);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        static string ReadJson(FileInfo file, string flag) {
            string text;

            try {
                text = File.ReadAllText(file.FullName);
            } catch (IOException exception) {
                throw new CycUsageException($"{flag} '{file.FullName}' cannot be read: {exception.Message}");
            } catch (UnauthorizedAccessException exception) {
                throw new CycUsageException($"{flag} '{file.FullName}' cannot be read: {exception.Message}");
            }

            try {
                using var parsed = JsonDocument.Parse(text);

                if (parsed.RootElement.ValueKind != JsonValueKind.Object) {
                    throw new CycUsageException($"{flag} '{file.FullName}' is JSON but not an object.");
                }
            } catch (JsonException exception) {
                throw new CycUsageException($"{flag} '{file.FullName}' is not JSON: {exception.Message}");
            }

            return text;
        }

        static string Setting(CycInvocation invocation, string? flag, string setting, string flagName) {
            if (flag is { Length: > 0 }) {
                return flag;
            }

            if (invocation.Settings.Get(setting) is { Length: > 0 } configured) {
                return configured;
            }

            throw new CycUsageException(
                $"{flagName} is required and nothing supplies it. Pass {flagName}, set "
                + Configuration.CycSettings.VariableFor(setting)
                + $", or put '{setting} = …' in profile '{invocation.Settings.Profile}' of ~/.cyc/config."
            );
        }
    }
}
