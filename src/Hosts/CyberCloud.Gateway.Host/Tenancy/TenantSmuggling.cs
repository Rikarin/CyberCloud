using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tenancy;

/// <summary>
///     Where a caller can put a tenant id, and what the gateway does about each.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read this before changing anything in this file.</b> docs/plan/00 § The
///         tenant-separation row, corrected establishes, from a decompilation of
///         <c>Orleans.Multitenant</c> 4.0.0, that <c>TenantSeparatingCallFilter</c> consults
///         <c>ICrossTenantAuthorizer</c> only when a call has a source grain that is neither a client
///         nor a system target. The gateway is an Orleans <b>client</b> by deliberate design
///         (<c>CreateClient</c>, so a gateway deploy does not move grains), so it is
///         <b>permanently</b> outside tenant separation — not "until it is finished".
///         <c>CyberCloud.Tenancy.Tests.CrossTenantReachabilityTests.Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN</c>
///         demonstrates the hole with separation fully wired.
///     </para>
///     <para>
///         So this type, plus <c>ForTenant</c> everywhere, is the <i>whole</i> of the tenancy
///         boundary for every request that arrives over HTTP. There is no second mechanism below it
///         that would catch a mistake here.
///     </para>
///     <para>
///         <b>The rule, stated once.</b> The tenant comes from the token's <c>tid</c>. Every other
///         surface a caller can write a tenant id into is checked for <i>disagreement</i> and never
///         read for a <i>decision</i>. A disagreement is a <c>404</c>, byte-identical to the one an
///         absent resource gets — a <c>403</c> would confirm that the named tenant exists, which is
///         the enumeration oracle docs/plan/07 § The enforcement seam closes for resources and which
///         applies at least as strongly to tenants.
///     </para>
/// </remarks>
static class TenantSmuggling {
    /// <summary>The query parameter callers sometimes copy from other clouds' URLs.</summary>
    public const string TenantQueryParameter = "tenantId";

    /// <summary>The two body property names that carry a tenant in Azure-shaped payloads.</summary>
    static readonly string[] BodyProperties = ["tenantId", "tid"];

    /// <summary>
    ///     Whether any caller-controlled surface names a tenant other than the token's.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="body">
    ///     The body as text, or empty. ⚠ Parsed here with <see cref="JsonDocument" /> and discarded;
    ///     a parse failure is <i>not</i> a smuggling attempt and is left to stage 7 to report.
    /// </param>
    /// <param name="tokenTenant">The tenant from the token. The only authority.</param>
    /// <param name="surface">Which surface disagreed, for the log line.</param>
    /// <returns>
    ///     <c>true</c> when some surface named a different tenant, which the caller must be answered
    ///     with a <c>404</c>.
    /// </returns>
    public static bool Disagrees(
        HttpRequest request,
        string body,
        Guid tokenTenant,
        out string surface
    ) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);

        surface = "";

        if (PathTenant(request.Path.Value ?? "") is { } fromPath && fromPath != tokenTenant) {
            surface = "the path";
            return true;
        }

        if (request.Headers.TryGetValue(Http.GatewayHeaders.TenantIdHint, out var header)
            && GatewayGuid.TryParseD(header.ToString(), out var fromHeader)
            && fromHeader != tokenTenant) {
            surface = "the " + Http.GatewayHeaders.TenantIdHint + " header";
            return true;
        }

        if (request.Query.TryGetValue(TenantQueryParameter, out var queried)
            && GatewayGuid.TryParseD(queried.ToString(), out var fromQuery)
            && fromQuery != tokenTenant) {
            surface = "the " + TenantQueryParameter + " query parameter";
            return true;
        }

        return BodyTenantDisagrees(body, tokenTenant, ref surface);
    }

    /// <summary>
    ///     The tenant a resource-id path names, or <see langword="null" /> when the path names none.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately does <b>not</b> go through <see cref="ResourceId.ParsePath" />. A path that
    ///     names tenant B and is malformed further along must still be caught here: reporting the
    ///     malformed-path <c>400</c> first would tell a prober that their tenant id got past the
    ///     tenant check, which is a bit of information they should not have.
    /// </remarks>
    public static Guid? PathTenant(string path) {
        ArgumentNullException.ThrowIfNull(path);

        const string prefix = "/tenants/";

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var segment = slash < 0 ? rest : rest[..slash];

        return GatewayGuid.TryParseD(segment, out var tenant) ? tenant : null;
    }

    static bool BodyTenantDisagrees(string body, Guid tokenTenant, ref string surface) {
        if (body.Length == 0) {
            return false;
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException) {
            // Not a smuggling attempt — an unparseable body. Stage 7 reports it as a 400, and
            // answering 404 here would turn every typo into a mystery.
            return false;
        }

        using (document) {
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return false;
            }

            foreach (var property in BodyProperties) {
                if (document.RootElement.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && GatewayGuid.TryParseD(value.GetString() ?? "", out var fromBody)
                    && fromBody != tokenTenant) {
                    surface = $"the '{property}' property of the request body";
                    return true;
                }
            }
        }

        return false;
    }
}
