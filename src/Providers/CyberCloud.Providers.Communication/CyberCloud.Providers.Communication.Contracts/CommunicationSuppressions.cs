using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Communication/services/suppressions</c>: one
///     address a service must never send to, declared by the tenant.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/17 § The parts that are actually the work: <i>"Bounces, complaints, opt-outs —
///         per tenant, honoured before dispatch. Ignoring a complaint is how a sending domain gets
///         blocked."</i> This type is the fourth kind of entry, the one the sentence does not list:
///         a <see cref="SuppressionReason.ManualBlock" /> the tenant places themselves. The other
///         three arrive on their own — a hard bounce or a complaint through a delivery receipt, an
///         opt-out through an inbound <c>STOP</c> — and are not resources, because nobody PUTs them;
///         <c>CommunicationServices.ListSuppressionsAction</c> is where a tenant sees the whole list.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE ONE RULE THAT MAKES THIS TYPE SAFE TO EXPOSE: A RESOURCE ONLY EVER OWNS A MANUAL
///             BLOCK, AND NEVER DOWNGRADES A STRONGER ENTRY.
///         </b> The grain's <c>SuppressAsync</c> updates the reason on an address already listed.
///         Left alone, a tenant could PUT a suppression for an address that had complained, turning
///         the complaint into a manual block — and then DELETE the resource, which releases a manual
///         block. Two ordinary operations, one un-unsubscribed recipient. So the reconciler reads
///         first: an address already held for a complaint, an opt-out or a hard bounce is left
///         exactly as it is and reported converged — the address <i>is</i> suppressed, which is all
///         the body asked for — and the delete path releases only an entry whose reason is still
///         <see cref="SuppressionReason.ManualBlock" />. A stronger entry survives the resource
///         that happened to name it, and the delete succeeds without touching it.
///         <c>SuppressionEnforcementTests</c> pins both halves, and the second was sabotage-tested.
///     </para>
/// </remarks>
public static class CommunicationSuppressions {
    /// <summary>The type path, under <see cref="CommunicationServices.TypePath" />.</summary>
    public const string TypePath = CommunicationServices.TypePath + "/suppressions";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(CommunicationServices.ProviderNamespace, TypePath);

    /// <summary>The longest note. Long enough for a carrier's sentence, short enough not to be a document.</summary>
    public const int NoteMaxLength = 512;

    /// <summary>The body shape at <see cref="CommunicationServices.V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the suppression is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The suppression's own settings."),
                new(
                    "/properties/channel",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The channel the address is blocked on. Suppression is per channel: an "
                    + "email bounce says nothing about a phone number."
                ) { AllowedValues = ChannelKinds.AllowedValues, Immutable = true, ExampleJson = "\"email\"" },
                new(
                    "/properties/destination",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The address, in any spelling. Normalized before it is stored, so two "
                    + "spellings of one address are one entry."
                ) {
                    MinLength = 1,
                    MaxLength = CommunicationServices.DestinationMaxLength,
                    Immutable = true,
                    ExampleJson = "\"someone@example.com\""
                },
                new(
                    "/properties/note",
                    SchemaKind.Text,
                    Description: "Why, in the tenant's words. What a support case reads."
                ) { MaxLength = NoteMaxLength, DefaultJson = "\"\"", ExampleJson = "\"Asked us to stop by phone, 2026-09-01\"" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="channel">Which channel.</param>
    /// <param name="destination">The address.</param>
    /// <param name="note">Why.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        string channel = "email",
        string destination = "blocked@example.com",
        string note = "Asked us to stop.",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject { ["channel"] = channel, ["destination"] = destination, ["note"] = note }
        }.ToJsonString();

    /// <summary>The channel a body blocks on.</summary>
    public static ChannelKind ChannelOf(JsonElement desired) =>
        ChannelKinds.Parse(Bodies.Text(Bodies.Property(desired, "channel"), string.Empty));

    /// <summary>The address a body blocks, as written.</summary>
    public static string DestinationOf(JsonElement desired) => Bodies.Text(Bodies.Property(desired, "destination"), string.Empty);

    /// <summary>The note a body carries.</summary>
    public static string NoteOf(JsonElement desired) => Bodies.Text(Bodies.Property(desired, "note"), string.Empty);

    /// <summary>
    ///     Whether an entry the grain holds satisfies the body: the address is suppressed on the
    ///     channel, and — only for an entry this type owns — the note is the body's.
    /// </summary>
    /// <param name="entry">The entry the grain holds for the address.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ A complaint, an opt-out or a hard bounce on the address matches <i>regardless of the
    ///     note</i>. The body asked for the address to be suppressed and it is, for a reason the
    ///     tenant may not overwrite — see this type's remarks.
    /// </remarks>
    public static bool Matches(SuppressionEntry entry, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Channel != ChannelOf(desired)) {
            return false;
        }

        var normalized = Destinations.Normalize(entry.Channel, DestinationOf(desired));
        if (!normalized.TryGetValue(out var destination) || !string.Equals(entry.Destination, destination, StringComparison.Ordinal)) {
            return false;
        }

        return entry.Reason != SuppressionReason.ManualBlock
            || string.Equals(entry.Note, NoteOf(desired), StringComparison.Ordinal);
    }
}
