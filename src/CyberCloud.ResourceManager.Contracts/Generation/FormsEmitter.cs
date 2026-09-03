using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     ADR-012's fourth surface of five: the JSON the Angular schema-renderer consumes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/02 § ADR-012's portal row: <i>"Angular reactive forms + xUI controls from the
///         schema, with <c>x-cybercloud-*</c> hints for widgets (a <c>storageclass</c> picker, a
///         region picker)"</i>, and docs/plan/20 § The shape that makes 100 resource types affordable
///         gives the schema→widget table this implements.
///     </para>
///     <para>
///         ⚠ <b>The control is decided here, once, and not in the renderer.</b> docs/plan/20's table
///         is a mapping — <c>string + enum ≤ 8</c> is a select, more is a suggest, <c>integer</c> with
///         bounds is a slider — and a renderer that re-derived it from raw JSON Schema would be a
///         second interpretation of the document, which is the drift ADR-012 exists to remove. The
///         renderer's job is to draw <c>xui-select</c>; deciding that it <i>is</i> a select is this
///         emitter's.
///     </para>
///     <para>
///         ⚠ <b>The escape hatch is named in the output rather than assumed.</b> docs/plan/20 allows a
///         hand-written form keyed by <c>(resourceType, apiVersion)</c> and requires that <i>"every
///         override must render the same schema"</i>. Each form carries the key an override is
///         registered under and the pointer list it must cover, so the test that document asks for —
///         submit the override's output against the schema — has something machine-readable to check
///         against instead of a convention.
///     </para>
/// </remarks>
public static class FormsEmitter {
    /// <summary>The directory, under the generated root, this surface is written to.</summary>
    public const string DirectoryName = "forms";

    /// <summary>The schema version of the form format itself.</summary>
    public const string FormatVersion = "1";

    /// <summary>Emits every resource type's form for one api-version's document.</summary>
    /// <param name="document">An emitted OpenAPI document.</param>
    public static JsonObject Emit(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        var version = DocumentReader.VersionOf(document);
        var forms = new JsonObject();

        foreach (var type in DocumentReader.TypesOf(document)) {
            forms[type.ResourceType] = Form(type, version);
        }

        var scopes = new JsonObject();

        // ⚠ A SECOND MAP RATHER THAN THREE MORE ENTRIES IN `forms` — issue #63. A scope has no
        // resource type, so it has no key of the shape every entry in `forms` is keyed by, and
        // inventing one would make the portal's lookup ambiguous the day a provider registered
        // `CyberCloud.Resources/subscriptions`. Separate maps also let the portal render the
        // subscription picker without walking twenty-two resource forms looking for the three that
        // are not resources.
        foreach (var scope in DocumentReader.ScopesOf(document)) {
            if (!scope.Creatable) {
                // ⚠ No form for a scope the API will not create. A portal that rendered a tenant
                // form would render a submit button whose request is refused before routing runs —
                // see OpenApiEmitter.ScopePathItems.
                continue;
            }

            scopes[scope.Kind] = ScopeForm(scope, version);
        }

        return new JsonObject {
            ["format"] = FormatVersion,
            ["apiVersion"] = version,
            ["generatedFrom"] = OpenApiArtifacts.DirectoryName + "/" + version + ".json",
            ["description"] =
                "Portal form schemas at api-version " + version + ". Generated from the OpenAPI "
                + "document — docs/plan/21 § Generation — and consumed by libs/resource-forms' "
                + "schema-renderer (docs/plan/20).",
            ["forms"] = forms,
            ["scopeForms"] = scopes
        };
    }

    /// <summary>One scope's create form.</summary>
    /// <param name="scope">The scope.</param>
    /// <param name="version">The api-version.</param>
    /// <remarks>
    ///     ⚠ <b>The same field vocabulary as a resource form, so the renderer needs no second
    ///     code path.</b> docs/plan/20's schema→widget table is applied by
    ///     <see cref="ControlOf" /> either way; what differs is the envelope, and it differs because
    ///     a scope genuinely has no tags, no cluster, no soft-delete window and no actions.
    /// </remarks>
    static JsonObject ScopeForm(DocumentScope scope, string version) {
        var fields = new JsonArray();
        var covered = new JsonArray();

        foreach (var leaf in DocumentReader.LeavesOf(scope.Body)) {
            fields.Add(ScopeField(leaf));
            covered.Add(leaf.JsonPointer);
        }

        var address = new JsonArray();

        foreach (var placeholder in DocumentReader.PlaceholdersOf(scope.Path)) {
            address.Add(placeholder);
        }

        return new JsonObject {
            ["scope"] = scope.Kind,
            ["scopeType"] = scope.TypeName,
            ["apiVersion"] = version,
            ["path"] = scope.Path,
            ["method"] = "PUT",
            // ⚠ The placeholders the portal has to fill from its own scope picker rather than from
            // the form. A group's subscription is where you are, not what you are typing.
            ["addressPlaceholders"] = address,
            ["title"] = scope.DisplayName,
            ["plural"] = scope.DisplayPlural,
            ["summary"] = scope.Summary,
            ["overrideKey"] = scope.TypeName + "@" + version,
            ["coveredPointers"] = covered,
            // ⚠ Said rather than left to be inferred from the absent 202. A portal that showed a
            // progress bar for a scope create would show one that never moves.
            ["longRunning"] = false,
            ["fields"] = fields
        };
    }

    static JsonObject ScopeField(SchemaLeaf leaf) {
        var field = new JsonObject {
            ["jsonPointer"] = leaf.JsonPointer,
            ["name"] = leaf.Name,
            ["label"] = Label(leaf.Name),
            ["control"] = ControlOf(leaf.Schema, leaf.IsObject),
            ["required"] = leaf.Required
        };

        if (DocumentReader.Text(leaf.Schema["description"]) is { Length: > 0 } description) {
            field["help"] = description;
        }

        if (DocumentReader.Text(leaf.Schema["x-cybercloud-widget"]) is { Length: > 0 } widget) {
            field["widget"] = widget;
        }

        return field;
    }

    /// <summary>The file one api-version's forms are written to.</summary>
    /// <param name="apiVersion">The api-version.</param>
    public static string FileNameOf(string apiVersion) => apiVersion + ".json";

    static JsonObject Form(DocumentType type, string version) {
        var fields = new JsonArray();
        var covered = new JsonArray();

        foreach (var leaf in DocumentReader.LeavesOf(type.Body)) {
            fields.Add(Field(type, leaf));
            covered.Add(leaf.JsonPointer);
        }

        var actions = new JsonArray();

        foreach (var action in type.Actions) {
            actions.Add(Action(action));
        }

        return new JsonObject {
            ["resourceType"] = type.ResourceType,
            ["apiVersion"] = version,
            ["title"] = type.DisplayName,
            ["plural"] = type.DisplayPlural,
            ["summary"] = type.Summary,
            // docs/plan/20 § The escape hatch, and its limit: a hand-written component may replace
            // this form, keyed by exactly this pair, and must render the same schema.
            ["overrideKey"] = type.ResourceType + "@" + version,
            ["coveredPointers"] = covered,
            ["supportsTags"] = type.SupportsTags,
            ["requiresCluster"] = type.RequiresCluster,
            ["clusterIdPointer"] = type.ClusterIdPointer,
            ["softDeleteDays"] = type.SoftDeleteDays,
            // ⚠ The pointer as well as the window, because a portal that knows a type is recoverable
            // and not which field turns purge protection on cannot render the toggle — the same gap
            // `requiresCluster` without `clusterIdPointer` had.
            ["purgePermission"] = type.PurgePermission,
            ["purgeProtectionPointer"] = type.PurgeProtectionPointer,
            ["fields"] = fields,
            ["actions"] = actions
        };
    }

    static JsonObject Field(DocumentType type, SchemaLeaf leaf) {
        var schema = leaf.Schema;
        var control = ControlOf(schema, leaf.IsObject);

        var field = new JsonObject {
            // ⚠ The pointer is the field's identity, and it is the same string an error's `target`
            // carries — docs/plan/08 § Errors: "a JSON Pointer into the request body so the portal can
            // highlight the field". The renderer keys its control registry on it, so a server-side
            // failure lands on the control that produced it without any translation step.
            ["jsonPointer"] = leaf.JsonPointer,
            ["name"] = leaf.Name,
            ["label"] = Label(leaf.Name),
            ["control"] = control,
            ["type"] = DocumentReader.TypeOf(schema),
            ["required"] = leaf.Required,
            ["section"] = SectionOf(leaf.JsonPointer)
        };

        if (DocumentReader.Text(schema["description"]) is { Length: > 0 } description) {
            field["hint"] = description;
        }

        if (DocumentReader.IsNullable(schema)) {
            // The renderer has to offer an explicit "unset" affordance rather than treating an empty
            // control as absence — a nullable property's null and an omitted property are different
            // requests. ⚠ On a PATCH they are the same bytes; see SchemaProperty.Nullable.
            field["nullable"] = true;
        }

        var values = DocumentReader.EnumOf(schema);
        if (!values.IsEmpty) {
            var choices = new JsonArray();
            foreach (var value in values) {
                choices.Add(new JsonObject { ["value"] = value, ["label"] = Label(value) });
            }

            field["choices"] = choices;
        }

        foreach (var keyword in Keywords) {
            if (schema[keyword] is { } constraint) {
                // Carried through unchanged so the client-side validation and the server's are the
                // same numbers. docs/plan/20's validation/ folder is "schema + async server
                // validation, one message shape" — one shape needs one set of bounds.
                field[keyword] = constraint.DeepClone();
            }
        }

        if (DocumentReader.Text(schema["format"]) is { Length: > 0 } format) {
            field["format"] = format;
        }

        if (DocumentReader.Flag(schema["readOnly"])) {
            field["readOnly"] = true;
        }

        if (DocumentReader.Flag(schema["x-cybercloud-secret"]) || DocumentReader.Text(schema["format"]) == "password") {
            // docs/plan/20: "never a plain value". The control is a Vault SecretRef picker and the
            // value never round-trips, because ResourceSchema.Project drops it on every read.
            field["secret"] = true;
            field["writeOnly"] = true;
        }

        if (DocumentReader.Flag(schema["x-cybercloud-immutable"])) {
            // docs/plan/20: "Disabled after create, with a tooltip naming why."
            field["disabledAfterCreate"] = true;
            field["disabledReason"] =
                "This value cannot change after the resource is created. Create a new "
                + type.DisplayName + " to change it.";
        }

        if (schema["default"] is { } fallback) {
            field["default"] = fallback.DeepClone();
        }

        if (schema["example"] is { } example) {
            field["placeholder"] = example.DeepClone();
        }

        return field;
    }

    static JsonObject Action(DocumentAction action) {
        var node = new JsonObject {
            ["name"] = action.Name,
            ["label"] = Label(action.Name),
            ["permission"] = action.Permission,
            ["longRunning"] = action.LongRunning,
            ["secret"] = action.Secret
        };

        if (action.Request is { } request) {
            var fields = new JsonArray();

            foreach (var leaf in DocumentReader.LeavesOf(request)) {
                fields.Add(new JsonObject {
                    ["jsonPointer"] = leaf.JsonPointer,
                    ["name"] = leaf.Name,
                    ["label"] = Label(leaf.Name),
                    ["control"] = ControlOf(leaf.Schema, leaf.IsObject),
                    ["required"] = leaf.Required
                });
            }

            node["fields"] = fields;
        } else {
            // ⚠ Honest: an action with no declared request cannot be given a dialog, so the portal
            // shows a confirm rather than inventing controls for parameters nobody described.
            node["fields"] = new JsonArray();
            node["confirmOnly"] = true;
        }

        return node;
    }

    /// <summary>
    ///     docs/plan/20 § The shape that makes 100 resource types affordable's schema→widget table,
    ///     in order of specificity.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A declared <c>x-cybercloud-widget</c> wins over everything.</b> It is the provider's
    ///     explicit statement and the shape-based rules are the fallback — a region is a
    ///     <c>string + pattern</c> to a shape rule and a region picker to a person, and only the
    ///     registry knows which.
    /// </remarks>
    static string ControlOf(JsonObject schema, bool isObject) {
        if (DocumentReader.Text(schema["x-cybercloud-widget"]) is { Length: > 0 } declared) {
            return "xui-" + declared;
        }

        if (DocumentReader.Flag(schema["x-cybercloud-secret"])
            || DocumentReader.Text(schema["format"]) == "password") {
            return "xui-secret-ref";
        }

        var type = DocumentReader.TypeOf(schema);
        var values = DocumentReader.EnumOf(schema);

        if (!values.IsEmpty) {
            // docs/plan/20: "@xui/select (≤ 8) or @xui/suggest (more)". A dropdown of forty is a
            // dropdown nobody scrolls.
            return values.Length <= SelectLimit ? "xui-select" : "xui-suggest";
        }

        return type switch {
            "boolean" => "xui-switch",
            "integer" or "number" when schema["minimum"] is not null && schema["maximum"] is not null =>
                "xui-slider",
            "integer" or "number" => "xui-numeric-input",
            // docs/plan/20: "object + additionalProperties: string → @xui/tag-input".
            "object" when schema["additionalProperties"] is JsonObject => "xui-tag-input",
            "object" when isObject => "xui-group",
            "array" when DocumentReader.TypeOf(schema["items"] as JsonObject ?? []) == "object" =>
                "xui-data-table",
            "array" => "xui-chip-input",
            _ => "xui-input"
        };
    }

    /// <summary>docs/plan/20's threshold between a select and a suggest.</summary>
    const int SelectLimit = 8;

    /// <summary>
    ///     The constraint keywords a form carries through to its client-side validation.
    /// </summary>
    static readonly string[] Keywords = [
        "minimum",
        "maximum",
        "minLength",
        "maxLength",
        "maxProperties",
        "pattern"
    ];

    /// <summary>
    ///     Which tab or group a field belongs to — docs/plan/20's <c>layout/</c> folder,
    ///     "<c>@section</c> annotations → tabs and groups".
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Derived from the pointer's own shape and it is honestly a placeholder.</b> The
    ///     registry has no section vocabulary, so this groups by the property's parent — everything
    ///     under <c>/properties/sku</c> becomes the <c>sku</c> group and everything at the root
    ///     becomes <c>general</c>. That is enough for a renderer to make tabs and it is not what a
    ///     designer would choose. The right fix is a <c>Section</c> on <see cref="Registry.SchemaProperty" />;
    ///     inventing an ordering here instead would be this emitter deciding a thing the registry
    ///     should say.
    /// </remarks>
    static string SectionOf(string jsonPointer) {
        var segments = jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length <= 1) {
            return "general";
        }

        return segments.Length == 2 && string.Equals(segments[0], "properties", StringComparison.Ordinal)
            ? "general"
            : segments[^2];
    }

    /// <summary>
    ///     A member name as a person reads it: <c>clusterId</c> becomes <c>Cluster id</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Invariant casing throughout. A label built with culture-sensitive casing would differ
    ///     between a build in Istanbul and a build anywhere else, and the file is byte-compared.
    /// </remarks>
    static string Label(string name) {
        var words = CliEmitter.Kebab(name).Split('-', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0) {
            return name;
        }

        var first = words[0];
        var head = char.ToUpperInvariant(first[0]) + first[1..];

        return words.Length == 1 ? head : head + " " + string.Join(' ', words[1..]);
    }
}
