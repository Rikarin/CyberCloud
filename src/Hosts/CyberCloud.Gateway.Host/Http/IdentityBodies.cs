using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Http;

/// <summary>
///     The bodies of the identity administration API — the one request body it reads, an
///     application registration, and every object it answers. Issue #41, with #43's invitation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Azure's envelope, as for a role assignment:</b> <c>id</c> is the object's own
///         address, <c>name</c> its id in the <c>N</c> form, <c>type</c>
///         <c>CyberCloud.Identity/{collection}</c>, and the rest under <c>properties</c>. A
///         collection is <c>{ "value": [ … ] }</c> with no <c>nextLink</c>: every list here is one
///         directory index, capped at <c>DirectoryIndexPolicy.MaxEntries</c>, and read whole.
///     </para>
///     <para>
///         ⚠ <b>No secret is ever rendered except in the one answer that issues it.</b> An
///         invitation is written without its link, an application without its secret, and a
///         session without its refresh handle. <see cref="ApplicationRegistered" /> is the single
///         exception, and the dispatcher marks that response <c>no-store</c>.
///     </para>
/// </remarks>
static class IdentityBodies {
    /// <summary>The members of a registration body.</summary>
    public const string DisplayNameMember = "displayName";

    /// <summary>The redirect URIs, an array of strings.</summary>
    public const string RedirectUrisMember = "redirectUris";

    /// <summary>The scopes, an array of strings.</summary>
    public const string ScopesMember = "scopes";

    /// <summary>Whether the client is public, a Boolean — required, never assumed.</summary>
    public const string PublicClientMember = "publicClient";

    /// <summary>
    ///     The registration a <c>POST</c> on the applications collection asks for, or the refusal
    ///     that says what the body should be.
    /// </summary>
    /// <param name="body">The request body, as the pipeline buffered it.</param>
    /// <remarks>
    ///     ⚠ <c>publicClient</c> is required. A default either way is a guess about the caller's
    ///     threat model: assume confidential and a SPA's registration mints a secret it will ship to
    ///     every browser; assume public and a server loses the secret that makes a stolen code
    ///     useless. What the values may be is the application grain's to judge; this reads their
    ///     types.
    /// </remarks>
    public static Result<ApplicationDraft> ApplicationDraft(string body) {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Length == 0) {
            return DraftShape();
        }

        try {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(DisplayNameMember, out var name)
                || name.ValueKind != JsonValueKind.String
                || !root.TryGetProperty(PublicClientMember, out var isPublic)
                || isPublic.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || Strings(root, RedirectUrisMember) is not { } redirectUris
                || Strings(root, ScopesMember) is not { } scopes) {
                return DraftShape();
            }

            return Result<ApplicationDraft>.Success(
                new() {
                    DisplayName = name.GetString()!,
                    RedirectUris = redirectUris,
                    Scopes = scopes,
                    IsPublicClient = isPublic.GetBoolean()
                }
            );
        } catch (JsonException exception) {
            return Result<ApplicationDraft>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The request body is not valid JSON: {exception.Message}"
            );
        }
    }

    /// <summary>An invitation — its id, the user it created, and where it stands (#43, #41).</summary>
    /// <param name="invitation">The invitation as the manager reports it.</param>
    /// <remarks>
    ///     ⚠ <b>Never the link.</b> Its secret went to the invited address and nowhere else; a
    ///     response that echoed it would hand the inviter a way to accept on the invitee's behalf.
    ///     <c>userId</c> is here because it is the principal a role is granted to next —
    ///     <c>PUT {scope}/providers/CyberCloud.Authorization/roleAssignments/reader-user-{userId}</c>.
    /// </remarks>
    public static string Invitation(InvitationSnapshot invitation) => Write(writer => WriteInvitation(writer, invitation));

    /// <summary>The tenant's invitations.</summary>
    /// <param name="invitations">The invitations, as the directory answered them.</param>
    public static string Invitations(IReadOnlyList<InvitationSnapshot> invitations) =>
        Collection(invitations, WriteInvitation);

    /// <summary>A member, as removing one answers.</summary>
    /// <param name="tenantId">The tenant, for the member's address.</param>
    /// <param name="member">The member.</param>
    public static string Member(Guid tenantId, MemberSnapshot member) => Write(writer => WriteMember(writer, tenantId, member));

    /// <summary>The tenant's members.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="members">The members.</param>
    public static string Members(Guid tenantId, IReadOnlyList<MemberSnapshot> members) =>
        Collection(members, (writer, member) => WriteMember(writer, tenantId, member));

    /// <summary>A registered client, without its secret.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="application">The client.</param>
    public static string Application(Guid tenantId, ApplicationSnapshot application) =>
        Write(writer => WriteApplication(writer, tenantId, application, string.Empty));

    /// <summary>
    ///     A client just registered or rotated — the one answer with <c>properties.clientSecret</c>,
    ///     for a confidential client.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="registered">The client and its new secret.</param>
    public static string ApplicationRegistered(Guid tenantId, ApplicationRegistered registered) {
        ArgumentNullException.ThrowIfNull(registered);

        return Write(writer => WriteApplication(writer, tenantId, registered.Application, registered.ClientSecret));
    }

    /// <summary>The tenant's registered clients, without their secrets.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="applications">The clients.</param>
    public static string Applications(Guid tenantId, IReadOnlyList<ApplicationSnapshot> applications) =>
        Collection(applications, (writer, application) => WriteApplication(writer, tenantId, application, string.Empty));

    /// <summary>The caller's sessions.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="sessions">The sessions.</param>
    public static string Sessions(Guid tenantId, IReadOnlyList<SessionSnapshot> sessions) =>
        Collection(sessions, (writer, session) => WriteSession(writer, tenantId, session));

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    static void Envelope(Utf8JsonWriter writer, IdentityAddress item) {
        writer.WriteString("id", item.Path);
        writer.WriteString("name", N(item.Id));
        writer.WriteString("type", item.ItemType);
    }

    static void WriteInvitation(Utf8JsonWriter writer, InvitationSnapshot invitation) {
        ArgumentNullException.ThrowIfNull(invitation);

        writer.WriteStartObject();
        Envelope(writer, IdentityAddress.Invitations(invitation.TenantId).Item(invitation.InvitationId));
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteString("email", invitation.Email);
        writer.WriteString("userId", N(invitation.UserId));
        writer.WriteString("status", invitation.Status);
        writer.WriteString("expiresAt", invitation.ExpiresAt);

        if (invitation.InvitedBy != Guid.Empty) {
            writer.WriteString("invitedBy", N(invitation.InvitedBy));
        }

        if (invitation.Sendings > 0) {
            writer.WriteString("sentAt", invitation.SentAt);
            writer.WriteNumber("sendings", invitation.Sendings);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    static void WriteMember(Utf8JsonWriter writer, Guid tenantId, MemberSnapshot member) {
        ArgumentNullException.ThrowIfNull(member);

        writer.WriteStartObject();
        Envelope(writer, IdentityAddress.Members(tenantId).Item(member.UserId));
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteString("email", member.Email);
        writer.WriteString("displayName", member.DisplayName);
        writer.WriteString("status", member.Status);
        writer.WriteString("createdAt", member.CreatedAt);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    static void WriteApplication(Utf8JsonWriter writer, Guid tenantId, ApplicationSnapshot application, string secret) {
        ArgumentNullException.ThrowIfNull(application);

        writer.WriteStartObject();
        Envelope(writer, IdentityAddress.Applications(tenantId).Item(application.ApplicationId));
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteString("clientId", application.ClientId);
        writer.WriteString(DisplayNameMember, application.DisplayName);
        WriteStrings(writer, RedirectUrisMember, application.RedirectUris);
        WriteStrings(writer, ScopesMember, application.Scopes);
        writer.WriteBoolean(PublicClientMember, application.IsPublicClient);
        writer.WriteString("createdAt", application.CreatedAt);

        if (application.ClientSecretIssuedAt is { } issued) {
            writer.WriteString("clientSecretIssuedAt", issued);
        }

        if (secret.Length > 0) {
            writer.WriteString("clientSecret", secret);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    static void WriteSession(Utf8JsonWriter writer, Guid tenantId, SessionSnapshot session) {
        ArgumentNullException.ThrowIfNull(session);

        writer.WriteStartObject();
        Envelope(writer, IdentityAddress.Sessions(tenantId).Item(session.SessionId));
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteString("clientId", session.ClientId);
        writer.WriteString("deviceLabel", session.DeviceLabel);
        writer.WriteString("createdAt", session.CreatedAt);
        writer.WriteString("lastUsedAt", session.LastUsedAt);
        WriteStrings(writer, "methods", session.Methods);
        writer.WriteBoolean("current", session.IsCurrent);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values) {
        writer.WritePropertyName(name);
        writer.WriteStartArray();

        foreach (var value in values) {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    static string Collection<T>(IReadOnlyList<T> items, Action<Utf8JsonWriter, T> writeItem) =>
        Write(writer => {
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                writer.WriteStartArray();

                foreach (var item in items) {
                    writeItem(writer, item);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        );

    static string Write(Action<Utf8JsonWriter> write) {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer)) {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static List<string>? Strings(JsonElement root, string member) {
        if (!root.TryGetProperty(member, out var array) || array.ValueKind != JsonValueKind.Array) {
            return null;
        }

        List<string> values = [];

        foreach (var element in array.EnumerateArray()) {
            if (element.ValueKind != JsonValueKind.String) {
                return null;
            }

            values.Add(element.GetString()!);
        }

        return values;
    }

    static Result<ApplicationDraft> DraftShape() =>
        Result<ApplicationDraft>.Failure(
            ErrorCode.InvalidRequestBody,
            "An application is a JSON object with \"displayName\" (a string), \"redirectUris\" and \"scopes\" "
            + "(arrays of strings) and \"publicClient\" (true for a browser or native app, false for a server) — "
            + "{ \"displayName\": \"Acme dashboard\", \"redirectUris\": [\"https://acme.example/cb\"], "
            + "\"scopes\": [\"openid\", \"profile\"], \"publicClient\": true }."
        );
}
