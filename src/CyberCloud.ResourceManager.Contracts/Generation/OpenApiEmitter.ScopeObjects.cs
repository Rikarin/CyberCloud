using CyberCloud.Core.Policy;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

// ── The third non-registry source: objects addressed ON a scope — issue #46's policy ─────────
//
// ⚠ #63'S QUESTION ASKED A FIFTH TIME, AND ANSWERED THE WAY #63 ANSWERED IT. The gateway has routed
// {scope}/providers/CyberCloud.Policy/… since #46, and until this file every generated surface was
// silent about it: the namespace is reserved, so no provider registers it and the registry these
// emitters read never saw it. docs/plan/10 records the same gap for role assignments and the resource
// graph; this is the source they wait on, built for policy first.
//
// ⚠ NOT A REGISTRY TYPE AND NOT A SCOPE, AND A THIRD DISCRIMINATOR SAYS SO. DocumentReader.TypesOf keys
// on x-cybercloud-resource-type and DocumentReader.ScopesOf on x-cybercloud-scope; a path item here
// carries neither, so both readers pass over it, and DocumentReader.ScopeObjectsOf finds it by
// x-cybercloud-scope-object. A definition is not a resource — no provisioning state, no 202, a rule no
// ResourceSchema can express — and making it read as one is the failure DocumentScope's remarks warn
// about one level up.
//
// ⚠ Which scope takes which object is PolicyAddress.AllowsScope's answer, read rather than restated,
// so the document can't declare a definition on a resource group that the router answers with a 400.
public static partial class OpenApiEmitter {
    /// <summary>
    ///     The extension a scope-object path item is recognised by: the object's Azure-shaped type,
    ///     <c>CyberCloud.Policy/policyDefinitions</c>.
    /// </summary>
    public const string ScopeObjectExtension = "x-cybercloud-scope-object";

    /// <summary>The extension naming the kind of scope the object sits on — <see cref="ScopeExtension" />'s vocabulary.</summary>
    public const string ScopeObjectScopeExtension = "x-cybercloud-scope-object-scope";

    /// <summary>
    ///     The extension a scope-object <i>collection</i> carries beside
    ///     <see cref="ScopeObjectExtension" /> — the discriminator <see cref="ScopeCollectionExtension" />
    ///     is for a scope, for the same reason.
    /// </summary>
    public const string ScopeObjectCollectionExtension = "x-cybercloud-scope-object-collection";

    /// <summary>
    ///     The extension marking a member that holds any JSON value, which a surface passes through as it
    ///     is rather than typing member by member.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A policy rule is a tree, and a tree is what the derived surfaces can't flatten.</b> The
    ///     member keeps its <c>$ref</c> to <see cref="PolicyRuleSchema" /> so the document describes the
    ///     rule exactly; the surfaces read this marker instead and type it as a JSON value — a
    ///     <c>JsonNode</c>, a Python <c>Any</c>, a Go <c>json.RawMessage</c>, a TypeScript
    ///     <c>unknown</c>, a <c>cyc</c> flag that takes JSON. Without it an untyped object reads as the
    ///     tag bag's <c>string → string</c> map, and a rule would round-trip as nothing.
    /// </remarks>
    public const string JsonValueExtension = "x-cybercloud-json";

    /// <summary>The placeholder a policy definition's own name fills.</summary>
    public const string PolicyDefinitionPlaceholder = "policyDefinitionName";

    /// <summary>The placeholder a policy assignment's own name fills.</summary>
    public const string PolicyAssignmentPlaceholder = "policyAssignmentName";

    const string PolicyDefinitionNameParameter = "PolicyDefinitionName";
    const string PolicyAssignmentNameParameter = "PolicyAssignmentName";

    /// <summary>A policy definition as the API renders it.</summary>
    /// <remarks>⚠ Public because the derived emitters name their models after it — a <c>$ref</c> is an unchecked string.</remarks>
    public const string PolicyDefinitionSchema = "Policy.Definition";

    /// <summary>The body of a <c>PUT</c> that writes a policy definition.</summary>
    public const string PolicyDefinitionContentSchema = "Policy.DefinitionContent";

    /// <summary>A policy assignment as the API renders it.</summary>
    public const string PolicyAssignmentSchema = "Policy.Assignment";

    /// <summary>The body of a <c>PUT</c> that writes a policy assignment.</summary>
    public const string PolicyAssignmentContentSchema = "Policy.AssignmentContent";

    /// <summary>One row of <c>policyStates</c>.</summary>
    public const string PolicyStateSchema = "Policy.State";

    const string PolicyRuleSchema = "Policy.Rule";
    const string PolicyConditionSchema = "Policy.Condition";
    const string PolicyModificationSchema = "Policy.Modification";

    /// <summary>One scope an object can sit on: its kind, its path and the parameters that address it.</summary>
    readonly record struct ObjectScope(ScopeKind Kind, string Name, string Display, string Path, ImmutableArray<string> Parameters);

    static readonly ImmutableArray<ObjectScope> ObjectScopes = [
        new(ScopeKind.Tenant, "tenant", "tenant", TenantPathTemplate, ["TenantId"]),
        new(ScopeKind.ManagementGroup, "managementGroup", "management group", ManagementGroupPathTemplate, ["TenantId", "ManagementGroupName"]),
        new(ScopeKind.Subscription, "subscription", "subscription", SubscriptionPathTemplate, ["TenantId", "SubscriptionId"]),
        new(ScopeKind.ResourceGroup, "resourceGroup", "resource group", ResourceGroupPathTemplate, ["TenantId", "SubscriptionId", "ResourceGroupName"])
    ];

    /// <summary>One kind of policy object, and everything the document says about it.</summary>
    /// <param name="Kind">Which object.</param>
    /// <param name="Segment">The type segment under the namespace.</param>
    /// <param name="Placeholder">The name placeholder, or empty for a collection-only object.</param>
    /// <param name="Parameter">The name parameter's component, or empty.</param>
    /// <param name="Component">The read schema.</param>
    /// <param name="Content">The write body's schema, or empty for a read-only object.</param>
    /// <param name="Display">The singular display name.</param>
    /// <param name="Plural">The plural display name.</param>
    /// <param name="Summary">One sentence.</param>
    readonly record struct PolicyObject(
        PolicyObjectKind Kind,
        string Segment,
        string Placeholder,
        string Parameter,
        string Component,
        string Content,
        string Display,
        string Plural,
        string Summary
    ) {
        public string TypeName => PolicyAddress.ProviderNamespace + "/" + Segment;

        public string Operation => SdkEmitter.Pascal(Segment);
    }

    static readonly ImmutableArray<PolicyObject> PolicyObjects = [
        new(
            PolicyObjectKind.Definition,
            PolicyAddress.DefinitionsSegment,
            PolicyDefinitionPlaceholder,
            PolicyDefinitionNameParameter,
            PolicyDefinitionSchema,
            PolicyDefinitionContentSchema,
            "Policy definition",
            "Policy definitions",
            "A deny, audit or modify rule over resource bodies. It does nothing until an assignment applies it. "
            + "docs/plan/08 § Policy."
        ),
        new(
            PolicyObjectKind.Assignment,
            PolicyAddress.AssignmentsSegment,
            PolicyAssignmentPlaceholder,
            PolicyAssignmentNameParameter,
            PolicyAssignmentSchema,
            PolicyAssignmentContentSchema,
            "Policy assignment",
            "Policy assignments",
            "A definition applied at a scope: step 5 of every write beneath it evaluates the rule. docs/plan/08 § Policy."
        ),
        new(
            PolicyObjectKind.State,
            PolicyAddress.StatesSegment,
            "",
            "",
            PolicyStateSchema,
            "",
            "Policy state",
            "Policy states",
            "The compliance an audit recorded, one row per resource and assignment beneath the scope. Read only. "
            + "docs/plan/08 § Policy."
        )
    ];

    /// <summary>
    ///     Every policy path item: an item and a collection per definition and assignment scope, and a
    ///     collection per state scope.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>No <c>202</c>, no <c>PATCH</c>, no <c>POST</c>.</b> One catalog write converges before the
    ///     call returns, so a <c>PUT</c> answers <c>201</c> or <c>200</c> and a <c>DELETE</c> <c>204</c>;
    ///     a definition is replaced whole, and the gateway answers <c>405</c> to the other verbs. The
    ///     document must not claim otherwise — <c>DispatchStage.PolicyAsync</c> is the other half.
    /// </remarks>
    static JsonObject ScopeObjectPathItems() {
        var items = new JsonObject();

        foreach (var policy in PolicyObjects) {
            foreach (var scope in ObjectScopes.Where(x => PolicyAddress.AllowsScope(policy.Kind, x.Kind))) {
                var collection = scope.Path + PolicyAddress.NamespaceSegment + policy.Segment;

                items[collection] = ScopeObjectCollectionItem(policy, scope);

                if (policy.Placeholder.Length > 0) {
                    items[collection + "/{" + policy.Placeholder + "}"] = ScopeObjectItem(policy, scope);
                }
            }
        }

        return items;
    }

    static JsonArray ScopeObjectParameters(ObjectScope scope) {
        var parameters = new JsonArray();

        foreach (var parameter in scope.Parameters) {
            parameters.Add(Ref("parameters", parameter));
        }

        return parameters;
    }

    static JsonObject ScopeObjectMarkers(PolicyObject policy, ObjectScope scope, JsonObject item) {
        item[ScopeObjectExtension] = policy.TypeName;
        item[ScopeObjectScopeExtension] = scope.Name;
        item["x-cybercloud-display"] = new JsonObject {
            ["name"] = policy.Display, ["plural"] = policy.Plural, ["summary"] = policy.Summary
        };

        return item;
    }

    static JsonObject ScopeObjectItem(PolicyObject policy, ObjectScope scope) {
        var parameters = ScopeObjectParameters(scope);
        parameters.Add(Ref("parameters", policy.Parameter));
        parameters.Add(Ref("parameters", "ApiVersion"));

        var on = " on a " + scope.Display;
        var display = policy.Display.ToLowerInvariant();
        var at = "At" + SdkEmitter.Pascal(scope.Name);

        var item = new JsonObject {
            ["parameters"] = parameters,
            ["get"] = new JsonObject {
                ["operationId"] = policy.Operation + "_Get" + at,
                ["summary"] = "Read a " + display + on + ".",
                ["description"] =
                    "Reads one " + display + ". ⚠ 404 for a caller who can't read the scope, the same answer as "
                    + "for an object that doesn't exist — docs/plan/07 § The enforcement seam.",
                ["responses"] = new JsonObject {
                    ["200"] = Json("The object.", policy.Component)
                }.WithErrors()
            },
            ["put"] = new JsonObject {
                ["operationId"] = policy.Operation + "_CreateOrUpdate" + at,
                ["summary"] = "Create or replace a " + display + on + ".",
                ["description"] =
                    "Writes the object whole. ⚠ 201 the first time and 200 after, and no operation to poll: one "
                    + "catalog write converges before the call returns. Writing needs assignRole on the scope, which "
                    + "is Owner — a contributor who could assign a deny could stop every other contributor's "
                    + "writes. docs/plan/08 § Policy.",
                ["requestBody"] = new JsonObject {
                    ["required"] = true,
                    ["content"] = new JsonObject {
                        ["application/json"] = new JsonObject { ["schema"] = Ref("schemas", policy.Content) }
                    }
                },
                ["responses"] = new JsonObject {
                    ["200"] = Json("The object was replaced, or was already this.", policy.Component),
                    ["201"] = Json("The object was created.", policy.Component)
                }.WithErrors()
            },
            ["delete"] = new JsonObject {
                ["operationId"] = policy.Operation + "_Delete" + at,
                ["summary"] = "Delete a " + display + on + ".",
                ["description"] =
                    "Deletes the object; an object already gone is a success too. "
                    + (policy.Kind == PolicyObjectKind.Definition
                        ? "⚠ 409 while an assignment still names the definition, listing them: deleting a deny out "
                        + "from under its assignments would lift an enforcement nobody lifted on purpose."
                        : "The assignment stops applying to the next write beneath it."),
                ["responses"] = new JsonObject {
                    ["204"] = new JsonObject { ["description"] = "Deleted, or already gone." }
                }.WithErrors()
            }
        };

        return ScopeObjectMarkers(policy, scope, item);
    }

    static JsonObject ScopeObjectCollectionItem(PolicyObject policy, ObjectScope scope) {
        var parameters = ScopeObjectParameters(scope);
        parameters.Add(Ref("parameters", "ApiVersion"));

        foreach (var parameter in PagingParameters()) {
            parameters.Add(parameter);
        }

        var description = policy.Kind == PolicyObjectKind.State
            ? "Lists the compliance every audit assignment recorded for the resources beneath this "
            + scope.Display
            + ", one page at a time. ⚠ A verdict is produced by a write, so a resource nobody has written "
            + "since an assignment was made has no row yet — docs/plan/08 § Policy. Stop when nextLink is absent."
            : "Lists the "
            + policy.Plural.ToLowerInvariant()
            + " on this "
            + scope.Display
            + " — directly on it, not inherited from above — one page at a time, ordered by id. Stop when "
            + "nextLink is absent.";

        var item = new JsonObject {
            ["parameters"] = parameters,
            ["get"] = new JsonObject {
                ["operationId"] = policy.Operation + "_ListAt" + SdkEmitter.Pascal(scope.Name),
                ["summary"] = "List " + policy.Plural.ToLowerInvariant() + " on a " + scope.Display + ".",
                ["description"] = description,
                ["responses"] = new JsonObject {
                    ["200"] = Json("One page.", policy.Component + ".List")
                }.WithErrors()
            },
            [ScopeObjectCollectionExtension] = true
        };

        return ScopeObjectMarkers(policy, scope, item);
    }

    static JsonObject Json(string description, string component) =>
        new() {
            ["description"] = description,
            ["content"] = new JsonObject {
                ["application/json"] = new JsonObject { ["schema"] = Ref("schemas", component) }
            }
        };

    // ── The schemas ────────────────────────────────────────────────────────────────────────────

    /// <summary>The objects, their write bodies, their pages, and the rule language.</summary>
    /// <remarks>
    ///     ⚠ <b>The member names are <see cref="PolicyBodyProperties" />' and the vocabularies
    ///     <c>CyberCloud.Core.Policy</c>'s</b> — the effects, the operators, the combinators, the
    ///     rewrite operations and the limits — so the document is the parser's closed sets and not a
    ///     copy of them.
    /// </remarks>
    static JsonObject ScopeObjectSchemas() {
        var schemas = new JsonObject {
            [PolicyDefinitionSchema] = PolicyObjectSchema(
                PolicyDefinitionSchema,
                "A policy definition, as the API renders it.",
                PolicyAddress.DefinitionTypeName,
                "The definition's own address. An assignment names its definition by this.",
                DefinitionProperties(read: true)
            ),
            [PolicyDefinitionContentSchema] = ContentSchema(
                PolicyDefinitionContentSchema,
                "The body of a PUT that writes a policy definition.",
                DefinitionProperties(read: false)
            ),
            [PolicyAssignmentSchema] = PolicyObjectSchema(
                PolicyAssignmentSchema,
                "A policy assignment, as the API renders it.",
                PolicyAddress.AssignmentTypeName,
                "The assignment's own address.",
                AssignmentProperties(read: true)
            ),
            [PolicyAssignmentContentSchema] = ContentSchema(
                PolicyAssignmentContentSchema,
                "The body of a PUT that writes a policy assignment.",
                AssignmentProperties(read: false)
            ),
            [PolicyStateSchema] = StateSchema(),
            [PolicyRuleSchema] = RuleSchema(),
            [PolicyConditionSchema] = ConditionSchema(),
            [PolicyModificationSchema] = ModificationSchema()
        };

        foreach (var policy in PolicyObjects) {
            schemas[policy.Component + ".List"] = PageSchema(policy);
        }

        return schemas;
    }

    static JsonObject PolicyObjectSchema(string title, string description, string typeName, string id, JsonObject properties) =>
        new() {
            ["type"] = "object",
            ["title"] = title,
            ["description"] = description,
            ["properties"] = new JsonObject {
                ["id"] = new JsonObject { ["type"] = "string", ["description"] = id },
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The last segment of the address." },
                ["properties"] = properties,
                ["type"] = new JsonObject {
                    ["type"] = "string",
                    ["description"] = "The Azure-shaped type string.",
                    ["enum"] = new JsonArray { typeName }
                }
            },
            ["required"] = new JsonArray { "id", "name", "properties", "type" },
            ["additionalProperties"] = false
        };

    static JsonObject ContentSchema(string title, string description, JsonObject properties) =>
        new() {
            ["type"] = "object",
            ["title"] = title,
            ["description"] = description + " ⚠ id, name and type are allowed only when they repeat the address.",
            ["properties"] = new JsonObject { ["properties"] = properties },
            ["required"] = new JsonArray { "properties" },
            ["additionalProperties"] = false
        };

    static JsonObject DisplayNameMember() =>
        new() {
            ["type"] = "string",
            ["maxLength"] = PolicyBodyProperties.MaxDisplayName,
            ["description"] = "What a person reads."
        };

    static JsonObject DefinitionProperties(bool read) {
        var properties = new JsonObject {
            [PolicyBodyProperties.Description] = new JsonObject {
                ["type"] = "string",
                ["maxLength"] = PolicyBodyProperties.MaxDescription,
                ["description"] = "The longer explanation."
            },
            [PolicyBodyProperties.DisplayName] = DisplayNameMember(),
            [PolicyBodyProperties.PolicyRule] = new JsonObject {
                ["$ref"] = "#/components/schemas/" + PolicyRuleSchema,
                ["type"] = "object",
                ["description"] =
                    "The rule — { \"if\": <condition>, \"then\": { \"effect\": \"deny\" | \"audit\" | \"modify\" } }. "
                    + "A word outside the closed sets is refused by name. docs/plan/08 § Policy.",
                [JsonValueExtension] = true
            }
        };

        return new JsonObject {
            ["type"] = "object",
            ["description"] = "The definition's rule and its names.",
            ["properties"] = properties,
            ["required"] = read
                ? new JsonArray { PolicyBodyProperties.Description, PolicyBodyProperties.DisplayName, PolicyBodyProperties.PolicyRule }
                : new JsonArray { PolicyBodyProperties.PolicyRule },
            ["additionalProperties"] = false
        };
    }

    static JsonObject AssignmentProperties(bool read) {
        var properties = new JsonObject {
            [PolicyBodyProperties.DisplayName] = DisplayNameMember(),
            [PolicyBodyProperties.NotScopes] = new JsonObject {
                ["type"] = "array",
                ["maxItems"] = PolicyBodyProperties.MaxNotScopes,
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] =
                    "Scopes or resources beneath the assignment it does not apply to, by address. Beneath a "
                    + "management group the tree decides, not the path's spelling."
            },
            [PolicyBodyProperties.PolicyDefinitionId] = new JsonObject {
                ["type"] = "string",
                ["description"] =
                    "The definition to apply, by address. ⚠ It must sit on this scope or above it — a "
                    + "subscription owner can't assign another subscription's rules."
            }
        };

        var required = new JsonArray { PolicyBodyProperties.PolicyDefinitionId };

        if (read) {
            properties[PolicyBodyProperties.Scope] = new JsonObject {
                ["type"] = "string",
                ["description"] = "The scope the assignment sits on."
            };

            required = new JsonArray {
                PolicyBodyProperties.DisplayName,
                PolicyBodyProperties.NotScopes,
                PolicyBodyProperties.PolicyDefinitionId,
                PolicyBodyProperties.Scope
            };
        }

        return new JsonObject {
            ["type"] = "object",
            ["description"] = "What the assignment applies, and where it doesn't.",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    static JsonObject StateSchema() {
        var states = new JsonArray();
        foreach (var state in Enum.GetValues<PolicyComplianceState>().Where(static x => x != PolicyComplianceState.Unknown)) {
            states.Add(state.ToString());
        }

        return new JsonObject {
            ["type"] = "object",
            ["title"] = PolicyStateSchema,
            ["description"] =
                "One resource's compliance with one audit assignment. ⚠ timestamp is when the verdict last "
                + "changed, not when it was last evaluated: re-applying the same body writes nothing.",
            ["properties"] = new JsonObject {
                ["complianceState"] = new JsonObject {
                    ["type"] = "string", ["enum"] = states, ["description"] = "Whether the audit rule matched."
                },
                ["policyAssignmentId"] = new JsonObject { ["type"] = "string", ["description"] = "The audit assignment." },
                ["policyDefinitionId"] = new JsonObject { ["type"] = "string", ["description"] = "Its definition." },
                ["resourceId"] = new JsonObject { ["type"] = "string", ["description"] = "The resource's canonical path." },
                ["resourceType"] = new JsonObject { ["type"] = "string", ["description"] = "The resource's type." },
                ["timestamp"] = new JsonObject {
                    ["type"] = "string", ["format"] = "date-time", ["description"] = "When the verdict last changed."
                }
            },
            ["required"] = new JsonArray {
                "complianceState", "policyAssignmentId", "policyDefinitionId", "resourceId", "resourceType", "timestamp"
            },
            ["additionalProperties"] = false
        };
    }

    static JsonObject PageSchema(PolicyObject policy) =>
        new() {
            ["type"] = "object",
            ["title"] = policy.Component + ".List",
            ["description"] =
                "One page of " + policy.Plural.ToLowerInvariant() + ". Stop when nextLink is absent, never when a "
                + "page is smaller than you asked for.",
            ["properties"] = new JsonObject {
                ["nextLink"] = new JsonObject {
                    ["type"] = "string", ["description"] = "The absolute URL of the next page. Absent on the last page."
                },
                ["value"] = new JsonObject {
                    ["type"] = "array",
                    ["description"] = "The objects on this page, ordered by id.",
                    ["items"] = Ref("schemas", policy.Component)
                }
            },
            ["required"] = new JsonArray { "value" },
            ["additionalProperties"] = false
        };

    static JsonArray Strings(IEnumerable<string> values) {
        var array = new JsonArray();
        foreach (var value in values) {
            array.Add(value);
        }

        return array;
    }

    static JsonObject RuleSchema() =>
        new() {
            ["type"] = "object",
            ["title"] = PolicyRuleSchema,
            ["description"] =
                "A policy rule: a condition over the request and the body it would leave, and what happens when it "
                + "holds. Modify runs first, then deny, then audit. ⚠ A rule that doesn't name the operation "
                + "applies to creates and updates only. docs/plan/08 § Policy.",
            ["properties"] = new JsonObject {
                ["if"] = Ref("schemas", PolicyConditionSchema),
                ["then"] = new JsonObject {
                    ["type"] = "object",
                    ["description"] = "What happens when the condition holds.",
                    ["properties"] = new JsonObject {
                        ["effect"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(PolicyRule.Effects) },
                        ["operations"] = new JsonObject {
                            ["type"] = "array",
                            ["maxItems"] = PolicyRule.MaxOperations,
                            ["description"] = "A modify's rewrites, applied in order. Required for modify, refused otherwise.",
                            ["items"] = Ref("schemas", PolicyModificationSchema)
                        }
                    },
                    ["required"] = new JsonArray { "effect" },
                    ["additionalProperties"] = false
                }
            },
            ["required"] = new JsonArray { "if", "then" },
            ["additionalProperties"] = false
        };

    static JsonObject ConditionSchema() =>
        new() {
            ["type"] = "object",
            ["title"] = PolicyConditionSchema,
            ["description"] =
                "Exactly one of "
                + string.Join(", ", PolicyCondition.Combinators)
                + ", or a field with exactly one of "
                + string.Join(", ", PolicyCondition.Operators)
                + ". ⚠ Bounded at parse time: "
                + PolicyCondition.MaxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " levels and "
                + PolicyCondition.MaxNodes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " nodes. Strings compare ignoring case, and an absent field equals nothing.",
            ["properties"] = new JsonObject {
                ["allOf"] = Branches("Holds when every branch holds."),
                ["anyOf"] = Branches("Holds when any branch holds."),
                ["equals"] = new JsonObject { ["description"] = "Holds when the field equals this JSON value." },
                ["exists"] = new JsonObject { ["type"] = "boolean", ["description"] = "Holds when the field is present, or absent for false." },
                ["field"] = new JsonObject {
                    ["type"] = "string",
                    ["description"] =
                        "One of " + string.Join(", ", PolicyField.Type, PolicyField.Name, PolicyField.Operation, PolicyField.Action)
                        + ", or an RFC 6901 pointer into the body such as /tags/env. operation is one of "
                        + string.Join(", ", PolicyOperations.All) + "."
                },
                ["in"] = new JsonObject {
                    ["type"] = "array",
                    ["maxItems"] = PolicyCondition.MaxValues,
                    ["description"] = "Holds when the field equals one of these values."
                },
                ["like"] = new JsonObject {
                    ["type"] = "string", ["description"] = "A glob whose one metacharacter is *, not a regular expression."
                },
                ["not"] = Ref("schemas", PolicyConditionSchema)
            },
            ["additionalProperties"] = false
        };

    static JsonObject Branches(string description) =>
        new() {
            ["type"] = "array",
            ["maxItems"] = PolicyCondition.MaxBranches,
            ["description"] = description,
            ["items"] = Ref("schemas", PolicyConditionSchema)
        };

    static JsonObject ModificationSchema() =>
        new() {
            ["type"] = "object",
            ["title"] = PolicyModificationSchema,
            ["description"] =
                "One rewrite. add writes only where the body has no value — a default the caller can override; "
                + "replace writes whatever the caller sent. ⚠ Never on a secret property: the write is refused.",
            ["properties"] = new JsonObject {
                ["field"] = new JsonObject { ["type"] = "string", ["description"] = "An RFC 6901 pointer into the body." },
                ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(PolicyModification.Names) },
                ["value"] = new JsonObject { ["description"] = "The JSON value it writes." }
            },
            ["required"] = new JsonArray { "field", "operation", "value" },
            ["additionalProperties"] = false
        };
}
