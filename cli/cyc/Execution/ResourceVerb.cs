using System.CommandLine;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Execution;

/// <summary>
///     Runs one generated verb: fill the path, build the body, send it through the SDK's pipeline,
///     and render what comes back.
/// </summary>
/// <remarks>
///     ⚠ <b>One implementation for every verb of every provider, because the verb tree says
///     everything that differs.</b> A host with a method per verb would be a second copy of the
///     registry — the thing docs/plan/21 opens by ruling out: <i>"100 resource types × 2 surfaces ×
///     N versions is not a thing humans keep correct."</i>
/// </remarks>
static class ResourceVerb {
    /// <summary>
    ///     How each path placeholder is filled.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The emitter does not declare this mapping and it should.</b> The tree gives a path of
    ///     <c>/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/…</c>
    ///     and a flag list containing <c>--tenant</c>, <c>--subscription</c> and
    ///     <c>--resource-group</c>, and nothing connects the two: the host has to know that
    ///     <c>{resourceGroupName}</c> is what <c>--resource-group</c> fills. Reported — a flag carries
    ///     a <c>jsonPointer</c> for the body and needs the equivalent for the path.
    /// </remarks>
    static readonly (string Placeholder, string Flag, string? Setting)[] AddressMap = [
        ("tenantId", "--tenant", "tenant"),
        ("subscriptionId", "--subscription", "subscription"),
        ("resourceGroupName", "--resource-group", "resource-group"),
        ("resourceName", "--name", null),
    ];

    /// <summary>Runs the verb.</summary>
    /// <param name="invocation">The resolved context.</param>
    /// <param name="verb">The verb, as the generator described it.</param>
    /// <param name="bindings">The verb's flags.</param>
    /// <param name="waitOptions">The <c>--wait</c> and <c>--no-wait</c> options, on a long-running verb.</param>
    /// <param name="parse">The parse result.</param>
    /// <param name="cancellationToken">The token, carrying <c>--timeout</c>.</param>
    public static async Task<int> RunAsync(
        CycInvocation invocation,
        VerbTreeVerb verb,
        IReadOnlyList<FlagBinding> bindings,
        WaitOptions? waitOptions,
        ParseResult parse,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(parse);

        var tenant = Address(invocation, bindings, parse, "--tenant", "tenant", required: false);
        var path = ResolvePath(invocation, verb, bindings, parse);
        var body = RequestBody.Build(bindings, parse);

        using var client = invocation.CreateClient(tenant);
        var context = client.Context;
        var uri = new Uri(context.Endpoint, path);

        using var request = context.CreateRequest(new HttpMethod(verb.Method), uri);

        if (body is not null)
            CyberCloudClientContext.SetJsonBody(request, body);

        invocation.Trace($"{verb.Method} {Redaction.Url(uri)}");

        // ⚠ A verb the tree marks `secret` has its body kept out of the trace whatever --verbose
        // says: docs/plan/21 § Decisions bans a plaintext token cache for the same reason a shell
        // history full of key material is a leak, and a terminal scrollback is a shell history.
        if (body is not null && !verb.Secret)
            invocation.Trace($"body: {Encoding.UTF8.GetString(body)}");

        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        invocation.Trace($"{response.Status} {response.ReasonPhrase} (request id {response.ServiceRequestId ?? "none"})");

        if (response.IsError)
            throw CycRequestException.From(response, RequestBody.FlagFor(bindings, ErrorTarget(response)));

        // A verb the tree calls long-running is still allowed to finish inline: the platform answers
        // 202 when it started an operation and 200 when there was nothing to do. Branching on the
        // status rather than on the tree keeps a no-op create from waiting on a poll URL that was
        // never sent.
        if (verb.LongRunning && response.Status == 202) {
            return await WaitAsync(invocation, verb, context, uri, response, waitOptions, parse, cancellationToken)
                .ConfigureAwait(false);
        }

        using var parsed = ResponseBody.Parse(response);
        invocation.Render(parsed.Value);

        return (int)ExitCode.Ok;
    }

    /// <summary>
    ///     Follows a long-running operation, streaming its progress array to stderr as it arrives.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Progress goes to stderr, and the answer to stdout.</b> docs/plan/21 § Decisions
    ///         wants <c>--wait</c> to stream progress because <i>"this is what makes a nine-minute
    ///         cluster creation bearable in a terminal"</i>; putting those lines on stdout would put
    ///         them inside the JSON document a script is reading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Enumerating <c>GetProgressAsync</c> is what drives the polling.</b> Each turn of
    ///         the loop waits the service's <c>Retry-After</c>, polls once, and yields what that poll
    ///         added — so a line appears when it happens rather than all of them at the end.
    ///         <c>WaitStreamsProgressTests</c> asserts the interleaving by counting round trips
    ///         between lines.
    ///     </para>
    /// </remarks>
    static async Task<int> WaitAsync(
        CycInvocation invocation,
        VerbTreeVerb verb,
        CyberCloudClientContext context,
        Uri uri,
        Response accepted,
        WaitOptions? waitOptions,
        ParseResult parse,
        CancellationToken cancellationToken) {
        if (waitOptions is not null && waitOptions.NoWait(parse)) {
            invocation.Render(Accepted(accepted));

            return (int)ExitCode.Ok;
        }

        var name = $"{verb.Name} {uri.AbsolutePath}";

        // ⚠ A DELETE has no resource to read when it finishes, which is what the SDK's non-generic
        // Operation is for — asking it to GET the resource afterwards would be a 404 at the end of a
        // successful delete.
        if (string.Equals(verb.Method, "DELETE", StringComparison.Ordinal)) {
            var deletion = new Operation(context, uri, accepted, name);

            await StreamAsync(invocation, deletion.GetProgressAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            await deletion.WaitForCompletionResponseAsync(cancellationToken).ConfigureAwait(false);

            invocation.Render(Payload.Object([
                new KeyValuePair<string, Payload>("operationId", Payload.Text(deletion.Id)),
                new KeyValuePair<string, Payload>("status", Payload.Text("Succeeded")),
            ]));

            return (int)ExitCode.Ok;
        }

        var operation = new Operation<ResponseBody>(new ResponseBodyOperationSource(), context, uri, accepted, name);

        await StreamAsync(invocation, operation.GetProgressAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

        var result = await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        using var final = result.Value;
        invocation.Render(final.Value);

        return (int)ExitCode.Ok;
    }

    static async Task StreamAsync(CycInvocation invocation, IAsyncEnumerable<OperationProgress> progress, CancellationToken cancellationToken) {
        await foreach (var entry in progress.WithCancellation(cancellationToken).ConfigureAwait(false)) {
            invocation.Console.Note(entry.PercentComplete is { } percent
                ? string.Create(CultureInfo.InvariantCulture, $"  {percent,3}%  {entry.Step}: {entry.Message}")
                : $"        {entry.Step}: {entry.Message}");
        }
    }

    /// <summary>What a <c>--no-wait</c> prints: what was accepted, and how to follow it.</summary>
    static Payload Accepted(Response response) {
        var pollUrl = response.TryGetHeader(CyberCloudHeaders.AsyncOperation, out var url) ? url : null;

        return Payload.Object([
            new KeyValuePair<string, Payload>("status", Payload.Text("Accepted")),
            new KeyValuePair<string, Payload>(
                "operationId",
                pollUrl is null ? Payload.Null : Payload.Text(new Uri(pollUrl).Segments[^1].TrimEnd('/'))),
            new KeyValuePair<string, Payload>("operationUrl", pollUrl is null ? Payload.Null : Payload.Text(pollUrl)),
        ]);
    }

    static string? ErrorTarget(Response response) => CyberCloudError.TryParse(response.Content)?.Target;

    static string ResolvePath(CycInvocation invocation, VerbTreeVerb verb, IReadOnlyList<FlagBinding> bindings, ParseResult parse) {
        var path = verb.Path;

        foreach (var (placeholder, flag, setting) in AddressMap) {
            var token = "{" + placeholder + "}";

            if (!path.Contains(token, StringComparison.Ordinal))
                continue;

            var value = Address(invocation, bindings, parse, flag, setting, required: true)!;
            path = path.Replace(token, Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        var unresolved = path.IndexOf('{', StringComparison.Ordinal);

        if (unresolved < 0)
            return path;

        var end = path.IndexOf('}', unresolved);

        throw new CycUsageException(
            $"The verb tree's path for this command contains {path[unresolved..(end < 0 ? path.Length : end + 1)]}, "
            + "which this build of cyc does not know how to fill. Upgrade cyc.");
    }

    /// <summary>
    ///     One address value: the flag, then the environment variable the tree names, then the
    ///     profile.
    /// </summary>
    /// <exception cref="CycUsageException">
    ///     Nothing supplies a value the path needs. ⚠ The message names all three places, because
    ///     "missing --subscription" is unhelpful to somebody who thought their profile had one.
    /// </exception>
    static string? Address(
        CycInvocation invocation,
        IReadOnlyList<FlagBinding> bindings,
        ParseResult parse,
        string flagName,
        string? setting,
        bool required) {
        var binding = bindings.FirstOrDefault(x => string.Equals(x.Flag.Name, flagName, StringComparison.Ordinal));

        if (binding is not null && binding.Provided(parse) && binding.Text(parse) is { Length: > 0 } typed)
            return typed;

        var resolved = setting is null ? null : invocation.Settings.Get(setting);

        if (resolved is { Length: > 0 })
            return resolved;

        if (!required)
            return null;

        var variable = setting is null ? null : Configuration.CycSettings.VariableFor(setting);

        throw new CycUsageException(
            $"{flagName} is required and nothing supplies it. Pass {flagName}"
            + (variable is null ? string.Empty : $", set {variable}")
            + (setting is null ? string.Empty : $", or put '{setting} = …' in profile '{invocation.Settings.Profile}' of ~/.cyc/config")
            + ".");
    }
}

/// <summary>
///     The <c>--wait</c> / <c>--no-wait</c> pair, from a verb's <c>waitFlags</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Waiting is the default and <c>--wait</c> is the explicit spelling of it.</b> A CLI whose
///     default was <c>--no-wait</c> would answer "Accepted" to <c>cyc … create</c> and leave every
///     script to invent its own polling loop — the thing docs/plan/10 § Long-running operations, over
///     HTTP standardises so that nobody has to.
/// </remarks>
sealed class WaitOptions {
    readonly Option<bool> wait;
    readonly Option<bool> noWait;

    /// <summary>Creates the pair.</summary>
    /// <param name="wait">The <c>--wait</c> option.</param>
    /// <param name="noWait">The <c>--no-wait</c> option.</param>
    public WaitOptions(Option<bool> wait, Option<bool> noWait) {
        this.wait = wait;
        this.noWait = noWait;
    }

    /// <summary>Both options, to add to a command.</summary>
    public IReadOnlyList<Option> Options => [wait, noWait];

    /// <summary>Whether this command line asked not to wait.</summary>
    /// <param name="parse">The parse result.</param>
    /// <exception cref="CycUsageException">Both flags were given.</exception>
    public bool NoWait(ParseResult parse) {
        ArgumentNullException.ThrowIfNull(parse);

        if (parse.GetValue(wait) && parse.GetValue(noWait))
            throw new CycUsageException("--wait and --no-wait contradict each other.");

        return parse.GetValue(noWait);
    }
}
