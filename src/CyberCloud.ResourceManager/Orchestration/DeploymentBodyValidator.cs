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
///     ⚠ <b>A <c>PATCH</c> that carries no template is not checked here</b>: a merge patch omits what
///     it does not change, and the template it would leave in place is the one that already passed
///     this check. A <c>PATCH</c> that does carry one is evaluated with the parameters it carries, which
///     is stricter than the merged result for the case where it changes the template and not the
///     parameters — the parent operation evaluates the merged body again on its first pass, so nothing
///     is missed, only reported later.
/// </remarks>
public sealed class DeploymentBodyValidator : IResourceBodyValidator {
    /// <inheritdoc />
    public ResourceTypeName Type => Deployments.Type;

    /// <inheritdoc />
    public Result Validate(ResourceId id, JsonElement body, WriteVerb verb) {
        if (verb == WriteVerb.Patch
            && !(body.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty("template", out _))) {
            return Result.Success;
        }

        var planned = DeploymentTemplate.EvaluateBody(id, body);

        return planned.TryGetError(out var error) ? Result.Failure(error) : Result.Success;
    }
}
