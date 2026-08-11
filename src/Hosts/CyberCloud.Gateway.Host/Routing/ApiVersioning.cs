using CyberCloud.Gateway.Host.Http;
using CyberCloud.ResourceManager.Contracts.Registry;

namespace CyberCloud.Gateway.Host.Routing;

/// <summary>
///     The <c>?api-version=</c> rule. docs/plan/10 § API versioning.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/10 § API versioning on why a query parameter rather than a header:
///         <i>"it survives being pasted into a browser, it appears in logs without extra
///         configuration, and it is what every Azure tool already emits. Header versioning is cleaner
///         and loses all three."</i>
///     </para>
///     <para>
///         ⚠ <b>There is no "latest", and that is the whole design.</b> A default would mean a
///         client written today silently starts receiving tomorrow's shape, which is precisely what
///         immutable dated versions exist to prevent — docs/plan/10 § API versioning: <i>"an old
///         client keeps getting the shape it was written against indefinitely."</i> A missing
///         parameter is therefore a <c>400</c> and never a guess.
///     </para>
/// </remarks>
static class ApiVersioning {
    /// <summary>The query parameter's name. Azure's spelling.</summary>
    public const string QueryParameter = "api-version";

    /// <summary>Reads and checks the parameter.</summary>
    /// <param name="query">The request's query string.</param>
    /// <param name="registration">
    ///     The type's registration, when the path resolved to a known type. Pass
    ///     <see langword="null" /> for a route with no type — the operations endpoint and the hubs
    ///     still require the parameter, they just have no per-type version list to check it against.
    /// </param>
    /// <param name="current">
    ///     The version to name in the message when the caller sent none. docs/plan/10
    ///     § API versioning requires the <c>400</c> to name it.
    /// </param>
    /// <returns>
    ///     The version, or <see cref="ErrorCode.InvalidApiVersion" />. ⚠ A version that is <i>older</i>
    ///     than <paramref name="current" /> but still registered succeeds — that is the contract.
    /// </returns>
    public static Result<ApiVersion> Resolve(
        IQueryCollection query,
        ResourceTypeRegistration? registration,
        ApiVersion current
    ) {
        ArgumentNullException.ThrowIfNull(query);

        var supplied = query.TryGetValue(QueryParameter, out var values) ? values.ToString() : string.Empty;

        if (string.IsNullOrWhiteSpace(supplied)) {
            return Result<ApiVersion>.Failure(GatewayErrors.ApiVersionRequired("", current.Value));
        }

        if (!ApiVersion.TryParse(supplied, out var version)) {
            return Result<ApiVersion>.Failure(GatewayErrors.ApiVersionRequired(supplied, current.Value));
        }

        if (registration is null) {
            return Result<ApiVersion>.Success(version);
        }

        // ⚠ The registry, not a comparison against `current`. A retired version and a version that
        // never existed are both refused here, and the message names the type's own newest rather
        // than the platform's — a caller of an old provider does not want to be told about a version
        // that provider never had.
        var schema = registration.SchemaFor(version);

        return schema.TryGetError(out _)
            ? Result<ApiVersion>.Failure(GatewayErrors.ApiVersionRequired(supplied, registration.Newest.Value))
            : Result<ApiVersion>.Success(version);
    }
}
