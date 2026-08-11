namespace CyberCloud.Core.Contracts.Serialization;

/// <summary>
///     The errors a surrogate substitutes when a payload cannot be turned back into a valid value.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every one of these resolves to a failure, never to a success.</b> That is the whole
///         rule this class exists to enforce. A deserialiser that cannot make sense of a
///         <see cref="Result" /> has exactly two safe options — throw, or produce a failure — and
///         it has one unsafe one, which is to produce a success. docs/plan/00 § Coding standards
///         makes every grain method return a <see cref="Result" />, so "the payload was odd, so the
///         call succeeded" would be a silent wrong answer on every call in the platform.
///     </para>
///     <para>
///         <b>Why substitute rather than throw.</b> docs/plan/00 § The quality bar, concretely
///         budgets <i>zero</i> failed tenant requests across a rolling upgrade of a 30-silo cluster,
///         and the payloads that land
///         here are precisely the cross-version ones: a field a peer did not write, an error code a
///         newer silo knows and this one does not. Throwing turns each of those into a failed
///         request; substituting turns them into a failed <i>operation</i>, which is a shape the
///         gateway already renders — docs/plan/00 § Coding standards. Malformed
///         <i>identifiers</i> are the exception and still throw — see
///         <c>ResourceTypeNameSurrogate</c>.
///     </para>
/// </remarks>
static class WireErrors {
    /// <summary>
    ///     An <see cref="Error" /> arrived with a blank message.
    /// </summary>
    /// <remarks>
    ///     Core's <see cref="Error" /> constructor rejects a blank message
    ///     (<c>ArgumentException</c>), so the value has to be supplied here or the payload cannot be
    ///     materialised at all.
    /// </remarks>
    internal const string MissingMessage =
        "An error arrived over the wire with no message. The peer that produced it is out of "
        + "contract: docs/plan/08 § Errors requires a message that names the actual values.";

    /// <summary>
    ///     A <see cref="Result" /> arrived saying "failure" and carrying no error.
    /// </summary>
    /// <remarks>
    ///     Reachable two ways, both of which must stay failures: a peer that wrote
    ///     <c>IsSuccess = false</c> and nothing else, and a surrogate that was never populated at
    ///     all (Orleans hands the converter <c>default(TSurrogate)</c> when no field was written,
    ///     and <c>default(ResultSurrogate).IsSuccess</c> is <see langword="false" />). Both are the
    ///     wire counterpart of <c>default(Result)</c>, which docs Core's <c>Result</c> remarks
    ///     already make a failure.
    /// </remarks>
    internal static readonly Error MissingError = new(
        ErrorCode.InternalError,
        "A failed Result arrived over the wire with no error attached. This is the wire counterpart "
        + "of default(Result): it is reported as a failure, never as a success, because a "
        + "deserialiser that cannot read an outcome must not invent a successful one."
    );

    /// <summary>
    ///     A <see cref="Result{T}" /> arrived saying "success" and carrying no value.
    /// </summary>
    /// <remarks>
    ///     <c>Result&lt;T&gt;</c> is constrained <c>T : notnull</c> and
    ///     <c>Result&lt;T&gt;.Success(null)</c> throws, so this payload cannot be honoured as
    ///     written. It is downgraded to a failure rather than thrown, per the remarks on this class.
    /// </remarks>
    internal static readonly Error MissingValue = new(
        ErrorCode.InternalError,
        "A successful Result<T> arrived over the wire with no value. Result<T> is constrained "
        + "T : notnull, so 'succeeded, and the answer is null' is not a state it can hold; the "
        + "outcome is reported as a failure."
    );

    /// <summary>
    ///     Resolves a wire code string to a registered <see cref="ErrorCode" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <see cref="ErrorCode" /> is a <b>closed</b> registry — its constructor is private,
    ///         so a code this build does not know cannot be manufactured here. During a rolling
    ///         upgrade (docs/plan/04 § Failure and upgrade) an N+1 silo can return a code an N silo has never heard
    ///         of, and that is the case this method exists for.
    ///     </para>
    ///     <para>
    ///         The unknown code becomes <see cref="ErrorCode.InternalError" /> and the original
    ///         string is preserved at the front of the message, so nothing is lost and a support
    ///         engineer greps the same token they would have greppped for. Silently dropping it
    ///         would make an upgrade look like an outage in the logs.
    ///     </para>
    /// </remarks>
    internal static (ErrorCode Code, string Message) Resolve(string? code, string message) =>
        ErrorCode.TryFromValue(code, out var known)
            ? (known, message)
            : (ErrorCode.InternalError,
                $"[unrecognised error code '{code}'] {message}");
}
