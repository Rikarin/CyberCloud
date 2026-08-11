using System.Text.Json;

namespace CyberCloud.Kubernetes.Apply;

/// <summary>
///     Turns the API server's 409 into the list of fields another manager owns.
/// </summary>
/// <remarks>
///     <para>
///         ADR-013's stated payoff: <i>"if a tenant hand-edits a field we own, the next apply reports
///         a conflict rather than silently reverting, and <b>that</b> becomes a drift event with a
///         name."</i> "With a name" is the requirement this class exists for — a drift event that
///         said only "conflict" would be no more useful than the exception it replaced.
///     </para>
///     <para>
///         <b>The wire shape.</b> A conflicting apply returns HTTP 409 and a <c>v1.Status</c>:
///     </para>
///     <code>
///     {
///       "kind": "Status", "status": "Failure", "code": 409, "reason": "Conflict",
///       "message": "Apply failed with 1 conflict: conflict with \"rival\" using apps/v1: .spec.replicas",
///       "details": { "causes": [ {
///           "reason": "FieldManagerConflict",
///           "message": "conflict with \"rival\" using apps/v1",
///           "field": ".spec.replicas" } ] }
///     }
///     </code>
///     <para>
///         ⚠ <b>The owning manager is only in the prose.</b> The structured <c>causes[].field</c>
///         gives the path, but the manager that holds it appears nowhere except inside
///         <c>causes[].message</c>, between the first pair of double quotes. That is unpleasant to
///         depend on, so it is isolated here, it degrades to <see cref="UnknownManager" /> rather
///         than throwing, and it is asserted against a <i>real</i> k3s response in
///         <c>ServerSideApplyTests</c> rather than against a string this file made up.
///     </para>
/// </remarks>
public static class ConflictParser
{
    /// <summary>Reported when the API server's message does not name a manager.</summary>
    public const string UnknownManager = "(unknown)";

    /// <summary>The <c>reason</c> the API server puts on a field-manager conflict cause.</summary>
    public const string FieldManagerConflictReason = "FieldManagerConflict";

    /// <summary>
    ///     Extracts the conflicting fields from a <c>v1.Status</c> body.
    /// </summary>
    /// <param name="statusJson">The 409 response body.</param>
    /// <returns>
    ///     The conflicts, or an empty list if the body is not a parseable status — in which case the
    ///     caller reports the raw message rather than pretending to know more than it does.
    /// </returns>
    public static IReadOnlyList<FieldConflict> Parse(string? statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(statusJson);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("details", out var details)
                || !details.TryGetProperty("causes", out var causes)
                || causes.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var conflicts = new List<FieldConflict>();
            foreach (var cause in causes.EnumerateArray())
            {
                var reason = Text(cause, "reason");

                // Other causes can ride along on a 409 (a failed precondition, for one). Only the
                // field-manager ones are drift.
                if (reason.Length > 0
                    && !string.Equals(reason, FieldManagerConflictReason, StringComparison.Ordinal))
                {
                    continue;
                }

                conflicts.Add(new FieldConflict
                {
                    Field = Text(cause, "field"),
                    OwnedBy = ManagerFrom(Text(cause, "message")),
                });
            }

            return conflicts;
        }
    }

    /// <summary>
    ///     Pulls the manager out of <c>conflict with "rival" using apps/v1</c>.
    /// </summary>
    /// <param name="message">The cause's message.</param>
    public static string ManagerFrom(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return UnknownManager;
        }

        var open = message.IndexOf('"', StringComparison.Ordinal);
        if (open < 0)
        {
            return UnknownManager;
        }

        var close = message.IndexOf('"', open + 1);
        return close <= open + 1 ? UnknownManager : message[(open + 1)..close];
    }

    static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
