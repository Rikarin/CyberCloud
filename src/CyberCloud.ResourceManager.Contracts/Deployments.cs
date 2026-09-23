using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     <c>CyberCloud.Resources/deployments</c> — a template of resources, deployed at a resource group
///     as one parent operation with a child operation per resource. docs/plan/08 § Long-running
///     operations, "Nested operations".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An ordinary resource whose operation is the parent, and not a provider.</b> A
///         deployment is created by a <c>PUT</c> through all twelve steps of the write path — its own
///         check at the group, its own lock, index claim, membership and parent edge — so it is
///         listed, read, locked and deleted the way every resource is. What is not ordinary is its
///         operation: <c>OperationGrain</c> drives it through <c>DeploymentDriver</c> rather than
///         through a reconciler, because a reconciler acts as the platform and has no caller to put
///         in step 3, and a deployment's whole work is <c>PUT</c>s made <i>as</i> its creator.
///     </para>
///     <para>
///         ⚠ <b>The parent is the operation grain, and docs/plan/08 asked for an
///         <c>IDeploymentGrain</c>.</b> The grain it described — durable, holding the template, the
///         caller and a step cursor, re-registering its reminder after a silo loss, driving children
///         to a terminal state — is <c>IOperationGrain</c> with a child list, which it already had a
///         slot for (<see cref="OperationStatus.Children" />, <see cref="OperationSpec.ParentOperationId" />).
///         The template is the resource's desired body, which <see cref="OperationSpec.Desired" />
///         persists; the caller is <see cref="OperationSpec.Caller" />; the cursor is the operation's
///         own state. A second grain would have been a second resume path, a second reminder, a
///         second cancel flag and a second id to poll, each able to disagree with the first.
///     </para>
///     <para>
///         ⚠ <b>The body is text where the vocabulary cannot say more.</b>
///         <see cref="SchemaKind" /> refuses an array of objects and has no free-form object, so a
///         template — whose <c>resources</c> is exactly an array of objects of any shape — is a
///         JSON document carried in a string (<see cref="TemplatePointer" />), and so are the
///         parameter values. What the registry cannot check, <c>DeploymentTemplate</c> does at step 2
///         of the write path, so a template that uses an unsupported function or has a cycle is a
///         <c>400</c> at the <c>PUT</c> rather than a failed operation afterwards.
///     </para>
///     <para>
///         ⚠ <b>The history is the body.</b> The read-only properties are written by the parent
///         operation when it ends — which resources were created or updated, one line per step, the
///         failure naming the child that stopped it, and the rollback record. The collection of
///         deployments in a group is the group's deployment history, as it is Azure's.
///     </para>
/// </remarks>
public static class Deployments {
    /// <summary>The provider namespace — Azure's <c>Microsoft.Resources</c>, where deployments live.</summary>
    public const string ProviderNamespace = "CyberCloud.Resources";

    /// <summary>The type path within the namespace.</summary>
    public const string TypePath = "deployments";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The dry run — <c>POST …/deployments/{name}/whatIf</c>.</summary>
    public const string WhatIfAction = "whatIf";

    /// <summary>
    ///     The name of the entry point the gateway routes <see cref="WhatIfAction" /> to — see
    ///     <see cref="ActionRegistration.EntryPoint" />.
    /// </summary>
    public const string WhatIfEntryPoint = nameof(IDeploymentManager);

    /// <summary>The template, as JSON text.</summary>
    public const string TemplatePointer = "/properties/template";

    /// <summary>The parameter values, as JSON text.</summary>
    public const string ParametersPointer = "/properties/parameters";

    /// <summary>Read-only: every resource the last run created or updated, in the order it did.</summary>
    public const string OutputResourcesPointer = "/properties/outputResources";

    /// <summary>Read-only: one line per template resource, in dependency order.</summary>
    public const string StepsPointer = "/properties/steps";

    /// <summary>Read-only: why the last run failed, naming the resource that stopped it.</summary>
    public const string ErrorPointer = "/properties/error";

    /// <summary>Read-only: what a rollback would have to remove, and that it was not performed.</summary>
    public const string RollbackPointer = "/properties/rollback";

    /// <summary>The type, as the registry and a path spell it.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>Whether <paramref name="type" /> is <c>CyberCloud.Resources/deployments</c>.</summary>
    /// <param name="type">The type a path or a spec names.</param>
    /// <returns><c>true</c> when it is the deployment type, compared as the registry compares types.</returns>
    public static bool Is(ResourceTypeName type) => type.Equals(Type);

    /// <summary>Whether a resource path addresses a deployment.</summary>
    /// <param name="path">A resource path, as <see cref="OperationSpec.ResourcePath" /> records one.</param>
    /// <returns><c>true</c> when the path parses and names the deployment type.</returns>
    public static bool IsPath(string path) => ResourceId.TryParsePath(path, out var id) && Is(id.Type);

    /// <summary>The body at <see cref="V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new("/properties", SchemaKind.Nested, Description: "The template, its parameters, and the record of the last run."),
                new(
                    TemplatePointer,
                    SchemaKind.Text,
                    true,
                    Description: "The template, as JSON text: parameters, variables and resources, each with type, "
                    + "name, apiVersion, properties and dependsOn. Expressions are parameters(), variables(), "
                    + "resourceId() and concat(); anything else is refused with that list."
                ) { MaxLength = DeploymentLimits.MaxTemplateLength },
                new(
                    ParametersPointer,
                    SchemaKind.Text,
                    Description: "The parameter values, as JSON text: { \"name\": { \"value\": … } }."
                ) { MaxLength = DeploymentLimits.MaxParametersLength },
                new(
                    OutputResourcesPointer,
                    SchemaKind.Array,
                    ReadOnly: true,
                    Description: "Every resource the last run created or updated, in the order it did."
                ) { ElementKind = SchemaKind.Text },
                new(
                    StepsPointer,
                    SchemaKind.Array,
                    ReadOnly: true,
                    Description: "One line per template resource, in dependency order: its state, its id and the "
                    + "operation that drove it."
                ) { ElementKind = SchemaKind.Text },
                new(
                    ErrorPointer,
                    SchemaKind.Text,
                    ReadOnly: true,
                    Description: "Why the last run failed, naming the resource that stopped it. Empty when it did not."
                ),
                new(
                    RollbackPointer,
                    SchemaKind.Text,
                    ReadOnly: true,
                    Description: "What a rollback would remove. Rollback is recorded and never performed."
                )
            ]
        );

    /// <summary>The <see cref="WhatIfAction" /> body — the template and its parameters, as for a <c>PUT</c>.</summary>
    public static ResourceSchema WhatIfRequest { get; } =
        ResourceSchema.Of(
            [
                new("/properties", SchemaKind.Nested, true, Description: "The template to evaluate and its parameters."),
                new(
                    TemplatePointer,
                    SchemaKind.Text,
                    true,
                    Description: "The template, as JSON text — the same shape a PUT takes."
                ) { MaxLength = DeploymentLimits.MaxTemplateLength },
                new(
                    ParametersPointer,
                    SchemaKind.Text,
                    Description: "The parameter values, as JSON text."
                ) { MaxLength = DeploymentLimits.MaxParametersLength }
            ]
        );

    /// <summary>
    ///     Declares the type. Called by <c>CyberCloud.Providers.Resources</c>' provider, and by a test
    ///     that needs the real declaration rather than a copy of it.
    /// </summary>
    /// <param name="builder">The provider's builder.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No reconciler, no meter, no cluster.</b> Its operation is driven by
    ///         <c>DeploymentDriver</c> (see the class remarks), it renders nothing, and the resources it
    ///         deploys each reserve their own quota through their own write path — a meter here would
    ///         bill the history record.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="WhatIfAction" /> is served by an entry point, not a handler</b>, and it
    ///         needs <c>write</c> rather than <c>read</c>: Azure's <c>deployments/whatIf/action</c>
    ///         sits in Contributor, and a dry run is how a deployment is prepared, not how a resource
    ///         is read. Every resource it compares is read with the caller's own subject.
    ///     </para>
    /// </remarks>
    public static void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(TypePath)
            .ApiVersion(V2026, Schema2026)
            .Permissions("read", "write", "delete")
            .Action(WhatIfAction, ActionKind.Post, "write", request: WhatIfRequest, entryPoint: WhatIfEntryPoint)
            .Display(
                "Deployment",
                "Deployments",
                "deployment",
                "A template of resources deployed in dependency order, each through the write path as its creator."
            );
    }
}

/// <summary>The bounds a template is held to.</summary>
/// <remarks>
///     ⚠ <b>Bounds rather than hopes.</b> A template is evaluated at step 2 of the write path, inside a
///     request, and again on the parent operation's first pass; an unbounded one is a request that
///     can hold a gateway thread for as long as its author likes. The numbers are Azure's where Azure
///     has one — 800 resources, 256 parameters, 4 MB — scaled down to what one resource group
///     deployed in sequence can finish inside a sensible window.
/// </remarks>
public static class DeploymentLimits {
    /// <summary>The largest template, in characters.</summary>
    public const int MaxTemplateLength = 1_048_576;

    /// <summary>The largest parameter document, in characters.</summary>
    public const int MaxParametersLength = 262_144;

    /// <summary>The most resources one template may declare.</summary>
    public const int MaxResources = 100;

    /// <summary>The most parameters, and separately the most variables, one template may declare.</summary>
    public const int MaxParameters = 256;

    /// <summary>How deeply one expression may nest calls, and one variable may reference others.</summary>
    public const int MaxDepth = 32;
}

/// <summary>
///     The entry point for what a deployment does that the write path cannot: the dry run.
///     docs/plan/08 § Long-running operations.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Beside <see cref="IResourceManager" />, like <see cref="IScopeManager" /> and
///         <see cref="IRoleAssignmentManager" />, and for their reason.</b> A what-if answers for a
///         deployment that may not exist yet — Azure's does — and <see cref="IResourceManager.ActionAsync" />
///         refuses an action on an absent resource by design, because <c>POST</c> never creates. It
///         also reads every resource the template names <i>as the caller</i>, which is the thing an
///         <c>IResourceActionHandler</c> is built to be unable to do. So the gateway routes the action
///         here, and the registry records that it does (<see cref="ActionRegistration.EntryPoint" />).
///     </para>
///     <para>
///         ⚠ <b>The deployment's own <c>PUT</c> is not here.</b> It is an ordinary write through
///         <see cref="IResourceManager.WriteAsync" />, with the template checked at step 2. Routing it
///         here as well would have meant a second copy of step 1's ownership checks in front of the
///         first, for no property the write path does not already have.
///     </para>
/// </remarks>
public interface IDeploymentManager {
    /// <summary>
    ///     Evaluates a template against the resources it names, as the caller sees them, and says what
    ///     a deployment of it would create, modify or leave alone. Writes nothing.
    /// </summary>
    /// <param name="request">
    ///     The request, as the gateway parsed it — <see cref="WriteRequest.Path" /> is the deployment's
    ///     address and <see cref="WriteRequest.Body" /> is the <see cref="Deployments.WhatIfRequest" />
    ///     body.
    /// </param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>
    ///     The changes, one per template resource in the order a deployment would make them; or the
    ///     canonical <c>404</c> for a scope the caller may not see, <c>403</c> for one they may read
    ///     and not deploy to, and <c>400</c> for a template <c>DeploymentTemplate</c> refuses.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>A resource the caller cannot read is reported as a create</b>, because that is the
    ///     only answer that says nothing about it: <see cref="IResourceManager.ReadAsync" /> answers
    ///     the same <c>404</c> for "absent" and "hidden", and a what-if that distinguished them would
    ///     be an existence oracle one template wide. The real deployment will be refused at that
    ///     resource's step 3 if the caller cannot write it, which is the answer that matters.
    /// </remarks>
    Task<Result<DeploymentWhatIf>> WhatIfAsync(WriteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The spellings of a what-if change and a property change.</summary>
public static class WhatIfChangeTypes {
    /// <summary>The resource does not exist (as far as the caller can see) and would be created.</summary>
    public const string Create = "Create";

    /// <summary>The resource exists and at least one property would change.</summary>
    public const string Modify = "Modify";

    /// <summary>The resource exists and the template would leave every property as it is.</summary>
    public const string NoChange = "NoChange";

    /// <summary>A property the resource does not have would be set.</summary>
    public const string PropertyCreate = "Create";

    /// <summary>A property would change value.</summary>
    public const string PropertyModify = "Modify";

    /// <summary>
    ///     A property the resource has would be removed — a <c>PUT</c> is a full replacement, so a
    ///     property the template omits is one the write takes away.
    /// </summary>
    public const string PropertyDelete = "Delete";
}

/// <summary>One property a deployment would change.</summary>
/// <param name="Path">The property's JSON Pointer within the resource body.</param>
/// <param name="ChangeType">One of the <c>Property*</c> members of <see cref="WhatIfChangeTypes" />.</param>
/// <param name="Before">The current value, as JSON text, or <see langword="null" /> for a create.</param>
/// <param name="After">The value the template would write, as JSON text, or <see langword="null" /> for a delete.</param>
public sealed record WhatIfPropertyChange(string Path, string ChangeType, string? Before, string? After);

/// <summary>What a deployment would do to one resource.</summary>
/// <param name="ResourceId">The resource's path.</param>
/// <param name="ResourceType">The resource's type.</param>
/// <param name="ChangeType">One of <see cref="WhatIfChangeTypes.Create" />, <c>Modify</c> or <c>NoChange</c>.</param>
/// <param name="Delta">The property changes; empty for <c>NoChange</c>.</param>
public sealed record WhatIfChange(
    string ResourceId,
    string ResourceType,
    string ChangeType,
    ImmutableArray<WhatIfPropertyChange> Delta
);

/// <summary>A what-if's answer — one change per template resource, in deployment order.</summary>
/// <param name="Changes">The changes.</param>
public sealed record DeploymentWhatIf(ImmutableArray<WhatIfChange> Changes) {
    /// <summary>
    ///     The response body — <c>{ "status": "Succeeded", "changes": [ … ] }</c>, Azure's shape with
    ///     this platform's resource ids.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("status", "Succeeded");
            writer.WriteStartArray("changes");

            foreach (var change in Changes) {
                writer.WriteStartObject();
                writer.WriteString("resourceId", change.ResourceId);
                writer.WriteString("resourceType", change.ResourceType);
                writer.WriteString("changeType", change.ChangeType);
                writer.WriteStartArray("delta");

                foreach (var property in change.Delta) {
                    writer.WriteStartObject();
                    writer.WriteString("path", property.Path);
                    writer.WriteString("propertyChangeType", property.ChangeType);
                    WriteValue(writer, "before", property.Before);
                    WriteValue(writer, "after", property.After);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteValue(Utf8JsonWriter writer, string name, string? json) {
        if (json is null) {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);

        using var document = JsonDocument.Parse(json);
        document.RootElement.WriteTo(writer);
    }
}
