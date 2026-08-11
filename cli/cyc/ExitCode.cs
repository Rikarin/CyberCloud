namespace CyberCloud.Cli;

/// <summary>
///     The process exit codes, from docs/plan/21 § Decisions: <i>"<c>0</c> ok · <c>1</c> client error
///     · <c>2</c> usage · <c>3</c> auth · <c>4</c> server · <c>5</c> timeout. Documented, stable, so
///     CI can branch on them."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These numbers are a published contract and are asserted against the generated verb
///         tree.</b> <c>generated/cli/{version}.json</c> carries an <c>exitCodes</c> object emitted by
///         <c>CliEmitter</c>, and <c>ExitCodeTests</c> compares it member for member with this enum —
///         so a change on either side fails a test rather than silently retiring a branch in
///         somebody's pipeline.
///     </para>
///     <para>
///         ⚠ <b>The split between <see cref="ClientError" /> and <see cref="ServerError" /> follows
///         the HTTP status, and <see cref="Auth" /> is carved out of the <c>4xx</c> range.</b> A
///         script that retries on <c>4</c> and stops on <c>1</c> is doing the right thing; one that
///         retried a <c>403</c> would not be, which is why <c>401</c> and <c>403</c> answer
///         <c>3</c> rather than <c>1</c>.
///     </para>
/// </remarks>
enum ExitCode {
    /// <summary>The command did what it was asked.</summary>
    Ok = 0,

    /// <summary>The request reached the platform and was refused — a <c>4xx</c> that is not an auth failure.</summary>
    ClientError = 1,

    /// <summary>
    ///     The command line was wrong: an unknown group, verb or flag, a missing required flag, a
    ///     value outside a closed set, an api-version this build does not carry, or a
    ///     <c>--query</c> that does not parse. Nothing was sent.
    /// </summary>
    Usage = 2,

    /// <summary>No usable credential, or the platform rejected the one presented — <c>401</c> and <c>403</c>.</summary>
    Auth = 3,

    /// <summary>The platform failed — a <c>5xx</c>, after the pipeline's retries gave up.</summary>
    ServerError = 4,

    /// <summary>The deadline passed: <c>--timeout</c> elapsed, or a long-running operation outlived it.</summary>
    Timeout = 5,
}
