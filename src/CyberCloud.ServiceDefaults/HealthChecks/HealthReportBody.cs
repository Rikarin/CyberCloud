using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace CyberCloud.ServiceDefaults.HealthChecks;

/// <summary>The body <c>/api/health</c> writes.</summary>
/// <remarks>
///     Survival writes this with Newtonsoft. <c>System.Text.Json</c> is already in the shared
///     framework and docs/plan/02's register lists no Newtonsoft, so the shape is copied and the
///     library is not.
/// </remarks>
sealed record HealthReportBody
{
    /// <summary>The aggregate status: <c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>How long every check took together.</summary>
    [JsonPropertyName("duration")]
    public required TimeSpan Duration { get; init; }

    /// <summary>One entry per registered check.</summary>
    [JsonPropertyName("checks")]
    public required IReadOnlyList<HealthEntry> Checks { get; init; }

    /// <summary>
    ///     Serialises the report to the response.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Never a 500.</b> A health endpoint that throws is a health endpoint that reports
    ///     "unknown" as "down", so the writer carries no logic that could fail: no formatting of
    ///     caller-supplied strings, no reflection over exception types, and — see
    ///     <see cref="HealthEntry" /> — no exception <i>detail</i>.
    /// </remarks>
    internal static Task WriteAsync(
        HttpContext context,
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        context.Response.ContentType = MediaTypeNames.Application.Json;

        var body = new HealthReportBody
        {
            Status = report.Status.ToString(),
            Duration = report.TotalDuration,
            Checks = [.. report.Entries.Select(entry => new HealthEntry
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description,
                Duration = entry.Value.Duration,
                Error = entry.Value.Exception?.Message,
                Tags = [.. entry.Value.Tags],
            })],
        };

        return context.Response.WriteAsJsonAsync(body, Options);
    }

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One check's result inside a <see cref="HealthReportBody" />.</summary>
sealed record HealthEntry
{
    /// <summary>The registered name, for example <c>silo-ready</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary><c>Healthy</c>, <c>Degraded</c> or <c>Unhealthy</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>What the check said, when it said anything.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>How long this check took.</summary>
    [JsonPropertyName("duration")]
    public required TimeSpan Duration { get; init; }

    /// <summary>
    ///     The exception <i>message</i>, if the check threw.
    /// </summary>
    /// <remarks>
    ///     ⚠ The message only — never <c>ToString()</c>, never the stack. docs/plan/08:190 forbids
    ///     exception detail in any body a caller can read, and <c>/api/health</c> is reachable from
    ///     the cluster network. The stack goes to the trace.
    /// </remarks>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>The check's tags — see <see cref="HealthCheckTags" />.</summary>
    [JsonPropertyName("tags")]
    public required IReadOnlyList<string> Tags { get; init; }
}
