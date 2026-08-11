namespace CyberCloud.Sdk;

/// <summary>
///     Authentication was attempted and failed.
/// </summary>
/// <remarks>
///     ⚠ <b>No message built by this SDK ever contains a client secret, a certificate's private key,
///     an assertion, a refresh token or an access token</b> — docs/plan/08 § Errors' <i>"no exception
///     details, ever"</i> read from the client side, and the property
///     <c>CredentialsNeverLeakTests</c> asserts over every credential type. The identity server's
///     <c>error</c> and <c>error_description</c> are reproduced because they are written for a human
///     and contain no secret; the request that produced them is not.
/// </remarks>
public class AuthenticationFailedException : Exception {
    /// <summary>Creates the exception.</summary>
    public AuthenticationFailedException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public AuthenticationFailedException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public AuthenticationFailedException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>The OAuth <c>error</c> code, when the identity server sent one — <c>invalid_grant</c>.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
///     This credential does not apply in this environment: no CLI on the path, no federated token
///     file, no environment variables, an empty token cache.
/// </summary>
/// <remarks>
///     ⚠ <b>The distinction from <see cref="AuthenticationFailedException" /> is the whole reason
///     <see cref="ChainedTokenCredential" /> can work.</b> "Not applicable" means try the next one;
///     "failed" means stop and tell the user, because a wrong client secret that silently fell
///     through to an interactive browser prompt is a credential chain that hides its own
///     misconfiguration.
/// </remarks>
public sealed class CredentialUnavailableException : AuthenticationFailedException {
    /// <summary>Creates the exception.</summary>
    public CredentialUnavailableException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why this credential does not apply, in terms the user can act on.</param>
    public CredentialUnavailableException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why this credential does not apply.</param>
    /// <param name="innerException">The cause.</param>
    public CredentialUnavailableException(string message, Exception? innerException) : base(message, innerException) { }
}
