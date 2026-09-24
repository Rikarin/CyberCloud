using CyberCloud.Kubernetes.Contracts.Tunnel;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ResourceManager.Reconcile;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Actions;

/// <summary>
///     Resolves the handler a declared action names, runs it, and checks what it returned against the
///     shape the provider published.
/// </summary>
/// <remarks>
///     <para>
///         <b>The action path's <c>ReconcileDriver</c>, and deliberately its twin.</b> Resolution is
///         the same: the registry stores a <see cref="Type" />, the container is asked for it, and a
///         registration that is not in the container is a failure naming the type rather than a
///         silent no-op. Nothing new was invented for actions because nothing needed to be.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE RESPONSE IS VALIDATED HERE AND NOT TRUSTED, WHICH IS THE ONE THING THIS ADDS OVER
///             THE RECONCILE PATH.
///         </b> A provider declares <c>ActionRegistration.Response</c> and that
///         schema reaches the OpenAPI document, the generated SDK, the CLI's output shape and the
///         portal's form. A handler returning a different shape makes all four a lie, and no compiler
///         catches it: the handler returns JSON text and the schema is data. So the dispatcher runs
///         the same <see cref="ResourceSchema.Validate" /> a resource body goes through, and a drift
///         is an <see cref="ErrorCode.InternalError" /> naming the handler rather than a <c>200</c>
///         carrying something the caller's generated client cannot deserialize.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An action with no declared response is not validated, and that is not the same as
///             validating against nothing.
///         </b> <see langword="null" /> means the provider has not said
///         what the action returns — see <see cref="ActionRegistration.Response" /> — and checking
///         against <see cref="ResourceSchema.Empty" /> would refuse every action that returns
///         anything at all.
///     </para>
///     <para>
///         ⚠ <b>Bounded at <see cref="ReconcileDriver.PassBudget" />, borrowed rather than reinvented.</b>
///         An action runs on the request path, so the ceiling that matters is the caller's own
///         patience — but a handler that never returns holds a gateway request thread, and the
///         platform already has one number for "how long a provider may hold us up". A second number
///         would be a second thing to keep in step.
///     </para>
/// </remarks>
/// <param name="services">Where a handler type is resolved from.</param>
/// <param name="clusters">Turns a cluster id into a connection, or <see langword="null" />.</param>
/// <param name="secrets">
///     The secret seam a handler reads through. ⚠ <c>UnavailableSecretResolver</c> in a host with no
///     vault, which refuses legibly — which is what a <c>listKeys</c> on an unwired platform should
///     say.
/// </param>
/// <param name="agents">
///     The agent-tunnel seam <c>listInstallCommand</c> mints through, or <see langword="null" /> for
///     the refusing default — every dispatcher built by a test, and the right answer for a host that
///     serves no connected cluster.
/// </param>
/// <param name="terminals">
///     Where <c>connect</c> registers the terminal session it starts, or <see langword="null" /> for
///     the refusing default. <c>AddCyberCloudResourceManager</c> registers the grain-backed one.
/// </param>
/// <param name="relay">
///     Where an action goes when its type declares <c>RequiresCluster</c> and
///     <paramref name="clusters" /> has no connection for it: <see cref="GrainClusterActionRelay" /> in
///     the gateway, <see langword="null" /> everywhere else. ⚠ Only the gateway registers one. It
///     can't reach a cluster and a silo can; <see cref="IClusterActionGrain" /> says why.
/// </param>
public sealed class ActionDispatcher(
    IServiceProvider services,
    IClusterConnectionFactory clusters,
    ISecretResolver secrets,
    IAgentTunnels? agents = null,
    ITerminalSessions? terminals = null,
    IClusterActionRelay? relay = null
) {
    /// <summary>Runs one action and returns its response body.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="registration">The resource type, for the cluster requirement.</param>
    /// <param name="action">The action, which names the handler and the response shape.</param>
    /// <param name="input">The resource as stored — its desired body, api-version and cluster.</param>
    /// <param name="body">The validated <c>POST</c> body.</param>
    /// <param name="caller">
    ///     Who asked, handed to the handler as <see cref="ActionContext.Caller" /> — a fact to bind a
    ///     session to, never a second authorization. <see langword="null" /> when the caller of this
    ///     dispatcher has none to give.
    /// </param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The response JSON, or a failure.</returns>
    public Task<Result<string>> InvokeAsync(
        ResourceId id,
        ResourceTypeRegistration registration,
        ActionRegistration action,
        ReconcileInput input,
        JsonElement body,
        CallerContext? caller = null,
        CancellationToken cancellationToken = default
    ) =>
        InvokeCoreAsync(id, registration, action, input, body, caller, relay, cancellationToken);

    /// <summary>
    ///     Runs one action in this process and never relays it, which is what
    ///     <see cref="ClusterActionGrain" /> does with an action the gateway relayed.
    /// </summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="registration">The resource type, for the cluster requirement.</param>
    /// <param name="action">The action, which names the handler and the response shape.</param>
    /// <param name="input">The resource as stored.</param>
    /// <param name="body">The validated <c>POST</c> body.</param>
    /// <param name="caller">Who asked, handed to the handler as <see cref="ActionContext.Caller" />.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The response JSON, or a failure.</returns>
    public Task<Result<string>> InvokeHereAsync(
        ResourceId id,
        ResourceTypeRegistration registration,
        ActionRegistration action,
        ReconcileInput input,
        JsonElement body,
        CallerContext? caller = null,
        CancellationToken cancellationToken = default
    ) =>
        InvokeCoreAsync(id, registration, action, input, body, caller, null, cancellationToken);

    async Task<Result<string>> InvokeCoreAsync(
        ResourceId id,
        ResourceTypeRegistration registration,
        ActionRegistration action,
        ReconcileInput input,
        JsonElement body,
        CallerContext? caller,
        IClusterActionRelay? elsewhere,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(input);

        if (action.HandlerType is null) {
            // ⚠ InternalError, a 500, and not a 404 or a 400. The caller did nothing wrong: they
            // POSTed an action this platform publishes in its own OpenAPI document. The gap is ours,
            // and a 4xx would send them looking at their request.
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{id.Type}' declares the action '{action.Name}' and no handler for it, so it cannot "
                + "be run. The declaration reaches the OpenAPI document, the SDK and the CLI, which is "
                + "why this refuses by name rather than answering an operation that does nothing. Name "
                + "an IResourceActionHandler in the provider's Action(...) declaration."
            );
        }

        if (services.GetService(action.HandlerType) is not IResourceActionHandler handler) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{action.HandlerType.FullName}' is declared as the handler for "
                + $"'{id.Type}/{action.Name}' and is not registered in the container. Register it as a "
                + "singleton — AddCyberCloudProvider does that for every handler a provider names."
            );
        }

        // ⚠ Checked, because a handler serving the wrong type would read another provider's body. The
        // registry cannot catch this: it stores a Type, and a Type says nothing about what its
        // instance will claim at run time.
        if (handler.Type != id.Type) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{action.HandlerType.FullName}' is declared as the handler for "
                + $"'{id.Type}/{action.Name}' and reports its type as '{handler.Type}'."
            );
        }

        if (handler.Action.Length > 0
            && !string.Equals(handler.Action, action.Name, StringComparison.OrdinalIgnoreCase)) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{action.HandlerType.FullName}' is declared as the handler for "
                + $"'{id.Type}/{action.Name}' and reports its action as '{handler.Action}'. A handler "
                + "that serves every action on its type reports an empty action instead."
            );
        }

        var connection = clusters.Connect(input.ClusterId);

        if (registration.RequiresCluster && connection is null && elsewhere is not null) {
            // ⚠ The gateway's case. The handler was still resolved and checked above, so a gateway
            // composed without it fails by name here; it then runs on a silo, where the connection is,
            // and the silo's dispatcher checks the response against the declared shape.
            return await elsewhere.InvokeAsync(id, action.Name, input, body, caller, cancellationToken);
        }

        if (registration.RequiresCluster && connection is null) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{id.Type}' declares RequiresCluster and no connection was available for cluster "
                + $"{input.ClusterId:D}, so '{action.Name}' cannot reach it. The action refuses rather "
                + "than handing the handler a null it would dereference — the same rule "
                + "ReconcileDriver applies to a pass."
            );
        }

        var context = new ActionContext(
            id,
            input.ApiVersion,
            action.Name,
            body,
            ParseOrEmpty(input.Desired),
            ReconcileDriver.NamespaceFor(id),
            connection,
            secrets
        ) {
                // ⚠ The host's seam, or the refusing default when a caller built this dispatcher
                // without one — which every test double does, and which is the right answer for a
                // dispatcher that serves no connected cluster.
                Agents = agents ?? new UnavailableAgentTunnels(),
                Terminals = terminals ?? new UnavailableTerminalSessions(),
                Caller = caller
            };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ReconcileDriver.PassBudget);

        Result<string> invoked;
        try {
            invoked = await handler.InvokeAsync(context, budget.Token);
        } catch (OperationCanceledException) when (budget.IsCancellationRequested
                                                   && !cancellationToken.IsCancellationRequested) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{handler.GetType().Name}' did not return within "
                + $"{ReconcileDriver.PassBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} "
                + $"seconds, so '{id.Type}/{action.Name}' was abandoned. An action answers the caller "
                + "directly and has no operation to fall back to."
            );
        }

        if (invoked.TryGetError(out var invokeError)) {
            return Result<string>.Failure(invokeError);
        }

        var response = invoked.GetValueOrThrow();

        return action.Response is { } schema
            ? Checked(handler, id, action, schema, response)
            : Result<string>.Success(response);
    }

    /// <summary>
    ///     Checks a handler's output against the shape its provider published.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The failure message never carries the body.</b> A <c>secret: true</c> action's response
    ///     is the credential, and an error that quoted it to explain what was wrong would put the
    ///     credential in whatever logged the error — which is the leak the whole action path is careful
    ///     about, arrived at through the diagnostics.
    /// </remarks>
    static Result<string> Checked(
        IResourceActionHandler handler,
        ResourceId id,
        ActionRegistration action,
        ResourceSchema schema,
        string response
    ) {
        JsonDocument parsed;
        try {
            parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(response) ? "{}" : response);
        } catch (JsonException) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{handler.GetType().Name}' returned something that is not JSON for "
                + $"'{id.Type}/{action.Name}'. The value is not reproduced here — this action may carry "
                + "secret material."
            );
        }

        using (parsed) {
            var validated = schema.Validate(parsed.RootElement);

            return validated.TryGetError(out var error)
                ? Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{handler.GetType().Name}' returned a body that does not match the response shape "
                    + $"'{id.Type}' declares for '{action.Name}': {error.Message} That schema is what "
                    + "the OpenAPI document, the generated SDK and the portal form are built from, so a "
                    + "handler drifting from it publishes a contract nothing honours."
                )
                : Result<string>.Success(response);
        }
    }

    static JsonElement ParseOrEmpty(string json) {
        try {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        } catch (JsonException) {
            using var fallback = JsonDocument.Parse("{}");
            return fallback.RootElement.Clone();
        }
    }
}
