using CyberCloud.ResourceManager.Contracts.Registry;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Orchestration;

/// <summary>
///     Refuses a deployment that would set a child's <see cref="SchemaProperty.Secret" /> property.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A deployment would otherwise hand the secret back.</b> The read filter withholds a
///         <c>Secret</c> property from the child's own <c>GET</c>, but the value a template sets, or the
///         plain string parameter feeding it, is stored in the deployment's <c>template</c> and
///         <c>parameters</c> strings — which aren't secret, because they're the deployment's body. So
///         anyone who could read the deployment read the child's secret back. Only the
///         <c>securestring</c> and <c>secureobject</c> parameter types were refused, and those are one
///         spelling of the intent, not the only way to express it.
///     </para>
///     <para>
///         ⚠ <b>Three checks, because each sees what the others can't.</b>
///         <see cref="RefuseInTemplate" /> reads the raw template's resources <i>structurally</i>: a
///         secret key is refused whatever its value, so it needs no parameters and runs on a partial
///         <c>PATCH</c> too — but it can't resolve a resource whose type or api-version is an
///         expression. <see cref="RefuseInPlan" /> checks the evaluated plan, which resolves those, on
///         every body step 2 can evaluate (a <c>PUT</c>, and a <c>PATCH</c> carrying both halves).
///         What's left is a partial <c>PATCH</c> whose template names such a resource by expression:
///         it's stored, and <see cref="IResourceManager.WriteChildAsync" /> refuses the child with
///         <see cref="FirstSetIn(ResourceSchema, string)" />, so the secret never reaches a resource.
///         Keeping it out of the deployment's body in that one case would take the stored parameters
///         at step 2, which runs before step 3 has authorized the caller to read them.
///     </para>
/// </remarks>
static class DeploymentSecrets {
    /// <summary>Finds the first secret property set in a resource's body.</summary>
    /// <param name="schema">The child type's schema at the api-version being written.</param>
    /// <param name="resource">The resource's body, or a template's resource object — the same shape.</param>
    /// <returns>The property's pointer, or <see langword="null" /> when the body sets none.</returns>
    public static string? FirstSetIn(ResourceSchema schema, JsonElement resource) {
        foreach (var property in schema.Properties) {
            if (property.Secret && IsSet(resource, property.JsonPointer)) {
                return property.JsonPointer;
            }
        }

        return null;
    }

    /// <summary>Finds the first secret property set in a body carried as JSON text.</summary>
    /// <param name="schema">The child type's schema at the api-version being written.</param>
    /// <param name="body">The body. Text that isn't JSON sets nothing here; step 2 refuses it in its own words.</param>
    /// <returns>The property's pointer, or <see langword="null" />.</returns>
    public static string? FirstSetIn(ResourceSchema schema, string body) {
        try {
            using var parsed = JsonDocument.Parse(body);
            return FirstSetIn(schema, parsed.RootElement);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    ///     Refuses a template that sets a secret property on any resource whose type and api-version
    ///     it names literally.
    /// </summary>
    /// <param name="registry">The registry the children's schemas are resolved from.</param>
    /// <param name="templateJson">The template, as JSON text.</param>
    /// <returns>
    ///     Success, including for a template that isn't well-formed, which the evaluator refuses in its
    ///     own words; or <see cref="ErrorCode.InvalidRequestBody" /> naming the resource and the property.
    /// </returns>
    public static Result RefuseInTemplate(IProviderRegistry registry, string templateJson) {
        ArgumentNullException.ThrowIfNull(registry);

        JsonDocument template;

        try {
            template = JsonDocument.Parse(templateJson);
        } catch (JsonException) {
            return Result.Success;
        }

        using (template) {
            if (template.RootElement.ValueKind != JsonValueKind.Object
                || !template.RootElement.TryGetProperty("resources", out var resources)
                || resources.ValueKind != JsonValueKind.Array) {
                return Result.Success;
            }

            var index = 0;

            foreach (var resource in resources.EnumerateArray()) {
                if (Literal(resource, "type") is { } typeName
                    && Literal(resource, "apiVersion") is { } apiVersion
                    && ResourceTypeName.TryParse(typeName, out var type)
                    && registry.Resolve(type, apiVersion).TryGetValue(out var resolution)
                    && FirstSetIn(resolution.Schema, resource) is { } pointer) {
                    return Refused(typeName, pointer, ResourcePointer(index));
                }

                index++;
            }
        }

        return Result.Success;
    }

    /// <summary>Refuses an evaluated plan any of whose resources sets a secret property.</summary>
    /// <param name="registry">The registry the children's schemas are resolved from.</param>
    /// <param name="plan">The plan step 2 evaluated.</param>
    /// <returns>Success, or <see cref="ErrorCode.InvalidRequestBody" /> pointing at the template's resource.</returns>
    public static Result RefuseInPlan(IProviderRegistry registry, DeploymentPlan plan) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var resource in plan.Resources) {
            if (registry.Resolve(resource.Id.Type, resource.ApiVersion).TryGetValue(out var resolution)
                && FirstSetIn(resolution.Schema, resource.Body) is { } pointer) {
                return Refused(resource.Id.Type.ToString(), pointer, ResourcePointer(resource.TemplateIndex));
            }
        }

        return Result.Success;
    }

    /// <summary>The refusal every check gives.</summary>
    /// <param name="type">The child's type.</param>
    /// <param name="pointer">The secret property's pointer in the child's body.</param>
    /// <param name="target">Where the refusal points: into the template, or at the child.</param>
    public static Result Refused(string type, string pointer, string target) =>
        Result.Failure(
            ErrorCode.InvalidRequestBody,
            $"'{pointer}' on {type} is a secret property, and a deployment may not set it: the template "
            + "and parameters that carry its value are stored on the deployment, and anyone who can read "
            + "the deployment would read them back. Set it on the resource itself.",
            target
        );

    static string ResourcePointer(int index) => $"{Deployments.TemplatePointer}/resources/{index}";

    /// <summary>A member's string value when it's a literal rather than a template expression.</summary>
    static string? Literal(JsonElement resource, string name) =>
        resource.ValueKind == JsonValueKind.Object
        && resource.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
        && text[0] != '['
            ? text
            : null;

    /// <summary>Whether a JSON pointer names a member present in the element, with any value but null.</summary>
    static bool IsSet(JsonElement element, string pointer) {
        var current = element;

        foreach (var raw in pointer.Split('/').Skip(1)) {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) {
                return false;
            }
        }

        return current.ValueKind != JsonValueKind.Null;
    }
}
