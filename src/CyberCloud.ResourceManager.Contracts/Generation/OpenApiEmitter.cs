using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     The first of ADR-012's five surfaces: the provider registry rendered as an OpenAPI 3.1
///     document, one per api-version.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/02 § ADR-012 makes this the surface the CLI, the SDK and the portal forms are
///         generated <i>from</i> (docs/plan/21 § Generation), so this type is the load-bearing one: a
///         fact that cannot be expressed here cannot reach any of those three either.
///     </para>
///     <para>
///         ⚠ <b>The fifth surface is the exception and says so on itself.</b>
///         <see cref="ChartAnnotationEmitter" /> reads the registry directly, because the chart a type
///         renders is <c>ResourceTypeRegistration.Chart</c> — a registry fact this document does not
///         carry, so there is no pairing to be read back out of one.
///     </para>
///     <para>
///         ⚠ <b>There are exactly two sources, and the line between them is the whole design.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>The registry</b> — every path, every body, every permission and every api-version.
///             docs/plan/08 § The provider registry: <i>"the same registry that generates the CLI is
///             the one that validates the request body. That identity is what makes drift impossible
///             rather than merely detectable."</i> Nothing provider-shaped is written down here; if
///             the registry cannot say it, this emitter does not say it either, and the gap is
///             reported rather than filled in. Filling one in is how the registry stops being the
///             single source.
///         </item>
///         <item>
///             <b>The platform envelope</b> — the one Azure-shaped error body
///             (docs/plan/08 § Errors), the <c>?api-version=</c> parameter and the
///             <c>202</c>/<c>Azure-AsyncOperation</c>/<c>Retry-After</c> triple
///             (docs/plan/10 § API versioning and § Long-running operations, over HTTP), and the
///             operation-status resource. These are the same for every provider that will ever
///             exist, are specified by the plan rather than by a provider, and are therefore
///             constants here. A provider cannot vary them, which is the point.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Determinism is a correctness property, not a nicety.</b> The document is checked in
///         and diffed (docs/plan/21 § OpenAPI), so anything order-, culture- or machine-dependent
///         makes the gate red on a colleague's machine and green on the author's. Every collection
///         below is sorted with <see cref="StringComparer.Ordinal" />, every number reaches the
///         document as a JSON number rather than through a culture-sensitive <c>ToString</c>, and no
///         path, timestamp or machine name reaches the output at all.
///         <see cref="DeterministicJson" /> owns the byte-level half.
///     </para>
/// </remarks>
public static class OpenApiEmitter {
    /// <summary>The specification version the emitted documents declare.</summary>
    /// <remarks>
    ///     docs/plan/02 § ADR-012 says "OpenAPI 3.1 document". <c>3.1.1</c> is the current patch of
    ///     3.1 and is what the version field must carry — the field is the exact specification
    ///     version, not the minor series, so <c>"3.1"</c> is not a legal value.
    /// </remarks>
    public const string SpecificationVersion = "3.1.1";

    /// <summary>The document title. The product name, from <c>Directory.Build.props</c>.</summary>
    public const string Title = "Cyber Cloud";

    /// <summary>
    ///     The <c>info.version</c> of the index document — see <see cref="EmitIndex" />.
    /// </summary>
    public const string IndexVersion = "index";

    // ── Component names, in one place because $ref strings are unchecked strings ────────────────

    const string ErrorResponseSchema = "ErrorResponse";
    const string ErrorSchema = "Error";
    const string ErrorCodeSchema = "ErrorCode";
    const string OperationStatusSchema = "OperationStatus";
    const string OperationProgressSchema = "OperationProgress";
    const string OperationStateSchema = "OperationState";

    /// <summary>
    ///     Every api-version any registered type declares, oldest first and each appearing once.
    ///     One document is emitted per entry.
    /// </summary>
    /// <param name="registry">The built registry.</param>
    /// <remarks>
    ///     ⚠ <b>The union across types, not the newest of anything.</b> docs/plan/08 § The provider
    ///     registry keeps every version forever and a read at an old version projects down, so a
    ///     version stays in this list for as long as one type still serves it. A type that does not
    ///     declare a given version simply has no paths in that version's document — which is the
    ///     honest rendering of "that type did not exist yet".
    /// </remarks>
    public static ImmutableArray<ApiVersion> ApiVersionsOf(IProviderRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        var seen = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in registry.Types) {
            foreach (var version in type.ApiVersions) {
                seen.Add(version.Version.Value);
            }
        }

        // Sorted as strings, which is chronological too — see the remarks on ApiVersion for why
        // yyyy-MM-dd is fixed rather than parsed leniently.
        return [.. seen.Select(ApiVersion.Parse)];
    }

    /// <summary>
    ///     Emits the document for one api-version.
    /// </summary>
    /// <param name="registry">The built registry — the only source of anything provider-shaped.</param>
    /// <param name="version">The api-version to project onto.</param>
    /// <returns>A complete OpenAPI 3.1 document.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A registered schema cannot be expressed — a property whose parent is undeclared, a
    ///     property that is required and read-only at once, or a property with no kind. Each of those
    ///     is a bug in a provider's <c>Describe</c> that would otherwise reach the API as a body no
    ///     caller can satisfy; see <see cref="SchemaOf" />.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>An empty registry produces a complete, valid, empty document — not nothing.</b> A
    ///     generator that writes no file when it finds nothing is indistinguishable from a generator
    ///     that did not run, and that is the vacuous pass <c>Build.Architecture</c> already refuses to
    ///     print a tick for.
    /// </remarks>
    public static JsonObject Emit(IProviderRegistry registry, ApiVersion version) {
        ArgumentNullException.ThrowIfNull(registry);

        if (version.IsEmpty) {
            throw new ArgumentException(
                "A document is emitted for one api-version and 'default(ApiVersion)' is not one. Use "
                + $"{nameof(EmitIndex)} for the version-independent index.",
                nameof(version)
            );
        }

        var paths = new JsonObject();
        var schemas = EnvelopeSchemas();
        var serving = 0;

        foreach (var type in registry.Types.OrderBy(x => x.Type.ToString(), StringComparer.Ordinal)) {
            var schema = type.SchemaFor(version);
            if (schema.TryGetError(out _)) {
                // This type did not exist at this date. Not an error — see ApiVersionsOf.
                continue;
            }

            serving++;
            var component = ComponentNameOf(type.Type);
            schemas[component] = SchemaOf(type, schema.GetValueOrThrow());

            // ⚠ Read off ResourceTypeRegistration.RetiredOn, which is the half of
            // docs/plan/08 § The provider registry's 12-month notice window that the registry does
            // carry. Without this, a version that is three months from being switched off is a
            // document indistinguishable from a live one — and `deprecated` is the one keyword every
            // SDK generator and every portal already knows how to surface.
            var retiresOn = type.RetiredOn.TryGetValue(version, out var date) ? date : (DateOnly?)null;

            var resourcePath = PathOf(type.Type);
            paths[resourcePath] = ResourcePathItem(type, component, retiresOn);

            foreach (var action in type.Actions.OrderBy(x => x.Name, StringComparer.Ordinal)) {
                // ⚠ An action's request and response are components rather than inline schemas, for
                // the reason every other body is one: an SDK generator names a model after the
                // component key, and an inline schema gets an invented name that changes when the
                // generator does.
                var requestComponent = ActionComponentOf(component, action.Name, "Request");
                var responseComponent = ActionComponentOf(component, action.Name, "Response");

                if (action.Request is { } request) {
                    schemas[requestComponent] = BodySchema(
                        type.Type + "/" + action.Name,
                        request,
                        withTags: false,
                        requestComponent,
                        "The body of a POST to " + type.Type + "/" + action.Name + "."
                    );
                }

                if (action.Response is { } response) {
                    schemas[responseComponent] = BodySchema(
                        type.Type + "/" + action.Name,
                        response,
                        withTags: false,
                        responseComponent,
                        "What " + type.Type + "/" + action.Name + " returns."
                        + (action.Secret
                            ? " ⚠ Secret material. docs/plan/08 § The provider registry makes a "
                            + "secret action the only path a secret value leaves by; this schema is "
                            + "therefore the complete list of what does."
                            : string.Empty)
                    );
                }

                paths[resourcePath + "/" + action.Name] = ActionPathItem(
                    type,
                    action,
                    retiresOn,
                    action.Request is null ? null : requestComponent,
                    action.Response is null ? null : responseComponent
                );
            }
        }

        paths["/operations/{operationId}"] = OperationPathItem();

        return new JsonObject {
            ["openapi"] = SpecificationVersion,
            ["info"] = new JsonObject {
                ["title"] = Title,
                ["version"] = version.Value,
                ["description"] =
                    "The Cyber Cloud resource API at api-version " + version.Value + ". Generated from "
                    + "the provider registry (docs/plan/02 § ADR-012); hand edits are overwritten by "
                    + "./build.sh Generate and fail the Generated surfaces gate."
            },
            ["servers"] = new JsonArray {
                new JsonObject {
                    ["url"] = "/",
                    // Relative on purpose: the deployment's public host is a chart value, not a
                    // registry fact, and baking one in would make the document wrong for every
                    // installation but ours.
                    ["description"] = "Relative to the gateway that serves this document."
                }
            },
            ["paths"] = Sorted(paths),
            ["components"] = new JsonObject {
                ["parameters"] = Parameters(version),
                ["responses"] = Responses(),
                ["schemas"] = Sorted(schemas)
            },
            // What the run inspected, in the artifact itself. A document that covers nothing and a
            // document that was never generated are otherwise the same file.
            ["x-cybercloud-resource-type-count"] = serving
        };
    }

    /// <summary>
    ///     Emits the version-independent index: the entry point a tool reads to discover which
    ///     api-versions exist.
    /// </summary>
    /// <param name="registry">The built registry.</param>
    /// <returns>
    ///     A valid OpenAPI 3.1 document with no paths, carrying the platform's error vocabulary and
    ///     three <c>x-cybercloud-*</c> discovery arrays.
    /// </returns>
    /// <remarks>
    ///     <para>
    ///         docs/plan/10 § Shape gives the gateway <c>/openapi</c> — "the generated document, per
    ///         api-version" — and says nothing about how a caller learns which versions there are.
    ///         This is that: one file whose name does not change, listing the files whose names do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a real OpenAPI document rather than a bespoke manifest, and that is what
    ///         makes the empty-registry case honest.</b> With no providers registered there are no
    ///         api-versions and therefore no per-version documents, and a generator that wrote
    ///         nothing at all would be indistinguishable from one that crashed before writing. This
    ///         file is always written, always valid, and says <c>0</c> in three places when the answer
    ///         is zero.
    ///     </para>
    /// </remarks>
    public static JsonObject EmitIndex(IProviderRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        var versions = new JsonArray();
        foreach (var version in ApiVersionsOf(registry)) {
            versions.Add(new JsonObject {
                ["apiVersion"] = version.Value,
                ["document"] = FileNameOf(version)
            });
        }

        var namespaces = new JsonArray();
        foreach (var declared in registry.Namespaces.OrderBy(x => x, StringComparer.Ordinal)) {
            namespaces.Add(declared);
        }

        var types = new JsonArray();
        foreach (var type in registry.Types.OrderBy(x => x.Type.ToString(), StringComparer.Ordinal)) {
            var declaredVersions = new JsonArray();
            foreach (var version in type.ApiVersions.Select(x => x.Version.Value)
                         .OrderBy(x => x, StringComparer.Ordinal)) {
                declaredVersions.Add(version);
            }

            types.Add(new JsonObject {
                ["type"] = type.Type.ToString(),
                ["apiVersions"] = declaredVersions,
                // The CLI's verb tree is built from this file rather than from every per-version
                // document, so the name and the short form have to be here too — docs/plan/21
                // § Grammar's alias table, generated.
                ["display"] = Display(type)
            });
        }

        return new JsonObject {
            ["openapi"] = SpecificationVersion,
            ["info"] = new JsonObject {
                ["title"] = Title,
                ["version"] = IndexVersion,
                ["description"] =
                    "The index of Cyber Cloud's published api-versions. One document per version is "
                    + "listed in x-cybercloud-api-versions; this file declares no paths of its own. "
                    + "Generated from the provider registry — docs/plan/02 § ADR-012."
            },
            ["paths"] = new JsonObject(),
            ["components"] = new JsonObject {
                ["schemas"] = Sorted(EnvelopeSchemas())
            },
            ["x-cybercloud-providers"] = namespaces,
            ["x-cybercloud-resource-types"] = types,
            ["x-cybercloud-api-versions"] = versions
        };
    }

    /// <summary>The file one api-version's document is written to.</summary>
    /// <param name="version">The api-version.</param>
    public static string FileNameOf(ApiVersion version) => version.Value + ".json";

    /// <summary>The file <see cref="EmitIndex" /> is written to.</summary>
    public const string IndexFileName = "index.json";

    // ── Paths ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The URL template for a resource of one type.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Nested types interleave with their parents' names, exactly as Azure spells them</b>
    ///         — <c>/servers/{serversName}/databases/{resourceName}</c> — because that is what
    ///         <c>ResourceId.Path</c> renders and what <c>TryParsePath</c> parses. The generated
    ///         document says the same thing the id grammar says, which is the one property ADR-012
    ///         exists to hold. docs/plan/12 § Child resources records why the grammar is that shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A top-level type's template is byte-identical to what this emitted before the
    ///         grammar changed</b>, and that is deliberate rather than lucky: the two shapes differ
    ///         only from depth 2, the resource's own name keeps the parameter name
    ///         <c>resourceName</c> at every depth, and the ancestors are appended rather than
    ///         renaming anything. So no published path in <c>openapi/</c> moves and the
    ///         <see cref="OpenApiCompatibility" /> gate has nothing to report — see that type's
    ///         remarks on why a changed published path would be breaking.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An ancestor's parameter is the type segment verbatim plus <c>Name</c>
    ///         (<c>{serversName}</c>), not a singularised <c>{serverName}</c>.</b> English
    ///         pluralisation is not computable — <see cref="SdkEmitter" />'s model naming carries the
    ///         same warning and <c>GroupVersionKind.Plural</c> is carried rather than derived for the
    ///         same reason — so singularising is a guess that works until <c>addresses</c>. Ugly and
    ///         right beats pretty and wrong in a generated URL template.
    ///     </para>
    /// </remarks>
    static string PathOf(ResourceTypeName type) {
        var built = new StringBuilder(
            "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
            + "/providers/"
        ).Append(type.Namespace);

        var segments = type.Type.Split('/');

        for (var i = 0; i < segments.Length; i++) {
            built.Append('/').Append(segments[i])
                .Append(i == segments.Length - 1 ? "/{resourceName}" : "/{" + segments[i] + "Name}");
        }

        return built.ToString();
    }

    /// <summary>The <c>{…Name}</c> placeholders an ancestor of <paramref name="type" /> contributes.</summary>
    static ImmutableArray<string> AncestorParametersOf(ResourceTypeName type) {
        var segments = type.Type.Split('/');
        if (segments.Length == 1) {
            return [];
        }

        var built = ImmutableArray.CreateBuilder<string>(segments.Length - 1);
        for (var i = 0; i < segments.Length - 1; i++) {
            built.Add(segments[i] + "Name");
        }

        return built.ToImmutable();
    }

    /// <summary>The component key for a resource type — <c>/</c> is not legal in one.</summary>
    /// <remarks>
    ///     A component key must match <c>^[a-zA-Z0-9\.\-_]+$</c>, and a nested type path carries a
    ///     <c>/</c>. Replacing it with <c>.</c> is unambiguous because
    ///     <see cref="ResourceTypeName" />'s segments cannot contain either character —
    ///     <see cref="OpenApiStructure" /> re-checks the result rather than trusting that sentence.
    /// </remarks>
    static string ComponentNameOf(ResourceTypeName type) =>
        type.Namespace + "." + type.Type.Replace('/', '.');

    static JsonObject ResourcePathItem(ResourceTypeRegistration type, string component, DateOnly? retiresOn) {
        var body = new JsonObject {
            ["required"] = true,
            ["content"] = new JsonObject {
                ["application/json"] = new JsonObject {
                    ["schema"] = Ref("schemas", component)
                }
            }
        };

        var item = new JsonObject {
            ["parameters"] = ResourceParameters(type.Type),
            // A fixed member order rather than a sorted one: this is the order the four verbs are
            // read in, and sorting would put `delete` first in every path item in the document.
            ["get"] = new JsonObject {
                ["operationId"] = OperationIdOf(type.Type, "Get"),
                ["summary"] = "Read a " + type.Type + ".",
                ["description"] =
                    "The body is this api-version's projection of the resource: docs/plan/08 § The "
                    + "provider registry keeps grain state as a superset and a read at an older "
                    + "version drops what that version did not declare.",
                ["responses"] = new JsonObject {
                    ["200"] = new JsonObject {
                        ["description"] = "The resource.",
                        ["content"] = new JsonObject {
                            ["application/json"] = new JsonObject {
                                ["schema"] = Ref("schemas", component)
                            }
                        }
                    }
                }.WithErrors(),
                ["x-cybercloud-permission"] = type.ReadPermission
            },
            ["put"] = new JsonObject {
                ["operationId"] = OperationIdOf(type.Type, "CreateOrUpdate"),
                ["summary"] = "Create or replace a " + type.Type + ".",
                ["description"] =
                    "A full replacement. Every property the schema marks required must be present; a "
                    + "read-only property is refused rather than ignored — docs/plan/08 § The write "
                    + "path, end to end.",
                ["requestBody"] = body.DeepClone(),
                ["responses"] = Accepted(),
                ["x-cybercloud-permission"] = type.WritePermission
            },
            ["patch"] = new JsonObject {
                ["operationId"] = OperationIdOf(type.Type, "Update"),
                ["summary"] = "Merge-patch a " + type.Type + ".",
                ["description"] =
                    "A merge patch. The patch itself may omit required properties; the merged result "
                    + "is what is validated — see ResourceSchema.Validate.",
                ["requestBody"] = body,
                ["responses"] = Accepted(),
                ["x-cybercloud-permission"] = type.WritePermission
            },
            ["delete"] = new JsonObject {
                ["operationId"] = OperationIdOf(type.Type, "Delete"),
                ["summary"] = "Delete a " + type.Type + ".",
                // ⚠ THIS SENTENCE USED TO SAY THE WINDOW WAS NOT HONOURED, AND IT WAS TELLING THE
                // TRUTH — WHICH IS WHY IT HAD TO CHANGE WITH THE CODE AND NOT AFTER IT.
                //
                // While nothing in the resource manager read SoftDeleteDays, a type declaring seven
                // days published a recovery window the platform could not deliver, and the honest
                // thing the emitter could do was say so in the document. docs/plan/08 § Soft delete is
                // built now: the DELETE parks the resource, the name is held, the quota stays
                // committed and a purge ends it. So the description states what happens, and a
                // document that still carried the disclaimer would be the same defect with the sign
                // flipped — understating a guarantee callers can now rely on.
                ["description"] = type.SoftDeleteDays > 0
                    ? "A deleted "
                    + type.Type
                    + " is recoverable for "
                    + type.SoftDeleteDays.ToString(CultureInfo.InvariantCulture)
                    + " day(s) — docs/plan/06 § Tags, locks. It leaves its resource group and its old "
                    + "address answers 404; its name is held and its quota stays committed for the "
                    + "whole window. Restore it, or purge it to end the window early — a purge needs "
                    + "'"
                    + type.PurgePermission
                    + "', which is a separate right from '"
                    + type.DeletePermission
                    + "'."
                    : "The delete is permanent: this type declares no soft-delete window.",
                ["responses"] = Accepted(),
                ["x-cybercloud-permission"] = type.DeletePermission,
                ["x-cybercloud-soft-delete-days"] = type.SoftDeleteDays,
                // ⚠ Empty for a type with no window, which is what makes "does this type have a purge"
                // and "does this type have a window" one question in the document as well as in the
                // registry.
                ["x-cybercloud-purge-permission"] = type.PurgePermission
            },
            // Registry facts with no representation in a body. They are here rather than dropped
            // because the CLI and the portal-form emitters need them and this document is what
            // docs/plan/21 § Generation generates those from — and they are extensions rather than
            // properties because none of them is a field a caller sends.
            ["x-cybercloud-resource-type"] = type.Type.ToString(),
            ["x-cybercloud-display"] = Display(type),
            ["x-cybercloud-supports-tags"] = type.SupportsTags,
            ["x-cybercloud-requires-cluster"] = type.RequiresCluster,
            // ⚠ The pointer, not just the flag. `requires-cluster: true` told a generated surface
            // that a cluster was needed and not which field carries it, so a CLI could not offer a
            // --cluster flag and a form could not put the cluster picker on the right control. It is
            // also the fact ProviderBuilder now checks the schema against.
            ["x-cybercloud-cluster-id-pointer"] = type.ClusterIdPointer,
            ["x-cybercloud-soft-delete-days"] = type.SoftDeleteDays,
            ["x-cybercloud-purge-permission"] = type.PurgePermission,
            // ⚠ The pointer, not just "this type has purge protection", for the reason
            // x-cybercloud-cluster-id-pointer above is a pointer: a CLI cannot offer a flag and a form
            // cannot put a toggle on the right control from a boolean that names no field.
            ["x-cybercloud-purge-protection-pointer"] = type.PurgeProtectionPointer,
            // The quota meters a write draws against. A caller who is about to be told
            // QuotaExceeded (docs/plan/08 § Errors) can see which limit before sending.
            ["x-cybercloud-meters"] = Meters(type)
        };

        return Deprecate(item, retiresOn);
    }

    /// <summary>
    ///     What a type is called, for the surfaces a human reads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Always emitted, and it says whether anybody declared it.</b> A CLI verb tree and a
    ///         portal breadcrumb need a name; the registry's fallback is the type's own path segment,
    ///         which is what those surfaces would have invented anyway. Emitting the fallback here
    ///         rather than in three downstream emitters means the three cannot invent three different
    ///         names — and <c>declared: false</c> makes "nobody has named this type yet" a fact you can
    ///         grep the published document for.
    ///     </para>
    ///     <para>
    ///         The fallback for the plural is the singular, because English pluralisation is not
    ///         computable — the same reason <c>GroupVersionKind.Plural</c> is carried rather than
    ///         derived.
    ///     </para>
    /// </remarks>
    static JsonObject Display(ResourceTypeRegistration type) {
        var segment = type.Type.Type.Split('/')[^1];
        var name = type.Display.Name.Length > 0 ? type.Display.Name : segment;

        return new JsonObject {
            ["name"] = name,
            ["plural"] = type.Display.Plural.Length > 0 ? type.Display.Plural : name,
            ["alias"] = type.Display.Alias,
            ["summary"] = type.Display.Summary,
            ["declared"] = !type.Display.IsEmpty
        };
    }

    static JsonArray Meters(ResourceTypeRegistration type) {
        var meters = new JsonArray();

        foreach (var meter in type.Meters
                     .OrderBy(x => x.Meter.ToString(), StringComparer.Ordinal)
                     .ThenBy(x => x.AmountPointer, StringComparer.Ordinal)) {
            var reads = new JsonArray();
            foreach (var pointer in meter.Reads) {
                reads.Add(pointer);
            }

            meters.Add(new JsonObject {
                ["meter"] = meter.Meter.ToString(),
                ["amountPointer"] = meter.AmountPointer,
                ["fallback"] = meter.Fallback,
                // ⚠ THE TWO MEMBERS THAT KEEP A COMPUTED AMOUNT GENERABLE.
                //
                // This used to carry a pointer and a fallback and nothing else, on the argument that
                // "a pointer rather than a delegate is what makes this generable at all". The argument
                // was right about delegates and wrong about the conclusion: every managed service's
                // real amount is a Kubernetes quantity, is usually absent from the body and named by a
                // preset, and is per-instance × instances — so a pointer could not address any of them
                // and the honest generated document for a Postgres server listed one meter, `Resources`,
                // reserving one. A published pointer that was never the amount is not more generable
                // than a published formula; it is less true. MeterDerivation therefore has to declare
                // both of these, and every meter — flat, pointed or derived — reports them the same
                // way, so "what moves this quota" is answerable from the document for every type.
                ["expression"] = meter.Expression,
                ["reads"] = reads
            });
        }

        return meters;
    }

    /// <summary>
    ///     Marks every operation on a path item deprecated when its api-version is under notice.
    /// </summary>
    static JsonObject Deprecate(JsonObject item, DateOnly? retiresOn) {
        if (retiresOn is not { } date) {
            return item;
        }

        var stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var method in item.ToList()) {
            if (method.Value is JsonObject operation && !method.Key.StartsWith("x-", StringComparison.Ordinal)) {
                operation["deprecated"] = true;
            }
        }

        item["x-cybercloud-retires-on"] = stamp;
        return item;
    }

    /// <summary>The component key of an action's request or response schema.</summary>
    static string ActionComponentOf(string typeComponent, string action, string role) =>
        typeComponent + "." + Capitalise(action) + role;

    static JsonObject ActionPathItem(
        ResourceTypeRegistration type,
        ActionRegistration action,
        DateOnly? retiresOn,
        string? requestComponent,
        string? responseComponent
    ) {
        if (action.Kind is not ActionKind.Post) {
            throw new InvalidOperationException(
                $"Action '{type.Type}/{action.Name}' declares ActionKind.{action.Kind}, and the only "
                + "kind with a defined HTTP shape is Post — docs/plan/08 § The provider registry. An "
                + "action whose invocation this emitter had to guess would be a surface the registry "
                + "did not describe."
            );
        }

        var post = new JsonObject {
            ["operationId"] = OperationIdOf(type.Type, Capitalise(action.Name)),
            ["summary"] = action.Name + " a " + type.Type + ".",
            ["description"] =
                "An action never creates: a POST to a name that does not exist is a 404 — "
                + "docs/plan/08 § The write path, end to end."
                + (action.Secret
                    ? " ⚠ The response carries secret material. It is always audited and is never "
                    + "cached — docs/plan/08 § The provider registry."
                    : string.Empty)
                + (action.LongRunning
                    ? " This action is long-running: it answers 202 and an operation to poll, like "
                    + "every other write."
                    : string.Empty)
        };

        if (requestComponent is { }) {
            post["requestBody"] = new JsonObject {
                ["required"] = true,
                ["content"] = new JsonObject {
                    ["application/json"] = new JsonObject {
                        ["schema"] = Ref("schemas", requestComponent)
                    }
                }
            };
        }

        // ⚠ Long-running actions answer 202 and nothing else, exactly as a PUT does — there is one
        // long-running shape in this platform and an action that does work is not a second one.
        post["responses"] = action.LongRunning
            ? Accepted()
            : new JsonObject {
                ["200"] = new JsonObject {
                    ["description"] = responseComponent is null
                        // ⚠ Still reported rather than invented, for an action whose author has not
                        // declared a response. The registry can now say; this one has not.
                        ? "The action's result. ⚠ Unconstrained: this action declares no response "
                        + "schema, so no generated surface can type it. Declare one on "
                        + "IResourceTypeBuilder.Action."
                        : "The action's result.",
                    ["content"] = new JsonObject {
                        ["application/json"] = new JsonObject {
                            ["schema"] = responseComponent is null
                                ? new JsonObject()
                                : Ref("schemas", responseComponent)
                        }
                    }
                }
            }.WithErrors();

        post["x-cybercloud-permission"] = action.Permission;
        post["x-cybercloud-secret"] = action.Secret;
        post["x-cybercloud-long-running"] = action.LongRunning;

        var item = new JsonObject {
            ["parameters"] = ResourceParameters(type.Type),
            ["post"] = post,
            ["x-cybercloud-resource-type"] = type.Type.ToString(),
            ["x-cybercloud-action"] = action.Name
        };

        return Deprecate(item, retiresOn);
    }

    /// <summary>
    ///     <c>GET /operations/{operationId}</c> — the poll half of docs/plan/10 § Long-running
    ///     operations, over HTTP.
    /// </summary>
    static JsonObject OperationPathItem() =>
        new() {
            ["parameters"] = new JsonArray {
                Ref("parameters", "OperationId"),
                Ref("parameters", "ApiVersion")
            },
            ["get"] = new JsonObject {
                ["operationId"] = "Operations_Get",
                ["summary"] = "Poll a long-running operation.",
                ["description"] =
                    "The target of the Azure-AsyncOperation header a 202 returns. Poll until status "
                    + "is terminal, then GET the resource — docs/plan/10 § Long-running operations, "
                    + "over HTTP.",
                ["responses"] = new JsonObject {
                    ["200"] = new JsonObject {
                        ["description"] = "The operation's current state.",
                        ["content"] = new JsonObject {
                            ["application/json"] = new JsonObject {
                                ["schema"] = Ref("schemas", OperationStatusSchema)
                            }
                        }
                    }
                }.WithErrors()
            }
        };

    /// <summary>
    ///     The five shared parameters, plus one inline parameter per ancestor of a nested type.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The ancestors are inline rather than <c>$ref</c>s to <c>/components/parameters</c>,
    ///     and they have to be:</b> their names come from the type path, so a shared component would
    ///     need one entry per distinct ancestor segment across every provider — a component set that
    ///     grows with the registry and collides the moment two providers both nest under
    ///     <c>servers</c> with different descriptions. <see cref="OpenApiStructure" /> checks that
    ///     what is declared here and what appears in the template agree in both directions, so a
    ///     mismatch between this and <see cref="PathOf" /> fails <c>Generate</c> rather than shipping.
    /// </remarks>
    static JsonArray ResourceParameters(ResourceTypeName type) {
        var parameters = new JsonArray {
            Ref("parameters", "TenantId"),
            Ref("parameters", "SubscriptionId"),
            Ref("parameters", "ResourceGroupName")
        };

        foreach (var ancestor in AncestorParametersOf(type)) {
            parameters.Add(
                new JsonObject {
                    ["name"] = ancestor,
                    ["in"] = "path",
                    ["required"] = true,
                    ["description"] =
                        "The name of the parent resource this one lives inside. A child is addressed "
                        + "through its parent — docs/plan/12 § Child resources.",
                    ["schema"] = new JsonObject {
                        ["type"] = "string",
                        ["pattern"] = "^" + ResourceNaming.Pattern + "$",
                        ["minLength"] = ResourceNaming.MinLength,
                        ["maxLength"] = ResourceNaming.MaxLength
                    }
                }
            );
        }

        parameters.Add(Ref("parameters", "ResourceName"));
        parameters.Add(Ref("parameters", "ApiVersion"));

        return parameters;
    }

    /// <summary>
    ///     The <c>202</c> every write returns, with the two headers docs/plan/10 requires.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>202 and nothing else.</b> docs/plan/08 § The write path, end to end ends in a
    ///     <c>WriteAccepted</c> for every verb, so there is no synchronous success to declare. A
    ///     document that also offered <c>200</c> would make every generated SDK branch on a status it
    ///     will never see.
    /// </remarks>
    static JsonObject Accepted() =>
        new JsonObject {
            ["202"] = new JsonObject {
                ["description"] =
                    "Accepted. Poll the Azure-AsyncOperation target until the status is terminal.",
                ["headers"] = new JsonObject {
                    ["Azure-AsyncOperation"] = new JsonObject {
                        ["description"] = "The absolute URL of the operation to poll.",
                        ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "uri" }
                    },
                    ["Retry-After"] = new JsonObject {
                        ["description"] = "Seconds to wait before the first poll.",
                        ["required"] = true,
                        ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
                    }
                }
            }
        }.WithErrors();

    // ── Bodies, from the registry's schema and from nothing else ───────────────────────────────

    /// <summary>
    ///     One resource type's body at one api-version, built from the flat pointer list
    ///     <see cref="ResourceSchema.Properties" /> holds.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One schema serves both directions.</b> <c>readOnly</c> and <c>writeOnly</c> are
    ///         exactly the two JSON Schema keywords needed to say "the server owns this" and "this
    ///         never comes back", which are exactly <see cref="SchemaProperty.ReadOnly" /> and
    ///         <see cref="SchemaProperty.Secret" />. ⚠ <c>readOnly</c> is enforced —
    ///         <see cref="ResourceSchema.Validate" /> refuses a body that sets one — but
    ///         <c>writeOnly</c> is currently a wish: no runtime read strips a secret, for the reasons
    ///         the remarks on <see cref="SchemaProperty" /> lay out. Emitting a request schema and a
    ///         response schema instead would double every component and make the compatibility diff
    ///         compare four things where two would do.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three shapes are refused rather than emitted</b>, because each is a body no caller
    ///         can satisfy and each would otherwise be discovered by a customer:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <b>An orphan</b> — <c>/properties/sku/name</c> declared while
    ///             <c>/properties/sku</c> is not. <see cref="ResourceSchema.Validate" /> walks the body
    ///             and refuses every undeclared pointer, so the parent is refused and the child is
    ///             unreachable: every request fails, whatever it sends.
    ///         </item>
    ///         <item>
    ///             <b>A required read-only property</b> — required means "must be present on a PUT"
    ///             and read-only means "refused on a PUT", so the two together are a contradiction
    ///             that fails every PUT twice.
    ///         </item>
    ///         <item>
    ///             <b><see cref="SchemaKind.Unknown" /></b> — the never-assigned member, which reaches
    ///             a schema only through <c>default(SchemaProperty)</c>.
    ///         </item>
    ///     </list>
    /// </remarks>
    static JsonObject SchemaOf(ResourceTypeRegistration type, ResourceSchema schema) =>
        BodySchema(
            type.Type.ToString(),
            schema,
            type.SupportsTags,
            type.Type.ToString(),
            "The body of a " + type.Type + ". Generated from the provider registry's schema, which is "
            + "the same object the write path validates against — docs/plan/08 § The provider registry."
        );

    /// <summary>
    ///     One <see cref="ResourceSchema" /> as a JSON Schema object — a resource body, or an action's
    ///     request or response.
    /// </summary>
    /// <param name="owner">What to name in an error message when the schema cannot be expressed.</param>
    /// <param name="schema">The registry's schema.</param>
    /// <param name="withTags">Whether the platform's tag bag belongs in it.</param>
    /// <param name="title">The <c>title</c> keyword.</param>
    /// <param name="description">The <c>description</c> keyword.</param>
    /// <remarks>
    ///     ⚠ <b>An action's body goes through the same method as a resource's, and that is the point of
    ///     giving <c>ActionRegistration</c> schemas at all.</b> An action's parameters are now
    ///     described, constrained and generated by exactly the machinery a resource's properties are,
    ///     rather than by a second convention nobody would keep in step.
    /// </remarks>
    static JsonObject BodySchema(
        string owner,
        ResourceSchema schema,
        bool withTags,
        string title,
        string description
    ) {
        var byParent = new Dictionary<string, List<SchemaProperty>>(StringComparer.Ordinal);

        foreach (var property in schema.Properties) {
            // ⚠ Re-checked here even though ResourceSchema.Of already refuses one, because a schema
            // can be built by object initialiser and reach an emitter without passing through Of. A
            // constraint that contradicts its kind reaches the document as a keyword no validator will
            // apply — a promise the write path does not keep.
            var incoherences = property.Incoherences();

            if (!incoherences.IsEmpty) {
                throw new InvalidOperationException(
                    $"'{owner}' declares '{property.JsonPointer}' incoherently: "
                    + string.Join(" ", incoherences)
                );
            }

            if (property.Kind is SchemaKind.Unknown) {
                throw new InvalidOperationException(
                    $"'{owner}' declares '{property.JsonPointer}' with SchemaKind.Unknown, which is "
                    + "the never-assigned member. A property with no kind cannot be validated or "
                    + "generated."
                );
            }

            if (property.Required && property.ReadOnly) {
                throw new InvalidOperationException(
                    $"'{owner}' declares '{property.JsonPointer}' as both required and read-only. "
                    + "Required means required on a PUT and read-only means refused on a PUT, so every "
                    + "PUT would fail twice over — see the remarks on SchemaProperty."
                );
            }

            if (property.ParentPointer.Length > 0) {
                // Nullable<SchemaProperty> rather than FirstOrDefault: SchemaProperty is a struct, so
                // "not found" and "found a default one" would be the same value.
                SchemaProperty? parent = null;

                foreach (var candidate in schema.Properties) {
                    if (string.Equals(candidate.JsonPointer, property.ParentPointer, StringComparison.Ordinal)) {
                        parent = candidate;
                        break;
                    }
                }

                if (parent is null) {
                    throw new InvalidOperationException(
                        $"'{owner}' declares '{property.JsonPointer}' but not its parent "
                        + $"'{property.ParentPointer}'. ResourceSchema.Validate refuses every undeclared "
                        + "pointer, so the parent is rejected and this property is unreachable — every "
                        + "request would fail whatever it sent."
                    );
                }

                if (parent.Value.Kind is not SchemaKind.Nested) {
                    throw new InvalidOperationException(
                        $"'{owner}' declares '{property.JsonPointer}' inside "
                        + $"'{property.ParentPointer}', which is a {SchemaVocabulary.JsonTypeOf(parent.Value.Kind)} and "
                        + "not an object. Only a SchemaKind.Nested property can carry members."
                    );
                }
            }

            if (!byParent.TryGetValue(property.ParentPointer, out var siblings)) {
                siblings = [];
                byParent[property.ParentPointer] = siblings;
            }

            siblings.Add(property);
        }

        var root = ObjectAt(string.Empty, byParent, schema.RejectsUnknownProperties);

        if (withTags) {
            // ⚠ THE fix, and the reason this branch exists at all. The write path accepts a root-level
            // `tags` object for a type that declares SupportsTags (ResourceSchema.Validate's allowTags)
            // and a schema declares only the provider's own properties — so the emitted body said
            // `additionalProperties: false` and named no `tags`, over an API that took one. The
            // published document under-described the API in the one direction that matters: a
            // generated SDK had no member, a generated CLI no flag, a generated form no control, and a
            // caller reading the contract would have concluded tags were not supported.
            if (root["properties"] is JsonObject members) {
                members[TagRules.Name] = TagsSchema();
                root["properties"] = Sorted(members);
            }
        }

        root["title"] = title;
        root["description"] = description;

        return Sorted(root);
    }

    /// <summary>
    ///     The platform's tag bag, identical for every type that declares <c>SupportsTags</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every number and name here is <see cref="TagRules" />', which the resource grain's cap
    ///     also reads. <c>maxProperties</c> is a true statement about the API rather than about
    ///     <see cref="ResourceSchema.Validate" />: the cap applies to the <i>merged</i> tag set and is
    ///     enforced by the grain, because a <c>PATCH</c> adding one tag to forty-nine is the request
    ///     that crosses it and the schema only ever sees the patch.
    /// </remarks>
    static JsonObject TagsSchema() =>
        new() {
            ["type"] = "object",
            ["description"] =
                "Key/value tags, at most "
                + TagRules.MaxTags.ToString(CultureInfo.InvariantCulture)
                + " pairs — docs/plan/06 § Tags, locks. Values are strings; the cap applies to the "
                + "merged set, so a PATCH that adds one tag to a full bag is refused.",
            ["additionalProperties"] = new JsonObject { ["type"] = "string" },
            ["maxProperties"] = TagRules.MaxTags,
            ["x-cybercloud-widget"] = SchemaVocabulary.Of(WidgetHint.TagInput)
        };

    static JsonObject ObjectAt(
        string pointer,
        Dictionary<string, List<SchemaProperty>> byParent,
        bool rejectsUnknown
    ) {
        var properties = new JsonObject();
        var required = new JsonArray();

        if (byParent.TryGetValue(pointer, out var children)) {
            foreach (var child in children.OrderBy(x => x.Name, StringComparer.Ordinal)) {
                properties[child.Name] = PropertyAt(child, byParent, rejectsUnknown);

                if (child.Required) {
                    required.Add(child.Name);
                }
            }
        }

        var node = new JsonObject {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0) {
            node["required"] = required;
        }

        // Stated in both directions rather than omitted when true: `additionalProperties` going from
        // true to false is a narrowing, and the compatibility diff can only see that if the earlier
        // document said `true` out loud.
        node["additionalProperties"] = !rejectsUnknown;
        return node;
    }

    static JsonObject PropertyAt(
        SchemaProperty property,
        Dictionary<string, List<SchemaProperty>> byParent,
        bool rejectsUnknown
    ) {
        JsonObject node;

        switch (property.Kind) {
            case SchemaKind.Nested:
                node = ObjectAt(property.JsonPointer, byParent, rejectsUnknown);
                break;

            case SchemaKind.Array:
                node = new JsonObject {
                    ["type"] = "array",
                    // ⚠ Typed, at last. `items: {}` reached an SDK generator as `object[]` and gave a
                    // CLI no way to type a repeated flag. The value constraints — enum, format,
                    // pattern, bounds — belong on the element rather than on the array, which is also
                    // where ResourceSchema.Validate applies them.
                    ["items"] = Sorted(
                        Constrained(
                            new JsonObject { ["type"] = SchemaVocabulary.JsonTypeOf(property.ElementKind) },
                            property
                        )
                    )
                };

                break;

            default:
                node = Constrained(
                    new JsonObject { ["type"] = SchemaVocabulary.JsonTypeOf(property.Kind) },
                    property
                );

                break;
        }

        if (property.Nullable) {
            // OpenAPI 3.1 is JSON Schema 2020-12, where nullability is a type union rather than 3.0's
            // `nullable: true`. ⚠ The union is on the property and never on an array's `items`: a
            // nullable array may be absent-as-null, and a null *element* is a separate declaration
            // this model deliberately does not have — see ResourceSchema.ValueProblems.
            node["type"] = new JsonArray { node["type"]?.DeepClone(), "null" };
        }

        if (property.Description.Length > 0) {
            node["description"] = property.Description;
        }

        if (property.ReadOnly) {
            node["readOnly"] = true;
        }

        if (property.Secret) {
            // writeOnly because that is what a secret property is meant to be — ⚠ the runtime read
            // path does not yet strip one, see SchemaProperty's remarks — and format=password because
            // that is what a portal form masks on.
            node["writeOnly"] = true;
            node["format"] = "password";
            node["x-cybercloud-secret"] = true;
        }

        if (property.Widget is not WidgetHint.None) {
            // ADR-012's promised hint, and the first time the registry has had anywhere to put one.
            node["x-cybercloud-widget"] = SchemaVocabulary.Of(property.Widget);
        }

        if (property.Immutable) {
            node["x-cybercloud-immutable"] = true;
        }

        if (Literal(property.DefaultJson) is { } fallback) {
            node["default"] = fallback;
        }

        if (Literal(property.ExampleJson) is { } example) {
            node["example"] = example;
        }

        return Sorted(node);
    }

    /// <summary>
    ///     Adds the value constraints — the keywords that narrow what a scalar may be.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every keyword here is one <see cref="ResourceSchema.Validate" /> enforces.</b> That is
    ///     the whole claim: a caller reading the document and the write path refusing a body are
    ///     reading one declaration. A keyword emitted here that the validator did not apply would be
    ///     the drift ADR-012 exists to remove, pointing the other way — documentation the runtime does
    ///     not honour.
    /// </remarks>
    static JsonObject Constrained(JsonObject node, SchemaProperty property) {
        if (!property.AllowedValues.IsEmpty) {
            var values = new JsonArray();

            // ⚠ Declaration order, not sorted. `enum` is a set to the compatibility diff, so order
            // carries no contract — but it is the order a provider wrote and therefore the order a
            // generated CLI's completion and a portal's select will show, and re-ordering it would
            // move `standard` above `basic` in every dropdown for no reason.
            foreach (var allowed in property.AllowedValues) {
                values.Add(allowed);
            }

            node["enum"] = values;
        }

        if (SchemaVocabulary.Of(property.Format) is { Length: > 0 } format) {
            node["format"] = format;
        }

        if (property.Minimum is { } low) {
            node["minimum"] = low;
        }

        if (property.Maximum is { } high) {
            node["maximum"] = high;
        }

        if (property.MinLength is { } shortest) {
            node["minLength"] = shortest;
        }

        if (property.MaxLength is { } longest) {
            node["maxLength"] = longest;
        }

        if (property.Pattern.Length > 0) {
            // ⚠ Anchored in the document because it is anchored in the validator. JSON Schema's
            // `pattern` is a search; ours is a whole-value match, and emitting the bare pattern would
            // publish a looser rule than the one the API applies.
            node["pattern"] = "^(?:" + property.Pattern + ")$";
        }

        return node;
    }

    /// <summary>A declared JSON literal, or <see langword="null" /> when there is none.</summary>
    /// <exception cref="InvalidOperationException">The literal is not JSON.</exception>
    /// <remarks>
    ///     ⚠ Parsed rather than pasted. A literal that is not JSON would make the emitted document
    ///     unparseable, and a generator that produced a broken file would be found by whoever opened
    ///     it rather than by the run that produced it.
    /// </remarks>
    static JsonNode? Literal(string literal) {
        if (literal.Length == 0) {
            return null;
        }

        try {
            return JsonNode.Parse(literal);
        } catch (JsonException malformed) {
            throw new InvalidOperationException(
                $"'{literal}' is declared as a JSON literal and does not parse: {malformed.Message}",
                malformed
            );
        }
    }

    // ── The platform envelope ──────────────────────────────────────────────────────────────────

    static JsonObject Parameters(ApiVersion version) =>
        new() {
            ["ApiVersion"] = new JsonObject {
                ["name"] = "api-version",
                ["in"] = "query",
                ["required"] = true,
                ["description"] =
                    "Required on every request. Missing is a 400 naming the current version. There is "
                    + "no 'latest' and no '-preview': a version is a date and is immutable — "
                    + "docs/plan/10 § API versioning.",
                ["schema"] = new JsonObject {
                    ["type"] = "string",
                    // The enum is the single version this document describes. A caller that sends a
                    // different date is talking to a different document, which is the whole of the
                    // immutable-date rule expressed in one keyword.
                    ["enum"] = new JsonArray { version.Value }
                }
            },
            ["OperationId"] = new JsonObject {
                ["name"] = "operationId",
                ["in"] = "path",
                ["required"] = true,
                ["description"] = "The operation, from the Azure-AsyncOperation header of a 202.",
                ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }
            },
            ["ResourceGroupName"] = NameParameter(
                "resourceGroupName",
                "The resource group — the lifecycle boundary. docs/plan/06 § The hierarchy."
            ),
            ["ResourceName"] = NameParameter(
                "resourceName",
                "The resource's name within its group."
            ),
            ["SubscriptionId"] = GuidParameter(
                "subscriptionId",
                "The subscription — the billing and quota boundary. docs/plan/06 § The hierarchy."
            ),
            ["TenantId"] = GuidParameter("tenantId", "The owning tenant. docs/plan/06 § The hierarchy.")
        };

    static JsonObject GuidParameter(string name, string description) =>
        new() {
            ["name"] = name,
            ["in"] = "path",
            ["required"] = true,
            ["description"] =
                description
                + " Hyphenated 'D' form; braced, parenthesised and bare-hex forms are rejected so that "
                + "one resource has exactly one path.",
            ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" }
        };

    /// <summary>
    ///     A path segment that is a name, constrained by <see cref="ResourceNaming" />'s own constants
    ///     rather than by a pattern retyped here.
    /// </summary>
    static JsonObject NameParameter(string name, string description) =>
        new() {
            ["name"] = name,
            ["in"] = "path",
            ["required"] = true,
            ["description"] = description,
            ["schema"] = new JsonObject {
                ["type"] = "string",
                ["minLength"] = ResourceNaming.MinLength,
                ["maxLength"] = ResourceNaming.MaxLength,
                ["pattern"] = "^" + ResourceNaming.Pattern + "$"
            }
        };

    /// <summary>
    ///     The error responses every operation can produce, one per status any registered
    ///     <see cref="ErrorCode" /> maps onto.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Derived from <see cref="ErrorCode.HttpStatuses" />, and it used to be a list
    ///         written here from the plan's prose.</b> That was a reported gap: the emitter and the
    ///         gateway would each have carried a copy of the code-to-status mapping and would have had
    ///         to agree by hand. <see cref="ErrorCode.HttpStatus" /> is now where it lives, so adding a
    ///         code with a status nothing else uses adds the response to every operation in every
    ///         document without anybody editing this method.
    ///     </para>
    ///     <para>
    ///         <b>Each response names the codes that produce it</b>, in
    ///         <c>x-cybercloud-error-codes</c>. A caller handling a <c>409</c> can see the five things
    ///         that mean, which is the difference between a retry that helps and one that hammers.
    ///     </para>
    /// </remarks>
    static JsonObject Responses() {
        var responses = new JsonObject();

        foreach (var status in ErrorCode.HttpStatuses) {
            var codes = new JsonArray();

            foreach (var code in ErrorCode.WithStatus(status)
                         .Select(x => x.Value)
                         .OrderBy(x => x, StringComparer.Ordinal)) {
                codes.Add(code);
            }

            responses[ResponseNameOf(status)] = ErrorResponse(StatusDescription(status), codes);
        }

        return responses;
    }

    /// <summary>
    ///     The component key one status's response is published under. ⚠ Stable: it is the target of a
    ///     <c>$ref</c> in every operation, so renaming one is a breaking change to every path.
    /// </summary>
    internal static string ResponseNameOfPublic(int status) => ResponseNameOf(status);

    static string ResponseNameOf(int status) =>
        status switch {
            400 => "BadRequest",
            403 => "Forbidden",
            404 => "NotFound",
            409 => "Conflict",
            412 => "PreconditionFailed",
            429 => "TooManyRequests",
            500 => "InternalServerError",
            _ => throw new InvalidOperationException(
                $"An ErrorCode maps onto HTTP {status.ToString(CultureInfo.InvariantCulture)} and this "
                + "emitter has no component name for it. A new status needs a name here, because the "
                + "name is the $ref target every operation in every published document points at."
            )
        };

    /// <summary>What a status means on this platform, beyond what HTTP already says.</summary>
    static string StatusDescription(int status) =>
        status switch {
            400 => "The request is malformed: an invalid body, an unknown property, a missing or "
                   + "unparseable api-version.",
            403 => "The caller can read the resource but may not perform this action — "
                   + "docs/plan/07 § The enforcement seam.",
            404 => "The resource does not exist, or the caller may not read it. ⚠ The two are "
                   + "deliberately indistinguishable — docs/plan/07 § The enforcement seam.",
            409 => "The write conflicts: the name is taken, an operation is already running, or the "
                   + "scope is locked.",
            412 => "An If-Match etag did not match — docs/plan/06 § Tags, locks. A conditional retry "
                   + "keys on this rather than on the 409.",
            429 => "The caller is being rate limited, or a quota meter would be exceeded — "
                   + "docs/plan/10 § Rate limiting and docs/plan/06 § Quota.",
            500 => "An internal error. ⚠ Never carries exception detail: the correlation id is in the "
                   + "response header and the detail is in the trace — docs/plan/08 § Errors.",
            _ => "An error."
        };

    static JsonObject ErrorResponse(string description, JsonArray codes) =>
        new() {
            ["description"] = description,
            ["content"] = new JsonObject {
                ["application/json"] = new JsonObject {
                    ["schema"] = Ref("schemas", ErrorResponseSchema)
                }
            },
            ["x-cybercloud-error-codes"] = codes
        };

    /// <summary>
    ///     The schemas every document carries: the one error body and the operation-status resource.
    /// </summary>
    static JsonObject EnvelopeSchemas() {
        var codes = new JsonArray();
        foreach (var code in ErrorCode.All.Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal)) {
            codes.Add(code);
        }

        var states = new JsonArray();
        foreach (var state in Enum.GetValues<OperationState>()
                     .Where(x => x is not OperationState.Unknown)
                     .Select(x => x.ToString())
                     .OrderBy(x => x, StringComparer.Ordinal)) {
            states.Add(state);
        }

        // ⚠ Code → status, published. Before ErrorCode carried a status this mapping existed only as
        // prose in two plan documents and as a literal list inside this emitter, so a gateway and a
        // generated client had to agree with it by hand. It is an x- extension because a client that
        // switches on the code needs it and the compatibility diff treats a hint as prose — a code's
        // status moving is caught by the response list changing, which is contract.
        var statuses = new JsonObject();
        foreach (var code in ErrorCode.All.OrderBy(x => x.Value, StringComparer.Ordinal)) {
            statuses[code.Value] = code.HttpStatus;
        }

        return new JsonObject {
            [ErrorCodeSchema] = new JsonObject {
                ["type"] = "string",
                ["description"] =
                    "A stable, documented, greppable identifier. ⚠ It is part of the API contract and "
                    + "changing one is a breaking change — docs/plan/08 § Errors. The closed set is "
                    + "CyberCloud.Core.ErrorCode.All.",
                ["enum"] = codes,
                ["x-cybercloud-http-status"] = statuses
            },
            [ErrorSchema] = new JsonObject {
                ["type"] = "object",
                ["description"] = "One error. docs/plan/08 § Errors.",
                ["properties"] = new JsonObject {
                    ["code"] = Ref("schemas", ErrorCodeSchema),
                    ["details"] = new JsonObject {
                        ["type"] = "array",
                        ["description"] =
                            "Every other problem found. A form that has to be fixed one field per round "
                            + "trip is a form nobody finishes.",
                        ["items"] = Ref("schemas", ErrorSchema)
                    },
                    ["message"] = new JsonObject {
                        ["type"] = "string",
                        ["description"] =
                            "For a human, naming the actual numbers. 'Quota exceeded' without the meter, "
                            + "the request and the remainder is a support ticket by construction."
                    },
                    ["target"] = new JsonObject {
                        ["type"] = "string",
                        ["description"] =
                            "An RFC 6901 JSON Pointer into the request body, so the portal can highlight "
                            + "the field."
                    }
                },
                ["required"] = new JsonArray { "code", "message" },
                ["additionalProperties"] = false
            },
            [ErrorResponseSchema] = new JsonObject {
                ["type"] = "object",
                ["description"] = "One shape, everywhere, Azure's. docs/plan/08 § Errors.",
                ["properties"] = new JsonObject {
                    ["error"] = Ref("schemas", ErrorSchema)
                },
                ["required"] = new JsonArray { "error" },
                ["additionalProperties"] = false
            },
            [OperationStateSchema] = new JsonObject {
                ["type"] = "string",
                ["description"] =
                    "Azure's status vocabulary. ⚠ Canceled is reached only after the delete path has run "
                    + "for everything already applied — cancellation completes rather than abandoning.",
                ["enum"] = states
            },
            [OperationProgressSchema] = new JsonObject {
                ["type"] = "object",
                ["description"] =
                    "One progress entry. The array is this platform's addition to Azure's pattern and is "
                    + "what makes a nine-minute cluster creation tolerable — docs/plan/10.",
                ["properties"] = new JsonObject {
                    ["at"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                    ["message"] = new JsonObject {
                        ["type"] = "string",
                        ["description"] = "What happened, naming the actual numbers."
                    },
                    ["percentComplete"] = Percent(),
                    ["step"] = new JsonObject {
                        ["type"] = "string",
                        ["description"] = "The phase a reconciler reported — applying, waiting-for-ready."
                    }
                },
                ["required"] = new JsonArray { "at", "step" },
                ["additionalProperties"] = false
            },
            [OperationStatusSchema] = new JsonObject {
                ["type"] = "object",
                ["description"] =
                    "An operation's current state. ⚠ The members are docs/plan/10 § Long-running "
                    + "operations, over HTTP's, which is a subset of the OperationStatus wire type — no "
                    + "HTTP projection of that record is declared anywhere in the tree.",
                ["properties"] = new JsonObject {
                    ["error"] = Ref("schemas", ErrorSchema),
                    ["percentComplete"] = Percent(),
                    ["progress"] = new JsonObject {
                        ["type"] = "array",
                        ["items"] = Ref("schemas", OperationProgressSchema)
                    },
                    ["status"] = Ref("schemas", OperationStateSchema)
                },
                ["required"] = new JsonArray { "status" },
                ["additionalProperties"] = false
            }
        };
    }

    static JsonObject Percent() =>
        new() {
            ["type"] = "integer",
            ["minimum"] = 0,
            ["maximum"] = 100
        };

    // ── Small shared machinery ─────────────────────────────────────────────────────────────────

    static JsonObject Ref(string section, string name) =>
        new() { ["$ref"] = "#/components/" + section + "/" + name };

    /// <summary>
    ///     An <c>operationId</c>, which the specification requires to be unique across the whole
    ///     document and which every SDK generator turns into a method name.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The provider namespace is in it, and leaving it out is the obvious mistake.</b> Azure's
    ///     own specs spell this <c>Servers_CreateOrUpdate</c> because each resource provider gets its
    ///     own document; ours is one document per api-version across every provider, so
    ///     <c>CyberCloud.DBforPostgreSQL/servers</c> and <c>CyberCloud.DBforMySQL/servers</c> — two
    ///     types docs/plan/03 § Providers plans to have — would both be <c>servers_CreateOrUpdate</c>.
    ///     <see cref="OpenApiStructure" /> catches the collision, but catching it at the eleventh
    ///     provider is worse than not having it.
    /// </remarks>
    static string OperationIdOf(ResourceTypeName type, string verb) =>
        type.Namespace.Replace('.', '_') + "_" + type.Type.Replace('/', '_') + "_" + verb;

    static string Capitalise(string value) =>
        value.Length == 0
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>
    ///     Returns a copy whose members are in ordinal order.
    /// </summary>
    /// <remarks>
    ///     ⚠ Used only where the member set is data-driven — the component maps, and a property node
    ///     whose keywords depend on flags. A hand-written literal is already in a chosen order and
    ///     sorting it would put <c>additionalProperties</c> above <c>type</c> in every schema.
    /// </remarks>
    static JsonObject Sorted(JsonObject value) {
        var sorted = new JsonObject();

        foreach (var member in value.ToList().OrderBy(x => x.Key, StringComparer.Ordinal)) {
            // Detached from the old parent first: a JsonNode belongs to one parent and re-adding an
            // attached node throws.
            value.Remove(member.Key);
            sorted[member.Key] = member.Value;
        }

        return sorted;
    }
}

/// <summary>Small extensions that keep the emitter's literals readable.</summary>
static class OpenApiEmitterExtensions {
    /// <summary>
    ///     Adds one error response per status any <see cref="ErrorCode" /> maps onto, plus a
    ///     <c>default</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read off <see cref="ErrorCode.HttpStatuses" />, not written out.</b> The list used to
    ///     be six literals here, and the mapping they encoded lived nowhere a gateway or a generator
    ///     could read — the emitter's own remarks reported it as a gap. Now a code and its status are
    ///     declared together and every surface reads the same declaration.
    ///     <para>
    ///         <c>default</c> as well, because a status a future release adds should reach an existing
    ///         generated client as a correctly-shaped error rather than as an untyped stream.
    ///     </para>
    /// </remarks>
    public static JsonObject WithErrors(this JsonObject responses) {
        ArgumentNullException.ThrowIfNull(responses);

        foreach (var status in ErrorCode.HttpStatuses) {
            responses[status.ToString(CultureInfo.InvariantCulture)] =
                Reference(OpenApiEmitter.ResponseNameOfPublic(status));
        }

        responses["default"] = Reference(OpenApiEmitter.ResponseNameOfPublic(500));
        return responses;
    }

    static JsonObject Reference(string name) =>
        new() { ["$ref"] = "#/components/responses/" + name };
}
