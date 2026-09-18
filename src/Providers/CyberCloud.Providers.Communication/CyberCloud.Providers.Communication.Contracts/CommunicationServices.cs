using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Communication/services</c>: the type, its
///     api-version, its body shape, the grain it converges onto, and the four actions that send
///     through it and read back what happened.
/// </summary>
/// <remarks>
///     <para>
///         [17 § <c>CyberCloud.Communication/services</c>](../../../../docs/plan/17-communication-and-email.md)
///         · M2 · 2.0 EM. The <b>sending</b> product — <i>"an API a tenant calls"</i> — and not
///         <c>CyberCloud.Mail</c>, which is a server the platform runs. docs/plan/17 opens by
///         insisting the two are different products, and this family holds that line the same way
///         <c>CyberCloud.Communication.csproj</c> does.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE FIRST CLUSTERLESS FAMILY IN THE CATALOGUE, AND EVERYTHING UNUSUAL ABOUT IT
///             FOLLOWS FROM THAT.
///         </b> docs/plan/08 § What the resource manager deliberately does not do requires the manager
///         to <i>"work for a provider with no cluster at all (a DNS zone, a mail domain, a role
///         assignment)"</i>, and fourteen families later this is the first that takes it up on that.
///         No type here declares <c>RequiresCluster</c>, none names a chart, none renders an object:
///         what a reconciler converges is <b>grain state</b> in <c>CyberCloud.Communication</c>,
///         reached through <see cref="ICommunicationControlPlane" />, and what it reads back is the
///         same grain. The shared conformance suite grew a clusterless half to say what that proves —
///         see <c>IProviderCaseSource.ConvergedModule</c>.
///     </para>
///     <para>
///         ⚠ <b>The grain is keyed by the resource's <i>address</i>, not its GUID</b> —
///         <see cref="ServiceIdOf" />, and <see cref="CommunicationGrainKeys.ResourceIdFor" /> for
///         why. The consequence worth knowing at this level: the platform's own service —
///         <c>SiloIdentityOptions.ServiceId</c>, the one every OTP goes through — is the id this
///         function derives for whatever <c>services/{name}</c> resource the platform tenant creates
///         for itself, and an operator configures that value rather than a GUID read off a listing.
///     </para>
///     <para>
///         ⚠ <b>What this row does NOT build, in the order it matters.</b> No relay: the module ships
///         one carrier since #93 — email over SMTP, registered when the silo's
///         <c>CyberCloud:Communication:Smtp</c> names a relay, which the AppHost's does and no chart's
///         does — and every other channel resolves to <c>CyberCloud.Communication</c>'s refusing seam
///         unless a host registers a real <c>IChannelProvider</c>, so an SMS <c>send</c> today refuses
///         honestly rather than sending. No
///         <c>senders</c> type: docs/plan/17's sender-id registration flow has its grain
///         (<c>ISenderIdentityGrain</c>) and no resource surface, so
///         <c>ChannelConfiguration.SenderId</c> is always empty here. No inbound forwarding: a
///         <c>STOP</c> suppresses (the router does that) and is forwarded nowhere. Each is recorded
///         where the repository keeps owed things — <c>charts/bundle/bundle.yaml § owed</c>, because
///         a family with no chart has no <c>conformance.yaml</c> to carry them.
///     </para>
/// </remarks>
public static class CommunicationServices {
    /// <summary>The provider namespace, as docs/plan/17 spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.Communication";

    /// <summary>The root type. docs/plan/17 § <c>CyberCloud.Communication/services</c>.</summary>
    public const string TypePath = "services";

    /// <summary>The one api-version. ⚠ Immutable — adding a field is a new date.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>
    ///     A BCP 47 language tag, or empty. ⚠ Empty is allowed on purpose: the property's default is
    ///     empty, and a pattern that refused it would be a default no body could carry — the defect
    ///     <c>CyberCloud.Mail/domains</c> found at static construction.
    /// </summary>
    public const string OptionalLocalePattern = "([a-z]{2,3}(-[A-Za-z0-9]{2,8})*)?";

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     One property beyond the platform's own, and it is the one thing a service holds that is
    ///     not a channel, a template or a suppression: the locale a send falls back to. Channels,
    ///     templates and suppressions are child types, because each is a thing a tenant adds and
    ///     removes on its own and this platform's schema model has no array of objects to hold them
    ///     in — see the remarks on <c>SchemaKind.Array</c>.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the service is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The service's own settings."),
                new(
                    "/properties/defaultLocale",
                    SchemaKind.Text,
                    Description: "The locale a send is rendered in when the request names none — a BCP 47 "
                    + "tag such as cs-CZ. Empty falls back to en, then to whichever body the template "
                    + "has first."
                ) { Pattern = OptionalLocalePattern, MaxLength = 35, DefaultJson = "\"\"", ExampleJson = "\"en-GB\"" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="defaultLocale">The fallback locale, or empty.</param>
    /// <param name="location">The region.</param>
    public static string Body(string defaultLocale = "", string location = "eu-central") =>
        new JsonObject {
            ["location"] = location, ["properties"] = new JsonObject { ["defaultLocale"] = defaultLocale }
        }.ToJsonString();

    /// <summary>The fallback locale a body asks for.</summary>
    public static string DefaultLocaleOf(JsonElement desired) => Bodies.Text(Bodies.Property(desired, "defaultLocale"), string.Empty);

    // ── The grain a resource converges onto ────────────────────────────────────────────────────

    /// <summary>
    ///     The id of the service grains — <c>ICommunicationServiceGrain</c>,
    ///     <c>ISuppressionListGrain</c>, <c>ISendLimitGrain</c> — for a service or anything under it.
    /// </summary>
    /// <param name="id">The service, or a channel, template or suppression beneath one.</param>
    /// <remarks>
    ///     ⚠ Walks up to the <c>services</c> level first, so a child's reconciler and its parent's
    ///     derive the same id from different addresses. <see cref="CommunicationGrainKeys.ResourceIdFor" />
    ///     says why the id is derived from the address at all.
    /// </remarks>
    public static Guid ServiceIdOf(ResourceId id) {
        var service = id;
        while (service.Parent is { } parent) {
            service = parent;
        }

        return CommunicationGrainKeys.ResourceIdFor(service.TenantId, service.CanonicalPath);
    }

    /// <summary>Whether what the grain holds is what the body asks for.</summary>
    /// <param name="service">The grain's snapshot.</param>
    /// <param name="id">The resource the snapshot should describe.</param>
    /// <param name="desired">The desired body.</param>
    public static bool Matches(CommunicationService service, ResourceId id, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(service);

        return string.Equals(service.Name, id.Name, StringComparison.Ordinal)
            && string.Equals(service.DefaultLocale, DefaultLocaleOf(desired), StringComparison.Ordinal);
    }

    // ── Actions ────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>POST …/services/{name}/send</c>. One message, through this service.</summary>
    /// <remarks>
    ///     ⚠ <b>An action rather than a <c>messages</c> resource type, and the reason is what a
    ///     resource is.</b> A resource is desired state the platform converges and re-converges; a
    ///     message is an event that happened once. Modelling one as a resource would give it a PUT
    ///     whose second body could never be applied — the grain answers <c>Conflict</c> to a second
    ///     message under one idempotency key, deliberately — and a delete that could not un-send.
    ///     The idempotency the resource shape would have offered is already the grain's: the same
    ///     <c>idempotencyKey</c> answers with the same message and calls no carrier twice.
    /// </remarks>
    public const string SendAction = "send";

    /// <summary><c>POST …/services/{name}/status</c>. Where a message got to, receipts included.</summary>
    public const string StatusAction = "status";

    /// <summary><c>POST …/services/{name}/checkSuppression</c>. Whether one address is on the list.</summary>
    public const string CheckSuppressionAction = "checkSuppression";

    /// <summary><c>POST …/services/{name}/listSuppressions</c>. The whole list, bounces and complaints included.</summary>
    /// <remarks>
    ///     ⚠ The <c>suppressions</c> child type is the tenant's <i>own</i> entries — the manual
    ///     blocks. Hard bounces, complaints and opt-outs arrive through delivery receipts and inbound
    ///     messages and are not resources, so without this action a tenant could see only the half
    ///     of the list they wrote themselves. <c>ISuppressionListGrain</c>'s remarks make
    ///     enumeration a first-class operation for exactly this reason.
    /// </remarks>
    public const string ListSuppressionsAction = "listSuppressions";

    /// <summary>A name-and-value pair as one string: <c>name=value</c>. The value may be empty.</summary>
    /// <remarks>
    ///     ⚠ A string rather than an object because this platform's schema model has no array of
    ///     objects — see the remarks on <c>SchemaKind.Array</c> — and a template argument is a pair.
    ///     Everything after the first <c>=</c> is the value, verbatim, so a value may itself contain
    ///     one. The name is a C identifier, which is also what a template's <c>{name}</c> placeholder
    ///     accepts.
    /// </remarks>
    public const string ArgumentPattern = @"[A-Za-z_][A-Za-z0-9_]*=[\s\S]*";

    /// <summary>The most characters a destination may carry. An email address's own maximum is 320.</summary>
    public const int DestinationMaxLength = 320;

    /// <summary>The most characters a free-text body may carry.</summary>
    public const int BodyMaxLength = 65_536;

    /// <summary>What a <c>send</c> carries.</summary>
    public static ResourceSchema SendRequest { get; } =
        ResourceSchema.Of(
            [
                new("/channel", SchemaKind.Text, Required: true, Description: "Which channel to send on.") {
                    AllowedValues = ChannelKinds.AllowedValues
                },
                new(
                    "/to",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The recipient — an E.164 number for sms, whatsapp and voice, an address for "
                    + "email, a device token for push."
                ) { MinLength = 1, MaxLength = DestinationMaxLength, ExampleJson = "\"+420777123456\"" },
                new(
                    "/idempotencyKey",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The caller's key for this message. A retry carrying the same key returns the "
                    + "message already sent and calls no carrier; derive it from the thing being notified "
                    + "about, never from the attempt."
                ) { MinLength = 1, MaxLength = 200, ExampleJson = "\"invoice-2026-0042-issued\"" },
                new(
                    "/template",
                    SchemaKind.Text,
                    Description: "The name of a template under this service to render, or empty to send body as "
                    + "written. WhatsApp requires one."
                ) { MaxLength = 63, DefaultJson = "\"\"" },
                new(
                    "/templateVersion",
                    SchemaKind.WholeNumber,
                    Description: "Which version of the template, or 0 for the newest one a send may use."
                ) { Minimum = 0, DefaultJson = "0" },
                new(
                    "/locale",
                    SchemaKind.Text,
                    Description: "The locale to render in, or empty for the service's default."
                ) { Pattern = OptionalLocalePattern, MaxLength = 35, DefaultJson = "\"\"" },
                new(
                    "/arguments",
                    SchemaKind.Array,
                    Description: "The template's arguments, one name=value per element."
                ) { ElementKind = SchemaKind.Text, Pattern = ArgumentPattern, ExampleJson = "[\"code=482913\"]" },
                new(
                    "/body",
                    SchemaKind.Text,
                    Description: "The message text, when no template is named."
                ) { MaxLength = BodyMaxLength, DefaultJson = "\"\"" }
            ]
        );

    /// <summary>What a <c>status</c> carries: the key the message was sent under.</summary>
    public static ResourceSchema StatusRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/idempotencyKey",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The key the message was sent under."
                ) { MinLength = 1, MaxLength = 200 }
            ]
        );

    /// <summary>The message statuses a response spells, lower-cased <see cref="MessageStatus" />.</summary>
    public static ImmutableArray<string> StatusValues { get; } = ["queued", "dispatched", "delivered", "failed", "refused"];

    /// <summary>What <c>send</c> and <c>status</c> both return: the message as the platform holds it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>/receipts</c> is one line of text per receipt, and that is a schema limit rather
    ///     than a design.</b> A delivery receipt is a record — a status, the carrier's own status
    ///     word, a timestamp, a detail — and this platform's schema model has no array of objects, so
    ///     each is rendered as <c>{occurredAt} {status} {providerStatus}: {detail}</c> by
    ///     <see cref="ReceiptLine" />. The structured form is owed to the api-version that grows the
    ///     tree; it is recorded as such, and the line is stable enough to grep in the meantime.
    /// </remarks>
    public static ResourceSchema MessageResponse { get; } =
        ResourceSchema.Of(
            [
                new("/messageId", SchemaKind.Text, Required: true, Description: "The platform's id for the message.") {
                    Format = SchemaFormat.Uuid
                },
                new("/status", SchemaKind.Text, Required: true, Description: "Where the message is in its life.") {
                    AllowedValues = StatusValues
                },
                new("/channel", SchemaKind.Text, Required: true, Description: "The channel it went on.") {
                    AllowedValues = ChannelKinds.AllowedValues
                },
                new("/to", SchemaKind.Text, Required: true, Description: "The recipient, normalized."),
                new("/provider", SchemaKind.Text, Description: "Which carrier implementation served it."),
                new("/providerMessageId", SchemaKind.Text, Description: "The carrier's own id, once it has one."),
                new("/queuedAt", SchemaKind.Text, Required: true, Description: "When the platform accepted it.") {
                    Format = SchemaFormat.DateTime
                },
                new("/dispatchedAt", SchemaKind.Text, Description: "When a carrier accepted it. Absent until then.") {
                    Format = SchemaFormat.DateTime
                },
                new("/settledAt", SchemaKind.Text, Description: "When it was delivered, failed or refused. Absent until then.") {
                    Format = SchemaFormat.DateTime
                },
                new("/cost", SchemaKind.Number, Description: "What the carrier charged, once it said."),
                new("/currency", SchemaKind.Text, Description: "The currency of cost."),
                new("/detail", SchemaKind.Text, Description: "The last thing the platform or the carrier said about it."),
                new("/receiptCount", SchemaKind.WholeNumber, Required: true, Description: "How many delivery receipts have arrived."),
                new(
                    "/receipts",
                    SchemaKind.Array,
                    Description: "Every delivery receipt, oldest first, one line each: "
                    + "'{occurredAt} {status} {providerStatus}: {detail}'."
                ) { ElementKind = SchemaKind.Text }
            ]
        );

    /// <summary>What a <c>checkSuppression</c> carries.</summary>
    public static ResourceSchema CheckSuppressionRequest { get; } =
        ResourceSchema.Of(
            [
                new("/channel", SchemaKind.Text, Required: true, Description: "The channel the address would be sent on.") {
                    AllowedValues = ChannelKinds.AllowedValues
                },
                new("/destination", SchemaKind.Text, Required: true, Description: "The address, in any spelling.") {
                    MinLength = 1, MaxLength = DestinationMaxLength
                }
            ]
        );

    /// <summary>What a <c>checkSuppression</c> returns.</summary>
    public static ResourceSchema CheckSuppressionResponse { get; } =
        ResourceSchema.Of(
            [
                new("/suppressed", SchemaKind.Boolean, Required: true, Description: "Whether a send to it would be refused."),
                new("/reason", SchemaKind.Text, Description: "Why, when it is: hardBounce, complaint, optOut or manualBlock."),
                new("/suppressedAt", SchemaKind.Text, Description: "When it was suppressed.") { Format = SchemaFormat.DateTime },
                new("/note", SchemaKind.Text, Description: "The carrier's, the recipient's or the operator's words.")
            ]
        );

    /// <summary>What a <c>listSuppressions</c> carries: optionally, one channel.</summary>
    public static ResourceSchema ListSuppressionsRequest { get; } =
        ResourceSchema.Of(
            [
                // ⚠ Not AllowedValues, because the sixth value would be "" — an enum member with no
                // name, which a generated SDK cannot spell. The handler refuses anything that is
                // neither empty nor one of the five, with the five in the message.
                new(
                    "/channel",
                    SchemaKind.Text,
                    Description: "One of sms, whatsapp, email, push or voice, or empty for every channel."
                ) { MaxLength = 16, DefaultJson = "\"\"" }
            ]
        );

    /// <summary>What a <c>listSuppressions</c> returns.</summary>
    /// <remarks>
    ///     Text lines for the reason <see cref="MessageResponse" /> gives; <see cref="SuppressionLine" />
    ///     renders <c>{channel} {destination} {reason} {suppressedAt}: {note}</c>.
    /// </remarks>
    public static ResourceSchema ListSuppressionsResponse { get; } =
        ResourceSchema.Of(
            [
                new("/count", SchemaKind.WholeNumber, Required: true, Description: "How many entries are on the list."),
                new(
                    "/entries",
                    SchemaKind.Array,
                    Required: true,
                    Description: "Every entry, oldest first, one line each: "
                    + "'{channel} {destination} {reason} {suppressedAt}: {note}'."
                ) { ElementKind = SchemaKind.Text }
            ]
        );

    // ── Between the action bodies and the module's wire types ──────────────────────────────────

    /// <summary>The <see cref="CyberCloud.Communication.Contracts.SendRequest" /> a <c>send</c> body describes.</summary>
    /// <param name="serviceId">The service, from <see cref="ServiceIdOf" />.</param>
    /// <param name="body">The validated action body.</param>
    /// <returns>
    ///     <see cref="ErrorCode.InvalidRequestBody" /> for an argument with no <c>=</c>, which the
    ///     schema's pattern already refuses; kept here so the function is safe on any input.
    /// </returns>
    public static Result<SendRequest> ToSendRequest(Guid serviceId, JsonElement body) {
        var arguments = ParseArguments(Bodies.Root(body, "arguments"));
        if (arguments.TryGetError(out var malformed)) {
            return Result<SendRequest>.Failure(malformed);
        }

        return Result<SendRequest>.Success(
            new() {
                ServiceId = serviceId,
                Channel = ChannelKinds.Parse(Bodies.Text(Bodies.Root(body, "channel"), string.Empty)),
                Destination = Bodies.Text(Bodies.Root(body, "to"), string.Empty),
                TemplateName = Bodies.Text(Bodies.Root(body, "template"), string.Empty),
                TemplateVersion = (int)Bodies.Whole(Bodies.Root(body, "templateVersion"), 0),
                Locale = Bodies.Text(Bodies.Root(body, "locale"), string.Empty),
                Arguments = arguments.GetValueOrThrow(),
                Body = Bodies.Text(Bodies.Root(body, "body"), string.Empty),
                IdempotencyKey = Bodies.Text(Bodies.Root(body, "idempotencyKey"), string.Empty)
            }
        );
    }

    /// <summary>Splits <c>name=value</c> strings into arguments.</summary>
    /// <param name="element">The array, or <see langword="null" /> for none.</param>
    public static Result<ImmutableArray<TemplateArgument>> ParseArguments(JsonElement? element) {
        var parsed = ImmutableArray.CreateBuilder<TemplateArgument>();

        foreach (var pair in Bodies.Strings(element)) {
            var split = pair.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0) {
                return Result<ImmutableArray<TemplateArgument>>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{pair}' is not a template argument. Write name=value — everything after the "
                    + "first '=' is the value."
                );
            }

            parsed.Add(new() { Name = pair[..split], Value = pair[(split + 1)..] });
        }

        return Result<ImmutableArray<TemplateArgument>>.Success(parsed.ToImmutable());
    }

    /// <summary>The <see cref="MessageResponse" /> body for a snapshot.</summary>
    /// <param name="message">The message as the grain holds it.</param>
    public static string MessageJson(MessageSnapshot message) {
        ArgumentNullException.ThrowIfNull(message);

        var receipts = message.Receipts.IsDefault ? [] : message.Receipts;

        var json = new JsonObject {
            ["messageId"] = message.MessageId.ToString("D", CultureInfo.InvariantCulture),
            ["status"] = SpellStatus(message.Status),
            ["channel"] = ChannelKinds.Spell(message.Channel),
            ["to"] = message.Destination,
            ["provider"] = message.Provider,
            ["providerMessageId"] = message.ProviderMessageId,
            ["queuedAt"] = Bodies.Stamp(message.QueuedAt),
            ["cost"] = message.Cost,
            ["currency"] = message.Currency,
            ["detail"] = message.Detail,
            ["receiptCount"] = receipts.Length,
            ["receipts"] = new JsonArray([.. receipts.Select(x => (JsonNode?)ReceiptLine(x))])
        };

        // Absent rather than null: the response schema declares neither as nullable, and "not yet"
        // is what absence means on every surface the document reaches.
        if (message.DispatchedAt is { } dispatched) {
            json["dispatchedAt"] = Bodies.Stamp(dispatched);
        }

        if (message.SettledAt is { } settled) {
            json["settledAt"] = Bodies.Stamp(settled);
        }

        return json.ToJsonString();
    }

    /// <summary>One receipt, as <see cref="MessageResponse" />'s <c>/receipts</c> spells it.</summary>
    public static string ReceiptLine(DeliveryReceipt receipt) {
        ArgumentNullException.ThrowIfNull(receipt);

        return $"{Bodies.Stamp(receipt.OccurredAt)} {SpellStatus(receipt.Status)} {receipt.ProviderStatus}: {receipt.Detail}";
    }

    /// <summary>One entry, as <see cref="ListSuppressionsResponse" />'s <c>/entries</c> spells it.</summary>
    public static string SuppressionLine(SuppressionEntry entry) {
        ArgumentNullException.ThrowIfNull(entry);

        return $"{ChannelKinds.Spell(entry.Channel)} {entry.Destination} {SpellReason(entry.Reason)} "
            + $"{Bodies.Stamp(entry.SuppressedAt)}: {entry.Note}";
    }

    /// <summary>The <see cref="ListSuppressionsResponse" /> body for a list.</summary>
    public static string SuppressionListJson(ImmutableArray<SuppressionEntry> entries) {
        var list = entries.IsDefault ? [] : entries;

        return new JsonObject {
            ["count"] = list.Length, ["entries"] = new JsonArray([.. list.Select(x => (JsonNode?)SuppressionLine(x))])
        }.ToJsonString();
    }

    /// <summary>The <see cref="CheckSuppressionResponse" /> body for a check.</summary>
    public static string SuppressionCheckJson(SuppressionCheck check) {
        ArgumentNullException.ThrowIfNull(check);

        var json = new JsonObject { ["suppressed"] = check.IsSuppressed };

        if (check.Entry is { } entry) {
            json["reason"] = SpellReason(entry.Reason);
            json["suppressedAt"] = Bodies.Stamp(entry.SuppressedAt);
            json["note"] = entry.Note;
        }

        return json.ToJsonString();
    }

    /// <summary>The lower-cased status word a response carries.</summary>
    public static string SpellStatus(MessageStatus status) =>
        status switch {
            MessageStatus.Queued => "queued",
            MessageStatus.Dispatched => "dispatched",
            MessageStatus.Delivered => "delivered",
            MessageStatus.Failed => "failed",
            MessageStatus.Refused => "refused",
            _ => "queued"
        };

    /// <summary>The camel-cased reason a response carries.</summary>
    public static string SpellReason(SuppressionReason reason) =>
        reason switch {
            SuppressionReason.HardBounce => "hardBounce",
            SuppressionReason.Complaint => "complaint",
            SuppressionReason.OptOut => "optOut",
            SuppressionReason.ManualBlock => "manualBlock",
            _ => string.Empty
        };
}
