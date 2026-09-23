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
    ///     Who invoked the action, as the gateway built it from the token — or <see langword="null" />
    ///     when the dispatcher was not handed one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A FACT TO BIND SOMETHING TO, NEVER AN INPUT TO AN ALLOW-OR-DENY.
    ///         </b> The manager has already decided: step 3 checked the action's permission on this
    ///         resource through ReBAC before the handler was reached, and docs/plan/07 § The
    ///         enforcement seam keeps that decision there. A handler that compared this against
    ///         something and refused would be a second authorization engine, in a provider.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It exists for the cloud terminal's <c>connect</c>, and the reason is ownership.</b>
    ///         A shell is a session that belongs to the person who opened it, and until this property
    ///         existed a handler could not name that person: the omission this record's remarks used
    ///         to defend — "a synchronous action has no operation" — was right about the log and left
    ///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
    ///         <c>connect-cannot-see-its-caller</c>, with no route to closing. <c>connect</c> binds its
    ///         session to this caller, and the session grain then refuses every other person.
    ///     </para>
    /// </remarks>
    public CallerContext? Caller { get; init; }
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
