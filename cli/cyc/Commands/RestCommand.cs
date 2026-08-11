using System.CommandLine;
using CyberCloud.Cli.Execution;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc rest</c> — docs/plan/21 § Grammar's <i>"the escape hatch for anything not yet a
///     verb"</i>.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/21 is emphatic about why it exists: <i>"⚠ <c>cyc rest</c> matters more than it
///         looks. A generated CLI always lags the API by a release; without a raw escape hatch the
///         answer to 'how do I call the new endpoint' is 'wait'. With it, the CLI is never a
///         blocker."</i>
///     </para>
///     <para>
///         ⚠ <b>Raw means untyped, not unauthenticated.</b> This command builds an
///         <see cref="HttpRequestMessage" /> and hands it to the same
///         <see cref="CyberCloudPipeline" /> every generated verb uses, so it carries the bearer
///         token, the correlation id and the api-version, and it retries a <c>429</c> with the
///         service's <c>Retry-After</c>. A command that opened its own <c>HttpClient</c> would be a
///         second, worse client — one whose requests are invisible to the platform's own
///         correlation, and whose 429s hammer a service that asked it to wait.
///     </para>
/// </remarks>
static class RestCommand {
    /// <summary>Builds the command.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree, for the api-version the request carries.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        var method = new Option<string>("--method", "-m") {
            Description = "The HTTP method. Defaults to GET.",
            DefaultValueFactory = _ => "GET",
        };

        method.AcceptOnlyFromAmong("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS");

        var uri = new Option<string>("--uri", "-u") {
            Description = "The path, relative to the endpoint — /tenants/…/providers/… — or an absolute URL.",
            Required = true,
        };

        var body = new Option<string>("--body", "-b") {
            Description = "The request body as JSON, @file to read a file, or - to read stdin.",
        };

        var headers = new Option<string[]>("--header") {
            Description = "An extra request header, as name=value. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("rest", "Call the API directly. Authenticated, retried and correlated — just untyped.") {
            method, uri, body, headers,
        };

        command.SetAction(async (parse, cancellationToken) => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);

            using var client = invocation.CreateClient(invocation.Settings.Get("tenant"));
            var context = client.Context;

            var target = Resolve(context.Endpoint, parse.GetRequiredValue(uri));
            using var request = context.CreateRequest(new HttpMethod(parse.GetValue(method) ?? "GET"), target);

            foreach (var header in parse.GetValue(headers) ?? [])
                AddHeader(request, header);

            if (ReadBody(invocation, parse.GetValue(body)) is { } content)
                CyberCloudClientContext.SetJsonBody(request, content);

            invocation.Trace($"{request.Method} {Redaction.Url(target)}");

            var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

            invocation.Trace($"{response.Status} {response.ReasonPhrase} (request id {response.ServiceRequestId ?? "none"})");

            foreach (var header in response.Headers)
                invocation.Trace("< " + Redaction.Header(header.Key, header.Value));

            if (response.IsError)
                throw CycRequestException.From(response, flag: null);

            using var parsed = ResponseBody.Parse(response);
            invocation.Render(parsed.Value);

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     Resolves <c>--uri</c> against the endpoint.
    /// </summary>
    /// <exception cref="CycUsageException">
    ///     An absolute URL points somewhere else. ⚠ Refused rather than followed: the pipeline
    ///     attaches this platform's bearer token to whatever it is given, and a token sent to
    ///     <c>https://example.com/</c> is a token that has left the building.
    /// </exception>
    static Uri Resolve(Uri endpoint, string value) {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            return new Uri(endpoint, value);

        if (!string.Equals(absolute.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase))
            throw new CycUsageException(
                $"--uri names {absolute.Host} and this profile's endpoint is {endpoint.Host}. cyc will not "
                + "send your access token to another host. Change the endpoint with 'cyc config set "
                + "endpoint …' if that is really where the API is.");

        return absolute;
    }

    static void AddHeader(HttpRequestMessage request, string header) {
        var separator = header.IndexOf('=', StringComparison.Ordinal);

        if (separator <= 0)
            throw new CycUsageException($"--header takes name=value pairs; '{header}' has no '='.");

        var name = header[..separator];

        // ⚠ The pipeline owns Authorization. Letting --header set it would be a way to send an
        // arbitrary token through a command that advertises itself as authenticated, and the header
        // would be overwritten by BearerTokenHandler anyway — so it would fail confusingly rather
        // than dangerously, which is still not a good answer.
        if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
            throw new CycUsageException(
                "--header cannot set Authorization. cyc rest is authenticated by the SDK's pipeline with "
                + "the signed-in credential — that is the difference between it and curl.");

        request.Headers.TryAddWithoutValidation(name, header[(separator + 1)..]);
    }

    /// <summary>
    ///     Reads the body: inline JSON, <c>@file</c>, or <c>-</c> for stdin.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>@file</c> and <c>-</c> exist so that a body containing a secret never has to be an
    ///     argument. docs/plan/21 § Grammar's <c>cyc vault secret set --value-from-stdin</c> is the
    ///     same instinct one level down.
    /// </remarks>
    static byte[]? ReadBody(CycInvocation invocation, string? value) {
        if (string.IsNullOrEmpty(value))
            return null;

        var text = value switch {
            "-" => Console.In.ReadToEnd(),
            _ when value.StartsWith('@') => ReadFile(value[1..]),
            _ => value,
        };

        try {
            using var document = JsonDocument.Parse(text);
        } catch (JsonException e) {
            throw new CycUsageException($"--body is not valid JSON: {e.Message}", e);
        }

        invocation.Trace($"body: {text.Length} byte(s)");

        return Encoding.UTF8.GetBytes(text);
    }

    static string ReadFile(string path) {
        try {
            return File.ReadAllText(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new CycUsageException($"'{path}' could not be read: {e.Message}", e);
        }
    }
}
