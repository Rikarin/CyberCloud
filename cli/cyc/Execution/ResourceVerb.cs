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
    ///     Which placeholders a <i>profile</i> can also supply, and under what setting name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This used to be the mapping from placeholder to flag, and the note above it read
    ///         "the emitter does not declare this mapping and it should". It does — since 2026-08-12,
    ///         each address flag carries a <c>pathPlaceholder</c>.</b> The table outlived the fact,
    ///         and while it did, the five nested types could not be addressed at all: a table of four
    ///         has no row for <c>{virtualNetworksName}</c>, so <c>cyc network
    ///         virtual-networks-subnets show</c> ended in <i>"which this build of cyc does not know
    ///         how to fill. Upgrade cyc."</i> — advice no newer build could have satisfied, because
    ///         the missing knowledge was the table rather than the version.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is left here is genuinely host knowledge and cannot come from the tree.</b>
    ///         A <c>~/.cyc/config</c> profile names where you are working; the tree describes a URL.
    ///         An <i>ancestor</i> deliberately has no row — docs/plan/21 § Decisions makes a profile
    ///         context, and which virtual network a subnet is in is address.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     The placeholder that names the tenant, which the client authenticates against as well as
    ///     addressing. ⚠ Spelled the same in <c>DocumentReader.TenantPlaceholder</c>, which this
    ///     assembly cannot reference — the tree it reads is the agreement between them.
    /// </summary>
    const string TenantPlaceholder = "tenantId";

    static readonly (string Placeholder, string Setting)[] ProfileAddress = [
        ("tenantId", "tenant"),
        ("subscriptionId", "subscription"),
        ("resourceGroupName", "resource-group"),
    ];

    /// <summary>Runs the verb.</summary>
    /// <param name="invocation">The resolved context.</param>
    /// <param name="verb">The verb, as the generator described it.</param>
    /// <param name="bindings">The verb's flags.</param>
    /// <param name="waitOptions">The <c>--wait</c> and <c>--no-wait</c> options, on a long-running verb.</param>
    /// <param name="pageOptions">The <c>--all</c> option, on a paged verb.</param>
    /// <param name="parse">The parse result.</param>
    /// <param name="cancellationToken">The token, carrying <c>--timeout</c>.</param>
    public static async Task<int> RunAsync(
        CycInvocation invocation,
        VerbTreeVerb verb,
        IReadOnlyList<FlagBinding> bindings,
        WaitOptions? waitOptions,
        PageOptions? pageOptions,
        ParseResult parse,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(parse);

        // ⚠ Everything the command line alone can be wrong about is settled before a socket is
        // opened. A contradictory --wait/--no-wait that was noticed only after the write had been
        // accepted would be exit 2 reported over a resource that now exists.
        var noWait = waitOptions is not null && waitOptions.NoWait(parse);
        var tenant = Address(
            invocation,
            BindingFor(bindings, TenantPlaceholder),
            parse,
            TenantPlaceholder,
            required: false);

        var path = ResolvePath(invocation, verb, bindings, parse);
        var body = RequestBody.Build(bindings, parse);

        using var client = invocation.CreateClient(tenant);
        var context = client.Context;
        var uri = new Uri(context.Endpoint, path + Query(bindings, parse));

        if (verb.Paged && pageOptions is not null && pageOptions.All(parse))
            return await AllPagesAsync(invocation, context, uri, cancellationToken).ConfigureAwait(false);

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
            return await WaitAsync(invocation, verb, context, uri, response, noWait, cancellationToken)
                .ConfigureAwait(false);
        }

        using var parsed = ResponseBody.Parse(response);

        // ⚠ SAID OUT LOUD, BECAUSE A TRUNCATED LISTING LOOKS EXACTLY LIKE A SHORT ONE. The page is
        // clamped at ListRequest.MaxPageSize and there is deliberately no `count` in the envelope —
        // a total would say how many resources exist that the caller may not see. So the only
        // evidence of a further page is `nextLink`, which `--output table` does not show and a
        // `--query` over `value[]` throws away. On stderr rather than stdout: a script reading the
        // JSON document must not find prose in it.
        if (verb.Paged && parsed.Value.Member("nextLink").AsString() is { Length: > 0 })
            invocation.Console.Note(
                "cyc: this is one page and there are more. Pass --all to page through them, or "
                + "--skip-token with the nextLink's token to resume. A page is what you may read and "
                + "a short one never means that is all there is.");

        invocation.Render(parsed.Value);

        return (int)ExitCode.Ok;
    }

    /// <summary>
    ///     Follows <c>nextLink</c> to the end and renders every page as one <c>{ "value": [ … ] }</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One command, N round trips, and only when asked.</b> Paging by default would make
    ///         the cost of <c>cyc … list</c> depend on how much is in the group, and would make
    ///         <c>--top</c> mean nothing. Opt in, and the single-page path says on stderr that the
    ///         option exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The result carries no <c>nextLink</c>, because there is no next page.</b> Echoing
    ///         the last one would hand a script a URL that returns nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>nextLink</c> is requested as it was handed out.</b> docs/plan/10 makes it an
    ///         absolute URL for exactly this reason — a client that rebuilt the next request from a
    ///         bare token would need to know the endpoint's paging parameter. It already carries its
    ///         own <c>api-version</c>, and <c>ApiVersionHandler</c> leaves a request that has one
    ///         alone.
    ///     </para>
    /// </remarks>
    static async Task<int> AllPagesAsync(
        CycInvocation invocation,
        CyberCloudClientContext context,
        Uri first,
        CancellationToken cancellationToken) {
        var pages = new List<ResponseBody>();
        var values = new List<Payload>();

        try {
            var next = first;
            var count = 0;

            while (true) {
                using var request = context.CreateRequest(HttpMethod.Get, next);

                invocation.Trace($"GET {Redaction.Url(next)}");

                var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

                invocation.Trace($"{response.Status} {response.ReasonPhrase} (request id {response.ServiceRequestId ?? "none"})");

                if (response.IsError)
                    throw CycRequestException.From(response, flag: null);

                // ⚠ Held rather than disposed per page: a Payload is a view over its document's
                // buffer, so the documents have to outlive the render at the end.
                var page = ResponseBody.Parse(response);
                pages.Add(page);
                values.AddRange(page.Value.Member("value").Items);
                count++;

                if (page.Value.Member("nextLink").AsString() is not { Length: > 0 } link)
                    break;

                next = new Uri(link, UriKind.Absolute);
            }

            invocation.Trace($"paged: {count} request(s), {values.Count} resource(s)");
            invocation.Render(Payload.Object([new KeyValuePair<string, Payload>("value", Payload.Array(values))]));
        } finally {
            foreach (var page in pages)
                page.Dispose();
        }

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
        bool noWait,
        CancellationToken cancellationToken) {
        if (noWait) {
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

    /// <summary>
    ///     Fills the verb's path template from the flags that declare which placeholder they fill.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Driven by the tree's <c>pathPlaceholder</c> members rather than by a table of names
    ///     here.</b> The emitter reads a type's placeholders off its own URL template, so a nested
    ///     type carries as many address flags as its depth needs; a host iterating a fixed list would
    ///     be the second place that knowledge lives and the shorter of the two.
    /// </remarks>
    static string ResolvePath(CycInvocation invocation, VerbTreeVerb verb, IReadOnlyList<FlagBinding> bindings, ParseResult parse) {
        var path = verb.Path;

        foreach (var binding in bindings) {
            if (binding.Flag.PathPlaceholder is not { Length: > 0 } placeholder)
                continue;

            var token = "{" + placeholder + "}";

            if (!path.Contains(token, StringComparison.Ordinal))
                continue;

            var value = Address(invocation, binding, parse, placeholder, required: true)!;
            path = path.Replace(token, Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        var unresolved = path.IndexOf('{', StringComparison.Ordinal);

        if (unresolved < 0)
            return path;

        var end = path.IndexOf('}', unresolved);

        throw new CycUsageException(
            $"The verb tree's path for this command contains {path[unresolved..(end < 0 ? path.Length : end + 1)]} "
            + "and declares no flag that fills it, so cyc cannot build the URL. This is a defect in the "
            + "generated verb tree rather than in what you typed — regenerate it with ./build.sh Generate.");
    }

    /// <summary>
    ///     The query string the flags carrying a <c>queryParameter</c> build, or <c>""</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Only flags the user actually typed.</b> The same rule <see cref="FlagBinding" />
    ///     applies to the body: sending the tree's <c>default</c> for <c>$top</c> would turn "the
    ///     platform's page size" into "whatever this build of cyc was generated against", and the
    ///     platform clamps rather than refuses, so nothing would say the number had changed.
    /// </remarks>
    static string Query(IReadOnlyList<FlagBinding> bindings, ParseResult parse) {
        var built = new StringBuilder();

        foreach (var binding in bindings) {
            if (binding.Flag.QueryParameter is not { Length: > 0 } parameter
                || !binding.Provided(parse)
                || binding.Text(parse) is not { Length: > 0 } value)
                continue;

            built.Append(built.Length == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(parameter))
                .Append('=')
                .Append(Uri.EscapeDataString(value));
        }

        return built.ToString();
    }

    /// <summary>The profile setting a placeholder may also come from, or <c>null</c>.</summary>
    static string? SettingFor(string placeholder) {
        foreach (var (candidate, setting) in ProfileAddress) {
            if (string.Equals(candidate, placeholder, StringComparison.Ordinal))
                return setting;
        }

        return null;
    }

    /// <summary>The binding that fills one placeholder, or <see langword="null" />.</summary>
    static FlagBinding? BindingFor(IReadOnlyList<FlagBinding> bindings, string placeholder)
        => bindings.FirstOrDefault(
            x => string.Equals(x.Flag.PathPlaceholder, placeholder, StringComparison.Ordinal));

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
        FlagBinding? binding,
        ParseResult parse,
        string placeholder,
        bool required) {
        if (binding is not null && binding.Provided(parse) && binding.Text(parse) is { Length: > 0 } typed)
            return typed;

        var setting = SettingFor(placeholder);
        var resolved = setting is null ? null : invocation.Settings.Get(setting);

        if (resolved is { Length: > 0 })
            return resolved;

        if (!required)
            return null;

        var flagName = binding?.Flag.Name ?? "--" + placeholder;
        var variable = setting is null ? null : Configuration.CycSettings.VariableFor(setting);

        throw new CycUsageException(
            $"{flagName} is required and nothing supplies it. Pass {flagName}"
            + (variable is null ? string.Empty : $", set {variable}")
            + (setting is null ? string.Empty : $", or put '{setting} = …' in profile '{invocation.Settings.Profile}' of ~/.cyc/config")
            + ".");
    }
}

/// <summary>
///     The <c>--all</c> switch, from a paged verb's <c>pageFlags</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Separate from the paging flags the tree lists in <c>flags</c>, and the split is the
///     point.</b> <c>--top</c> and <c>--skip-token</c> are query parameters the document declares, so
///     they bind like any other flag and the host needs no name for them. <c>--all</c> sends nothing:
///     it is a loop in this process, so it is host behaviour, declared here and authorised by the
///     tree — the same division <see cref="WaitOptions" /> draws.
/// </remarks>
sealed class PageOptions {
    readonly Option<bool> all;

    /// <summary>Creates the option.</summary>
    /// <param name="all">The <c>--all</c> option.</param>
    public PageOptions(Option<bool> all) => this.all = all;

    /// <summary>The option, to add to a command.</summary>
    public Option Option => all;

    /// <summary>Whether this command line asked for every page.</summary>
    /// <param name="parse">The parse result.</param>
    public bool All(ParseResult parse) {
        ArgumentNullException.ThrowIfNull(parse);

        return parse.GetValue(all);
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
