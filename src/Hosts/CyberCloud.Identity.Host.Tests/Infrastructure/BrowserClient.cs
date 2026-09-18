using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     An <see cref="HttpClient" /> that behaves like a browser tab on the portal's origin: it keeps
///     the cookies the host sets, sends them back, never follows a redirect, and stamps
///     <c>Origin</c> on a cross-origin <c>POST</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The cookie jar is hand-rolled because <see cref="CookieContainer" /> is not a
///             browser.
///         </b> Every cookie this host sets is <c>Secure</c>, and the .NET container refuses
///         to store a <c>Secure</c> cookie received over plain <c>http://</c> — which is what the
///         test host is — while Chromium and Firefox accept one on <c>http://localhost</c>. The jar
///         here does what those browsers do with what the host sends: a value replaces the earlier
///         one by name, and <c>Max-Age=0</c> or a past <c>Expires</c> removes it. It does not model
///         <c>SameSite</c>, so the <c>Origin</c> check is the only thing between a cross-origin
///         request and the cookie — which is exactly the case the host's handler exists for.
///     </para>
///     <para>
///         No redirect is ever followed. The <c>Location</c> header is the assertion, and a client
///         that followed it would turn a redirect to the sign-in page into a <c>404</c> from a page
///         nothing here serves.
///     </para>
/// </remarks>
public sealed class BrowserClient : IDisposable {
    readonly HttpClient http;
    readonly Dictionary<string, string> jar = new(StringComparer.Ordinal);

    /// <summary>Opens a tab against <paramref name="baseAddress" /> from <paramref name="origin" />.</summary>
    /// <param name="baseAddress">The identity host.</param>
    /// <param name="origin">The origin the page lives on — the <c>Origin</c> header on every request.</param>
    public BrowserClient(Uri baseAddress, string origin) {
        http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) {
            BaseAddress = baseAddress
        };
        Origin = origin;
    }

    /// <summary>The origin every request claims to come from.</summary>
    public string Origin { get; set; }

    /// <summary>
    ///     An access token to send as <c>Authorization: Bearer</c> on every request, or
    ///     <see langword="null" /> for none — the portal calling <c>/userinfo</c>.
    /// </summary>
    public string? Bearer { get; set; }

    /// <summary>
    ///     An <c>X-Forwarded-For</c> to send on every request, or <see langword="null" /> for none —
    ///     what an ingress in front of the host would stamp, or what a caller claims.
    /// </summary>
    public string? ForwardedFor { get; set; }

    /// <summary>The cookies the tab holds, by name.</summary>
    public IReadOnlyDictionary<string, string> Cookies => jar;

    /// <summary>Overwrites one cookie — to replay an old value, or to drop one.</summary>
    /// <param name="name">The cookie's name.</param>
    /// <param name="value">Its value, or <see langword="null" /> to forget it.</param>
    public void SetCookie(string name, string? value) {
        if (value is null) {
            jar.Remove(name);
        } else {
            jar[name] = value;
        }
    }

    /// <summary>A <c>GET</c>, as a navigation.</summary>
    public Task<HttpResponseMessage> GetAsync(string pathAndQuery, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(pathAndQuery, UriKind.Relative)), cancellationToken);

    /// <summary>A <c>POST</c> of JSON, as the identity app's pages make.</summary>
    public Task<HttpResponseMessage> PostJsonAsync(string path, object body, CancellationToken cancellationToken) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            },
            cancellationToken
        );

    /// <summary>A <c>POST</c> of a form, as the portal's <c>fetch</c> to <c>/token</c> makes.</summary>
    public Task<HttpResponseMessage> PostFormAsync(
        string path,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken
    ) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) {
                Content = new FormUrlEncodedContent(form)
            },
            cancellationToken
        );

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        if (jar.Count > 0) {
            request.Headers.Add("Cookie", string.Join("; ", jar.Select(static x => x.Key + "=" + x.Value)));
        }

        request.Headers.Add("Origin", Origin);

        if (Bearer is { } bearer) {
            request.Headers.Authorization = new("Bearer", bearer);
        }

        if (ForwardedFor is { } forwardedFor) {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        var response = await http.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies)) {
            foreach (var header in setCookies) {
                Absorb(header);
            }
        }

        return response;
    }

    void Absorb(string header) {
        var parts = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var equals = parts[0].IndexOf('=', StringComparison.Ordinal);

        if (equals < 0) {
            return;
        }

        var name = parts[0][..equals];
        var value = parts[0][(equals + 1)..];

        var gone = value.Length == 0
            || parts.Skip(1).Any(static x => x.StartsWith("max-age=0", StringComparison.OrdinalIgnoreCase))
            || parts.Skip(1)
                .Any(static x => x.StartsWith("expires=", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(x["expires=".Length..], out var expires)
                    && expires < DateTimeOffset.UtcNow
                );

        if (gone) {
            jar.Remove(name);
        } else {
            jar[name] = value;
        }
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    // ── PKCE, as the portal computes it ──────────────────────────────────────────────────────────

    /// <summary>A fresh verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) Pkce() {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        return (verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    /// <summary>Base64url, unpadded.</summary>
    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A JWT's payload, decoded and not verified — the test reading its own token.</summary>
    public static JsonElement Payload(string jwt) {
        var segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = segment.PadRight(segment.Length + (4 - segment.Length % 4) % 4, '=');

        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded))).RootElement.Clone();
    }

    /// <summary>The query of a URL as a dictionary, decoded.</summary>
    public static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static pair => Uri.UnescapeDataString(pair[0]),
                static pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal
            );

    /// <summary>The <c>Location</c> a redirect names, as an absolute URI.</summary>
    public static Uri Location(HttpResponseMessage response) =>
        response.Headers.Location
        ?? throw new InvalidOperationException($"{(int)response.StatusCode} with no Location header");

    /// <summary>A response's body as JSON.</summary>
    public static async Task<JsonElement> JsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    ) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();

    /// <summary>One <c>Set-Cookie</c> header by cookie name, or <see langword="null" />.</summary>
    public static string? SetCookieHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    /// <summary>The value of one response header, or <see langword="null" />.</summary>
    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    /// <summary>Whether the response has a JSON content type — an OpenIddict error body, say.</summary>
    public static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType is MediaTypeHeaderValue { MediaType: "application/json" };
}
