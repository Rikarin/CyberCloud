using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     The first of ADR-012's four surfaces: the provider registry rendered as an OpenAPI 3.1
///     document, one per api-version.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/02 § ADR-012 makes this the surface the other three are generated <i>from</i>
///         (docs/plan/21 § Generation), so this type is the load-bearing one: a fact that cannot be
///         expressed here cannot reach the CLI, the SDK or a portal form either.
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
                paths[resourcePath + "/" + action.Name] = ActionPathItem(type, action, retiresOn);
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
                ["apiVersions"] = declaredVersions
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
    ///     ⚠ <b>Nested types are <i>not</i> interleaved with their parents' names, and that is this
    ///     platform's grammar rather than an omission here.</b> Azure spells a nested type
    ///     <c>/servers/{serverName}/databases/{databaseName}</c>;
    ///     <see cref="CyberCloud.Core.Resources.ResourceId" /> spells it
    ///     <c>/servers/databases/{name}</c> — one name, at the end, with the type path whole in the
    ///     middle. That is what <c>ResourceId.Path</c> renders and what <c>TryParsePath</c> parses
    ///     (its comment reads <c>8..^1:{type…} ^1:{name}</c>), so the generated document says the same
    ///     thing the id grammar says. Emitting Azure's shape would produce URLs that
    ///     <c>ResourceId.TryParsePath</c> rejects — a generated surface disagreeing with the runtime,
    ///     which is the one failure ADR-012 exists to make impossible.
    /// </remarks>
    static string PathOf(ResourceTypeName type) =>
        "/tenants/{tenantId}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}"
        + "/providers/" + type.Namespace + "/" + type.Type + "/{resourceName}";

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
            ["parameters"] = ResourceParameters(),
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
                ["responses"] = Accepted(),
                ["x-cybercloud-permission"] = type.DeletePermission
            },
            // Registry facts with no representation in a body. They are here rather than dropped
            // because the CLI and the portal-form emitters need them and this document is what
            // docs/plan/21 § Generation generates those from — and they are extensions rather than
            // properties because none of them is a field a caller sends.
            ["x-cybercloud-resource-type"] = type.Type.ToString(),
            ["x-cybercloud-supports-tags"] = type.SupportsTags,
            ["x-cybercloud-requires-cluster"] = type.RequiresCluster,
            ["x-cybercloud-soft-delete-days"] = type.SoftDeleteDays,
            // The quota meters a write draws against. A caller who is about to be told
            // QuotaExceeded (docs/plan/08 § Errors) can see which limit before sending.
            ["x-cybercloud-meters"] = Meters(type)
        };

        return Deprecate(item, retiresOn);
    }

    static JsonArray Meters(ResourceTypeRegistration type) {
        var meters = new JsonArray();

        foreach (var meter in type.Meters
                     .OrderBy(x => x.Meter.ToString(), StringComparer.Ordinal)
                     .ThenBy(x => x.AmountPointer, StringComparer.Ordinal)) {
            meters.Add(new JsonObject {
                ["meter"] = meter.Meter.ToString(),
                // A pointer rather than a delegate is what makes this generable at all — see the
                // remarks on IResourceTypeBuilder.Meter.
                ["amountPointer"] = meter.AmountPointer,
                ["fallback"] = meter.Fallback
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

    static JsonObject ActionPathItem(
        ResourceTypeRegistration type,
        ActionRegistration action,
        DateOnly? retiresOn
    ) {
        if (action.Kind is not ActionKind.Post) {
            throw new InvalidOperationException(
                $"Action '{type.Type}/{action.Name}' declares ActionKind.{action.Kind}, and the only "
                + "kind with a defined HTTP shape is Post — docs/plan/08 § The provider registry. An "
                + "action whose invocation this emitter had to guess would be a surface the registry "
                + "did not describe."
            );
        }

        var item = new JsonObject {
            ["parameters"] = ResourceParameters(),
            ["post"] = new JsonObject {
                ["operationId"] = OperationIdOf(type.Type, Capitalise(action.Name)),
                ["summary"] = action.Name + " a " + type.Type + ".",
                ["description"] =
                    "An action never creates: a POST to a name that does not exist is a 404 — "
                    + "docs/plan/08 § The write path, end to end."
                    + (action.Secret
                        ? " ⚠ The response carries secret material. It is always audited and is never "
                        + "cached — docs/plan/08 § The provider registry."
                        : string.Empty),
                ["responses"] = new JsonObject {
                    ["200"] = new JsonObject {
                        // ⚠ An unconstrained schema, and it is a reported registry gap rather than a
                        // shrug: ActionRegistration carries a name, a kind, a permission and a secret
                        // flag, and no request or response schema. There is nothing to narrow this to
                        // without inventing it, and inventing it here is exactly how the registry
                        // stops being the single source.
                        ["description"] =
                            "The action's result. ⚠ Unconstrained: the registry declares no request or "
                            + "response schema for an action, so no generated surface can type this.",
                        ["content"] = new JsonObject {
                            ["application/json"] = new JsonObject {
                                ["schema"] = new JsonObject()
                            }
                        }
                    }
                }.WithErrors(),
                ["x-cybercloud-permission"] = action.Permission,
                ["x-cybercloud-secret"] = action.Secret
            },
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

    static JsonArray ResourceParameters() =>
        new() {
            Ref("parameters", "TenantId"),
            Ref("parameters", "SubscriptionId"),
            Ref("parameters", "ResourceGroupName"),
            Ref("parameters", "ResourceName"),
            Ref("parameters", "ApiVersion")
        };

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
    ///         <see cref="SchemaProperty.Secret" /> — <see cref="ResourceSchema.Project" /> drops
    ///         secrets on every read, so <c>writeOnly</c> is a statement of what the code does rather
    ///         than a wish. Emitting a request schema and a response schema instead would double
    ///         every component and make the compatibility diff compare four things where two would do.
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
    static JsonObject SchemaOf(ResourceTypeRegistration type, ResourceSchema schema) {
        var byParent = new Dictionary<string, List<SchemaProperty>>(StringComparer.Ordinal);

        foreach (var property in schema.Properties) {
            if (property.Kind is SchemaKind.Unknown) {
                throw new InvalidOperationException(
                    $"'{type.Type}' declares '{property.JsonPointer}' with SchemaKind.Unknown, which is "
                    + "the never-assigned member. A property with no kind cannot be validated or "
                    + "generated."
                );
            }

            if (property.Required && property.ReadOnly) {
                throw new InvalidOperationException(
                    $"'{type.Type}' declares '{property.JsonPointer}' as both required and read-only. "
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
                        $"'{type.Type}' declares '{property.JsonPointer}' but not its parent "
                        + $"'{property.ParentPointer}'. ResourceSchema.Validate refuses every undeclared "
                        + "pointer, so the parent is rejected and this property is unreachable — every "
                        + "request would fail whatever it sent."
                    );
                }

                if (parent.Value.Kind is not SchemaKind.Nested) {
                    throw new InvalidOperationException(
                        $"'{type.Type}' declares '{property.JsonPointer}' inside "
                        + $"'{property.ParentPointer}', which is a {JsonTypeOf(parent.Value.Kind)} and "
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
        root["title"] = type.Type.ToString();
        root["description"] =
            "The body of a " + type.Type + ". Generated from the provider registry's schema, which is "
            + "the same object the write path validates against — docs/plan/08 § The provider registry.";

        return Sorted(root);
    }

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
        var node = property.Kind is SchemaKind.Nested
            ? ObjectAt(property.JsonPointer, byParent, rejectsUnknown)
            : new JsonObject { ["type"] = JsonTypeOf(property.Kind) };

        if (property.Kind is SchemaKind.Array) {
            // ⚠ Unconstrained, and reported: SchemaKind's own remarks say "element shape is not
            // modelled; that is a later api-version's problem". An `items` this emitter made up would
            // be a claim the write path does not check.
            node["items"] = new JsonObject();
        }

        if (property.Description.Length > 0) {
            node["description"] = property.Description;
        }

        if (property.ReadOnly) {
            node["readOnly"] = true;
        }

        if (property.Secret) {
            // writeOnly because ResourceSchema.Project drops every secret property on a read, and
            // format=password because that is what a portal form masks on.
            node["writeOnly"] = true;
            node["format"] = "password";
            node["x-cybercloud-secret"] = true;
        }

        return Sorted(node);
    }

    static string JsonTypeOf(SchemaKind kind) =>
        kind switch {
            SchemaKind.Text => "string",
            SchemaKind.Number => "number",
            SchemaKind.WholeNumber => "integer",
            SchemaKind.Boolean => "boolean",
            SchemaKind.Nested => "object",
            SchemaKind.Array => "array",
            _ => throw new InvalidOperationException(
                $"SchemaKind.{kind} has no JSON type. Every member but Unknown maps to one — see the "
                + "remarks on SchemaKind."
            )
        };

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
    ///     The error responses every operation can produce.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The status codes are this emitter's, and that is a reported gap.</b>
    ///     <see cref="ErrorCode" /> is a closed, checked-in registry of stable identifiers and it
    ///     carries no HTTP status; docs/plan/07 § The enforcement seam fixes 404-versus-403 and
    ///     docs/plan/10 § API versioning fixes the 400, but nothing in the tree maps the other
    ///     twenty-odd codes onto a status. So this list is the plan's prose rendered once, here, and
    ///     the gateway will have to agree with it by hand until the mapping moves into
    ///     <see cref="ErrorCode" /> where the generator could read it.
    /// </remarks>
    static JsonObject Responses() =>
        new() {
            ["BadRequest"] = ErrorResponse(
                "The request is malformed: an invalid body, an unknown property, a missing or "
                + "unparseable api-version."
            ),
            ["Conflict"] = ErrorResponse(
                "The write conflicts: the name is taken, an operation is already running, the scope is "
                + "locked, or a precondition failed."
            ),
            ["Forbidden"] = ErrorResponse(
                "The caller can read the resource but may not perform this action — "
                + "docs/plan/07 § The enforcement seam."
            ),
            ["InternalServerError"] = ErrorResponse(
                "An internal error. ⚠ Never carries exception detail: the correlation id is in the "
                + "response header and the detail is in the trace — docs/plan/08 § Errors."
            ),
            ["NotFound"] = ErrorResponse(
                "The resource does not exist, or the caller may not read it. ⚠ The two are "
                + "deliberately indistinguishable — docs/plan/07 § The enforcement seam."
            ),
            ["TooManyRequests"] = ErrorResponse(
                "The caller is being rate limited — docs/plan/10 § Rate limiting."
            )
        };

    static JsonObject ErrorResponse(string description) =>
        new() {
            ["description"] = description,
            ["content"] = new JsonObject {
                ["application/json"] = new JsonObject {
                    ["schema"] = Ref("schemas", ErrorResponseSchema)
                }
            }
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

        return new JsonObject {
            [ErrorCodeSchema] = new JsonObject {
                ["type"] = "string",
                ["description"] =
                    "A stable, documented, greppable identifier. ⚠ It is part of the API contract and "
                    + "changing one is a breaking change — docs/plan/08 § Errors. The closed set is "
                    + "CyberCloud.Core.ErrorCode.All.",
                ["enum"] = codes
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
    ///     Adds the six error responses to a <c>responses</c> object, plus a <c>default</c>.
    /// </summary>
    /// <remarks>
    ///     <c>default</c> as well as the six because docs/plan/08 § Errors makes the body's shape
    ///     universal but the code-to-status mapping is not written down anywhere — so a generated SDK
    ///     gets typed errors for the statuses we are sure of and a correctly-shaped fallback for the
    ///     rest, rather than an untyped stream for one that surprised it.
    /// </remarks>
    public static JsonObject WithErrors(this JsonObject responses) {
        responses["400"] = Reference("BadRequest");
        responses["403"] = Reference("Forbidden");
        responses["404"] = Reference("NotFound");
        responses["409"] = Reference("Conflict");
        responses["429"] = Reference("TooManyRequests");
        responses["500"] = Reference("InternalServerError");
        responses["default"] = Reference("InternalServerError");
        return responses;
    }

    static JsonObject Reference(string name) =>
        new() { ["$ref"] = "#/components/responses/" + name };
}
