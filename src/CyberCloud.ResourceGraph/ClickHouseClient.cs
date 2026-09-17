using System.Net.Http.Headers;
using System.Text;

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
    readonly HttpClient http;
    readonly Uri endpoint;

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
    /// <returns>The body, or a failure carrying ClickHouse's own error text.</returns>
    public async Task<Result<string>> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(sql);

        var query = new StringBuilder("?date_time_input_format=best_effort&output_format_json_quote_64bit_integers=0");

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
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"ClickHouse at {endpoint} answered {(int)response.StatusCode} {response.ReasonPhrase}: {body.Trim()}"
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
