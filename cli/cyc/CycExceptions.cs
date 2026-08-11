namespace CyberCloud.Cli;

/// <summary>
///     The command line was wrong and nothing was sent — <see cref="ExitCode.Usage" />.
/// </summary>
/// <remarks>
///     ⚠ <b>The message names what <i>is</i> available.</b> An unknown group, verb or api-version is
///     a question the binary can answer out of the tree it is already holding, and "unknown
///     api-version 2025-01-01" without the list is a support question by construction. Every throw
///     site in this assembly follows that rule and <c>UnknownSurfaceTests</c> checks it.
/// </remarks>
sealed class CycUsageException : Exception {
    /// <summary>Creates the exception.</summary>
    public CycUsageException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong, and what would have been right.</param>
    public CycUsageException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What was wrong, and what would have been right.</param>
    /// <param name="innerException">The cause.</param>
    public CycUsageException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
///     The command reached the platform, or tried to, and failed in a way that is nobody's typing
///     mistake — <see cref="ExitCode.ClientError" />.
/// </summary>
sealed class CycClientException : Exception {
    /// <summary>Creates the exception.</summary>
    public CycClientException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public CycClientException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public CycClientException(string message, Exception? innerException) : base(message, innerException) { }
}
