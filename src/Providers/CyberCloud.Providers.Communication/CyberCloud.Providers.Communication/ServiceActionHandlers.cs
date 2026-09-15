using System.Text.Json;

namespace CyberCloud.Providers.Communication;

/// <summary>
///     Serves <c>POST …/services/{name}/send</c>: one message through this service, or the one
///     already sent under the same idempotency key.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A refusal comes back as a failure, and that is the module's rule rather than this
///             handler's.
///         </b> <c>IMessageGrain.SendAsync</c> returns a suppressed address, a missing template
///         argument, a turned-off channel or an exhausted limit as a <see cref="Result" /> failure
///         carrying the reason, so the caller gets a <c>4xx</c> with the sentence and not a
///         <c>200</c> with a <c>status: refused</c> to notice. Nothing was sent and no carrier was
///         called; <c>SuppressionEnforcementTests</c> pins the suppression half through this exact
///         path.
///     </para>
///     <para>
///         ⚠ <b>The service's id is derived from the address, not read off the resource.</b>
///         <see cref="CommunicationServices.ServiceIdOf" /> is the same function the reconcilers use,
///         so a send reaches the grain the service's own reconcile pass wrote to.
///     </para>
/// </remarks>
/// <param name="sender">The module's client-side seam — the one identity holds too.</param>
public sealed class ServiceSendHandler(IMessageSender sender) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationServices.Type;

    /// <inheritdoc />
    public string Action => CommunicationServices.SendAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var request = CommunicationServices.ToSendRequest(CommunicationServices.ServiceIdOf(context.Id), context.Body);
        if (request.TryGetError(out var malformed)) {
            return Result<string>.Failure(malformed);
        }

        var sent = await sender.SendAsync(context.Id.TenantId, request.GetValueOrThrow(), cancellationToken);

        return sent.TryGetError(out var refused)
            ? Result<string>.Failure(refused)
            : Result<string>.Success(CommunicationServices.MessageJson(sent.GetValueOrThrow()));
    }
}

/// <summary>
///     Serves <c>POST …/services/{name}/status</c>: where a message got to, every delivery receipt
///     included. The answer to "did it arrive".
/// </summary>
/// <remarks>
///     ⚠ <see cref="ErrorCode.ResourceNotFound" /> both for a key never sent under and for one sent
///     more than <c>IMessageGrain.Retention</c> ago, and the two are deliberately indistinguishable
///     — a hot tier that could tell them apart would be a durable tier. The message names the
///     retention so a caller reading it knows which question to ask.
/// </remarks>
/// <param name="sender">The module's client-side seam.</param>
public sealed class ServiceStatusHandler(IMessageSender sender) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationServices.Type;

    /// <inheritdoc />
    public string Action => CommunicationServices.StatusAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var key = context.Body.ValueKind == JsonValueKind.Object
            && context.Body.TryGetProperty("idempotencyKey", out var found)
            && found.ValueKind == JsonValueKind.String
                ? found.GetString() ?? string.Empty
                : string.Empty;

        var status = await sender.GetStatusAsync(context.Id.TenantId, CommunicationServices.ServiceIdOf(context.Id), key, cancellationToken);

        return status.TryGetError(out var missing)
            ? Result<string>.Failure(missing)
            : Result<string>.Success(CommunicationServices.MessageJson(status.GetValueOrThrow()));
    }
}

/// <summary>
///     Serves <c>POST …/services/{name}/checkSuppression</c>: whether a send to one address would be
///     refused, and why.
/// </summary>
/// <param name="plane">The module's control-plane seam.</param>
public sealed class ServiceCheckSuppressionHandler(ICommunicationControlPlane plane) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationServices.Type;

    /// <inheritdoc />
    public string Action => CommunicationServices.CheckSuppressionAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var channel = ChannelKinds.Parse(Text(context.Body, "channel"));
        if (channel == ChannelKind.Unknown) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                "The channel is not one of " + string.Join(", ", ChannelKinds.AllowedValues) + ".",
                "/channel"
            );
        }

        var checked_ = await plane.CheckSuppressionAsync(
            context.Id.TenantId,
            CommunicationServices.ServiceIdOf(context.Id),
            channel,
            Text(context.Body, "destination"),
            cancellationToken
        );

        return checked_.TryGetError(out var failed)
            ? Result<string>.Failure(failed)
            : Result<string>.Success(CommunicationServices.SuppressionCheckJson(checked_.GetValueOrThrow()));
    }

    internal static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>
///     Serves <c>POST …/services/{name}/listSuppressions</c>: the whole list — the tenant's manual
///     blocks and every bounce, complaint and opt-out that arrived on its own.
/// </summary>
/// <param name="plane">The module's control-plane seam.</param>
public sealed class ServiceListSuppressionsHandler(ICommunicationControlPlane plane) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationServices.Type;

    /// <inheritdoc />
    public string Action => CommunicationServices.ListSuppressionsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var spelled = ServiceCheckSuppressionHandler.Text(context.Body, "channel");
        var channel = ChannelKinds.Parse(spelled);

        // Empty means every channel — ChannelKind.Unknown is what ISuppressionListGrain.ListAsync
        // takes for "all" — and anything else that fails to parse is a typo worth refusing.
        if (channel == ChannelKind.Unknown && spelled.Length > 0) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{spelled}' is not a channel. Name one of " + string.Join(", ", ChannelKinds.AllowedValues) + ", or none for all of them.",
                "/channel"
            );
        }

        var listed = await plane.ListSuppressionsAsync(context.Id.TenantId, CommunicationServices.ServiceIdOf(context.Id), channel, cancellationToken);

        return listed.TryGetError(out var failed)
            ? Result<string>.Failure(failed)
            : Result<string>.Success(CommunicationServices.SuppressionListJson(listed.GetValueOrThrow()));
    }
}

/// <summary>
///     Serves <c>POST …/templates/{name}/render</c>: what a send with these arguments would say,
///     rendered from the template's own body.
/// </summary>
/// <remarks>
///     ⚠ <b>Pure, and the only handler in this family that reaches no grain.</b>
///     <c>TemplateRenderer.Render</c> is a pure function and <see cref="ActionContext.Desired" /> is
///     the template's stored body, so this runs on the request path with nothing to await. A missing
///     required variable is refused naming every missing one at once — the same refusal a real send
///     would get, from the same function, before any carrier.
/// </remarks>
public sealed class TemplateRenderHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => CommunicationTemplates.Type;

    /// <inheritdoc />
    public string Action => CommunicationTemplates.RenderAction;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        var arguments = CommunicationServices.ParseArguments(
            context.Body.ValueKind == JsonValueKind.Object && context.Body.TryGetProperty("arguments", out var found) ? found : null
        );

        if (arguments.TryGetError(out var malformed)) {
            return Task.FromResult(Result<string>.Failure(malformed));
        }

        var version = CommunicationTemplates.VersionOf(context.Desired);
        var rendered = TemplateRenderer.Render(version, CommunicationTemplates.BodyOf(context.Desired).Locale, arguments.GetValueOrThrow());

        return Task.FromResult(
            rendered.TryGetError(out var refused)
                ? Result<string>.Failure(refused)
                : Result<string>.Success(CommunicationTemplates.RenderedJson(rendered.GetValueOrThrow()))
        );
    }
}
