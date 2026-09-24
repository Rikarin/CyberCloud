using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The body of an invitation — <c>{ "email": "…" }</c>, and nothing else is read. Issue #43.
/// </summary>
/// <remarks>
///     ⚠ <b>One member, on purpose.</b> No role (an invitation grants none — <c>IInvitationManager</c>'s
///     remarks), no tenant name (the mail says the tenant's own name, not one the caller chose), no
///     expiry (the invitation's is the platform's seven days). The address is handed on as typed;
///     the invitation grain normalizes it and refuses one that is not an address, with the sentence
///     that says why.
/// </remarks>
static class InvitationBody {
    /// <summary>The one member.</summary>
    public const string EmailMember = "email";

    /// <summary>The address the body names, or the refusal that says what the body should be.</summary>
    /// <param name="body">The request body, as the pipeline buffered it.</param>
    public static Result<string> Email(string body) {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Length == 0) {
            return Missing();
        }

        try {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(EmailMember, out var email)
                && email.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(email.GetString())
                    ? Result<string>.Success(email.GetString()!)
                    : Missing();
        } catch (JsonException exception) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is not valid JSON: {exception.Message}"
            );
        }
    }

    static Result<string> Missing() =>
        Result<string>.Failure(
            ErrorCode.InvalidRequestBody,
            """An invitation is a JSON object with a non-empty "email" member — { "email": "colleague@example.com" }. """
            + "It grants no role: a member's role is a role assignment, made after they accept."
        );
}
