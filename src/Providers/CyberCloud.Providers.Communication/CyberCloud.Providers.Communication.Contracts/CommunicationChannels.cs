using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Communication.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Communication/services/channels</c>: one channel a
///     service sends on — which carrier, whose account, what it may spend.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/17 § The channel abstraction: <i>"A tenant's service resource selects a channel
///         and either uses the platform's account (marked-up, no setup) or their own credentials
///         (BYO, cheaper) — and BYO is offered from day one."</i> Both are here. The platform's
///         account is <c>account: platform</c> and needs nothing else; the tenant's is
///         <c>account: tenant</c> with two <c>SecretRef</c> handles, and the grain refuses the second
///         without the handles rather than falling back to the first — see
///         <c>ICommunicationServiceGrain.ConfigureChannelAsync</c> for the invoice that fallback
///         would produce.
///     </para>
///     <para>
///         ⚠ <b>The kind is a body property and the name is free, and one service holds one
///         configuration per kind.</b> Two <c>channels</c> resources can therefore both say
///         <c>kind: email</c>. The reconciler refuses the second by name — the grain records
///         <see cref="ChannelConfiguration.OwnerResourceId" /> and a configuration owned by another
///         resource is a <c>Conflict</c>, not something to overwrite. The name could not have been
///         made to spell the kind: the shared conformance suite names resources itself.
///     </para>
///     <para>
///         ⚠ <b>The limits default to zero, and zero means nothing may be sent.</b>
///         <c>ChannelLimits.None</c> is the module's safe default — docs/plan/17 § The parts that are
///         actually the work: <i>"the limit is the only thing between a bug and that invoice"</i> — and
///         this schema keeps it. A tenant who wants to send sets a number; a tenant who forgot gets a
///         refusal naming the limit, not a bill.
///     </para>
/// </remarks>
public static class CommunicationChannels {
    /// <summary>The type path, under <see cref="CommunicationServices.TypePath" />.</summary>
    public const string TypePath = CommunicationServices.TypePath + "/channels";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(CommunicationServices.ProviderNamespace, TypePath);

    /// <summary>Whose carrier account pays: the body spellings of <see cref="CredentialMode" />.</summary>
    public static ImmutableArray<string> AccountValues { get; } = ["platform", "tenant"];

    /// <summary>
    ///     A vault handle as one string: <c>path#field</c>, optionally <c>@version</c>. Empty allowed.
    /// </summary>
    /// <remarks>
    ///     The same spelling <c>SecretRef.ToString</c> produces, so a handle read off one surface can
    ///     be pasted into this one. ⚠ A handle and never a value: the value never reaches a body, a
    ///     grain or a log, and <c>WidgetHint.SecretRef</c> is what keeps the portal from offering a
    ///     text box for one.
    /// </remarks>
    public const string OptionalSecretRefPattern = @"([^#@\s]+#[^#@\s]+(@[^#@\s]+)?)?";

    /// <summary>An <c>IChannelProvider.Name</c>, or empty for the channel's registered default.</summary>
    public const string OptionalProviderPattern = "([a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?)?";

    /// <summary>ISO 4217.</summary>
    public const string CurrencyPattern = "[A-Z]{3}";

    /// <summary>The body shape at <see cref="CommunicationServices.V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the channel is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The channel's own settings."),
                new(
                    "/properties/kind",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Which channel this configures. One service holds one configuration per kind."
                ) { AllowedValues = ChannelKinds.AllowedValues, Immutable = true, ExampleJson = "\"email\"" },
                new(
                    "/properties/provider",
                    SchemaKind.Text,
                    Description: "Which carrier implementation serves it — smtp is the one that ships, for "
                    + "kind: email — or empty for the one the platform registers for the kind. A name the "
                    + "platform has not registered refuses every send by that name."
                ) { Pattern = OptionalProviderPattern, MaxLength = 64, DefaultJson = "\"\"" },
                new(
                    "/properties/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether sending on this channel is allowed at all. The kill switch: turning it "
                    + "off keeps the configuration and refuses every send."
                ) { DefaultJson = "true" },
                new(
                    "/properties/account",
                    SchemaKind.Text,
                    Description: "Whose carrier account pays. platform is marked up and needs no setup; tenant "
                    + "is the tenant's own contract, reached through accountRef and authRef."
                ) { AllowedValues = AccountValues, DefaultJson = "\"platform\"" },
                new(
                    "/properties/accountRef",
                    SchemaKind.Text,
                    Description: "For account: tenant — the vault handle of the account identifier (a Twilio "
                    + "account SID, a Meta business account id), as path#field."
                ) { Pattern = OptionalSecretRefPattern, MaxLength = 512, Widget = WidgetHint.SecretRef, DefaultJson = "\"\"" },
                new(
                    "/properties/authRef",
                    SchemaKind.Text,
                    Description: "For account: tenant — the vault handle of the authenticating value (an auth "
                    + "token, a bearer, an access secret), as path#field."
                ) { Pattern = OptionalSecretRefPattern, MaxLength = 512, Widget = WidgetHint.SecretRef, DefaultJson = "\"\"" },
                new(
                    "/properties/signingRef",
                    SchemaKind.Text,
                    Description: "The vault handle of the carrier's webhook-signing value, when it signs its "
                    + "callbacks. A receipt that cannot be verified is data from the internet."
                ) { Pattern = OptionalSecretRefPattern, MaxLength = 512, Widget = WidgetHint.SecretRef, DefaultJson = "\"\"" },
                new("/properties/limits", SchemaKind.Nested, Description: "What the channel may send and spend per UTC day."),
                new(
                    "/properties/limits/maxMessagesPerDay",
                    SchemaKind.WholeNumber,
                    Description: "The most messages this channel dispatches in one UTC day. Zero — the default — "
                    + "means none: a channel with no limit is a channel that cannot send, on purpose."
                ) { Minimum = 0, DefaultJson = "0", ExampleJson = "1000" },
                new(
                    "/properties/limits/maxSpendPerDay",
                    SchemaKind.Number,
                    Description: "The most this channel spends in one UTC day, in currency. Zero means none."
                ) { Minimum = 0, DefaultJson = "0", ExampleJson = "50" },
                new(
                    "/properties/limits/currency",
                    SchemaKind.Text,
                    Description: "ISO 4217, for the spend limit and the refusal that names it."
                ) { Pattern = CurrencyPattern, DefaultJson = "\"EUR\"" },
                new(
                    "/properties/estimatedUnitCost",
                    SchemaKind.Number,
                    Description: "What one message is expected to cost, in currency, reserved before dispatch. "
                    + "Set it to the most expensive destination the channel sends to; an estimate that is too "
                    + "low turns the spend limit into a suggestion."
                ) { Minimum = 0, DefaultJson = "0", ExampleJson = "0.08" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="kind">Which channel.</param>
    /// <param name="maxMessagesPerDay">The message limit.</param>
    /// <param name="maxSpendPerDay">The spend limit.</param>
    /// <param name="provider">The carrier implementation, or empty for the default.</param>
    /// <param name="enabled">Whether sending is on.</param>
    /// <param name="account">Whose account pays.</param>
    /// <param name="accountRef">The account handle, for a tenant account.</param>
    /// <param name="authRef">The authenticating handle, for a tenant account.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        string kind = "email",
        long maxMessagesPerDay = 100,
        decimal maxSpendPerDay = 10m,
        string provider = "",
        bool enabled = true,
        string account = "platform",
        string accountRef = "",
        string authRef = "",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["kind"] = kind,
                ["provider"] = provider,
                ["enabled"] = enabled,
                ["account"] = account,
                ["accountRef"] = accountRef,
                ["authRef"] = authRef,
                ["signingRef"] = string.Empty,
                ["limits"] = new JsonObject {
                    ["maxMessagesPerDay"] = maxMessagesPerDay, ["maxSpendPerDay"] = maxSpendPerDay, ["currency"] = "EUR"
                },
                ["estimatedUnitCost"] = 0m
            }
        }.ToJsonString();

    /// <summary>The kind a body configures.</summary>
    public static ChannelKind KindOf(JsonElement desired) => ChannelKinds.Parse(Bodies.Text(Bodies.Property(desired, "kind"), string.Empty));

    /// <summary>The <see cref="ChannelConfiguration" /> a desired body describes.</summary>
    /// <param name="id">The channel resource, whose GUID becomes the configuration's owner.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     <see cref="ErrorCode.InvalidRequestBody" /> for a kind the schema would have refused, or a
    ///     tenant account with a handle that does not parse. Both are reachable only past the schema,
    ///     and both are refused rather than defaulted because the default would be the platform's
    ///     account.
    /// </returns>
    public static Result<ChannelConfiguration> ToConfiguration(ResourceId id, JsonElement desired) {
        var kind = KindOf(desired);
        if (kind == ChannelKind.Unknown) {
            return Result<ChannelConfiguration>.Failure(
                ErrorCode.InvalidRequestBody,
                "The channel's kind is not one of " + string.Join(", ", ChannelKinds.AllowedValues) + ".",
                "/properties/kind"
            );
        }

        var mode = Bodies.Text(Bodies.Property(desired, "account"), "platform") switch {
            "tenant" => CredentialMode.TenantAccount,
            _ => CredentialMode.PlatformAccount
        };

        var account = ParseSecretRef(Bodies.Text(Bodies.Property(desired, "accountRef"), string.Empty), "/properties/accountRef");
        if (account.TryGetError(out var badAccount)) {
            return Result<ChannelConfiguration>.Failure(badAccount);
        }

        var auth = ParseSecretRef(Bodies.Text(Bodies.Property(desired, "authRef"), string.Empty), "/properties/authRef");
        if (auth.TryGetError(out var badAuth)) {
            return Result<ChannelConfiguration>.Failure(badAuth);
        }

        var signing = ParseSecretRef(Bodies.Text(Bodies.Property(desired, "signingRef"), string.Empty), "/properties/signingRef");
        if (signing.TryGetError(out var badSigning)) {
            return Result<ChannelConfiguration>.Failure(badSigning);
        }

        return Result<ChannelConfiguration>.Success(
            new() {
                Channel = kind,
                Provider = Bodies.Text(Bodies.Property(desired, "provider"), string.Empty),
                Credentials = new() {
                    Mode = mode,
                    AccountRef = account.GetValueOrThrow(),
                    AuthRef = auth.GetValueOrThrow(),
                    SigningRef = signing.GetValueOrThrow()
                },
                Limits = new() {
                    MaxMessagesPerWindow = Bodies.Whole(Bodies.Member(desired, "limits", "maxMessagesPerDay"), 0),
                    MaxSpendPerWindow = Bodies.Amount(Bodies.Member(desired, "limits", "maxSpendPerDay"), 0m),
                    Currency = Bodies.Text(Bodies.Member(desired, "limits", "currency"), "EUR")
                },
                EstimatedUnitCost = Bodies.Amount(Bodies.Property(desired, "estimatedUnitCost"), 0m),
                Enabled = Bodies.Flag(Bodies.Property(desired, "enabled"), true),
                SenderId = Guid.Empty,
                OwnerResourceId = id.Id
            }
        );
    }

    /// <summary>Whether what the grain holds is what the body asks for, and is this resource's.</summary>
    /// <param name="held">The configuration the service grain holds for the kind.</param>
    /// <param name="id">The channel resource.</param>
    /// <param name="desired">The desired body.</param>
    public static bool Matches(ChannelConfiguration held, ResourceId id, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(held);

        return ToConfiguration(id, desired).TryGetValue(out var wanted) && held == wanted;
    }

    /// <summary>Parses <c>path#field[@version]</c>, or the empty handle for an empty string.</summary>
    /// <param name="spelled">The handle as a body spells it.</param>
    /// <param name="target">Which property, as the error's JSON Pointer target.</param>
    public static Result<CarrierSecretRef> ParseSecretRef(string spelled, string target) {
        if (string.IsNullOrWhiteSpace(spelled)) {
            return Result<CarrierSecretRef>.Success(new());
        }

        var hash = spelled.IndexOf('#', StringComparison.Ordinal);
        if (hash <= 0 || hash == spelled.Length - 1) {
            return Result<CarrierSecretRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{spelled}' is not a vault handle. Write path#field, optionally @version — the "
                + "spelling SecretRef prints.",
                target
            );
        }

        var rest = spelled[(hash + 1)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);

        return Result<CarrierSecretRef>.Success(
            new() {
                Path = spelled[..hash], Field = at < 0 ? rest : rest[..at], Version = at < 0 ? string.Empty : rest[(at + 1)..]
            }
        );
    }
}
