namespace CyberCloud.Cli;

/// <summary>
///     The two streams, kept apart on purpose.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything that is not the answer goes to <see cref="Error" />.</b> Diagnostics,
///         progress lines, the device-code prompt, the telemetry question, the update notice and every
///         failure. <c>--output json</c> has to stay machine-parseable <i>when the command fails</i>,
///         and the only way to promise that is for stdout to carry the payload and nothing else — so
///         <c>cyc … --output json | jq .</c> either reads a document or reads nothing, never a
///         sentence.
///     </para>
///     <para>
///         ⚠ <b>Writes are synchronous, and CA1849 is not suppressed for them.</b> A
///         <see cref="TextWriter" /> over a console is already synchronous underneath;
///         <c>WriteLineAsync</c> would add a state machine per line and change nothing about when the
///         bytes land. What matters for <c>--wait</c> is that each line is flushed as it is written,
///         which <see cref="Flush" /> makes explicit at the one place it is load-bearing.
///     </para>
/// </remarks>
sealed class CycConsole {
    CycConsole(TextWriter output, TextWriter error, bool outputRedirected, bool errorRedirected) {
        Out = output;
        Error = error;
        IsOutputRedirected = outputRedirected;
        IsErrorRedirected = errorRedirected;
    }

    /// <summary>The answer, and nothing else.</summary>
    public TextWriter Out { get; }

    /// <summary>Everything a human reads while waiting: progress, prompts, warnings, failures.</summary>
    public TextWriter Error { get; }

    /// <summary>Whether stdout is a pipe or a file rather than a terminal.</summary>
    public bool IsOutputRedirected { get; }

    /// <summary>
    ///     Whether stderr is a pipe or a file. ⚠ Progress rendering checks this: a spinner rewritten
    ///     with <c>\r</c> is a readable terminal and an unreadable log file.
    /// </summary>
    public bool IsErrorRedirected { get; }

    /// <summary>The real console.</summary>
    public static CycConsole System()
        => new(Console.Out, Console.Error, Console.IsOutputRedirected, Console.IsErrorRedirected);

    /// <summary>A console backed by two writers — what the tests read their assertions from.</summary>
    /// <param name="output">Receives stdout.</param>
    /// <param name="error">Receives stderr.</param>
    /// <param name="redirected">Whether both streams should claim to be redirected.</param>
    public static CycConsole For(TextWriter output, TextWriter error, bool redirected = true)
        => new(output, error, redirected, redirected);

    /// <summary>Writes one line to stderr and flushes it.</summary>
    /// <param name="line">The line. Written whole, so two threads cannot interleave halves of it.</param>
    public void Note(string line) {
        Error.WriteLine(line);
        Error.Flush();
    }

    /// <summary>
    ///     Writes one character to stderr and flushes it — the progress ticker's only output.
    /// </summary>
    /// <param name="mark">The character.</param>
    /// <remarks>
    ///     ⚠ A method rather than <c>console.Error.Write(…)</c> at the call site, because that call
    ///     site is inside an <c>async</c> method and CA1849 — an error in this repository — refuses a
    ///     synchronous <see cref="TextWriter" /> call there. The rule is right in general and wrong
    ///     here: see this type's remarks.
    /// </remarks>
    public void Tick(char mark) {
        Error.Write(mark);
        Error.Flush();
    }

    /// <summary>Pushes anything buffered out of both streams.</summary>
    public void Flush() {
        Out.Flush();
        Error.Flush();
    }
}
