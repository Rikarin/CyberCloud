using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Orchestration;

/// <summary>One resource of an evaluated template, ready to be written.</summary>
/// <param name="Order">Its position in dependency order, from zero.</param>
/// <param name="TemplateIndex">Its position in the template's <c>resources</c> array, from zero.</param>
/// <param name="Id">Its address, with <see cref="Guid.Empty" /> for its id — a path, not an identity.</param>
/// <param name="ApiVersion">The api-version the template wrote it at.</param>
/// <param name="Body">
///     The body a <c>PUT</c> carries, as JSON text: <c>location</c>, <c>tags</c> and <c>properties</c>
///     as the template gave them, every expression evaluated.
/// </param>
/// <param name="DependsOn">The paths of the resources it waits for, in the template's order.</param>
public sealed record PlannedResource(
    int Order,
    int TemplateIndex,
    ResourceId Id,
    string ApiVersion,
    string Body,
    ImmutableArray<string> DependsOn
);

/// <summary>An evaluated template: every resource, in the order a deployment writes them.</summary>
/// <param name="Resources">The resources, dependencies first.</param>
public sealed record DeploymentPlan(ImmutableArray<PlannedResource> Resources);

/// <summary>
///     Evaluates a deployment template: parameters, variables, the closed expression set, and the
///     dependency order. docs/plan/08 § Long-running operations, "Nested operations".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The expression set is closed, and a refusal names it.</b> <c>parameters()</c>,
///         <c>variables()</c>, <c>resourceId()</c> and <c>concat()</c> — enough to name resources and
///         wire one to another, and nothing that reads the platform. ARM's other functions divide into
///         ones that would need a decision this platform has not made (<c>reference()</c> reads another
///         resource's runtime state, which is the cross-resource seam's question;
///         <c>uniqueString()</c> needs a hash whose stability is a contract) and ones that are
///         convenience. Accepting an unknown function and evaluating it to something would be worse than
///         refusing it: a template that "works" differently here than on Azure is a template nobody can
///         debug. Property and index access (<c>parameters('x').y</c>, <c>[0]</c>) are refused for the
///         same reason.
///     </para>
///     <para>
///         ⚠ <b>Pure, and run twice.</b> Once at step 2 of the deployment's own <c>PUT</c>, so a bad
///         template is a <c>400</c> at the request; once on the parent operation's first pass, from the
///         body the resource grain stored, so a re-drive after a silo loss plans exactly what the request
///         validated. Nothing here reads a grain, a clock or the registry, which is what makes the two
///         runs the same run.
///     </para>
///     <para>
///         ⚠ <b>Every resource lands in the deployment's own subscription.</b> A resource may name a
///         different resource group (<c>resourceGroup</c>, the way ARM's nested deployments scope one),
///         and nothing else about its address: the tenant and the subscription are the deployment's.
///         Crossing a subscription is a billing boundary and is refused rather than checked.
///     </para>
/// </remarks>
public static class DeploymentTemplate {
    /// <summary>The functions an expression may call, in the order a refusal lists them.</summary>
    public static ImmutableArray<string> SupportedFunctions { get; } = ["parameters", "variables", "resourceId", "concat"];

    /// <summary>The members a template may have.</summary>
    public static ImmutableArray<string> TemplateMembers { get; } =
        ["$schema", "contentVersion", "metadata", "parameters", "variables", "resources"];

    /// <summary>The members one template resource may have.</summary>
    public static ImmutableArray<string> ResourceMembers { get; } =
        ["type", "name", "apiVersion", "location", "tags", "properties", "dependsOn", "resourceGroup", "comments"];

    /// <summary>The members one parameter declaration may have.</summary>
    public static ImmutableArray<string> ParameterMembers { get; } = ["type", "defaultValue", "allowedValues", "metadata"];

    /// <summary>The parameter types, lower-case, as the template spells them.</summary>
    public static ImmutableArray<string> ParameterTypes { get; } = ["string", "int", "bool", "object", "array"];

    /// <summary>
    ///     Evaluates the template a deployment's body carries — <see cref="Deployments.TemplatePointer" />
    ///     and <see cref="Deployments.ParametersPointer" />.
    /// </summary>
    /// <param name="deployment">The deployment's address. Its tenant, subscription and group scope the resources.</param>
    /// <param name="body">The deployment's body, or a what-if's.</param>
    /// <returns>The plan, or an <see cref="ErrorCode.InvalidRequestBody" /> naming what is wrong and where.</returns>
    public static Result<DeploymentPlan> EvaluateBody(ResourceId deployment, JsonElement body) {
        if (!TryReadText(body, "template", out var template) || template.Length == 0) {
            return Refuse("The body carries no template. It is required, as JSON text.", Deployments.TemplatePointer);
        }

        TryReadText(body, "parameters", out var parameters);

        return Evaluate(deployment, template, parameters);
    }

    /// <summary>Evaluates a template and its parameter values.</summary>
    /// <param name="deployment">The deployment's address. Its tenant, subscription and group scope the resources.</param>
    /// <param name="templateJson">The template, as JSON text.</param>
    /// <param name="parametersJson">The parameter values, as JSON text, or empty for none.</param>
    /// <returns>The plan, or an <see cref="ErrorCode.InvalidRequestBody" /> naming what is wrong and where.</returns>
    public static Result<DeploymentPlan> Evaluate(ResourceId deployment, string templateJson, string parametersJson) {
        ArgumentNullException.ThrowIfNull(templateJson);

        JsonObject template;

        try {
            template = JsonNode.Parse(templateJson) as JsonObject
                ?? throw new JsonException("the template is not a JSON object");
        } catch (JsonException exception) {
            return Refuse($"The template is not a JSON object: {exception.Message}", Deployments.TemplatePointer);
        }

        JsonObject? values = null;

        if (!string.IsNullOrWhiteSpace(parametersJson)) {
            try {
                values = JsonNode.Parse(parametersJson) as JsonObject
                    ?? throw new JsonException("the parameters are not a JSON object");
            } catch (JsonException exception) {
                return Refuse(
                    $"The parameter values are not a JSON object: {exception.Message}",
                    Deployments.ParametersPointer
                );
            }

            // The parameters FILE shape — { "$schema", "contentVersion", "parameters": { … } } — is
            // what `az deployment` and `cyc` read from disk; accepting it here means a file can be
            // pasted into the body as it is.
            if ((values.ContainsKey("$schema") || values.ContainsKey("contentVersion"))
                && values["parameters"] is JsonObject inner) {
                values = inner;
            }
        }

        try {
            return Result<DeploymentPlan>.Success(new Evaluator(deployment, template, values).Plan());
        } catch (TemplateException refusal) {
            return Refuse(refusal.Message, refusal.Pointer);
        }
    }

    static bool TryReadText(JsonElement body, string member, out string text) {
        text = string.Empty;

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(member, out var value)
            || value.ValueKind != JsonValueKind.String) {
            return false;
        }

        text = value.GetString() ?? string.Empty;
        return true;
    }

    static Result<DeploymentPlan> Refuse(string message, string pointer) =>
        Result<DeploymentPlan>.Failure(ErrorCode.InvalidRequestBody, message, pointer);

    /// <summary>A refusal from inside the evaluation, carried out to the one place that makes it a result.</summary>
    sealed class TemplateException(string message, string pointer = Deployments.TemplatePointer) : Exception(message) {
        public string Pointer { get; } = pointer;
    }

    /// <summary>One evaluation, holding what the lazy parameter and variable resolution needs.</summary>
    sealed class Evaluator(ResourceId deployment, JsonObject template, JsonObject? values) {
        readonly Dictionary<string, JsonNode?> parameters = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, JsonNode?> variables = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> resolving = new(StringComparer.OrdinalIgnoreCase);
        JsonObject declaredParameters = [];
        JsonObject declaredVariables = [];

        public DeploymentPlan Plan() {
            foreach (var member in template) {
                if (!TemplateMembers.Contains(member.Key, StringComparer.Ordinal)) {
                    throw new TemplateException(
                        $"The template has a '{member.Key}' member, which is not supported. A template's members "
                        + $"are {string.Join(", ", TemplateMembers)}."
                    );
                }
            }

            declaredParameters = ObjectMember(template, "parameters", "The template's 'parameters'");
            declaredVariables = ObjectMember(template, "variables", "The template's 'variables'");

            if (declaredParameters.Count > DeploymentLimits.MaxParameters
                || declaredVariables.Count > DeploymentLimits.MaxParameters) {
                throw new TemplateException(
                    $"The template declares {declaredParameters.Count} parameter(s) and {declaredVariables.Count} "
                    + $"variable(s); each is capped at {DeploymentLimits.MaxParameters}."
                );
            }

            CheckValues();

            // Every declared parameter is resolved up front, so an unused one with a bad value is still
            // refused — a template whose parameter is wrong is wrong whether or not this run reads it.
            foreach (var declared in declaredParameters) {
                _ = Parameter(declared.Key);
            }

            foreach (var declared in declaredVariables) {
                _ = Variable(declared.Key);
            }

            if (template["resources"] is not JsonArray resources) {
                throw new TemplateException(
                    template.ContainsKey("resources")
                        ? "The template's 'resources' is not an array."
                        : "The template has no 'resources'. A deployment deploys at least one resource."
                );
            }

            if (resources.Count == 0) {
                throw new TemplateException("The template's 'resources' is empty. A deployment deploys at least one resource.");
            }

            if (resources.Count > DeploymentLimits.MaxResources) {
                throw new TemplateException(
                    $"The template declares {resources.Count} resources and a deployment is capped at "
                    + $"{DeploymentLimits.MaxResources}."
                );
            }

            var evaluated = new List<(ResourceId Id, string ApiVersion, string Body, ImmutableArray<string> DependsOn)>();

            for (var i = 0; i < resources.Count; i++) {
                evaluated.Add(Resource(resources[i], i));
            }

            return new(Order(evaluated));
        }

        // ── Parameters and variables ──────────────────────────────────────────────────────────

        void CheckValues() {
            if (values is null) {
                return;
            }

            foreach (var supplied in values) {
                if (!declaredParameters.ContainsKey(supplied.Key)) {
                    throw new TemplateException(
                        $"A value is supplied for '{supplied.Key}', which the template does not declare. Declared: "
                        + (declaredParameters.Count == 0 ? "none" : string.Join(", ", declaredParameters.Select(static x => x.Key)))
                        + ".",
                        Deployments.ParametersPointer
                    );
                }

                if (supplied.Value is not JsonObject entry || !entry.ContainsKey("value") || entry.Count != 1) {
                    throw new TemplateException(
                        $"The value for '{supplied.Key}' must be {{ \"value\": … }} and nothing else. A Key Vault "
                        + "'reference' is not supported.",
                        Deployments.ParametersPointer
                    );
                }
            }
        }

        JsonNode? Parameter(string name) {
            if (parameters.TryGetValue(name, out var known)) {
                return known?.DeepClone();
            }

            if (declaredParameters[name] is not JsonObject declaration) {
                throw new TemplateException(
                    declaredParameters.ContainsKey(name)
                        ? $"The parameter '{name}' is not declared as an object with a 'type'."
                        : $"parameters('{name}') names a parameter the template does not declare."
                );
            }

            foreach (var member in declaration) {
                if (!ParameterMembers.Contains(member.Key, StringComparer.Ordinal)) {
                    throw new TemplateException(
                        $"The parameter '{name}' has a '{member.Key}' member, which is not supported. A parameter's "
                        + $"members are {string.Join(", ", ParameterMembers)}."
                    );
                }
            }

            var type = declaration["type"] is JsonValue t && t.TryGetValue<string>(out var spelled)
                ? spelled.ToLowerInvariant()
                : "";

            if (type is "securestring" or "secureobject") {
                throw new TemplateException(
                    $"The parameter '{name}' is a {type}, which is not supported: a deployment's template and "
                    + "parameters are stored in its body, in plain text, and a secure type that is not secure is "
                    + "worse than none. Pass a secret reference the target type accepts instead."
                );
            }

            if (!ParameterTypes.Contains(type, StringComparer.Ordinal)) {
                throw new TemplateException(
                    $"The parameter '{name}' has the type '{type}'. Supported: {string.Join(", ", ParameterTypes)}."
                );
            }

            Enter("parameters", name);

            JsonNode? value;

            if (values is not null && values.TryGetPropertyValue(name, out var supplied) && supplied is JsonObject entry) {
                value = entry["value"]?.DeepClone();
            } else if (declaration.TryGetPropertyValue("defaultValue", out var fallback)) {
                value = Evaluate(fallback?.DeepClone(), 0);
            } else {
                throw new TemplateException(
                    $"The parameter '{name}' has no value and no defaultValue.",
                    Deployments.ParametersPointer
                );
            }

            Leave("parameters", name);

            if (!IsOfType(value, type)) {
                throw new TemplateException(
                    $"The parameter '{name}' is declared '{type}' and its value is {Describe(value)}.",
                    Deployments.ParametersPointer
                );
            }

            if (declaration["allowedValues"] is JsonArray allowed
                && !allowed.Any(x => JsonNode.DeepEquals(x, value))) {
                throw new TemplateException(
                    $"The parameter '{name}' is {Describe(value)}, which is not one of its allowedValues: "
                    + string.Join(", ", allowed.Select(static x => x?.ToJsonString() ?? "null"))
                    + ".",
                    Deployments.ParametersPointer
                );
            }

            parameters[name] = value;
            return value?.DeepClone();
        }

        JsonNode? Variable(string name) {
            if (variables.TryGetValue(name, out var known)) {
                return known?.DeepClone();
            }

            if (!declaredVariables.TryGetPropertyValue(name, out var declared)) {
                throw new TemplateException($"variables('{name}') names a variable the template does not declare.");
            }

            Enter("variables", name);
            var value = Evaluate(declared?.DeepClone(), 0);
            Leave("variables", name);

            variables[name] = value;
            return value?.DeepClone();
        }

        void Enter(string kind, string name) {
            if (!resolving.Add(kind + ":" + name)) {
                throw new TemplateException(
                    $"{kind}('{name}') depends on itself — through its own defaultValue or through a chain of "
                    + "variables that comes back to it."
                );
            }

            if (resolving.Count > DeploymentLimits.MaxDepth) {
                throw new TemplateException(
                    $"Resolving {kind}('{name}') reaches more than {DeploymentLimits.MaxDepth} parameters and "
                    + "variables deep."
                );
            }
        }

        void Leave(string kind, string name) => resolving.Remove(kind + ":" + name);

        // ── Resources ─────────────────────────────────────────────────────────────────────────

        (ResourceId Id, string ApiVersion, string Body, ImmutableArray<string> DependsOn) Resource(JsonNode? node, int index) {
            var where = $"Resource {index + 1}";

            if (node is not JsonObject resource) {
                throw new TemplateException($"{where} is not a JSON object.");
            }

            foreach (var member in resource) {
                if (!ResourceMembers.Contains(member.Key, StringComparer.Ordinal)) {
                    throw new TemplateException(
                        $"{where} has a '{member.Key}' member, which is not supported. A resource's members are "
                        + $"{string.Join(", ", ResourceMembers)}."
                    );
                }
            }

            var type = Text(resource, "type", where, true);
            var name = Text(resource, "name", where, true);
            var apiVersion = Text(resource, "apiVersion", where, true);
            var group = Text(resource, "resourceGroup", where, false);

            if (!ResourceTypeName.TryParse(type, out var typeName)) {
                throw new TemplateException(
                    $"{where}'s type '{type}' is not a resource type. It is spelled 'Namespace/type', for example "
                    + "'CyberCloud.Sample/widgets'."
                );
            }

            if (Deployments.Is(typeName)) {
                throw new TemplateException(
                    $"{where} is itself a deployment. A template may not deploy a deployment: nested deployments "
                    + "are not supported."
                );
            }

            var path = Path(typeName, name, group.Length > 0 ? group : deployment.ResourceGroup);
            var parsed = ResourceId.ParsePath(path);

            if (parsed.TryGetError(out var pathError)) {
                throw new TemplateException(
                    $"{where} ('{type}' named '{name}') does not make a valid address: {pathError.Message}"
                );
            }

            var body = new JsonObject();

            foreach (var member in (string[])["location", "tags", "properties"]) {
                if (resource.TryGetPropertyValue(member, out var value) && value is not null) {
                    body[member] = Evaluate(value.DeepClone(), 0);
                }
            }

            var dependsOn = ImmutableArray.CreateBuilder<string>();

            if (resource.TryGetPropertyValue("dependsOn", out var dependencies) && dependencies is not null) {
                if (dependencies is not JsonArray list) {
                    throw new TemplateException($"{where}'s 'dependsOn' is not an array.");
                }

                foreach (var dependency in list) {
                    if (Evaluate(dependency?.DeepClone(), 0) is not JsonValue v || !v.TryGetValue<string>(out var reference)) {
                        throw new TemplateException($"{where}'s 'dependsOn' holds something that is not a string.");
                    }

                    dependsOn.Add(reference);
                }
            }

            return (parsed.GetValueOrThrow(), apiVersion, body.ToJsonString(), dependsOn.ToImmutable());
        }

        string Text(JsonObject resource, string member, string where, bool required) {
            if (!resource.TryGetPropertyValue(member, out var node) || node is null) {
                return required ? throw new TemplateException($"{where} has no '{member}'.") : string.Empty;
            }

            if (Evaluate(node.DeepClone(), 0) is JsonValue value && value.TryGetValue<string>(out var text)) {
                return text;
            }

            throw new TemplateException($"{where}'s '{member}' does not evaluate to a string.");
        }

        string Path(ResourceTypeName type, string name, string group) {
            var typeSegments = type.Type.Split('/');
            var nameSegments = name.Split('/');

            if (typeSegments.Length != nameSegments.Length) {
                throw new TemplateException(
                    $"The resource '{name}' of type '{type}' has {nameSegments.Length} name segment(s) and the type "
                    + $"has {typeSegments.Length}. A child resource is named 'parent/child', one segment per level."
                );
            }

            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture, $"/tenants/{deployment.TenantId:D}");
            builder.Append(CultureInfo.InvariantCulture, $"/subscriptions/{deployment.SubscriptionId:D}");
            builder.Append("/resourceGroups/").Append(group);
            builder.Append("/providers/").Append(type.Namespace);

            for (var i = 0; i < typeSegments.Length; i++) {
                builder.Append('/').Append(typeSegments[i]).Append('/').Append(nameSegments[i]);
            }

            return builder.ToString();
        }

        // ── Ordering ──────────────────────────────────────────────────────────────────────────

        ImmutableArray<PlannedResource> Order(
            List<(ResourceId Id, string ApiVersion, string Body, ImmutableArray<string> DependsOn)> resources
        ) {
            var byPath = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < resources.Count; i++) {
                if (!byPath.TryAdd(resources[i].Id.CanonicalPath, i)) {
                    throw new TemplateException(
                        $"Resources {byPath[resources[i].Id.CanonicalPath] + 1} and {i + 1} are the same resource, "
                        + $"'{resources[i].Id.Path}'. A template names each resource once."
                    );
                }
            }

            var edges = new List<int>[resources.Count];
            var incoming = new int[resources.Count];

            for (var i = 0; i < resources.Count; i++) {
                edges[i] = [];
            }

            var dependsOnPaths = new ImmutableArray<string>[resources.Count];

            for (var i = 0; i < resources.Count; i++) {
                var paths = ImmutableArray.CreateBuilder<string>();

                foreach (var reference in resources[i].DependsOn) {
                    var target = Resolve(resources, byPath, reference, i);

                    if (!edges[target].Contains(i)) {
                        edges[target].Add(i);
                        incoming[i]++;
                        paths.Add(resources[target].Id.Path);
                    }
                }

                dependsOnPaths[i] = paths.ToImmutable();
            }

            // Kahn's algorithm, always taking the ready resource that came first in the template, so the
            // order is the template's wherever the dependencies leave a choice — a template author reads
            // the result the way they wrote it.
            var ready = new SortedSet<int>(Enumerable.Range(0, resources.Count).Where(x => incoming[x] == 0));
            var ordered = ImmutableArray.CreateBuilder<PlannedResource>(resources.Count);

            while (ready.Count > 0) {
                var next = ready.Min;
                ready.Remove(next);

                ordered.Add(
                    new(
                        ordered.Count,
                        next,
                        resources[next].Id,
                        resources[next].ApiVersion,
                        resources[next].Body,
                        dependsOnPaths[next]
                    )
                );

                foreach (var dependent in edges[next]) {
                    if (--incoming[dependent] == 0) {
                        ready.Add(dependent);
                    }
                }
            }

            if (ordered.Count < resources.Count) {
                var placed = new bool[resources.Count];

                foreach (var resource in ordered) {
                    placed[resource.TemplateIndex] = true;
                }

                throw new TemplateException(
                    "The template's dependsOn has a cycle, so no order deploys it: "
                    + Cycle(resources, edges, placed)
                    + "."
                );
            }

            return ordered.MoveToImmutable();
        }

        static int Resolve(
            List<(ResourceId Id, string ApiVersion, string Body, ImmutableArray<string> DependsOn)> resources,
            Dictionary<string, int> byPath,
            string reference,
            int from
        ) {
            // A full address, as resourceId() renders one.
            if (ResourceId.TryParsePath(reference, out var address) && byPath.TryGetValue(address.CanonicalPath, out var exact)) {
                return exact;
            }

            // 'Type/name' or a bare name, as ARM also accepts. A bare name must pick out one resource.
            var matches = Enumerable.Range(0, resources.Count)
                .Where(x => string.Equals(reference, resources[x].Id.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        reference,
                        $"{resources[x].Id.Type}/{resources[x].Id.Name}",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();

            return matches.Count switch {
                1 => matches[0],
                0 => throw new TemplateException(
                    $"Resource {from + 1} depends on '{reference}', which names no resource in this template."
                ),
                _ => throw new TemplateException(
                    $"Resource {from + 1} depends on '{reference}', which names {matches.Count} resources in this "
                    + "template. Use resourceId() to say which."
                )
            };
        }

        static string Cycle(
            List<(ResourceId Id, string ApiVersion, string Body, ImmutableArray<string> DependsOn)> resources,
            List<int>[] edges,
            bool[] placed
        ) {
            // ⚠ Walked BACKWARDS, along what each resource depends on. Every resource Kahn's algorithm
            // could not place still waits on at least one other unplaced resource — that is why it was
            // not placed — so following that wait from any of them must revisit one, and the revisit is
            // where the cycle closes. Walking forwards, along dependents, can dead-end at a resource
            // that is merely downstream of the cycle.
            var dependencies = new List<int>[resources.Count];

            for (var i = 0; i < resources.Count; i++) {
                dependencies[i] = [];
            }

            for (var from = 0; from < resources.Count; from++) {
                foreach (var dependent in edges[from]) {
                    dependencies[dependent].Add(from);
                }
            }

            var start = Array.FindIndex(placed, static x => !x);
            var walk = new List<int> { start };
            var seen = new Dictionary<int, int> { [start] = 0 };
            var current = start;

            while (true) {
                current = dependencies[current].First(x => !placed[x]);

                if (seen.TryGetValue(current, out var at)) {
                    return string.Join(" → ", walk.Skip(at).Append(current).Select(x => $"'{resources[x].Id.Name}'"))
                        + " (each depends on the next)";
                }

                seen[current] = walk.Count;
                walk.Add(current);
            }
        }

        // ── Expressions ───────────────────────────────────────────────────────────────────────

        JsonNode? Evaluate(JsonNode? node, int depth) {
            switch (node) {
                case JsonObject obj: {
                    foreach (var key in obj.Select(static x => x.Key).ToList()) {
                        obj[key] = Evaluate(obj[key]?.DeepClone(), depth);
                    }

                    return obj;
                }
                case JsonArray array: {
                    for (var i = 0; i < array.Count; i++) {
                        array[i] = Evaluate(array[i]?.DeepClone(), depth);
                    }

                    return array;
                }
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return Expression(text, depth);
                default:
                    return node;
            }
        }

        /// <summary>A string value: a literal, an escaped literal (<c>[[</c>), or a whole-string expression.</summary>
        JsonNode? Expression(string text, int depth) {
            if (text.Length < 2 || text[0] != '[' || text[^1] != ']') {
                return JsonValue.Create(text);
            }

            // ARM's escape: a string that starts '[[' is the literal with one bracket removed.
            if (text[1] == '[') {
                return JsonValue.Create(text[1..]);
            }

            var parser = new Parser(text[1..^1], text);
            var call = parser.ParseWhole();
            return Call(call, depth);
        }

        JsonNode? Call(Node node, int depth) {
            if (depth > DeploymentLimits.MaxDepth) {
                throw new TemplateException($"An expression nests more than {DeploymentLimits.MaxDepth} calls deep.");
            }

            if (node is Literal literal) {
                return literal.Value?.DeepClone();
            }

            var function = (Function)node;
            var arguments = function.Arguments.Select(x => Call(x, depth + 1)).ToList();

            switch (function.Name.ToLowerInvariant()) {
                case "parameters":
                    return Parameter(SingleText(function, arguments));
                case "variables":
                    return Variable(SingleText(function, arguments));
                case "concat":
                    return Concat(arguments);
                case "resourceid":
                    return JsonValue.Create(ResourceIdOf(arguments));
                default:
                    throw new TemplateException(
                        $"The template calls '{function.Name}()', which is not supported. The supported functions "
                        + $"are {string.Join(", ", SupportedFunctions.Select(static x => x + "()"))}."
                    );
            }
        }

        static string SingleText(Function function, List<JsonNode?> arguments) {
            if (arguments.Count != 1 || arguments[0] is not JsonValue v || !v.TryGetValue<string>(out var name)) {
                throw new TemplateException($"{function.Name}() takes one string: the name.");
            }

            return name;
        }

        static JsonNode Concat(List<JsonNode?> arguments) {
            if (arguments.Count == 0) {
                throw new TemplateException("concat() takes at least one argument.");
            }

            if (arguments.All(static x => x is JsonArray)) {
                var joined = new JsonArray();

                foreach (var item in arguments.Cast<JsonArray>().SelectMany(static x => x)) {
                    joined.Add(item?.DeepClone());
                }

                return joined;
            }

            var text = new StringBuilder();

            foreach (var argument in arguments) {
                if (argument is JsonValue v && v.TryGetValue<string>(out var s)) {
                    text.Append(s);
                } else if (argument is JsonValue n && n.TryGetValue<long>(out var number)) {
                    text.Append(number.ToString(CultureInfo.InvariantCulture));
                } else {
                    throw new TemplateException(
                        "concat() joins strings (and whole numbers, written as text) or joins arrays, and not a "
                        + $"mixture; it was given {Describe(argument)}."
                    );
                }
            }

            return JsonValue.Create(text.ToString());
        }

        /// <summary>
        ///     <c>resourceId([subscriptionId,] [resourceGroup,] type, name…)</c> — the full address, in this
        ///     platform's shape.
        /// </summary>
        string ResourceIdOf(List<JsonNode?> arguments) {
            var texts = arguments
                .Select(static x => x is JsonValue v && v.TryGetValue<string>(out var s)
                    ? s
                    : throw new TemplateException("resourceId() takes strings."))
                .ToList();

            var typeAt = texts.FindIndex(static x => x.Contains('/', StringComparison.Ordinal) && x.Contains('.', StringComparison.Ordinal));

            if (typeAt < 0 || !ResourceTypeName.TryParse(texts[typeAt], out var type)) {
                throw new TemplateException(
                    "resourceId() needs a resource type, for example resourceId('CyberCloud.Sample/widgets', 'name')."
                );
            }

            var names = texts.Skip(typeAt + 1).ToList();
            var group = deployment.ResourceGroup;

            switch (typeAt) {
                case 0:
                    break;
                case 1:
                    group = texts[0];
                    break;
                case 2:
                    if (!Guid.TryParse(texts[0], out var subscription) || subscription != deployment.SubscriptionId) {
                        throw new TemplateException(
                            $"resourceId() names the subscription '{texts[0]}'. A deployment deploys into its own "
                            + "subscription only."
                        );
                    }

                    group = texts[1];
                    break;
                default:
                    throw new TemplateException("resourceId() takes at most a subscription and a resource group before the type.");
            }

            return Path(type, string.Join('/', names), group);
        }

        static bool IsOfType(JsonNode? value, string type) =>
            type switch {
                "string" => value is JsonValue v && v.TryGetValue<string>(out _),
                "int" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out _),
                "bool" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
                "object" => value is JsonObject,
                "array" => value is JsonArray,
                _ => false
            };

        static string Describe(JsonNode? value) =>
            value switch {
                null => "null",
                JsonObject => "an object",
                JsonArray => "an array",
                JsonValue v => v.GetValueKind() switch {
                    JsonValueKind.String => $"the string {v.ToJsonString()}",
                    JsonValueKind.Number => $"the number {v.ToJsonString()}",
                    JsonValueKind.True or JsonValueKind.False => $"the boolean {v.ToJsonString()}",
                    _ => v.ToJsonString()
                },
                _ => value.ToJsonString()
            };

        static JsonObject ObjectMember(JsonObject owner, string member, string what) =>
            !owner.TryGetPropertyValue(member, out var node) || node is null
                ? []
                : node as JsonObject ?? throw new TemplateException($"{what} is not a JSON object.");
    }

    // ── The expression grammar ───────────────────────────────────────────────────────────────────
    //
    // expression := call | 'string' | integer
    // call       := identifier '(' [ expression { ',' expression } ] ')'
    //
    // ⚠ Nothing after a call. ARM allows `.property` and `[index]` on a call's result; this platform
    // refuses both, with a message saying so, rather than parsing them and doing something plausible.

    abstract record Node;

    sealed record Literal(JsonNode? Value) : Node;

    sealed record Function(string Name, ImmutableArray<Node> Arguments) : Node;

    sealed class Parser(string text, string whole) {
        int position;

        public Node ParseWhole() {
            var node = ParseExpression(0);
            SkipSpace();

            if (position < text.Length) {
                throw new TemplateException(
                    text[position] is '.' or '['
                        ? $"The expression '{whole}' reads a property or an index of a result, which is not supported. "
                        + $"Only whole calls to {string.Join(", ", SupportedFunctions.Select(static x => x + "()"))} are."
                        : $"The expression '{whole}' has '{text[position..]}' left over after it ends."
                );
            }

            return node;
        }

        Node ParseExpression(int depth) {
            if (depth > DeploymentLimits.MaxDepth) {
                throw new TemplateException($"The expression '{whole}' nests more than {DeploymentLimits.MaxDepth} calls deep.");
            }

            SkipSpace();

            if (position >= text.Length) {
                throw new TemplateException($"The expression '{whole}' ends where a value was expected.");
            }

            var c = text[position];

            if (c == '\'') {
                return new Literal(JsonValue.Create(ParseString()));
            }

            if (char.IsAsciiDigit(c) || c == '-') {
                var start = position++;

                while (position < text.Length && char.IsAsciiDigit(text[position])) {
                    position++;
                }

                if (!long.TryParse(text[start..position], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) {
                    throw new TemplateException($"The expression '{whole}' has a number that is not a whole number.");
                }

                return new Literal(JsonValue.Create(number));
            }

            if (!char.IsAsciiLetter(c)) {
                throw new TemplateException($"The expression '{whole}' has '{c}' where a value was expected.");
            }

            var nameStart = position;

            while (position < text.Length && char.IsAsciiLetterOrDigit(text[position])) {
                position++;
            }

            var name = text[nameStart..position];
            SkipSpace();

            if (position >= text.Length || text[position] != '(') {
                throw new TemplateException(
                    $"The expression '{whole}' uses '{name}' as a value. Only strings in single quotes, whole numbers and "
                    + $"calls to {string.Join(", ", SupportedFunctions.Select(static x => x + "()"))} are values."
                );
            }

            // ⚠ Refused where it is read, outermost first, so 'uniqueString(resourceGroup().id)' is
            // refused for uniqueString rather than for whatever inside it the grammar trips on next.
            if (!SupportedFunctions.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                throw new TemplateException(
                    $"The template calls '{name}()', which is not supported. The supported functions are "
                    + $"{string.Join(", ", SupportedFunctions.Select(static x => x + "()"))}."
                );
            }

            position++;
            var arguments = ImmutableArray.CreateBuilder<Node>();
            SkipSpace();

            if (position < text.Length && text[position] == ')') {
                position++;
                return new Function(name, arguments.ToImmutable());
            }

            while (true) {
                arguments.Add(ParseExpression(depth + 1));
                SkipSpace();

                if (position >= text.Length) {
                    throw new TemplateException($"The expression '{whole}' is missing a ')'.");
                }

                if (text[position] == ',') {
                    position++;
                    continue;
                }

                if (text[position] == ')') {
                    position++;
                    return new Function(name, arguments.ToImmutable());
                }

                if (text[position] is '.' or '[') {
                    throw new TemplateException(
                        $"The expression '{whole}' reads a property or an index of a result, which is not supported. "
                        + $"Only whole calls to {string.Join(", ", SupportedFunctions.Select(static x => x + "()"))} are."
                    );
                }

                throw new TemplateException($"The expression '{whole}' has '{text[position]}' where ',' or ')' was expected.");
            }
        }

        string ParseString() {
            position++;
            var value = new StringBuilder();

            while (position < text.Length) {
                var c = text[position++];

                if (c != '\'') {
                    value.Append(c);
                    continue;
                }

                // '' inside a string is one quote.
                if (position < text.Length && text[position] == '\'') {
                    value.Append('\'');
                    position++;
                    continue;
                }

                return value.ToString();
            }

            throw new TemplateException($"The expression '{whole}' has a string with no closing quote.");
        }

        void SkipSpace() {
            while (position < text.Length && char.IsWhiteSpace(text[position])) {
                position++;
            }
        }
    }
}
