using CyberCloud.Cli.Execution;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;
using System.CommandLine;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc graph query</c> — the resource graph's KQL over HTTP, docs/plan/08 § The resource-graph
///     projection and docs/plan/21 § Grammar.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Hand-written, like <c>cyc rest</c>, and for the reason docs/plan/10 § Shape gives
///         under "#63's question".</b> The address lives under a reserved namespace no provider
///         declares, so the generated document does not carry it and no generated group can. Until
///         the emitter learns a third non-registry source — the same shape #63 gave the scope
///         paths — this command is the CLI's whole knowledge of the endpoint: the address, the body's
///         three members, and how a page is followed. It sends through the same
///         <c>CyberCloudPipeline</c> every generated verb uses, so it is authenticated, correlated
///         and retried on a <c>429</c>.
///     </para>
///     <para>
///         ⚠ <b>A page is followed by <c>POST</c>ing the same body to the <c>nextLink</c>.</b> The
///         generated <c>--all</c> follows a <c>nextLink</c> with a <c>GET</c>, which is what every
///         collection of this API takes; this endpoint's link carries the page parameters in its
///         query string and wants the query text back in the body, so the paging here is its own
///         loop and not <c>ResourceVerb.AllPagesAsync</c>.
///     </para>
/// </remarks>
static class GraphCommands {
    /// <summary>The group's name — reserved in <c>CommandTree.ReservedGroups</c>.</summary>
    public const string GroupName = "graph";

    /// <summary>Builds the command.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree, for the api-version the request carries.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        var command = new Command(GroupName, "Query the resource graph: every resource you may read, in a KQL subset.") {
            Query(host, globals, tree)
        };

        return command;
    }

    static Command Query(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var query = new Argument<string>("query") {
            Description =
                "The KQL. Starts with the table: \"resources | where type =~ 'cybercloud.dbforpostgresql/servers' "
                + "| project name, location\". Operators: where, project, extend, summarize, order by, take, "
                + "distinct, count. Anything else is refused with the supported list."
        };

        var tenant = new Option<string>("--tenant") {
            Description = "The tenant to query. Defaults to the profile's tenant."
        };

        var top = new Option<int?>("--top") {
            Description = "Rows per page. The platform clamps it; the default is its default."
        };

        var skipToken = new Option<string>("--skip-token") {
            Description = "Resume from a previous page's token, as its nextLink carried it."
        };

        var all = new Option<bool>("--all") {
            Description =
                "Follow nextLink to the end and print every page as one list. ⚠ One request per page, "
                + "and a page is what you may read rather than everything there is."
        };

        var command = new Command("query", "Run a KQL query over the resource graph and print the rows.") {
            query, tenant, top, skipToken, all
        };

        command.SetAction(async (parse, cancellationToken) => {
                var invocation = CycRunner.Bind(host, globals, tree, parse);
                var tenantId = TenantFor(invocation, parse.GetValue(tenant));

                using var client = invocation.CreateClient(tenantId);
                var context = client.Context;
                var address = new Uri(context.Endpoint, $"/tenants/{tenantId}/providers/CyberCloud.ResourceGraph/resources");
                var text = parse.GetRequiredValue(query);

                if (parse.GetValue(all)) {
                    return await AllPagesAsync(invocation, context, address, text, parse.GetValue(top), cancellationToken).ConfigureAwait(false);
                }

                using var page = await PageAsync(invocation, context, address, text, parse.GetValue(top), parse.GetValue(skipToken), cancellationToken)
                    .ConfigureAwait(false);

                // Said out loud, as the generated list verbs say it: nextLink is not in --output table.
                if (page.Value.Member("nextLink").AsString() is { Length: > 0 }) {
                    invocation.Console.Note(
                        "cyc: this is one page and there are more. Pass --all to page through them, or "
                        + "--skip-token with the nextLink's $skipToken to resume."
                    );
                }

                invocation.Render(page.Value);

                return (int)ExitCode.Ok;
            }
        );

        return command;
    }

    /// <summary>
    ///     One page: the body is <c>{ "query", "$top", "$skipToken" }</c>, the answer the collection
    ///     envelope with <c>columns</c>.
    /// </summary>
    static async Task<ResponseBody> PageAsync(
        CycInvocation invocation,
        CyberCloudClientContext context,
        Uri address,
        string query,
        int? top,
        string? skipToken,
        CancellationToken cancellationToken
    ) {
        using var request = context.CreateRequest(HttpMethod.Post, address);

        // Written by hand rather than serialised: this binary is NativeAOT and the reflection
        // serialiser is IL2026/IL3050 here — CommandTree's remarks on the extension model say why.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("query", query);

            if (top is { } size) {
                writer.WriteNumber("$top", size);
            }

            if (skipToken is { Length: > 0 } token) {
                writer.WriteString("$skipToken", token);
            }

            writer.WriteEndObject();
        }

        CyberCloudClientContext.SetJsonBody(request, buffer.WrittenSpan.ToArray());
        invocation.Trace($"POST {Redaction.Url(address)}");
        invocation.Trace($"query: {query}");

        var response = await context.Pipeline.SendAsync(request, cancellationToken).ConfigureAwait(false);

        invocation.Trace($"{response.Status} {response.ReasonPhrase} (request id {response.ServiceRequestId ?? "none"})");

        if (response.IsError) {
            // The gateway's refusal names the operator and the supported list; the flag is the
            // argument, which is what the message is about.
            throw CycRequestException.From(response, flag: "query");
        }

        return ResponseBody.Parse(response);
    }

    /// <summary>
    ///     Every page as one <c>{ "columns": […], "value": [ … ] }</c>, following each page's
    ///     <c>$skipToken</c> with the same query.
    /// </summary>
    /// <remarks>
    ///     The token is read off the <c>nextLink</c> rather than re-derived, so a change in how the
    ///     gateway spells the link is not a change here; and the last document carries no
    ///     <c>nextLink</c>, for the reason <c>ResourceVerb.AllPagesAsync</c> gives.
    /// </remarks>
    static async Task<int> AllPagesAsync(
        CycInvocation invocation,
        CyberCloudClientContext context,
        Uri address,
        string query,
        int? top,
        CancellationToken cancellationToken
    ) {
        var pages = new List<ResponseBody>();
        var values = new List<Payload>();
        Payload columns = Payload.Array([]);

        try {
            string? skipToken = null;
            var count = 0;

            while (true) {
                var page = await PageAsync(invocation, context, address, query, top, skipToken, cancellationToken).ConfigureAwait(false);
                pages.Add(page);
                values.AddRange(page.Value.Member("value").Items);
                count++;

                if (count == 1) {
                    columns = page.Value.Member("columns");
                }

                if (page.Value.Member("nextLink").AsString() is not { Length: > 0 } link) {
                    break;
                }

                skipToken = SkipTokenOf(link);
            }

            invocation.Trace($"paged: {count} request(s), {values.Count} row(s)");
            invocation.Render(
                Payload.Object(
                    [
                        new KeyValuePair<string, Payload>("columns", columns),
                        new KeyValuePair<string, Payload>("value", Payload.Array(values))
                    ]
                )
            );
        } finally {
            foreach (var page in pages) {
                page.Dispose();
            }
        }

        return (int)ExitCode.Ok;
    }

    /// <summary>The <c>$skipToken</c> a <c>nextLink</c> carries, or a usage error naming the link.</summary>
    static string SkipTokenOf(string link) {
        var uri = new Uri(link, UriKind.Absolute);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0 && string.Equals(Uri.UnescapeDataString(pair[..separator]), "$skipToken", StringComparison.Ordinal)) {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new CycRequestException($"The gateway's nextLink carries no $skipToken: {link}");
    }

    /// <summary>The tenant from <c>--tenant</c> or the profile, or a usage error that names both.</summary>
    static string TenantFor(CycInvocation invocation, string? flag) {
        if (flag is { Length: > 0 }) {
            return flag;
        }

        if (invocation.Settings.Get("tenant") is { Length: > 0 } configured) {
            return configured;
        }

        throw new CycUsageException(
            "--tenant is required and nothing supplies it. Pass --tenant, set "
            + Configuration.CycSettings.VariableFor("tenant")
            + $", or put 'tenant = …' in profile '{invocation.Settings.Profile}' of ~/.cyc/config."
        );
    }
}
