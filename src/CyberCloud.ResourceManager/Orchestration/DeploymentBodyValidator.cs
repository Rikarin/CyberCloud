using CyberCloud.ResourceManager.Contracts.Registry;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Orchestration;

/// <summary>
///     A check step 2 of the write path runs after the registry's schema, for a type whose body means
///     more than the schema vocabulary can say.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>In the implementation assembly and not in the contracts, so no provider can supply
///         one.</b> A provider's body is whatever its schema says, and the schema is what every
///         generated surface is built from; a provider-side validator would be a refusal the document
///         cannot describe. The one implementation is the manager's own, for the one type whose body
///         is a program — <c>CyberCloud.Resources/deployments</c>.
///     </para>
///     <para>
///         ⚠ <b>Step 2, which is after step 1's ownership checks and before step 3.</b> A template
///         that does not evaluate is refused with a <c>400</c> naming what is wrong, and only a caller
///         who owns the tenant and the subscription in the path ever sees one — the same ordering a
///         schema failure has, for the same reason.
///     </para>
/// </remarks>
public interface IResourceBodyValidator {
    /// <summary>The type this validator checks.</summary>
    ResourceTypeName Type { get; }

    /// <summary>Checks one body that has already passed the schema.</summary>
    /// <param name="id">The resource being written.</param>
    /// <param name="body">The body — for a <c>PATCH</c>, the patch, not the merged result.</param>
    /// <param name="verb">The verb, so a validator can tell a replacement from a merge.</param>
    /// <returns>Success, or the refusal, with <see cref="Error.Target" /> pointing into the body.</returns>
    Result Validate(ResourceId id, JsonElement body, WriteVerb verb);
}

/// <summary>
///     Evaluates a deployment's template at step 2, so a template that uses an unsupported function,
///     names a resource that is not in it, or has a cycle is a <c>400</c> at the <c>PUT</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>A <c>PATCH</c> is checked only when it replaces both the template and the parameters.</b>
///     The template and the parameters are each one string, so a merge patch that carries both decides
///     the whole of what the merged body will evaluate — and that is the only patch whose result this
///     step can know. One that carries only the template is evaluated against parameters it cannot
///     see; checking it against none refused a valid change to a template whose required parameter was
///     already stored, which is a false <c>400</c>. Reading the stored body here is not the answer:
///     step 2 runs before step 3, and a refusal built from a resource's stored parameters would
///     describe that resource to a caller who has not yet been authorized to see it. So a partial
///     patch passes here and the parent operation evaluates the merged body on its first pass, where
///     a template that does not deploy fails the operation naming why.
///     <para>
///         ⚠ <b>Every template is also read for a secret property, a partial patch's included</b> —
///         see <see cref="DeploymentSecrets" />. The structural check needs no parameters, so it runs
///         whenever the body carries a template; the plan is checked too whenever it's evaluated here.
///     </para>
/// </remarks>
/// <param name="registry">Where each template resource's schema is resolved, for its secret properties.</param>
public sealed class DeploymentBodyValidator(IProviderRegistry registry) : IResourceBodyValidator {
    /// <inheritdoc />
    public ResourceTypeName Type => Deployments.Type;

    /// <inheritdoc />
    public Result Validate(ResourceId id, JsonElement body, WriteVerb verb) {
        var properties = body.TryGetProperty("properties", out var found) && found.ValueKind == JsonValueKind.Object
            ? found
            : default;

        var carriesTemplate = properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty("template", out var template)
            && template.ValueKind == JsonValueKind.String;

        if (verb == WriteVerb.Patch
            && !(carriesTemplate && properties.TryGetProperty("parameters", out _))) {
            return carriesTemplate
                ? DeploymentSecrets.RefuseInTemplate(registry, properties.GetProperty("template").GetString()!)
                : Result.Success;
        }

        var planned = DeploymentTemplate.EvaluateBody(id, body);

        if (planned.TryGetError(out var error)) {
            return Result.Failure(error);
        }

        var literal = carriesTemplate
            ? DeploymentSecrets.RefuseInTemplate(registry, properties.GetProperty("template").GetString()!)
            : Result.Success;

        return literal.IsFailure ? literal : DeploymentSecrets.RefuseInPlan(registry, planned.GetValueOrThrow());
    }
}
