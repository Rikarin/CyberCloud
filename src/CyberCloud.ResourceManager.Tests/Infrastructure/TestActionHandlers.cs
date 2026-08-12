using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Tests.Infrastructure;

/// <summary>
///     The handler behind <c>TestingProvider</c>'s <c>restart</c>: does no work and says so.
/// </summary>
/// <remarks>
///     ⚠ An action that declares no response is a real branch — <c>ActionRegistration.Response</c> is
///     <see langword="null" /> for it, and <c>ActionDispatcher</c> must then return whatever the
///     handler produced rather than validating it against nothing.
/// </remarks>
public sealed class RestartHandler : IResourceActionHandler {
    /// <summary>How many times the dispatcher reached this handler.</summary>
    /// <remarks>
    ///     ⚠ Static, and reset by the fixture. A handler is a singleton by concrete type, so an
    ///     instance field would be the hidden state clause 2 forbids of a reconciler and would be no
    ///     better here.
    /// </remarks>
    public static int Invocations { get; private set; }

    /// <summary>Puts the counter back.</summary>
    public static void Reset() => Invocations = 0;

    /// <inheritdoc />
    public ResourceTypeName Type => new("CyberCloud.Testing", "widgets");

    /// <inheritdoc />
    public string Action => "restart";

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        Invocations++;

        return Task.FromResult(Result<string>.Success("""{"restarted":true}"""));
    }
}

/// <summary>
///     The handler behind <c>TestingProvider</c>'s <c>listKeys</c>: returns a credential.
/// </summary>
/// <remarks>
///     ⚠ <b>The value is a constant rather than a resolve, and that is what makes it useful here.</b>
///     <c>ActionDispatchTests</c> greps the durable tier and the operation status for this
///     exact string, and a value that came out of a vault double would be one more thing that could
///     be empty for a reason the assertion cannot see.
/// </remarks>
public sealed class ListKeysHandler : IResourceActionHandler {
    /// <summary>The secret this handler hands out. Searched for by the containment suite.</summary>
    public const string Secret = "the-secret-access-key-nothing-else-may-hold";

    /// <summary>The key id, which is not secret and is expected to appear in the response.</summary>
    public const string KeyId = "AKIATESTKEYID0000000";

    /// <summary>Whether the handler returns a shape its declaration does not describe.</summary>
    /// <remarks>
    ///     ⚠ The switch failure class (c) is tested through: a handler drifting from its published
    ///     response schema is invisible to the compiler, so the dispatcher has to catch it.
    /// </remarks>
    public static bool ReturnTheWrongShape { get; set; }

    /// <summary>Puts the switch back.</summary>
    public static void Reset() => ReturnTheWrongShape = false;

    /// <inheritdoc />
    public ResourceTypeName Type => new("CyberCloud.Testing", "widgets");

    /// <inheritdoc />
    public string Action => "listKeys";

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<string>.Success(
                ReturnTheWrongShape
                    // Drops the required /secretAccessKey and invents a property the schema refuses.
                    ? new JsonObject { ["keys"] = new JsonArray { KeyId } }.ToJsonString()
                    : new JsonObject {
                        ["accessKeyId"] = KeyId, ["secretAccessKey"] = Secret
                    }.ToJsonString()
            )
        );
}
