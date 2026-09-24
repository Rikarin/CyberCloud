using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     One resource type, read back out of an emitted OpenAPI document.
/// </summary>
/// <param name="ResourceType">The fully qualified type, from <c>x-cybercloud-resource-type</c>.</param>
/// <param name="Path">The URL template.</param>
/// <param name="Component">The component key of the type's schema.</param>
/// <param name="Body">
///     The write body: the type's schema with the members its <c>allOf</c> inherits taken out.
///     <para>
///         ⚠ <b>Not the component verbatim, since issue #85.</b> The component is one schema for
///         both directions — the read envelope's five <c>readOnly</c> members repeated beside
///         <c>location</c>, <c>properties</c> and <c>tags</c>, and an <c>allOf</c> naming where they
///         came from. Every surface that walks this member is building the thing a caller writes:
///         a flag, a form field, a <c>{Model}Data</c> property. Handing them the envelope would
///         put <c>--etag</c> on <c>cyc create</c>, an <c>id</c> control on every form and an
///         <c>Id</c> that <c>System.Text.Json</c> serialises as <c>null</c> into every
///         <c>PUT</c>, which the write path refuses. The split is made once, here, from what the
///         document says the schema inherits, rather than by each of four emitters knowing five
///         names.
///     </para>
/// </param>
/// <param name="Envelope">
///     What the type's schema inherits through <c>allOf</c>, merged: the read envelope's
///     <c>properties</c> and its <c>x-cybercloud-read-required</c> list. Empty when the schema
///     inherits nothing — a document from before issue #85, or an action's body.
/// </param>
/// <param name="Display">The <c>x-cybercloud-display</c> object.</param>
/// <param name="SupportsTags">Whether the body carries the platform's tag bag.</param>
/// <param name="RequiresCluster">Whether the type is placed into a cluster.</param>
/// <param name="ClusterIdPointer">Where the cluster id is, or <c>""</c>.</param>
/// <param name="SoftDeleteDays">The recovery window, or 0.</param>
/// <param name="PurgePermission">The permission a purge needs, or <c>""</c> for a type with no window.</param>
/// <param name="PurgeProtectionPointer">Where the purge-protection flag is, or <c>""</c>.</param>
/// <param name="Actions">The declared actions.</param>
/// <param name="Deprecated">Whether this api-version is under a retirement notice.</param>
/// <param name="CollectionPath">
///     The path of this type's collection <c>GET</c>, or <c>""</c> when the document declares none.
///     <para>
///         ⚠ <b>Read out of the document rather than derived from <paramref name="Path" />.</b>
///         Stripping <c>/{resourceName}</c> would give the same answer today and would be an
///         assumption about the emitter rather than a fact about the document — so a surface would
///         emit a URL for a path that is not there, and the first thing anyone would notice is a
///         <c>404</c> from a generated client. An empty string here means the document has no such
///         path, which is a thing a surface can decide what to do about.
///     </para>
/// </param>
/// <param name="CollectionQuery">
///     The query parameters the collection <c>GET</c> declares, ordered by name. Empty when there is
///     no collection path.
///     <para>
///         ⚠ <b>Read out of the document for the same reason <paramref name="CollectionPath" /> is.</b>
///         <c>$top</c> and <c>$skipToken</c> are two strings that would otherwise be written once in
///         <c>OpenApiEmitter.CollectionParameters</c>, once in <c>CliEmitter</c> and once in the
///         <c>cyc</c> host — three assemblies, two of which cannot see the first. That is the shape of
///         the defect <c>CollectionPathTemplate</c> was added to close, and a CLI that sent
///         <c>?$skip-token=</c> at a gateway reading <c>$skipToken</c> would page for ever without
///         erroring: an ignored query parameter is a <c>200</c> holding page one again.
///     </para>
/// </param>
public sealed record DocumentType(
    string ResourceType,
    string Path,
    string Component,
    JsonObject Body,
    JsonObject Envelope,
    JsonObject Display,
    bool SupportsTags,
    bool RequiresCluster,
    string ClusterIdPointer,
    int SoftDeleteDays,
    string PurgePermission,
    string PurgeProtectionPointer,
    ImmutableArray<DocumentAction> Actions,
    bool Deprecated,
    string CollectionPath,
    ImmutableArray<DocumentQueryParameter> CollectionQuery
) {
    /// <summary>The provider namespace — everything before the <c>/</c>.</summary>
    public string ProviderNamespace {
        get {
            var slash = ResourceType.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? ResourceType : ResourceType[..slash];
        }
    }

    /// <summary>The type path within the provider — <c>servers/databases</c>.</summary>
    public string TypePath {
        get {
            var slash = ResourceType.IndexOf('/', StringComparison.Ordinal);
            return slash < 0 ? string.Empty : ResourceType[(slash + 1)..];
        }
    }

    /// <summary>The display name, which the emitter guarantees is present.</summary>
    public string DisplayName => Text(Display["name"]);

    /// <summary>The plural display name.</summary>
    public string DisplayPlural => Text(Display["plural"]);

    /// <summary>The short form, or <c>""</c>.</summary>
    public string Alias => Text(Display["alias"]);

    /// <summary>The one-sentence summary, or <c>""</c>.</summary>
    public string Summary => Text(Display["summary"]);

    static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}

/// <summary>
///     One scope — a tenant, a subscription or a resource group — read back out of an emitted
///     document.
/// </summary>
/// <param name="Kind">The <c>x-cybercloud-scope</c> value: <c>tenant</c>, <c>subscription</c> or <c>resourceGroup</c>.</param>
/// <param name="Path">The URL template.</param>
/// <param name="TypeName">The Azure-shaped type string a response carries.</param>
/// <param name="Display">The <c>x-cybercloud-display</c> object.</param>
/// <param name="Creatable">Whether the document declares a <c>PUT</c>.</param>
/// <param name="Component">The create body's component key, or <c>""</c>.</param>
/// <param name="Body">The create body schema, or empty.</param>
/// <param name="CollectionPath">
///     The URL template that lists scopes of this kind under their parent — a tenant's
///     subscriptions, a subscription's resource groups — or <c>""</c> when the document declares
///     none. ⚠ Empty for the tenant, and the absence is the contract: there is no tenant
///     collection, because the only tenant a request can address is its own.
/// </param>
/// <param name="CollectionQuery">
///     The query parameters the collection accepts, ordered by name — <c>$top</c> and
///     <c>$skipToken</c> — or empty when there is no collection.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b>A scope is not a <see cref="DocumentType" /> and must not be made one.</b> It has no
///         provider, no api-version of its own, no tags, no soft-delete window, no actions and no
///         collection — six members that would be permanently empty, and the next reader would have
///         to work out per member whether "empty" meant "not applicable" or "not declared". The same
///         argument <c>ScopeRequest</c> makes for not reusing <c>WriteRequest</c>.
///     </para>
///     <para>
///         ⚠ <b><paramref name="Creatable" /> is read off the document rather than off the kind.</b>
///         The tenant is the read-only one today, and a surface that hard-coded which is which would
///         be a second copy of a decision — see <c>OpenApiEmitter.ScopePathItems</c> for why a tenant
///         cannot have a create route at all.
///     </para>
/// </remarks>
public sealed record DocumentScope(
    string Kind,
    string Path,
    string TypeName,
    JsonObject Display,
    bool Creatable,
    string Component,
    JsonObject Body,
    string CollectionPath = "",
    ImmutableArray<DocumentQueryParameter> CollectionQuery = default
) {
    /// <summary>The collection's query parameters, never a default array.</summary>
    public ImmutableArray<DocumentQueryParameter> CollectionQuery { get; init; } =
        CollectionQuery.IsDefault ? [] : CollectionQuery;

    /// <summary>The display name, which the emitter guarantees is present.</summary>
    public string DisplayName => Text(Display["name"]);

    /// <summary>The plural display name.</summary>
    public string DisplayPlural => Text(Display["plural"]);

    /// <summary>The one-sentence summary.</summary>
    public string Summary => Text(Display["summary"]);

    /// <summary>
    ///     The placeholder this scope's own name occupies — the last one in its path.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The last placeholder rather than a name per kind.</b> A subscription's is
    ///     <c>{subscriptionId}</c> and a group's is <c>{resourceGroupName}</c>; reading the template
    ///     is the same rule <c>DocumentReader.PlaceholdersOf</c> already applies to a resource, and
    ///     it cannot disagree with the URL it fills.
    /// </remarks>
    public string NamePlaceholder => DocumentReader.PlaceholdersOf(Path) is [.., var last] ? last : string.Empty;

    /// <summary>
    ///     The placeholders that address this scope's ancestors, in template order.
    /// </summary>
    public ImmutableArray<string> AncestorPlaceholders =>
        DocumentReader.PlaceholdersOf(Path) is [.. var ancestors, _] ? [.. ancestors] : [];

    static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}

/// <summary>One query parameter, read back out of an emitted document.</summary>
/// <param name="Name">The name on the wire, <c>$</c> and all — <c>$skipToken</c>.</param>
/// <param name="Type">The schema's <c>type</c>, with a nullable union already collapsed.</param>
/// <param name="Description">The parameter's own description.</param>
/// <param name="Required">Whether the operation refuses without it.</param>
/// <remarks>
///     ⚠ <b><paramref name="Name" /> keeps the <c>$</c>.</b> It is what goes on the wire, and a
///     surface that stored the pretty form would have to put the sigil back — which is the one step
///     nothing would notice getting wrong, because a gateway ignores a query parameter it does not
///     recognise rather than refusing it.
/// </remarks>
public sealed record DocumentQueryParameter(string Name, string Type, string Description, bool Required);

/// <summary>One action, read back out of an emitted document.</summary>
/// <param name="Name">The action name.</param>
/// <param name="Permission">The ReBAC permission.</param>
/// <param name="Secret">Whether the response carries secret material.</param>
/// <param name="LongRunning">Whether it answers <c>202</c>.</param>
/// <param name="Request">The request schema, or <see langword="null" />.</param>
/// <param name="Response">The response schema, or <see langword="null" />.</param>
/// <param name="RemovesResource">
///     Whether a successful run leaves no resource at the address — the platform's <c>purge</c>,
///     which ends a parked resource's recovery window. docs/plan/08 § The write path, end to end:
///     "A converged delete or purge removes it".
///     <para>
///         ⚠
///         <b>
///             A poller that reads the resource after this succeeds gets a <c>404</c> for its
///             success.
///         </b> The Python and Go SDKs' <c>wait()</c> follows a long-running verb's
///         <c>202</c> to the resource — docs/plan/10 § Long-running operations, over HTTP: "then GET
///         the resource" — and until this member existed both did so after a purge and raised
///         <c>ResourceNotFound</c> from a purge that had worked. It is the document's fact, read once
///         here, for the reason <see cref="DocumentType.Body" /> is split once here: the .NET SDK's
///         hand-written poller has the same seam (<c>OperationPoller</c>'s
///         <c>fetchResourceOnSuccess</c>), and an emitter deciding it from the action's name would be a
///         second copy of what soft delete is.
///     </para>
/// </param>
/// <param name="EntryPoint">
///     The platform entry point that serves it instead of a handler (<c>x-cybercloud-entry-point</c>),
///     or empty. ⚠ Such an action is not refused with a <c>404</c> on a name that does not exist, so
///     a surface that prints the handler route's rule on it describes a refusal the API never gives.
/// </param>
public sealed record DocumentAction(
    string Name,
    string Permission,
    bool Secret,
    bool LongRunning,
    JsonObject? Request,
    JsonObject? Response,
    bool RemovesResource,
    string EntryPoint = ""
);

/// <summary>
///     Reads an emitted OpenAPI document back into the shape the derived emitters walk — the
///     <c>cyc</c> verb tree, the .NET, Python and Go SDKs, and the portal forms.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The derived surfaces read the <i>document</i>, not the registry, and that is
///             docs/plan/21 § Generation's rule rather than a convenience.
///         </b> That document says
///         <c>Build.Generate</c> walks "the provider registry → OpenAPI 3.1 → the SDK's models,
///         clients and pollers" — one hop, so the compatibility gate that protects the published
///         OpenAPI protects those four surfaces at once. A CLI generated straight from the registry
///         could describe a flag the published contract does not have, and no gate would notice.
///     </para>
///     <para>
///         ⚠
///         <b>
///             ADR-012's fifth surface does not come through here, and that is stated on it rather
///             than left to be discovered.
///         </b> <see cref="ChartAnnotationEmitter" /> reads the registry,
///         because the chart a type renders is <c>ResourceTypeRegistration.Chart</c> and no emitted
///         document carries it — there is nothing here to read a pairing back from. It also keeps
///         declaration order, which every document deliberately destroys by sorting
///         <c>properties</c> ordinally.
///     </para>
///     <para>
///         ⚠ <b>This reader is the price of that rule and it is worth naming.</b> Reading a document
///         back is a second interpretation of it, which is the thing ADR-012 is otherwise at pains to
///         avoid. It is acceptable here and only here because the document was written by
///         <see cref="OpenApiEmitter" /> in this same process seconds earlier — the pair is a
///         serialize/deserialize round trip within one build step, not two components agreeing about
///         a format. What it must never become is a reader for a <i>hand-edited</i> document.
///     </para>
/// </remarks>
public static class DocumentReader {
    /// <summary>Every resource type in a document, ordered by type.</summary>
    /// <param name="document">An emitted per-version document.</param>
    public static ImmutableArray<DocumentType> TypesOf(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document["paths"] is not JsonObject paths) {
            return [];
        }

        var schemas = document["components"]?["schemas"] as JsonObject;
        var found = new List<DocumentType>();

        foreach (var path in paths) {
            // ⚠ THREE PATH SHAPES NOW CARRY x-cybercloud-resource-type AND ONLY ONE OF THEM IS A
            // TYPE. An action was always separated here, which is why soft delete's `restore` and
            // `purge` needed no change to any emitter. A COLLECTION is the first shape that is
            // neither a resource nor an action, and without the second test below it reads as a
            // second type of the same name: CliEmitter throws on the duplicate command name,
            // SdkEmitter throws on the duplicate model name, FormsEmitter's indexer silently replaces
            // the real one, and DerivedSurfaceTests' "one command per resource type" invariant is the
            // thing that would have caught it.
            if (path.Value is not JsonObject item
                || item["x-cybercloud-action"] is not null
                || item["x-cybercloud-collection"] is not null
                || Text(item["x-cybercloud-resource-type"]) is not { Length: > 0 } resourceType) {
                continue;
            }

            var component = ComponentOf(item);
            var collection = CollectionOf(paths, document, resourceType);
            var (body, envelope) = Split(
                schemas,
                (component.Length > 0 ? schemas?[component] as JsonObject : null) ?? []
            );

            found.Add(
                new(
                    resourceType,
                    path.Key,
                    component,
                    body,
                    envelope,
                    item["x-cybercloud-display"] as JsonObject ?? [],
                    Flag(item["x-cybercloud-supports-tags"]),
                    Flag(item["x-cybercloud-requires-cluster"]),
                    Text(item["x-cybercloud-cluster-id-pointer"]),
                    Number(item["x-cybercloud-soft-delete-days"]),
                    Text(item["x-cybercloud-purge-permission"]),
                    Text(item["x-cybercloud-purge-protection-pointer"]),
                    ActionsOf(paths, schemas, path.Key, resourceType, Text(item["x-cybercloud-purge-permission"])),
                    Flag(item["get"]?["deprecated"]),
                    collection.Path,
                    collection.Query
                )
            );
        }

        return [.. found.OrderBy(static x => x.ResourceType, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Every scope in a document, ordered by path — so a tenant comes before its subscription
    ///     and a subscription before its group, because each path is a prefix of the next.
    /// </summary>
    /// <param name="document">An emitted per-version document.</param>
    /// <remarks>
    ///     ⚠ <b>The second thing this reader reads, and the reason it exists at all is issue #63.</b>
    ///     <see cref="TypesOf" /> answers "what did a provider register"; a scope was registered by
    ///     nobody, so every derived surface was silent about two addresses the gateway has served
    ///     since #1. The rule that surfaces read the <i>document</i> rather than the registry is what
    ///     makes one extension here enough for all four of them.
    /// </remarks>
    public static ImmutableArray<DocumentScope> ScopesOf(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document["paths"] is not JsonObject paths) {
            return [];
        }

        var schemas = document["components"]?["schemas"] as JsonObject;
        var found = new List<DocumentScope>();

        foreach (var path in paths) {
            // ⚠ TWO PATH SHAPES CARRY x-cybercloud-scope AND ONLY ONE OF THEM IS A SCOPE. A
            // collection carries the extension too — it has to, so a surface can find which kind it
            // lists — and without the second test it would read as a second scope of the same kind:
            // CliEmitter writes commands[kind] twice and keeps the last, FormsEmitter replaces the
            // create form with one that has no body, and DerivedSurfaces' scope count is off by
            // two. The same split TypesOf makes on x-cybercloud-collection, for the same reason.
            if (path.Value is not JsonObject item
                || Flag(item[ScopeCollectionExtension])
                || Text(item[ScopeExtension]) is not { Length: > 0 } kind) {
                continue;
            }

            var component = ComponentOf(
                item["put"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]
            );

            var collection = ScopeCollectionOf(paths, document, kind);

            found.Add(
                new(
                    kind,
                    path.Key,
                    Text(item["x-cybercloud-scope-type"]),
                    item["x-cybercloud-display"] as JsonObject ?? [],
                    // ⚠ The presence of the operation, not the absence of the read-only marker. A
                    // surface asks "may I create one" and the honest answer is whether the document
                    // declares the write.
                    item["put"] is JsonObject,
                    component,
                    (component.Length > 0 ? schemas?[component] as JsonObject : null) ?? [],
                    collection.Path,
                    collection.Query
                )
            );
        }

        return [.. found.OrderBy(static x => x.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     The extension a scope collection path item carries beside <see cref="ScopeExtension" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="OpenApiEmitter.ScopeCollectionExtension" />'s, for the reason
    ///     <see cref="ScopeExtension" /> is the emitter's.
    /// </remarks>
    public const string ScopeCollectionExtension = OpenApiEmitter.ScopeCollectionExtension;

    /// <summary>
    ///     The collection path declared for one scope kind and the query it accepts, or
    ///     <c>("", [])</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Matched on the kind and the collection flag and not on a path prefix, for the reason
    ///     <see cref="CollectionOf" /> gives: a collection's path is strictly shorter than the
    ///     item's, and the reverse prefix test would hand the resource-group item the subscription
    ///     collection, whose path it starts with.
    /// </remarks>
    static (string Path, ImmutableArray<DocumentQueryParameter> Query) ScopeCollectionOf(
        JsonObject paths,
        JsonObject document,
        string kind
    ) {
        foreach (var path in paths) {
            if (path.Value is JsonObject item
                && Flag(item[ScopeCollectionExtension])
                && string.Equals(Text(item[ScopeExtension]), kind, StringComparison.Ordinal)) {
                return (path.Key, QueryOf(item, document));
            }
        }

        return (string.Empty, []);
    }

    /// <summary>
    ///     The extension a scope path item is recognised by.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="OpenApiEmitter.ScopeExtension" />'s, not a second spelling of it. The two
    ///     halves of a round trip within one build step is the only thing that makes this reader
    ///     acceptable at all — see the remarks on this class — and a key each half spelled for itself
    ///     would be exactly the second interpretation it is not allowed to be.
    /// </remarks>
    public const string ScopeExtension = OpenApiEmitter.ScopeExtension;

    /// <summary>
    ///     The collection path declared for one resource type and the query it accepts, or
    ///     <c>("", [])</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Matched on the type and the collection flag, and NOT on a path prefix.</b> A prefix
    ///     test is what <see cref="ActionsOf" /> uses and it is right there because an action's path
    ///     is strictly longer than its resource's. A collection's is strictly <i>shorter</i>, so the
    ///     same test would find nothing — and the reverse test, "the resource path starts with this
    ///     one", matches every ancestor collection as well: <c>…/servers</c> is a prefix of
    ///     <c>…/servers/{n}/databases/{n}</c>, so a database would take its parent's collection.
    /// </remarks>
    static (string Path, ImmutableArray<DocumentQueryParameter> Query) CollectionOf(
        JsonObject paths,
        JsonObject document,
        string resourceType
    ) {
        foreach (var path in paths) {
            if (path.Value is JsonObject item
                && Flag(item["x-cybercloud-collection"])
                && string.Equals(Text(item["x-cybercloud-resource-type"]), resourceType, StringComparison.Ordinal)) {
                return (path.Key, QueryOf(item, document));
            }
        }

        return (string.Empty, []);
    }

    /// <summary>
    ///     The <c>in: query</c> parameters of a path item, ordered by name.
    /// </summary>
    /// <param name="item">The path item.</param>
    /// <param name="document">The document, for <c>components/parameters</c>.</param>
    /// <remarks>
    ///     ⚠ <b>A <c>$ref</c> is followed rather than skipped.</b> <c>OpenApiEmitter</c> writes the
    ///     paging pair inline today and the address parameters as references, so a reader that only
    ///     understood inline objects would be right by accident and would silently return nothing the
    ///     day a shared <c>$top</c> component was factored out. It is the same fact
    ///     <see cref="Resolve" /> already knows about schemas.
    /// </remarks>
    static ImmutableArray<DocumentQueryParameter> QueryOf(JsonObject item, JsonObject document) {
        if (item["parameters"] is not JsonArray parameters) {
            return [];
        }

        const string Prefix = "#/components/parameters/";
        var shared = document["components"]?["parameters"] as JsonObject;
        var found = new List<DocumentQueryParameter>();

        foreach (var entry in parameters) {
            if (entry is not JsonObject declared) {
                continue;
            }

            if (Text(declared["$ref"]) is { Length: > 0 } reference) {
                if (!reference.StartsWith(Prefix, StringComparison.Ordinal)
                    || shared?[reference[Prefix.Length..]] is not JsonObject resolved) {
                    continue;
                }

                declared = resolved;
            }

            if (!string.Equals(Text(declared["in"]), "query", StringComparison.Ordinal)
                || Text(declared["name"]) is not { Length: > 0 } name) {
                continue;
            }

            found.Add(
                new(
                    name,
                    declared["schema"] is JsonObject schema ? TypeOf(schema) : string.Empty,
                    Text(declared["description"]),
                    Flag(declared["required"])
                )
            );
        }

        return [.. found.OrderBy(static x => x.Name, StringComparer.Ordinal)];
    }

    /// <summary>The actions declared under one resource path, ordered by name.</summary>
    /// <param name="paths">The document's <c>paths</c>.</param>
    /// <param name="schemas">The document's components, for the request and response references.</param>
    /// <param name="resourcePath">The type's own path; an action's path is one segment under it.</param>
    /// <param name="resourceType">The type each action item must name in <c>x-cybercloud-resource-type</c>.</param>
    /// <param name="purgePermission">
    ///     The type's <c>x-cybercloud-purge-permission</c>. ⚠ Empty for a type with no window, which
    ///     <see cref="OpenApiEmitter" /> makes the one question "does this type have a purge" — so the
    ///     reserved name alone does not make an action a purge; the type has to declare the window the
    ///     purge ends.
    /// </param>
    static ImmutableArray<DocumentAction> ActionsOf(
        JsonObject paths,
        JsonObject? schemas,
        string resourcePath,
        string resourceType,
        string purgePermission
    ) {
        var found = new List<DocumentAction>();

        foreach (var path in paths) {
            if (path.Value is not JsonObject item
                || Text(item["x-cybercloud-action"]) is not { Length: > 0 } name
                || !string.Equals(Text(item["x-cybercloud-resource-type"]), resourceType, StringComparison.Ordinal)
                || !path.Key.StartsWith(resourcePath + "/", StringComparison.Ordinal)
                || item["post"] is not JsonObject post) {
                continue;
            }

            found.Add(
                new(
                    name,
                    Text(post["x-cybercloud-permission"]),
                    Flag(post["x-cybercloud-secret"]),
                    Flag(post["x-cybercloud-long-running"]),
                    Resolve(schemas, post["requestBody"]?["content"]?["application/json"]?["schema"]),
                    Resolve(schemas, post["responses"]?["200"]?["content"]?["application/json"]?["schema"]),
                    // ⚠ Case-insensitive, as SoftDeletePolicy.IsReserved is, because an action is
                    // matched as a URL segment is. The name is the identity: ProviderBuilder refuses a
                    // provider that declares `purge` on any type, so the only purge a document can
                    // carry is the one the platform synthesised.
                    purgePermission.Length > 0
                    && string.Equals(name, SoftDeletePolicy.PurgeAction, StringComparison.OrdinalIgnoreCase),
                    Text(post["x-cybercloud-entry-point"])
                )
            );
        }

        return [.. found.OrderBy(static x => x.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    ///     Splits a type's schema into the write body and what it inherits through <c>allOf</c> —
    ///     <see cref="DocumentType.Body" /> and <see cref="DocumentType.Envelope" />.
    /// </summary>
    /// <param name="schemas">The document's components, for the <c>allOf</c> references.</param>
    /// <param name="schema">The type's schema, as the component holds it. Not modified.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Driven by the schema's own <c>allOf</c>, never by a list of names.</b> A member
    ///         is the envelope's because a schema the type inherits declares it, so a sixth envelope
    ///         member reaches every surface the day the emitter adds it, and a document with no
    ///         <c>allOf</c> — one from before issue #85 — reads exactly as it did. A reader that knew
    ///         "the five" would be the fifth copy of a fact the emitter is the only owner of.
    ///     </para>
    ///     <para>
    ///         The write body keeps everything else: its <c>required</c>, its
    ///         <c>additionalProperties</c>, its title and description. Only the inherited members and
    ///         the <c>allOf</c> itself come out, because the body is what a caller sends and those
    ///         are the parts of the schema that say what a caller receives.
    ///     </para>
    /// </remarks>
    static (JsonObject Body, JsonObject Envelope) Split(JsonObject? schemas, JsonObject schema) {
        if (schema["allOf"] is not JsonArray inherits || inherits.Count == 0) {
            return (schema, []);
        }

        var members = new JsonObject();
        var readRequired = new JsonArray();

        foreach (var entry in inherits) {
            if (Resolve(schemas, entry) is not { } inherited) {
                continue;
            }

            if (inherited["properties"] is JsonObject declared) {
                foreach (var member in declared) {
                    members[member.Key] = member.Value?.DeepClone();
                }
            }

            foreach (var name in Strings(inherited[OpenApiEmitter.ReadRequiredExtension])) {
                readRequired.Add(name);
            }
        }

        var body = (JsonObject)schema.DeepClone();
        body.Remove("allOf");

        if (body["properties"] is JsonObject own) {
            foreach (var name in members.Select(static x => x.Key).ToList()) {
                own.Remove(name);
            }
        }

        var envelope = new JsonObject { ["type"] = "object", ["properties"] = members };

        if (readRequired.Count > 0) {
            envelope[OpenApiEmitter.ReadRequiredExtension] = readRequired;
        }

        return (body, envelope);
    }

    /// <summary>
    ///     The members a read always carries — <c>x-cybercloud-read-required</c> on an envelope.
    /// </summary>
    /// <param name="envelope">A <see cref="DocumentType.Envelope" />, or the component itself.</param>
    /// <remarks>
    ///     ⚠ Read from the extension and not from <c>required</c>, which the envelope deliberately
    ///     leaves empty — the remarks on <c>OpenApiEmitter.ResourceEnvelopeSchema</c> say why. A
    ///     surface that typed the five as optional because <c>required</c> was empty would make a
    ///     caller null-check an id that is never absent.
    /// </remarks>
    public static ImmutableHashSet<string> ReadRequiredOf(JsonObject envelope) {
        ArgumentNullException.ThrowIfNull(envelope);

        return [.. Strings(envelope[OpenApiEmitter.ReadRequiredExtension])];
    }

    static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Text).Where(static x => x.Length > 0) : [];

    /// <summary>The component key a path item's <c>200</c> body points at, or <c>""</c>.</summary>
    static string ComponentOf(JsonObject item) =>
        ComponentOf(item["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]);

    /// <summary>The component key a <c>$ref</c> node points at, or <c>""</c>.</summary>
    static string ComponentOf(JsonNode? reference) {
        const string Prefix = "#/components/schemas/";
        var text = Text(reference);

        return text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..] : string.Empty;
    }

    /// <summary>
    ///     Follows a local schema <c>$ref</c>, or returns an inline schema. ⚠ An empty inline schema
    ///     reads as <see langword="null" />: "unconstrained" and "not declared" are the same fact here
    ///     and both mean a generated surface cannot type it.
    /// </summary>
    static JsonObject? Resolve(JsonObject? schemas, JsonNode? node) {
        if (node is not JsonObject schema) {
            return null;
        }

        const string Prefix = "#/components/schemas/";
        var reference = Text(schema["$ref"]);

        if (reference.StartsWith(Prefix, StringComparison.Ordinal)) {
            return schemas?[reference[Prefix.Length..]] as JsonObject;
        }

        return schema.Count == 0 ? null : schema;
    }

    /// <summary>A node's string value, or <c>""</c>.</summary>
    public static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    /// <summary>A node's boolean value, or <see langword="false" />.</summary>
    public static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>A node's integer value, or 0.</summary>
    public static int Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    /// <summary>The placeholder the resource's own name occupies in a path template.</summary>
    public const string ResourceNamePlaceholder = "resourceName";

    /// <summary>The tenant placeholder.</summary>
    public const string TenantPlaceholder = "tenantId";

    /// <summary>The subscription placeholder.</summary>
    public const string SubscriptionPlaceholder = "subscriptionId";

    /// <summary>The resource group placeholder.</summary>
    public const string ResourceGroupPlaceholder = "resourceGroupName";

    /// <summary>
    ///     The <c>{…}</c> placeholders a path template carries, in the order they appear.
    /// </summary>
    /// <param name="path">A path template, as <see cref="DocumentType.Path" /> spells one.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Read off the template rather than derived a second time from the type path, and that
    ///         is the point of putting it here.
    ///     </b> <c>OpenApiEmitter.PathOf</c> is the one place the
    ///     interleaved grammar of docs/plan/12 § Child resources is turned into a URL — a nested type
    ///     is <c>…/servers/{serversName}/databases/{resourceName}</c>, alternating — and every other
    ///     surface is generated from the <i>document</i> rather than from the registry precisely so a
    ///     fact cannot reach one surface and stop at another (docs/plan/21 § Generation's one hop).
    ///     Two emitters each re-splitting the type path would be two more chances to disagree about
    ///     which server a database is in, and the disagreement would be silent: a CLI flag that is
    ///     absent and an SDK parameter that is absent both look like a surface that simply never had
    ///     one.
    /// </remarks>
    public static ImmutableArray<string> PlaceholdersOf(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var found = ImmutableArray.CreateBuilder<string>();

        for (var i = 0; i < path.Length; i++) {
            if (path[i] != '{') {
                continue;
            }

            var close = path.IndexOf('}', i + 1);
            if (close < 0) {
                break;
            }

            found.Add(path[(i + 1)..close]);
            i = close;
        }

        return found.ToImmutable();
    }

    /// <summary>
    ///     The ancestors' name placeholders — every one that is neither the platform envelope's nor
    ///     the resource's own. Empty for a top-level type.
    /// </summary>
    /// <param name="path">A path template.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         These are the parameters a child's caller has to be able to supply, and until
    ///         2026-08-12 no derived surface had them.
    ///     </b> <c>cyc</c> offered four address flags read
    ///     from a hard-coded list, so a command for <c>servers/databases</c> could say a database's
    ///     name and never which server; the SDK's collection took <c>(name, data)</c>, so a caller
    ///     could not address one either. Both lost the information without saying so — the flag list
    ///     was simply four long and the method signature simply had two parameters.
    /// </remarks>
    public static ImmutableArray<string> AncestorPlaceholdersOf(string path) => [
        .. PlaceholdersOf(path)
            .Where(static x =>
                x is not (TenantPlaceholder
                    or SubscriptionPlaceholder
                    or ResourceGroupPlaceholder
                    or ResourceNamePlaceholder)
            )
    ];

    /// <summary>
    ///     Every leaf of a body schema, flattened to <c>(pointer, schema, required)</c> in
    ///     depth-first, name-sorted order.
    /// </summary>
    /// <param name="schema">A body schema.</param>
    /// <remarks>
    ///     ⚠ Sorted by name at each level, because the emitted document already sorts its
    ///     <c>properties</c> and the derived surfaces must be as deterministic as the document is.
    ///     A CLI whose flag order depended on a dictionary's iteration would diff differently on two
    ///     machines.
    /// </remarks>
    public static ImmutableArray<SchemaLeaf> LeavesOf(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        var found = ImmutableArray.CreateBuilder<SchemaLeaf>();
        Walk(schema, string.Empty, found);
        return found.ToImmutable();
    }

    static void Walk(JsonObject node, string pointer, ImmutableArray<SchemaLeaf>.Builder found) {
        if (node["properties"] is not JsonObject properties) {
            return;
        }

        var required = node["required"] is JsonArray names
            ? names.Select(Text).ToHashSet(StringComparer.Ordinal)
            : [];

        foreach (var member in properties.ToList().OrderBy(static x => x.Key, StringComparer.Ordinal)) {
            if (member.Value is not JsonObject child) {
                continue;
            }

            var childPointer = pointer + "/" + member.Key;
            var isObject = TypeOf(child) == "object" && child["properties"] is JsonObject;

            found.Add(new(childPointer, member.Key, child, required.Contains(member.Key), isObject));

            if (isObject) {
                Walk(child, childPointer, found);
            }
        }
    }

    /// <summary>
    ///     A schema's <c>type</c>, with a nullable union collapsed to the non-null member.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <remarks>
    ///     ⚠ OpenAPI 3.1 spells nullability as <c>["string","null"]</c>, so every consumer has to know
    ///     the union. Collapsing it here means each derived emitter asks "what is it" and
    ///     <see cref="IsNullable" /> separately, rather than three of them re-deriving the union.
    /// </remarks>
    public static string TypeOf(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        return schema["type"] switch {
            JsonArray union => union.Select(Text).FirstOrDefault(static x => x is { Length: > 0 } and not "null")
                ?? string.Empty,
            var single => Text(single)
        };
    }

    /// <summary>Whether a schema's type union includes <c>null</c>.</summary>
    /// <param name="schema">The schema.</param>
    public static bool IsNullable(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        return schema["type"] is JsonArray union
            && union.Any(static x => string.Equals(Text(x), "null", StringComparison.Ordinal));
    }

    /// <summary>A schema's <c>enum</c> values, in document order.</summary>
    /// <param name="schema">The schema.</param>
    public static ImmutableArray<string> EnumOf(JsonObject schema) {
        ArgumentNullException.ThrowIfNull(schema);

        // ⚠ An array's enum lives on its `items`, because that is where the constraint applies — see
        // the remarks on SchemaProperty.ElementKind.
        var carrier = TypeOf(schema) == "array" && schema["items"] is JsonObject items ? items : schema;

        return carrier["enum"] is not JsonArray values ? [] : [.. values.Select(Text)];
    }

    /// <summary>The api-version a document describes.</summary>
    /// <param name="document">The document.</param>
    public static string VersionOf(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        return Text(document["info"]?["version"]);
    }

    /// <summary>A number rendered the one way every derived surface must render it.</summary>
    /// <param name="value">The number.</param>
    public static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One leaf of a body schema.</summary>
/// <param name="JsonPointer">Its pointer within the body.</param>
/// <param name="Name">Its own member name.</param>
/// <param name="Schema">Its schema object.</param>
/// <param name="Required">Whether its parent lists it as required.</param>
/// <param name="IsObject">Whether it is a container rather than a value.</param>
public readonly record struct SchemaLeaf(
    string JsonPointer,
    string Name,
    JsonObject Schema,
    bool Required,
    bool IsObject
);
