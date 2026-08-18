// ⚠ For `Result<string>`. `CyberCloud.Core.Resources` is global in this assembly and
// `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins over the
// `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Sample;

/// <summary>
///     Serves <c>POST …/widgets/{name}/ping</c>: the text the caller sent, and when it was served.
/// </summary>
/// <remarks>
///     <para>
///         <b>The reference provider's action, and the reason it now has a handler is that it is the
///         reference.</b> This action exists so the conformance suite has a real <c>POST</c> to
///         drive — docs/plan/08 § The write path, end to end makes <c>POST</c> "appear only for
///         actions on an existing resource … never for creation", and a suite with no action
///         registered cannot check the second half. It did that job while doing nothing, which was
///         fine until <c>ActionDispatcher</c> started refusing a synchronous action with no handler
///         by name: the provider a new provider is read from was the one demonstrating the <c>500</c>.
///     </para>
///     <para>
///         ⚠ <b>It reaches nothing, and that is what makes it the example.</b> No cluster read, no
///         vault, no engine. What it demonstrates is the three obligations every handler has and
///         nothing else: report the type and action it serves so the dispatcher can check them, read
///         the request out of <see cref="ActionContext.Body" /> — already validated against
///         <c>SampleWidgets.PingRequest</c>, so a present property is a well-typed one — and return
///         a body matching <c>SampleWidgets.PingResponse</c>, which the dispatcher validates rather
///         than trusts.
///     </para>
///     <para>
///         ⚠ <b>The echo default is applied here and not assumed.</b> The request schema gives
///         <c>/echo</c> a <c>DefaultJson</c>, and a default is what the generated surfaces show a
///         caller; it is not something the write path writes into a body it did not receive. An
///         action body that omitted the property arrives omitted, so the fallback is the handler's
///         to apply — and it is the same string the schema publishes, because two defaults that
///         disagree is a document that lies about one of them.
///     </para>
/// </remarks>
/// <param name="clock">
///     Where <c>/at</c> comes from. ⚠ Injected rather than <see cref="DateTimeOffset.UtcNow" />: a
///     handler resolves from the container by concrete type, so a constructor dependency is the
///     ordinary case, and a test that cannot fix the clock cannot assert the field at all.
/// </param>
public sealed class WidgetPingHandler(IClock clock) : IResourceActionHandler {
    /// <summary>What <c>/echo</c> is when the caller sent none.</summary>
    /// <remarks>
    ///     ⚠ The same value <c>SampleWidgets.PingRequest</c> publishes as the property's default. The
    ///     two are asserted equal by the provider's own tests, because a default a caller reads in the
    ///     OpenAPI document and a different one the platform applies is worse than having neither.
    /// </remarks>
    public const string DefaultEcho = "pong";

    /// <inheritdoc />
    public ResourceTypeName Type => SampleWidgets.Type;

    /// <inheritdoc />
    public string Action => SampleWidgets.PingAction;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        var echo = context.Body.ValueKind == JsonValueKind.Object
                   && context.Body.TryGetProperty("echo", out var sent)
                   && sent.ValueKind == JsonValueKind.String
            ? sent.GetString() ?? DefaultEcho
            : DefaultEcho;

        return Task.FromResult(
            Result<string>.Success(
                new JsonObject {
                    ["echo"] = echo,
                    ["at"] = clock.UtcNow.ToString("O")
                }.ToJsonString()
            )
        );
    }
}
