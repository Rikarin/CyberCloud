using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceGraph;

/// <summary>
///     ClickHouse over its HTTP interface: a statement is a <c>POST</c> body, a result is whatever
///     <c>FORMAT</c> the statement named, and the credential is two headers.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Parameters go in the query string as <c>param_{name}</c> and never into the SQL
///         text.</b> ClickHouse binds <c>{name:Type}</c> placeholders server-side, so a resource name
///         or a tag value with a quote in it cannot become part of the statement. The one thing this
///         client interpolates is an <b>identifier</b> — the tenant's database — and
///         <see cref="ResourceGraphTable.Database" /> derives that from a GUID's 32 hex digits, so
///         there is nothing in it to escape. <c>IAlertQuerySeam</c>'s remarks name the same rule for
///         the query the Monitor workspace owes.
///     </para>
///     <para>
///         ⚠ <b>Two settings ride on every request.</b> <c>date_time_input_format=best_effort</c>,
///         so an ISO-8601 timestamp with an offset is parsed rather than refused; and
///         <c>output_format_json_quote_64bit_integers=0</c>, so a <c>UInt64</c> comes back as a JSON
///         number and not a string — ClickHouse quotes 64-bit integers by default for JavaScript's
///         sake, and a reader expecting a number would parse a string as zero.
///     </para>
/// </remarks>
public sealed class ClickHouseClient {
    /// <summary>The response header ClickHouse puts its exception's number in, beside the body's <c>Code: N.</c></summary>
    public const string ExceptionCodeHeader = "X-ClickHouse-Exception-Code";

    /// <summary><c>TIMEOUT_EXCEEDED</c>: the statement ran past <c>max_execution_time</c>.</summary>
    public const int TimeoutExceeded = 159;

    /// <summary><c>TOO_MANY_ROWS</c>: the statement would read past <c>max_rows_to_read</c>.</summary>
    public const int TooManyRows = 158;

    /// <summary><c>TOO_MANY_ROWS_OR_BYTES</c>: the same limit, reported by a newer code path.</summary>
    public const int TooManyRowsOrBytes = 396;

    static readonly Regex ExceptionCodePattern = new(
        "\\(" + ExceptionCodeHeader + ": (?<code>[0-9]+)\\)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1)
    );

    readonly HttpClient http;
    readonly Uri endpoint;

    /// <summary>
    ///     The ClickHouse exception number a failure from <see cref="ExecuteAsync(string, IReadOnlyDictionary{string, string}?, IReadOnlyDictionary{string, string}?, CancellationToken)" />
    ///     carries, or <c>0</c> for a failure that is not the server's answer — unreachable, timed
    ///     out on this side, or a failure some other component wrote.
    /// </summary>
    /// <param name="failure">The error a call returned.</param>
    /// <remarks>
    ///     Read back out of the message, because <see cref="Error" /> has no field for a foreign
    ///     code and deliberately so; the spelling is this class's own and the pattern is anchored to
    ///     it, so a body that happens to contain the header's name does not match.
    /// </remarks>
    public static int ExceptionCode(Error failure) {
        ArgumentNullException.ThrowIfNull(failure);

        var match = ExceptionCodePattern.Match(failure.Message);

        return match.Success && int.TryParse(match.Groups["code"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code
            : 0;
    }

    /// <summary>Whether a failure is ClickHouse refusing the statement for exceeding its per-query budget.</summary>
    /// <param name="failure">The error a call returned.</param>
    public static bool IsBudgetExceeded(Error failure) =>
        ExceptionCode(failure) is TimeoutExceeded or TooManyRows or TooManyRowsOrBytes;

    /// <summary>Creates a client over one <see cref="HttpClient" /> it does not own.</summary>
    /// <param name="http">The client. One per store, long-lived.</param>
    /// <param name="options">The endpoint and the credential.</param>
    public ClickHouseClient(HttpClient http, ResourceGraphOptions options) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        this.http = http;
        endpoint = ValidatedEndpoint(options);

        http.DefaultRequestHeaders.Remove("X-ClickHouse-User");
        http.DefaultRequestHeaders.Remove("X-ClickHouse-Key");
        http.DefaultRequestHeaders.Add("X-ClickHouse-User", options.ClickHouseUser);
        http.DefaultRequestHeaders.Add("X-ClickHouse-Key", options.ClickHousePassword);
    }

    /// <summary>
    ///     The endpoint as an absolute URI, refusing plain HTTP unless the section opted in.
    /// </summary>
    /// <param name="options">The bound section.</param>
    /// <exception cref="ArgumentException">The endpoint is not an absolute URI, or is <c>http</c> without <see cref="ResourceGraphOptions.AllowInsecureTransport" />.</exception>
    public static Uri ValidatedEndpoint(ResourceGraphOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (!Uri.TryCreate(options.ClickHouseEndpoint, UriKind.Absolute, out var uri)) {
            throw new ArgumentException(
                $"{ResourceGraphOptions.SectionName}:ClickHouseEndpoint '{options.ClickHouseEndpoint}' is not an "
                + "absolute URI. ClickHouse's HTTP interface is http(s)://host:8123.",
                nameof(options)
            );
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !options.AllowInsecureTransport) {
            throw new ArgumentException(
                $"{ResourceGraphOptions.SectionName}:ClickHouseEndpoint '{options.ClickHouseEndpoint}' is plain HTTP and "
                + "the projector's credential would travel in clear. Set AllowInsecureTransport for a "
                + "container on a laptop; a production endpoint has TLS.",
                nameof(options)
            );
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException(
                $"{ResourceGraphOptions.SectionName}:ClickHouseEndpoint '{options.ClickHouseEndpoint}' is not http or https.",
                nameof(options)
            );
        }

        return uri;
    }

    /// <summary>
    ///     Runs one statement and returns the response body as text — empty for a DDL or an
    ///     <c>INSERT</c>, the formatted rows for a <c>SELECT</c>.
    /// </summary>
    /// <param name="sql">The statement, with <c>{name:Type}</c> placeholders for values.</param>
    /// <param name="parameters">The placeholders' values, by name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The body, or a failure carrying ClickHouse's own error text — for the log, not for a caller; see <see cref="ExceptionCode" />.</returns>
    public Task<Result<string>> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default
    ) =>
        ExecuteAsync(sql, parameters, null, cancellationToken);

    /// <summary>
    ///     <see cref="ExecuteAsync(string, IReadOnlyDictionary{string, string}?, CancellationToken)" />
    ///     with per-request ClickHouse settings — the query API's <c>max_execution_time</c>,
    ///     <c>max_rows_to_read</c> and <c>readonly</c>.
    /// </summary>
    /// <param name="sql">The statement, with <c>{name:Type}</c> placeholders for values.</param>
    /// <param name="parameters">The placeholders' values, by name.</param>
    /// <param name="settings">
    ///     Settings for this one request, each sent as a query-string key. ⚠ A setting name is
    ///     interpolated into the URL, so this takes names the caller spelled in code and never one
    ///     from a request; the values are escaped like a parameter's.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The body, or a failure carrying ClickHouse's own error text — for the log, not for a caller; see <see cref="ExceptionCode" />.</returns>
    public async Task<Result<string>> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyDictionary<string, string>? settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(sql);

        var query = new StringBuilder("?date_time_input_format=best_effort&output_format_json_quote_64bit_integers=0");

        if (settings is not null) {
            foreach (var (name, value) in settings) {
                query.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        if (parameters is not null) {
            foreach (var (name, value) in parameters) {
                query.Append("&param_").Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, query.ToString()));
        request.Content = new StringContent(sql, Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };

        try {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode) {
                // ⚠ The body IS the diagnosis: ClickHouse answers a bad statement with its own
                // exception text (`Code: 62. DB::Exception: Syntax error …`), and that text names
                // the column or the token. A status code alone would send a reader to the server log.
                // ⚠ And the diagnosis is for the LOG, never for a caller: the text quotes the whole
                // statement — the tenant database, every column, the access filter with the
                // caller's usersets in it — so whoever turns this failure into a response replaces
                // the message and keeps the code (ExceptionCode) to decide what to say.
                var code = response.Headers.TryGetValues(ExceptionCodeHeader, out var values)
                           && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;

                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"ClickHouse at {endpoint} answered {(int)response.StatusCode} {response.ReasonPhrase} ({ExceptionCodeHeader}: {code}): {body.Trim()}"
                );
            }

            return Result<string>.Success(body);
        }
        catch (HttpRequestException exception) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"ClickHouse at {endpoint} could not be reached: {exception.Message}"
            );
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"ClickHouse at {endpoint} did not answer within {http.Timeout.TotalSeconds:0}s."
            );
        }
    }
}
