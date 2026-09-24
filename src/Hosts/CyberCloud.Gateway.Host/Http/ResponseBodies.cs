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
    ///     not make the response carry two — <c>OpenApiEmitter</c> refuses such a schema at
    ///     generation since issue #85, for the same reason from the other side: the provider's value
    ///     would be accepted on a write and never served.
    ///     <para>
    ///         ⚠ Since issue #85 the published document declares these members too — every type's
    ///         schema <c>allOf</c>s a shared <c>Resource</c> component and repeats the five the
    ///         server owns as <c>readOnly</c> — and
    ///         <c>ServedShapesMatchTheDocumentTests.AReadValidatesAgainstTheGet200</c> validates
    ///         what this writer produces against it. Until then the document forbade five of the
    ///         eight members this class writes.
    ///     </para>
    /// </remarks>
    static readonly HashSet<string> EnvelopeMembers = new(StringComparer.Ordinal) {
        "id",
        "name",
        "type",
        "location",
        "provisioningState",
        "etag",
        "tags"
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
    ///     <c>properties</c> object carrying <c>scope</c>, <c>principalId</c>, <c>principalType</c>,
    ///     <c>roleDefinitionId</c> and <c>expiresOn</c>.
    /// </summary>
    /// <param name="assignment">The assignment as the manager rendered it.</param>
    /// <remarks>
    ///     ⚠ The property names are <c>RoleAssignmentBodyProperties</c>' — the same four a
    ///     <c>PUT</c> body may carry — so a client can read a <c>GET</c> back and send it as a
    ///     <c>PUT</c> unchanged, a just-in-time grant's end included. <c>roleDefinitionId</c> is a role <i>name</i>; there are no role
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

    /// <summary>
    ///     Renders a policy definition or assignment — <c>{ "id", "name", "type", "properties" }</c>,
    ///     Azure's shape for both. docs/plan/08 § Policy.
    /// </summary>
    /// <param name="snapshot">The object the policy manager returned.</param>
    public static string PolicyObject(PolicyObjectSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            WritePolicyObject(writer, snapshot);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Renders a page of policy definitions, assignments or compliance states in the collection
    ///     shape every listing of this API has.
    /// </summary>
    /// <param name="page">The page the policy manager built.</param>
    /// <param name="nextLink">The absolute next-page URL, or empty when there is no next page.</param>
    /// <remarks>
    ///     ⚠ A state row is Azure's <c>policyStates</c> shape cut to what this platform records:
    ///     <c>resourceId</c>, <c>resourceType</c>, <c>policyAssignmentId</c>,
    ///     <c>policyDefinitionId</c>, <c>complianceState</c> and <c>timestamp</c> — the time the verdict
    ///     last <i>changed</i>, which <c>PolicyStateRecord.Since</c> explains.
    /// </remarks>
    public static string PolicyPage(PolicyListPage page, string nextLink) {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(nextLink);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteStartArray();

            foreach (var snapshot in page.Objects) {
                WritePolicyObject(writer, snapshot);
            }

            foreach (var state in page.States) {
                writer.WriteStartObject();
                writer.WriteString("resourceId", state.ResourcePath);
                writer.WriteString("resourceType", state.ResourceType);
                writer.WriteString("policyAssignmentId", state.AssignmentPath);
                writer.WriteString("policyDefinitionId", state.DefinitionPath);
                writer.WriteString("complianceState", state.State.ToString());
                writer.WriteString("timestamp", state.Since);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            if (nextLink.Length > 0) {
                writer.WriteString("nextLink", nextLink);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void WritePolicyObject(Utf8JsonWriter writer, PolicyObjectSnapshot snapshot) {
        writer.WriteStartObject();
        writer.WriteString("id", snapshot.Path);
        writer.WriteString("name", snapshot.Name);
        writer.WriteString("type", snapshot.Type);
        writer.WritePropertyName("properties");

        // The manager rendered the properties object from the stored record; it is written through
        // verbatim rather than re-parsed member by member, so this file cannot drift from it.
        writer.WriteRawValue(snapshot.Properties.Length == 0 ? "{}" : snapshot.Properties);
        writer.WriteEndObject();
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

        // Written on every row, null included, for the reason `inherited` is: one shape for a
        // generated client. UTC and round-trippable, so a GET sent back as a PUT sets the same
        // instant — issue #49.
        if (assignment.ExpiresOn is { } expiresOn) {
            writer.WriteString(RoleAssignmentBodyProperties.ExpiresOn, expiresOn.ToUniversalTime());
        } else {
            writer.WriteNull(RoleAssignmentBodyProperties.ExpiresOn);
        }

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
            WriteScope(writer, scope);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Renders a page of scopes — a tenant's subscriptions or a subscription's resource groups —
    ///     in the same <c>{ "value": [ … ], "nextLink": … }</c> shape as <see cref="Collection" />.
    /// </summary>
    /// <param name="page">The page the scope manager built.</param>
    /// <param name="nextLink">The absolute next-page URL, or empty when there is no next page.</param>
    /// <remarks>
    ///     ⚠ Each element is the same object <see cref="Scope" /> writes, member for member — one
    ///     writer for both, for the reason <see cref="Collection" /> shares <c>WriteResource</c> —
    ///     and <c>nextLink</c> is omitted rather than written empty. There is no <c>count</c>, and
    ///     here the omission is the oracle again: the page holds what the caller may read, and a
    ///     subscription id leaks more than a resource name because it is the billing boundary.
    /// </remarks>
    public static string ScopeCollection(ScopeListPage page, string nextLink) {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(nextLink);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            writer.WriteStartArray();

            foreach (var scope in page.Items) {
                WriteScope(writer, scope);
            }

            writer.WriteEndArray();

            if (nextLink.Length > 0) {
                writer.WriteString("nextLink", nextLink);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    ///     Renders a page of a resource graph query in the same <c>{ "value": [ … ], "nextLink": … }</c>
    ///     shape as <see cref="Collection" />, plus the result's <c>columns</c>.
    ///     docs/plan/08 § The resource-graph projection.
    /// </summary>
    /// <param name="page">The page the query service built.</param>
    /// <param name="nextLink">The absolute next-page URL, or empty when there is no next page.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Each element is the row as ClickHouse rendered it, written raw</b> — the shape is
    ///         the query's (<c>ResourceGraphQueryPage.Rows</c>'s remarks), so there is no object this
    ///         renderer could type. <c>columns</c> is the one addition to the envelope: a
    ///         <c>project</c>'s result has no <c>type</c> member to tell a table renderer what it is
    ///         looking at, and Azure Resource Graph's response carries the same list for the same
    ///         reason. It is written before <c>value</c> so a streaming reader knows the shape before
    ///         the rows.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No <c>count</c> and no <c>totalRecords</c></b>, Azure's or anyone's: the page
    ///         holds what the caller may read, and a total would say how many rows exist that they
    ///         may not — the same oracle <see cref="Collection" /> declines to open.
    ///     </para>
    /// </remarks>
    public static string ResourceGraphPage(ResourceGraphQueryPage page, string nextLink) {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(nextLink);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("columns");
            writer.WriteStartArray();

            foreach (var column in page.Columns) {
                writer.WriteStartObject();
                writer.WriteString("name", column.Name);
                writer.WriteString("type", column.Type);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("value");
            writer.WriteStartArray();

            foreach (var row in page.Rows) {
                // ⚠ Raw, and trusted: the text came from ClickHouse's JSON format, which
                // ResourceGraphQueryService already parsed once to slice the page.
                writer.WriteRawValue(row, false);
            }

            writer.WriteEndArray();

            if (nextLink.Length > 0) {
                writer.WriteString("nextLink", nextLink);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The one scope object, written into whichever document is being built.</summary>
    static void WriteScope(Utf8JsonWriter writer, ScopeSnapshot scope) {
        writer.WriteStartObject();
        writer.WriteString("id", scope.Path);
        writer.WriteString("name", scope.Name);
        writer.WriteString("type", scope.Type);

        if (scope.Location.Length > 0) {
            writer.WriteString("location", scope.Location);
        }

        // Absent rather than empty, as `location` is: a client tests for the property. Present on a
        // subscription in a group and on a nested group; absent for the tenant root and for the two
        // kinds that never hang off a group — issue #39.
        if (scope.ManagementGroup.Length > 0) {
            writer.WriteString(ScopeBodyProperties.ManagementGroup, scope.ManagementGroup);
        }

        writer.WriteEndObject();
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
