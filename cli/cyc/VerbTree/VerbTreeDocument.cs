using System.Text.Json.Serialization;

namespace CyberCloud.Cli.VerbTree;

/// <summary>
///     One api-version's verb tree, as <c>CliEmitter</c> writes it into
///     <c>generated/cli/{version}.json</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is a reader, not a second source.</b> Nothing here invents a group, a verb, a
///         flag or an alias; every member maps onto a key the emitter produces, and a key the emitter
///         stops producing must be deleted here rather than defaulted. docs/plan/21 § Generation:
///         <i>"Everything else is regenerated per release and never edited."</i>
///     </para>
///     <para>
///         ⚠ <b><see cref="Format" /> is checked before anything else is read.</b> The emitter calls
///         it "a contract between the generator and the host", and a host that quietly accepted a
///         format it did not understand would mis-parse the flags rather than say so.
///     </para>
///     <para>
///         ⚠ <b>Every collection and string here reads through <c>field ?? …</c>, and that is not
///         belt-and-braces — it is required.</b> <c>CliEmitter § ToJson</c> omits every member that
///         would say <c>false</c>, <c>""</c> or <c>[]</c>, so "absent" is the common case rather than
///         the exception. A property initialiser does <b>not</b> survive source-generated
///         <c>System.Text.Json</c> deserialisation of an absent member: the first flag of the first
///         verb — <c>--allowed-cidrs</c>, which has no <c>choices</c> — arrived with
///         <c>Choices == null</c> and took down the whole command tree with a
///         <see cref="NullReferenceException" /> before <c>cyc --help</c> could print a line. Found by
///         running the binary; <c>VerbTreeTests.ReadsFlagsWithAndWithoutChoices</c> is the test that
///         keeps it fixed.
///     </para>
/// </remarks>
sealed class VerbTreeDocument {
    /// <summary>The schema version of the file's own shape — <c>CliEmitter.FormatVersion</c>.</summary>
    [JsonPropertyName("format")]
    public string Format { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The api-version this tree describes. There is no <c>latest</c> — docs/plan/10 § API versioning.</summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The OpenAPI document this was derived from.</summary>
    [JsonPropertyName("generatedFrom")]
    public string GeneratedFrom { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>What the file is, in the emitter's own words.</summary>
    [JsonPropertyName("description")]
    public string Description { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>
    ///     The flags every command takes. ⚠ Read for their help text and their choices; the options
    ///     themselves are declared in <see cref="GlobalOptions" />, because a global flag is host
    ///     behaviour and the tree cannot describe what <c>--query</c> <i>does</i>.
    /// </summary>
    [JsonPropertyName("globalFlags")]
    public IReadOnlyList<VerbTreeFlag> GlobalFlags { get => field ?? []; init; } = [];

    /// <summary>The exit-code table, compared against <see cref="ExitCode" /> by a test.</summary>
    [JsonPropertyName("exitCodes")]
    public IReadOnlyDictionary<string, int> ExitCodes { get => field ?? Empty<int>.Map; init; } = Empty<int>.Map;

    /// <summary>The top-level groups, keyed by the name a user types.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyDictionary<string, VerbTreeGroup> Groups { get => field ?? Empty<VerbTreeGroup>.Map; init; } = Empty<VerbTreeGroup>.Map;
}

/// <summary>The empty map a missing dictionary member reads as.</summary>
/// <typeparam name="T">The value type.</typeparam>
static class Empty<T> {
    /// <summary>The instance. Shared, because it is immutable and every absent member wants one.</summary>
    public static IReadOnlyDictionary<string, T> Map { get; } = new Dictionary<string, T>(StringComparer.Ordinal);
}

/// <summary>One provider's commands — <c>cyc sample …</c>.</summary>
sealed class VerbTreeGroup {
    /// <summary>The group name, lower-cased from the provider namespace's last segment.</summary>
    [JsonPropertyName("name")]
    public string Name { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>Help text for the group.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The resource types in the group, keyed by the name a user types.</summary>
    [JsonPropertyName("commands")]
    public IReadOnlyDictionary<string, VerbTreeCommand> Commands { get => field ?? Empty<VerbTreeCommand>.Map; init; } = Empty<VerbTreeCommand>.Map;
}

/// <summary>One resource type — <c>cyc sample widgets …</c>.</summary>
sealed class VerbTreeCommand {
    /// <summary>The command name, the type path kebab-cased.</summary>
    [JsonPropertyName("name")]
    public string Name { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The fully-qualified resource type — <c>CyberCloud.Sample/widgets</c>.</summary>
    [JsonPropertyName("resourceType")]
    public string ResourceType { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The type's display name.</summary>
    [JsonPropertyName("title")]
    public string Title { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The type's plural display name.</summary>
    [JsonPropertyName("plural")]
    public string Plural { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>Help text for the command.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The verbs, keyed by the name a user types.</summary>
    [JsonPropertyName("verbs")]
    public IReadOnlyDictionary<string, VerbTreeVerb> Verbs { get => field ?? Empty<VerbTreeVerb>.Map; init; } = Empty<VerbTreeVerb>.Map;

    /// <summary>
    ///     The short form, or <c>null</c>. ⚠ <b>Generated.</b> It comes from the resource type's own
    ///     <c>shortName</c> in the provider registry, so adding a provider adds its alias — see
    ///     <see cref="CommandTree" /> for why a hand-maintained copy here would be a second source.
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    /// <summary>Whether the type is on its way out. Rendered in help; nothing is refused.</summary>
    [JsonPropertyName("deprecated")]
    public bool Deprecated { get; init; }
}

/// <summary>One verb — <c>cyc sample widgets create …</c>.</summary>
sealed class VerbTreeVerb {
    /// <summary>The verb name — Azure CLI's vocabulary, not HTTP's.</summary>
    [JsonPropertyName("name")]
    public string Name { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>Help text for the verb.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The HTTP method.</summary>
    [JsonPropertyName("method")]
    public string Method { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The path template, with <c>{tenantId}</c>-style placeholders the host fills.</summary>
    [JsonPropertyName("path")]
    public string Path { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The api-version the verb belongs to.</summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>Whether the platform answers <c>202</c> and the host polls — docs/plan/10 § Long-running operations, over HTTP.</summary>
    [JsonPropertyName("longRunning")]
    public bool LongRunning { get; init; }

    /// <summary>The verb's flags, address flags and body flags together, sorted by the emitter.</summary>
    [JsonPropertyName("flags")]
    public IReadOnlyList<VerbTreeFlag> Flags { get => field ?? []; init; } = [];

    /// <summary>
    ///     <c>["--wait", "--no-wait"]</c> on a long-running verb, absent otherwise. Read rather than
    ///     assumed, so a verb the emitter stops marking stops offering them.
    /// </summary>
    [JsonPropertyName("waitFlags")]
    public IReadOnlyList<string> WaitFlags { get => field ?? []; init; } = [];

    /// <summary>
    ///     Whether the response is one page of a collection rather than the whole of it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A short page never means "that is all there is".</b> The platform filters a listing
    ///     one permission check per member and clamps the page at
    ///     <c>ListRequest.MaxPageSize</c>, so truncation looks exactly like a small result. The host
    ///     says so on stderr when a page carries a <c>nextLink</c> nobody asked it to follow.
    /// </remarks>
    [JsonPropertyName("paged")]
    public bool Paged { get; init; }

    /// <summary>
    ///     <c>["--all"]</c> on a paged verb, absent otherwise — the flags that are host behaviour
    ///     rather than contract.
    /// </summary>
    /// <remarks>
    ///     ⚠ Read rather than assumed, for the reason <see cref="WaitFlags" /> is: a verb the emitter
    ///     stops offering them for stops offering them. <c>--top</c> and <c>--skip-token</c> are
    ///     <i>not</i> here — they go on the wire, so they are in <see cref="Flags" /> with a
    ///     <see cref="VerbTreeFlag.QueryParameter" />.
    /// </remarks>
    [JsonPropertyName("pageFlags")]
    public IReadOnlyList<string> PageFlags { get => field ?? []; init; } = [];

    /// <summary>The <c>x-cybercloud-action</c> name, on an action verb.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>The permission an action needs — <c>read</c> or <c>write</c>.</summary>
    [JsonPropertyName("permission")]
    public string? Permission { get; init; }

    /// <summary>
    ///     Whether the response carries secret material. ⚠ A verb marked here is never echoed into a
    ///     <c>--verbose</c> trace, whatever the trace level.
    /// </summary>
    [JsonPropertyName("secret")]
    public bool Secret { get; init; }

    /// <summary>
    ///     Whether the action's request body was never described, so no flags exist for it. The host
    ///     offers <c>--body</c> instead of pretending the body is empty.
    /// </summary>
    [JsonPropertyName("rawBody")]
    public bool RawBody { get; init; }
}

/// <summary>One flag.</summary>
sealed class VerbTreeFlag {
    /// <summary>The long form, with its <c>--</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary><c>string</c>, <c>integer</c>, <c>number</c>, <c>switch</c> or <c>keyValue</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>Whether the verb refuses without it.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; }

    /// <summary>A second name for the same flag — <c>--cluster</c> for <c>--cluster-id</c>.</summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    /// <summary>Help text — the schema's own description.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get => field ?? string.Empty; init; } = string.Empty;

    /// <summary>The RFC 6901 pointer this flag sets in the request body, or <c>null</c> for an address flag.</summary>
    [JsonPropertyName("jsonPointer")]
    public string? JsonPointer { get; init; }

    /// <summary>
    ///     The <c>{…}</c> placeholder in the verb's own <c>path</c> this flag fills, or <c>null</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The emitter has declared this since 2026-08-12 and this reader did not have it, which
    ///     is why five commands could not build a URL at all.</b> <c>ResourceVerb</c> filled four
    ///     placeholders from a hard-coded table, so <c>cyc network virtual-networks-subnets show</c>
    ///     reached <c>{virtualNetworksName}</c> and answered <i>"which this build of cyc does not know
    ///     how to fill. Upgrade cyc."</i> — advice that could not have helped, because no newer build
    ///     would have known either. Read it here and the table shrinks to what a <i>profile</i> can
    ///     also supply.
    /// </remarks>
    [JsonPropertyName("pathPlaceholder")]
    public string? PathPlaceholder { get; init; }

    /// <summary>
    ///     The query parameter this flag becomes, <c>$</c> and all, or <c>null</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ The wire name, not the flag's: <c>--skip-token</c> sends <c>$skipToken</c>. A host that
    ///     rebuilt one from the other would be re-deriving a convention the emitter owns, and a
    ///     gateway ignores a query parameter it does not recognise — so the failure would be a
    ///     <c>200</c> holding page one again.
    /// </remarks>
    [JsonPropertyName("queryParameter")]
    public string? QueryParameter { get; init; }

    /// <summary>An environment variable that supplies the value — docs/plan/21 § Decisions.</summary>
    [JsonPropertyName("env")]
    public string? Env { get; init; }

    /// <summary>Whether the flag may be given more than once and collects into an array.</summary>
    [JsonPropertyName("repeated")]
    public bool Repeated { get; init; }

    /// <summary>Whether an explicit <c>null</c> is a legal value.</summary>
    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; }

    /// <summary>The closed set of values. Validated before a request is sent, and offered to completion.</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<string> Choices { get => field ?? []; init; } = [];

    /// <summary>Whether the value is secret. ⚠ Never echoed and never traced.</summary>
    [JsonPropertyName("secret")]
    public bool Secret { get; init; }

    /// <summary>Whether the value may not change after create. Said in help, refused by the platform.</summary>
    [JsonPropertyName("immutable")]
    public bool Immutable { get; init; }

    /// <summary>Whether the platform owns the value, so no verb offers a flag for it.</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; init; }

    /// <summary>The portal's widget hint. Carried through; the CLI shows it in help for <c>cidr</c> and friends.</summary>
    [JsonPropertyName("widget")]
    public string? Widget { get; init; }

    /// <summary>
    ///     The platform's default, shown in help. ⚠ Never sent: a <c>PATCH</c> that filled in
    ///     defaults for flags the user did not give would rewrite fields the user never mentioned.
    /// </summary>
    [JsonPropertyName("default")]
    public JsonElement Default { get; init; }

    /// <summary>An illustrative value, shown in help.</summary>
    [JsonPropertyName("example")]
    public JsonElement Example { get; init; }
}

/// <summary>
///     The source-generated serializer for the verb tree.
/// </summary>
/// <remarks>
///     ⚠ Source-generated because this project sets <c>IsAotCompatible</c>: a reflective
///     <c>JsonSerializer.Deserialize&lt;VerbTreeDocument&gt;(json)</c> is IL2026 here and fails the
///     build, which is the point.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(VerbTreeDocument))]
sealed partial class VerbTreeJsonContext : JsonSerializerContext;
