using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The success bodies. Hand-written, for the same reason <see cref="ErrorBody" /> is.
/// </summary>
/// <remarks>
///     ⚠ <b>A resource's <c>properties</c> is <i>already</i> JSON text and is written raw.</b>
///     <see cref="ResourceSnapshot.Properties" /> is the projection the registry produced for the
///     caller's api-version; re-serializing it through an object model would mean parsing and
///     re-emitting, which loses number formatting and property order and is how a response drifts
///     from the schema it was validated against.
/// </remarks>
static class ResponseBodies {
    /// <summary>Renders a resource.</summary>
    /// <param name="snapshot">The projected snapshot.</param>
    public static string Resource(ResourceSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer)) {
            WriteResource(writer, snapshot);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Renders a page of a collection, in the <c>{ "value": [ … ], "nextLink": … }</c> shape.
    /// </summary>
    /// <param name="page">The page the resource manager built.</param>
    /// <param name="nextLink">The absolute next-page URL, or empty when there is no next page.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Each element is the same object <see cref="Resource" /> writes, member for
    ///         member.</b> A list that rendered a thinner resource than a <c>GET</c> does would make
    ///         a generated SDK's collection type and its resource type two different shapes with one
    ///         name, and the first place anybody would notice is a deserializer dropping a field.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>nextLink</c> is omitted rather than written as <c>null</c> or <c>""</c> when
    ///         there is no next page.</b> That is the Azure shape an <c>AsyncPageable&lt;T&gt;</c>
    ///         stops on; an empty string is a URL a polite client will happily request.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is no <c>count</c>, and the omission is the security property rather than
    ///         an unfinished feature.</b> The page holds what the caller may read — see
    ///         <c>ResourceListPage</c> — so a total would say how many resources exist that they may
    ///         not, which is the enumeration oracle docs/plan/07 § The enforcement seam closes one
    ///         resource at a time.
    ///     </para>
    /// </remarks>
    public static string Collection(ResourceListPage page, string nextLink) {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(nextLink);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteStartArray();

            foreach (var snapshot in page.Resources) {
                WriteResource(writer, snapshot);
            }

            writer.WriteEndArray();

            if (nextLink.Length > 0) {
                writer.WriteString("nextLink", nextLink);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The one resource object, written into whichever document is being built.</summary>
    /// <remarks>
    ///     ⚠ One writer for both callers. Two copies would be two chances for a resource read on its
    ///     own and the same resource inside a listing to disagree about their own shape.
    /// </remarks>
    static void WriteResource(Utf8JsonWriter writer, ResourceSnapshot snapshot) {
        writer.WriteStartObject();
        writer.WriteString("id", snapshot.Path);
        writer.WriteString("name", snapshot.Name);
        writer.WriteString("type", snapshot.Type);
        writer.WriteString("location", snapshot.Location);
        writer.WriteString("provisioningState", snapshot.ProvisioningState.ToString());
        writer.WriteString("etag", snapshot.Etag);
        writer.WritePropertyName("properties");
        WriteRaw(writer, snapshot.Properties);

        if (!snapshot.Tags.IsEmpty) {
            writer.WritePropertyName("tags");
            writer.WriteStartObject();

            foreach (var (key, value) in snapshot.Tags) {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>Renders a scope — docs/plan/06 § The hierarchy's subscription or resource group.</summary>
    /// <param name="scope">The scope as the manager reports it.</param>
    /// <remarks>
    ///     ⚠ <b>The same four top-level names a resource carries — <c>id</c>, <c>name</c>,
    ///     <c>type</c>, <c>location</c> — and deliberately no <c>provisioningState</c> and no
    ///     <c>etag</c>.</b> Azure's own resource group renders exactly that shape, and a client that
    ///     already reads a resource reads a scope with no branch. The two absences are real rather
    ///     than unfinished: a scope has no two-phase create, so it is never in a transient state worth
    ///     naming, and no <c>If-Match</c> concurrency, so an <c>etag</c> would be a value nothing on
    ///     this path accepts back.
    /// </remarks>
    public static string Scope(ScopeSnapshot scope) {
        ArgumentNullException.ThrowIfNull(scope);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("id", scope.Path);
            writer.WriteString("name", scope.Name);
            writer.WriteString("type", scope.Type);

            if (scope.Location.Length > 0) {
                writer.WriteString("location", scope.Location);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Renders an operation, with the progress array of docs/plan/10 § Long-running operations.
    /// </summary>
    /// <param name="status">The operation.</param>
    /// <remarks>
    ///     ⚠ <b>The <c>progress</c> array is ours and is the reason the endpoint is tolerable.</b>
    ///     docs/plan/10 § Long-running operations: <i>"The <c>progress</c> array is our addition and
    ///     it is what makes a nine-minute cluster creation tolerable."</i> A caller staring at
    ///     <c>Running</c> for nine minutes has no way to tell a slow success from a stuck failure.
    ///     Everything else in this body is Azure's shape exactly, so <c>Operation&lt;T&gt;</c> in the
    ///     SDK and <c>--wait</c> in the CLI are the standard implementations.
    /// </remarks>
    public static string Operation(OperationStatus status) {
        ArgumentNullException.ThrowIfNull(status);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WriteString("id", status.OperationId.ToString("D"));
            writer.WriteString("status", status.State.ToString());
            writer.WriteNumber("percentComplete", status.PercentComplete);
            writer.WriteString("startTime", status.StartedAt);

            if (status.EndedAt != default) {
                writer.WriteString("endTime", status.EndedAt);
            }

            writer.WritePropertyName("progress");
            writer.WriteStartArray();

            foreach (var entry in status.Progress) {
                writer.WriteStartObject();
                writer.WriteString("at", entry.At);
                writer.WriteString("step", entry.Step);
                writer.WriteString("message", entry.Detail);
                writer.WriteNumber("percentComplete", entry.PercentComplete);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (status.Error is { } error) {
                // ⚠ The same shape as a top-level error body, and with the same absence of detail.
                // An operation that failed reports why in the vocabulary a caller already parses.
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteString("code", error.Code.Value);
                writer.WriteString("message", error.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void WriteRaw(Utf8JsonWriter writer, string json) {
        if (json.Length == 0) {
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }

        try {
            using var document = JsonDocument.Parse(json);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException) {
            // Grain state that is not JSON is a platform fault, not a caller's. An empty object
            // keeps the response parseable; the fault goes to the trace, never to the body.
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }
}
