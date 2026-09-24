using CyberCloud.Kubernetes.Contracts.Tunnel;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Everything one action invocation is allowed to know. The action path's answer to
///     <see cref="ReconcileContext" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Strictly less than <see cref="ReconcileContext" />, and the two omissions are the
///             contract.
///         </b> An action carries no <see cref="IReconcileLog" />, because a synchronous
///         action has no operation to report progress against — it answers the caller directly and is
///         over. And it carries no <see cref="ObservedState" />, because an action reads the world
///         itself if it needs to; handing it a cached observation would invite one to answer from a
///         reading somebody else took minutes ago.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <see cref="Body" /> has already been validated and <see cref="Desired" /> has not been
///             projected.
///         </b> The manager checks the body against
///         <c>ActionRegistration.Request</c> before a handler is reached, on the same terms a
///         resource body is checked, so a handler may assume every declared parameter is present and
///         well-typed. <see cref="Desired" /> is the resource's stored superset rather than a view at
///         <see cref="ApiVersion" />, because a handler reads facts about the resource rather than
///         rendering it back to a caller.
///     </para>
/// </remarks>
/// <param name="Id">The resource, with <see cref="ResourceId.Id" /> resolved.</param>
/// <param name="ApiVersion">The api-version the request named.</param>
/// <param name="Action">
///     Which action. ⚠ Present so one handler can serve several actions on a type, which is the
///     ordinary shape for <c>listKeys</c> beside <c>regenerateKeys</c>.
/// </param>
/// <param name="Body">The <c>POST</c> body, already validated, or an empty object when it had none.</param>
/// <param name="Desired">The resource's stored desired body.</param>
/// <param name="Namespace">The resource's Kubernetes namespace, or empty for a clusterless provider.</param>
/// <param name="Cluster">The cluster to reach, or <see langword="null" /> for a provider with none.</param>
/// <param name="Secrets">
///     Resolves <see cref="SecretRef" /> handles. ⚠ For a <c>secret: true</c> action this is where the
///     value comes from, and the value goes into the returned body and nowhere else.
/// </param>
public readonly record struct ActionContext(
    ResourceId Id,
    string ApiVersion,
    string Action,
    JsonElement Body,
    JsonElement Desired,
    string Namespace,
    IKubeClusterConnection? Cluster,
    ISecretResolver Secrets
) {
    /// <summary>
    ///     The agent-tunnel seam — what <c>listInstallCommand</c> mints through. Defaults to
    ///     <see cref="UnavailableAgentTunnels" />, as <see cref="ReconcileContext.Agents" /> does and
    ///     for the same reason; <c>ActionDispatcher</c> supplies the host's.
    /// </summary>
    public IAgentTunnels Agents { get; init; } = new UnavailableAgentTunnels();

    /// <summary>
    ///     The terminal-session seam — where <c>connect</c> registers the shell it started. Defaults to
    ///     <see cref="UnavailableTerminalSessions" />; <c>ActionDispatcher</c> supplies the host's.
    /// </summary>
    public ITerminalSessions Terminals { get; init; } = new UnavailableTerminalSessions();

    /// <summary>
    ///     Who invoked the action, as the manager checked the action's permission for. Empty when the
    ///     dispatcher was built without one, as every test double's is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>For a handler whose answer depends on more than the resource.</b> The manager has
    ///         checked the declared permission on the resource and nothing else. <c>showStatus</c> on a
    ///         budget that covers its subscription returns the subscription's spend, and a reader of the
    ///         budget's group may not read that — so the handler asks again, about the subscription, for
    ///         this caller. A handler that asks must refuse when this is empty rather than answer as
    ///         nobody in particular.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And for one that binds something to a person, which is the cloud terminal's
    ///         <c>connect</c>.</b> A shell belongs to the person who opened it: <c>connect</c> binds its
    ///         session to this caller, and the session grain then refuses every other person. That use
    ///         is a fact to bind to, not an allow-or-deny, and a <c>connect</c> handed an empty caller
    ///         refuses rather than open a session nobody owns.
    ///     </para>
    /// </remarks>
    public CallerContext Caller { get; init; } = new();

    /// <summary>
    ///     The resource's parent with its GUID resolved, or <see langword="null" /> for a top-level
    ///     resource and for a child whose parent no longer resolves.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The action path learns the parent's GUID and the reconcile path still doesn't,
    ///             and the difference is who resolves it.
    ///         </b> A reconcile pass runs from a reminder with the address it was written at;
    ///         an action runs on the request path, where <c>ResourceManagerService</c> already reads
    ///         the tenant's index for the resource itself, so the parent is one more read through the
    ///         same tenant-qualified factory. A handler that needs a fact the platform derives from the
    ///         parent's GUID — <c>CyberCloud.Monitor/workspaces/components</c> reads the workspace's
    ///         ClickHouse database, which is <c>ws_{guid:N}</c> — gets it from the index rather than
    ///         from an object in the tenant's namespace, which a tenant who administers that cluster
    ///         could rewrite to name another tenant's database.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The immediate parent only</b>, as the create path checks it. A dispatcher built by a
    ///         test with no parent leaves this <see langword="null" />, and a handler that needs it
    ///         refuses by name.
    ///     </para>
    /// </remarks>
    public ResourceId? Parent { get; init; }

    /// <summary>
    ///     Creates another resource in this resource's group, as the <b>caller</b> of this action,
    ///     through the whole write path. docs/plan/08 § What the resource manager deliberately does not
    ///     do, "An action may create, as its caller".
    /// </summary>
    /// <remarks>
    ///     ⚠ Defaults to <see cref="RefusingResourceCreator" />, which fails by name; the manager supplies
    ///     one bound to the request's caller, and nothing a handler holds can rebind it.
    /// </remarks>
    public IResourceCreator Creator { get; init; } = new RefusingResourceCreator();
}

/// <summary>
///     The one way an action handler brings a resource into existence: a <c>PUT</c> of a new name, made
///     as the action's caller.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Why an action may do what a reconciler may not.
///         </b> docs/plan/08 § The cross-resource seam refuses a reconciler any write because "a write
///         needs a caller, and a reconciler has none". An action has one: the person who POSTed it,
///         already authenticated, whose request is still open. So the create this seam performs is
///         <i>that person's</i> write — authorised against them for the created type's write
///         permission in this group, locked, quota-reserved, indexed, and recorded with them as its
///         author — and a caller who may <c>recover</c> from a vault but may not create a PostgreSQL
///         server is refused here exactly as their own <c>PUT</c> would be.
///     </para>
///     <para>
///         ⚠ <b>Create only, and only in this resource's own subscription and group.</b> A name that
///         already exists is refused with <see cref="ErrorCode.ResourceAlreadyExists" /> rather than
///         replaced: a restore never overwrites, and a handler that could PUT over an existing resource
///         would be one retry away from doing so. The check precedes the write and does not lock the
///         name, so two concurrent creates of one name can both pass it and the second becomes an
///         update; the created body is the same in that race, and it is recorded rather than closed.
///     </para>
/// </remarks>
public interface IResourceCreator {
    /// <summary>Creates one resource beside the action's own.</summary>
    /// <param name="type">The type to create.</param>
    /// <param name="name">Its name, in this resource's subscription and group.</param>
    /// <param name="apiVersion">The api-version the body is written at.</param>
    /// <param name="body">The body, as a caller would <c>PUT</c> it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The created resource and the operation creating it, or the write path's refusal.</returns>
    Task<Result<ResourceCreated>> CreateAsync(
        ResourceTypeName type,
        string name,
        string apiVersion,
        string body,
        CancellationToken cancellationToken = default
    );
}

/// <summary>What <see cref="IResourceCreator.CreateAsync" /> started.</summary>
/// <param name="Id">The new resource, with its GUID.</param>
/// <param name="OperationId">The create's operation, which the caller polls.</param>
public sealed record ResourceCreated(ResourceId Id, Guid OperationId);

/// <summary>
///     The <see cref="IResourceCreator" /> an <see cref="ActionContext" /> carries when nobody
///     supplied one.
/// </summary>
public sealed class RefusingResourceCreator : IResourceCreator {
    /// <inheritdoc />
    public Task<Result<ResourceCreated>> CreateAsync(
        ResourceTypeName type,
        string name,
        string apiVersion,
        string body,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<ResourceCreated>.Failure(
                ErrorCode.InternalError,
                $"This action context carries no resource creator, so '{type}/{name}' cannot be created. "
                + "ResourceManagerService.ActionAsync supplies one bound to the caller; a context built by "
                + "hand has to set ActionContext.Creator."
            )
        );
}

/// <summary>
///     Runs one declared action on a resource. docs/plan/08 § The provider registry.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the half of <c>IProviderBuilder.Action</c> that was missing.</b> A provider could
///         declare an action's name, permission, request shape and response shape, and every generated
///         surface published it — but nothing anywhere could run one. Twelve actions across nine
///         provider namespaces reached <c>openapi/2026-08-01.json</c> that way, and every one of them
///         answered <c>202</c> and then re-ran the type's <i>reconciler</i>, because that is what
///         <c>OperationGrain</c> does with any operation that is not a delete.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Resolved from the container by concrete type, exactly as
///             <c>ResourceTypeRegistration.ReconcilerType</c> is.
///         </b> The registry stores
///         <see cref="Registry.ActionRegistration.HandlerType" /> and <c>ActionDispatcher</c> resolves
///         it, so a second mechanism — a delegate on the registration, a scan for implementations —
///         was not invented. What follows from that precedent also follows here: register the
///         implementation <b>as a singleton</b>, because one instance per process is correct for
///         something holding only dependencies.
///     </para>
///     <para>
///         ⚠ <b>A handler must not write the value it returns anywhere durable.</b> The manager keeps
///         a synchronous action's result off the operation record for exactly this reason — see
///         <c>ResourceManagerService.ActionAsync</c> — and a handler that logged the credential on its
///         way out would put it back in the durable tier by another route.
///     </para>
///     <para>
///         ⚠ <b>An action never creates.</b> docs/plan/08 § The write path, end to end: a <c>POST</c>
///         to a name that does not exist is a <c>404</c>, and the manager checks that before any
///         handler runs. A handler is only ever called for a resource that exists.
///     </para>
/// </remarks>
public interface IResourceActionHandler {
    /// <summary>The resource type this handler serves.</summary>
    /// <remarks>
    ///     ⚠ Must match a type the provider's <c>Describe</c> declared. A handler naming nothing is a
    ///     handler that is never called, which is silent unless something looks.
    /// </remarks>
    ResourceTypeName Type { get; }

    /// <summary>
    ///     Which action this handler serves, or empty when it serves every action on
    ///     <see cref="Type" />.
    /// </summary>
    /// <remarks>
    ///     Matched case-insensitively, because the registry matches an action name that way and a URL
    ///     segment is not case-sensitive in practice.
    /// </remarks>
    string Action { get; }

    /// <summary>Runs the action and returns its <c>200</c> body.</summary>
    /// <param name="context">Everything this invocation may know.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>
    ///     The response body as JSON, or a failure. ⚠ The JSON is checked against the action's
    ///     declared <c>Response</c> schema by the dispatcher before it reaches a caller, so a handler
    ///     whose shape drifts from what the provider published fails loudly rather than making the
    ///     generated document a lie.
    /// </returns>
    Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default);
}
