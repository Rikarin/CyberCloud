using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The success bodies. Hand-written, for the same reason <see cref="ErrorBody" /> is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A resource's body is <i>already</i> JSON text and its members are written raw.</b>
///         <see cref="ResourceSnapshot.Body" /> is the projection the registry produced for the
///         caller's api-version; re-serializing it through an object model would mean parsing and
///         re-emitting, which loses number formatting and property order and is how a response
///         drifts from the schema it was validated against.
///     </para>
///     <para>
///         ⚠ <b>And it is the whole document, not the inner <c>properties</c> slice.</b> The grain
///         writes every declared pointer at its full path, so the body of a type declaring
///         <c>/location</c> and <c>/properties/message</c> is
///         <c>{"location":…,"properties":{"message":…}}</c> — exactly the body the published OpenAPI
///         document describes. This writer used to nest that document under a <c>properties</c>
///         member of its own and served <c>properties.properties.message</c> with <c>location</c>
///         twice (issue #72). It went unseen because the gateway suite's substitute manager
///         hand-wrote a snapshot in the shape this file expected; the substitute now builds its
///         snapshot from the real projection, and
///         <c>ResourceBodyShapeTests.TheBodyIsTheProjectedDocumentSplicedIntoTheEnvelope</c> reads
///         the served result back the way the SDK does.
///     </para>
/// </remarks>
static class ResponseBodies {
    /// <summary>
    ///     The names the envelope owns. A body member with one of these names is skipped rather than
    ///     written a second time.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>location</c> is the one that is actually both.</b> Every published type declares
    ///     <c>/location</c> as a required, immutable body property — that is how a caller states it
    ///     on a write — and the write path copies the value into
    ///     <see cref="ResourceSnapshot.Location" /> at the same time, so the projected body carries
    ///     it too. The envelope's copy is the one served: it is the manager's own record, the value
    ///     placement and the resource-graph projection read, and it is what the Azure envelope puts
    ///     beside <c>id</c>, <c>name</c> and <c>type</c>. The other five are here so that the rule is
    ///     a rule rather than a special case, and so a schema that ever declared <c>/etag</c> could
    ///     not make the response carry two.
    /// </remarks>
    static readonly HashSet<string> EnvelopeMembers = new(StringComparer.Ordinal) {
        "id", "name", "type", "location", "provisioningState", "etag", "tags"
    };
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
    ///         ⚠
    ///         <b>
    ///             Each element is the same object <see cref="Resource" /> writes, member for
    ///             member.
    ///         </b> A list that rendered a thinner resource than a <c>GET</c> does would make
    ///         a generated SDK's collection type and its resource type two different shapes with one
    ///         name, and the first place anybody would notice is a deserializer dropping a field.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>nextLink</c> is omitted rather than written as <c>null</c> or <c>""</c> when
    ///             there is no next page.
    ///         </b> That is the Azure shape an <c>AsyncPageable&lt;T&gt;</c>
    ///         stops on; an empty string is a URL a polite client will happily request.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             There is no <c>count</c>, and the omission is the security property rather than
    ///             an unfinished feature.
    ///         </b> The page holds what the caller may read — see
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
    ///     <para>
    ///         ⚠ One writer for both callers. Two copies would be two chances for a resource read on
    ///         its own and the same resource inside a listing to disagree about their own shape.
    ///     </para>
    ///     <para>
    ///         The envelope first — <c>id</c>, <c>name</c>, <c>type</c>, <c>location</c>,
    ///         <c>provisioningState</c>, <c>etag</c> — then every member of the projected body that
    ///         the envelope does not already own, then <c>tags</c>. <c>location</c> is omitted when
    ///         the manager holds none, as <see cref="Scope" /> omits it, rather than served as
    ///         <c>""</c>: an empty string is not a region, and the published document lists no type
    ///         without one. Nothing is invented on the body's behalf either — a projection with no
    ///         <c>properties</c> member serves none, which is what the document, where
    ///         <c>properties</c> is optional, already allows.
    ///     </para>
    /// </remarks>
    static void WriteResource(Utf8JsonWriter writer, ResourceSnapshot snapshot) {
        writer.WriteStartObject();
        writer.WriteString("id", snapshot.Path);
        writer.WriteString("name", snapshot.Name);
        writer.WriteString("type", snapshot.Type);

        if (snapshot.Location.Length > 0) {
            writer.WriteString("location", snapshot.Location);
        }

        writer.WriteString("provisioningState", snapshot.ProvisioningState.ToString());
        writer.WriteString("etag", snapshot.Etag);
        WriteBodyMembers(writer, snapshot.Body);

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

    /// <summary>
    ///     A role assignment, in Azure's envelope: <c>id</c>, <c>name</c>, <c>type</c> and a
    ///     <c>properties</c> object carrying <c>scope</c>, <c>principalId</c>, <c>principalType</c>
    ///     and <c>roleDefinitionId</c>.
    /// </summary>
    /// <param name="assignment">The assignment as the manager rendered it.</param>
    /// <remarks>
    ///     ⚠ The property names are <c>RoleAssignmentBodyProperties</c>' — the same three a
    ///     <c>PUT</c> body may carry — so a client can read a <c>GET</c> back and send it as a
    ///     <c>PUT</c> unchanged. <c>roleDefinitionId</c> is a role <i>name</i>; there are no role
    ///     definitions to address, and that class's remarks say why.
    /// </remarks>
    public static string RoleAssignment(RoleAssignmentSnapshot assignment) {
        ArgumentNullException.ThrowIfNull(assignment);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer)) {
            WriteRoleAssignment(writer, assignment);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Renders a page of role assignments, in the same <c>{ "value": [ … ], "nextLink": … }</c>
    ///     shape as <see cref="Collection" />.
    /// </summary>
    /// <param name="page">The page the manager built.</param>
    /// <param name="nextLink">The absolute next-page URL, or empty when there is no next page.</param>
    /// <remarks>
    ///     ⚠ Each element is the same object <see cref="RoleAssignment" /> writes, member for
    ///     member, and <c>nextLink</c> is omitted rather than written empty — both for the reasons
    ///     <see cref="Collection" /> gives. There is no <c>count</c> either, though here the reason
    ///     is symmetry with the other collection rather than an oracle: a caller who may read this
    ///     page may read all of it.
    /// </remarks>
    public static string RoleAssignments(RoleAssignmentPage page, string nextLink) {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(nextLink);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteStartArray();

            foreach (var assignment in page.Assignments) {
                WriteRoleAssignment(writer, assignment);
            }

            writer.WriteEndArray();

            if (nextLink.Length > 0) {
                writer.WriteString("nextLink", nextLink);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The one role assignment object, written into whichever document is being built.</summary>
    /// <remarks>
    ///     ⚠ <c>inherited</c> is written on every row, <c>false</c> included, so a generated client
    ///     reads one shape. When it is <c>true</c>, <c>id</c> and <c>properties.scope</c> are the
    ///     ancestor's — the tuple's own address — and not the scope the listing was asked at;
    ///     <c>IRoleAssignmentManager.ListAsync</c>'s remarks say why.
    /// </remarks>
    static void WriteRoleAssignment(Utf8JsonWriter writer, RoleAssignmentSnapshot assignment) {
        writer.WriteStartObject();
        writer.WriteString("id", assignment.Path);
        writer.WriteString("name", assignment.Name);
        writer.WriteString("type", RoleAssignmentId.TypeName);
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteString("scope", assignment.Scope);
        writer.WriteString(RoleAssignmentBodyProperties.PrincipalId, assignment.PrincipalId);
        writer.WriteString(RoleAssignmentBodyProperties.PrincipalType, assignment.PrincipalType);
        writer.WriteString(RoleAssignmentBodyProperties.RoleDefinitionId, assignment.RoleDefinitionId);
        writer.WriteBoolean("inherited", assignment.Inherited);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Renders a scope — docs/plan/06 § The hierarchy's subscription or resource group.</summary>
    /// <param name="scope">The scope as the manager reports it.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The same four top-level names a resource carries — <c>id</c>, <c>name</c>,
    ///         <c>type</c>, <c>location</c> — and deliberately no <c>provisioningState</c> and no
    ///         <c>etag</c>.
    ///     </b> Azure's own resource group renders exactly that shape, and a client that
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
    ///     docs/plan/10 § Long-running operations:
    ///     <i>
    ///         "The <c>progress</c> array is our addition and
    ///         it is what makes a nine-minute cluster creation tolerable."
    ///     </i> A caller staring at
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

    /// <summary>
    ///     Splices the projected body's members into the object being written, raw, skipping the
    ///     names in <see cref="EnvelopeMembers" />.
    /// </summary>
    /// <param name="writer">A writer positioned inside the resource object.</param>
    /// <param name="body">The projected body, as the grain rendered it.</param>
    static void WriteBodyMembers(Utf8JsonWriter writer, string body) {
        if (body.Length == 0) {
            return;
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(body);
        } catch (JsonException) {
            // Grain state that is not JSON is a platform fault, not a caller's. The envelope alone
            // keeps the response parseable; the fault goes to the trace, never to the body.
            return;
        }

        using (document) {
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return;
            }

            foreach (var member in document.RootElement.EnumerateObject()) {
                if (EnvelopeMembers.Contains(member.Name)) {
                    continue;
                }

                member.WriteTo(writer);
            }
        }
    }
}
