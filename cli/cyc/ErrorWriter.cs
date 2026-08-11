using System.CommandLine.Parsing;
using CyberCloud.Cli.Execution;
using CyberCloud.Cli.Output;

namespace CyberCloud.Cli;

/// <summary>
///     Turns a failure into an exit code and a message on stderr.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here ever touches stdout, and that is the contract this type exists to
///         keep.</b> <c>cyc … --output json</c> is read by scripts; a CLI that printed a human error
///         into that stream would break every one of them the first time a request failed. So stdout
///         carries the payload or carries nothing, and everything below goes to stderr — including,
///         under <c>--output json</c>, an error rendered <i>as</i> JSON, because a script that
///         captures stderr should be able to read it too.
///     </para>
///     <para>
///         ⚠ <b>The exit code is chosen from the platform's status, not from the exception's
///         type.</b> docs/plan/21 § Decisions makes the six codes a contract CI branches on;
///         <c>401</c> and <c>403</c> answer <see cref="ExitCode.Auth" /> rather than
///         <see cref="ExitCode.ClientError" /> so that "retry with a fresh credential" and "this
///         request is wrong" are different answers.
///     </para>
/// </remarks>
static class ErrorWriter {
    /// <summary>Reports a failure.</summary>
    /// <param name="console">The console. Only <see cref="CycConsole.Error" /> is written.</param>
    /// <param name="format">The output format, which decides whether the message is prose or JSON.</param>
    /// <param name="failure">What went wrong.</param>
    /// <param name="verbose">Whether to include the exception chain.</param>
    /// <returns>The process exit code.</returns>
    public static int Report(CycConsole console, OutputFormat format, Exception failure, bool verbose) {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(failure);

        var (code, message, detail) = Classify(failure);

        if (format == OutputFormat.Json) {
            var payload = Payload.Object([
                new KeyValuePair<string, Payload>("error", Payload.Object([
                    new KeyValuePair<string, Payload>("code", detail is null ? Payload.Null : Payload.Text(detail)),
                    new KeyValuePair<string, Payload>("message", Payload.Text(message)),
                    new KeyValuePair<string, Payload>("exitCode", Payload.Number((int)code)),
                ])),
            ]);

            console.Error.WriteLine(payload.ToJson(indented: true));
        } else {
            console.Error.WriteLine(message);
        }

        // ⚠ The stack is verbose-only and it is on stderr. docs/plan/08 § Errors bans exception detail
        // in a response body; this is the client's own stack, which is the one thing a bug report
        // needs and the one thing an ordinary failure must not be cluttered with.
        if (verbose)
            console.Error.WriteLine($"cyc: {failure}");

        console.Error.Flush();

        return (int)code;
    }

    /// <summary>Reports the parser's own errors — always <see cref="ExitCode.Usage" />.</summary>
    /// <param name="console">The console.</param>
    /// <param name="format">The output format.</param>
    /// <param name="errors">What the parser objected to.</param>
    public static int ReportParseErrors(CycConsole console, OutputFormat format, IReadOnlyList<ParseError> errors) {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(errors);

        var message = string.Join(Environment.NewLine, errors.Select(x => x.Message));

        return Report(console, format, new CycUsageException(message.Length > 0 ? message : "The command line could not be parsed."), verbose: false);
    }

    static (ExitCode Code, string Message, string? Detail) Classify(Exception failure)
        => failure switch {
            CycUsageException usage => (ExitCode.Usage, usage.Message, "UsageError"),

            CycRequestException request => (
                CodeFor(request.Status),
                Describe(request),
                request.ErrorCode ?? $"Http{request.Status}"),

            // The SDK throws this from a poll that reported a failed operation, and from
            // OperationPoller's own construction failures.
            CyberCloudRequestFailedException sdk => (
                CodeFor(sdk.Status == 200 ? 500 : sdk.Status),
                sdk.Message,
                sdk.ErrorCode),

            CredentialUnavailableException credential => (ExitCode.Auth, credential.Message, "CredentialUnavailable"),
            AuthenticationFailedException authentication => (ExitCode.Auth, authentication.Message, authentication.ErrorCode ?? "AuthenticationFailed"),

            // ⚠ Cancellation reaches here only from --timeout: Ctrl-C is handled by the runtime's
            // POSIX signal handling before this, and a caller-cancelled operation in a CLI is a
            // deadline by definition.
            OperationCanceledException => (ExitCode.Timeout, "The command did not finish before --timeout elapsed.", "Timeout"),
            TimeoutException timeout => (ExitCode.Timeout, timeout.Message, "Timeout"),

            CycClientException client => (ExitCode.ClientError, client.Message, "ClientError"),

            // ⚠ A transport failure is a client error rather than a server one: nothing reached the
            // platform, so retrying the same request against the same broken network is not the
            // advice exit 4 gives.
            HttpRequestException transport => (ExitCode.ClientError, $"The request could not be sent: {transport.Message}", "TransportError"),
            IOException io => (ExitCode.ClientError, io.Message, "IOError"),

            _ => (ExitCode.ClientError, failure.Message, failure.GetType().Name),
        };

    static ExitCode CodeFor(int status)
        => status switch {
            401 or 403 => ExitCode.Auth,
            408 or 504 => ExitCode.Timeout,
            >= 500 => ExitCode.ServerError,
            >= 400 => ExitCode.ClientError,
            _ => ExitCode.ClientError,
        };

    static string Describe(CycRequestException request) {
        var message = new StringBuilder(request.Message);

        if (request.Flag is { Length: > 0 } flag)
            message.Append(Environment.NewLine).Append(CultureInfo.InvariantCulture, $"The rejected value came from {flag}.");

        return message.ToString();
    }
}
