using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Billing.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Billing/budgets</c>: an amount, a period, thresholds
///     on the actual cost and on the forecast, and who is told when one is crossed.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/22 § Cost visibility · M2: <i>"Budgets and alerts — threshold at
///         50/80/100/forecast, delivered via [17]"</i>, and § Rating's list of billing resources,
///         <c>CyberCloud.Billing/…/budgets</c>. The namespace is doc 22's; docs/plan/01 files Cost
///         Management under <c>CyberCloud.Billing</c> too.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Thresholds are two arrays of percentages — <c>thresholds.actual</c> and
///             <c>thresholds.forecast</c> — because the schema has no array of objects.
///         </b> The same constraint the sending module's <c>listSuppressions</c> and the alert rule's
///         <c>listInstances</c> render around. It reads the way doc 22 says it: fifty, eighty and a
///         hundred on the actual, a hundred on the forecast.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A budget lives in a resource group and covers that group, or — with
///             <c>scope: subscription</c> — the whole subscription, once the budget itself is granted
///             reader on it.
///         </b> <c>IBudgetGrain</c>'s remarks carry the argument; the short form is that anyone who
///         can write in one group can create a budget, and a subscription-wide figure is not theirs
///         to read by default. For the same reason, whoever reads a budget's figures through
///         <see cref="StatusAction" /> must read what it covers, the group or the subscription, as well
///         as the budget.
///     </para>
/// </remarks>
public static class Budgets {
    // ── Identity ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The provider namespace — docs/plan/22's and docs/plan/01's.</summary>
    public const string ProviderNamespace = "CyberCloud.Billing";

    /// <summary>The type path.</summary>
    public const string TypePath = "budgets";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The one api-version, the platform's.</summary>
    public const string V2026 = "2026-08-01";

    // ── The vocabulary, as the body spells it ────────────────────────────────────────────────

    /// <summary>The body spellings of <see cref="BudgetPeriod" />.</summary>
    public static ImmutableArray<string> PeriodValues { get; } = ["monthly", "quarterly", "annually"];

    /// <summary>The body spellings of <see cref="BudgetScope" />.</summary>
    public static ImmutableArray<string> ScopeValues { get; } = ["resourceGroup", "subscription"];

    /// <summary>
    ///     The sending module's five channels, spelled here because this family may not name that
    ///     module's assembly — the same five words the alert rule's action group spells.
    /// </summary>
    public static ImmutableArray<string> ChannelValues { get; } = ["sms", "whatsapp", "email", "push", "voice"];

    /// <summary>The namespace and type a notification's service must be — literals, for the reason <see cref="ChannelValues" /> is.</summary>
    public const string ServiceNamespace = "CyberCloud.Communication";

    /// <summary>See <see cref="ServiceNamespace" />.</summary>
    public const string ServiceType = "services";

    // ── The limits ────────────────────────────────────────────────────────────────────────────

    /// <summary>The most thresholds one budget carries, actual and forecast together.</summary>
    public const int MaxThresholds = 10;

    /// <summary>The most recipients one notification carries.</summary>
    public const int MaxRecipients = 20;

    /// <summary>The highest threshold, in percent. A budget can alert on being ten times over.</summary>
    public const int MaxPercent = 1000;

    // ── The body shape ───────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the budget is evaluated in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The budget's own settings."),
                new(
                    "/properties/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the budget is evaluated. Off keeps its history and stops the clock."
                ) { DefaultJson = "true" },
                new(
                    "/properties/amount",
                    SchemaKind.Number,
                    true,
                    Description: "The amount for one period, in the billing account's currency."
                ) { Minimum = 0.01, ExampleJson = "500" },
                new(
                    "/properties/period",
                    SchemaKind.Text,
                    Description: "How long a period is. Periods are calendar-aligned in UTC: a month, a quarter "
                    + "from January, April, July or October, or a year."
                ) { AllowedValues = PeriodValues, DefaultJson = "\"monthly\"" },
                new(
                    "/properties/scope",
                    SchemaKind.Text,
                    Description: "What the figure covers: this resource group, or the whole subscription. A "
                    + "resource group's usage is priced on its own, as if it were the subscription's only usage, "
                    + "so that its figure says nothing about the other groups' use of a tiered price. A "
                    + "subscription budget is evaluated only once the budget itself has been granted reader on "
                    + "the subscription — a role assignment named reader-resource-{the budget's GUID, 32 hex "
                    + "digits} at the subscription, which only an owner of the subscription can make. A budget's "
                    + "figures are the spend of what it covers, so showStatus shows them only to a caller who may "
                    + "read that resource group or subscription, not only the budget."
                ) { AllowedValues = ScopeValues, DefaultJson = "\"resourceGroup\"" },
                new(
                    "/properties/thresholds",
                    SchemaKind.Nested,
                    Description: "Percentages of the amount that alert, each at most once per period."
                ),
                new(
                    "/properties/thresholds/actual",
                    SchemaKind.Array,
                    Description: "Percentages of the amount the period's cost so far is compared with — "
                    + "50, 80 and 100 is the usual set. At least one threshold across both lists and at most "
                    + "10; a body outside that is refused when the budget is reconciled."
                ) {
                    ElementKind = SchemaKind.Number,
                    Minimum = 1,
                    Maximum = MaxPercent,
                    ExampleJson = "[50, 80, 100]"
                },
                new(
                    "/properties/thresholds/forecast",
                    SchemaKind.Array,
                    Description: "Percentages of the amount the forecast is compared with. The forecast is "
                    + "linear on the trailing seven days, and an alert on it says it is an estimate."
                ) {
                    ElementKind = SchemaKind.Number,
                    Minimum = 1,
                    Maximum = MaxPercent,
                    ExampleJson = "[100]"
                },
                new("/properties/notification", SchemaKind.Nested, Description: "Who is told, and how."),
                new(
                    "/properties/notification/service",
                    SchemaKind.Text,
                    true,
                    Description: "The CyberCloud.Communication/services resource the alert is sent through, as "
                    + "its full resource id path. It must be in this budget's resource group."
                ) {
                    Format = SchemaFormat.ResourceId,
                    MaxLength = 512,
                    ExampleJson = "\"/tenants/11111111-1111-4111-8111-111111111111/subscriptions/"
                        + "33333333-3333-4333-8333-333333333333/resourceGroups/prod/providers/"
                        + "CyberCloud.Communication/services/alerts\""
                },
                new(
                    "/properties/notification/channel",
                    SchemaKind.Text,
                    true,
                    Description: "Which of that service's channels carries it. The service must have the "
                    + "channel configured and enabled, or every alert is refused by name."
                ) { AllowedValues = ChannelValues, ExampleJson = "\"email\"" },
                new(
                    "/properties/notification/recipients",
                    SchemaKind.Array,
                    true,
                    Description: "Where it goes — addresses or E.164 numbers, one send each, every one checked "
                    + "against the service's suppression list. At least one and at most 20."
                ) {
                    ElementKind = SchemaKind.Text,
                    MinLength = 1,
                    MaxLength = 320,
                    ExampleJson = """["finance@example.com"]"""
                }
            ]
        );

    // ── The status action ────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>POST …/budgets/{name}/showStatus</c> — the budget's figures and which thresholds have
    ///     fired, as its grain holds them. Issue #41.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An action because nothing else carries a budget's state to a caller.</b> The resource
    ///         envelope has no status member, and <c>BudgetReconciler.ObserveAsync</c>'s figures are what
    ///         the drift scan compares, refreshed at a reconcile rather than at each hourly evaluation. A
    ///         portal page that showed them would show a number an hour or a day stale with nothing to say
    ///         so. The <c>showAllocation</c> shape the public IP established: a read permission, no body.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It reads and never evaluates.</b> An evaluation fires thresholds, and a threshold
    ///         firing sends a message; a <c>read</c> that paged finance would be a write behind a read
    ///         permission. The figures are the last hourly evaluation's, and <c>/lastEvaluatedAt</c> says
    ///         when that was.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>read</c> on the budget isn't enough.</b> A budget's figures are the spend of the
    ///         group it covers, or with <c>scope: subscription</c> of the subscription, and
    ///         <c>BudgetStatusHandler</c> refuses them to a caller who may not read that scope. A reader
    ///         granted on the budget resource alone sees its spec and not its figures.
    ///     </para>
    /// </remarks>
    public const string StatusAction = "showStatus";

    /// <summary>What <see cref="StatusAction" /> returns.</summary>
    /// <remarks>
    ///     ⚠ <b>The fired thresholds are two arrays of percentages</b>, the way the body's thresholds are
    ///     and for the same reason: the schema has no array of objects. The alert history is text lines,
    ///     the <c>listSuppressions</c> shape, rendered by <see cref="AlertLine" />.
    /// </remarks>
    public static ResourceSchema StatusResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/evaluated",
                    SchemaKind.Boolean,
                    true,
                    Description: "Whether the budget has been evaluated in its current period. False until the first "
                    + "hourly evaluation, and for a disabled budget."
                ),
                new("/currency", SchemaKind.Text, true, Description: "The currency the figures are in. Empty until evaluated."),
                new("/amount", SchemaKind.Number, true, Description: "The amount for one period, from the budget's body."),
                new(
                    "/actual",
                    SchemaKind.Number,
                    true,
                    Description: "What the period has cost so far, rounded to the currency, as of the last evaluation."
                ),
                new(
                    "/forecast",
                    SchemaKind.Number,
                    true,
                    Description: "What the period will cost at the trailing seven days' rate, rounded. An estimate."
                ),
                new(
                    "/periodStart",
                    SchemaKind.Text,
                    Description: "The first instant of the period the figures are for. Absent until evaluated."
                ) { Format = SchemaFormat.DateTime },
                new(
                    "/periodEnd",
                    SchemaKind.Text,
                    Description: "The first instant of the next period. Absent until evaluated."
                ) { Format = SchemaFormat.DateTime },
                new(
                    "/lastEvaluatedAt",
                    SchemaKind.Text,
                    Description: "When the figures were computed. Absent until evaluated."
                ) { Format = SchemaFormat.DateTime },
                new(
                    "/lastError",
                    SchemaKind.Text,
                    true,
                    Description: "Why the last evaluation could not run, or empty. A subscription budget not yet "
                    + "granted reader on its subscription says so here."
                ),
                new(
                    "/firedActual",
                    SchemaKind.Array,
                    true,
                    Description: "The thresholds on the actual cost that have fired this period, as percentages."
                ) { ElementKind = SchemaKind.Number },
                new(
                    "/firedForecast",
                    SchemaKind.Array,
                    true,
                    Description: "The thresholds on the forecast that have fired this period, as percentages."
                ) { ElementKind = SchemaKind.Number },
                new(
                    "/alerts",
                    SchemaKind.Array,
                    true,
                    Description: "Every alert the budget has fired, oldest first, one line each: "
                    + "'{firedAt} {actual|forecast} {percent}% at {figure}: {notification}'."
                ) { ElementKind = SchemaKind.Text }
            ]
        );

    /// <summary>The <see cref="StatusAction" /> body for what the budget's grain holds.</summary>
    /// <param name="held">The budget as its grain holds it.</param>
    public static string StatusJson(BudgetSnapshot held) {
        ArgumentNullException.ThrowIfNull(held);

        var evaluated = held.LastEvaluatedAt is not null && held.Spec.Enabled;
        var alerts = held.Alerts.IsDefault ? [] : held.Alerts;
        var thisPeriod = alerts.Where(x => evaluated && x.PeriodStart == held.PeriodStart).ToList();

        var body = new JsonObject {
            ["evaluated"] = evaluated,
            ["currency"] = held.Currency,
            ["amount"] = held.Spec.Amount,
            ["actual"] = held.Actual,
            ["forecast"] = held.Forecast
        };

        if (held.LastEvaluatedAt is { } at) {
            body["periodStart"] = Stamp(held.PeriodStart);
            body["periodEnd"] = Stamp(held.PeriodEnd);
            body["lastEvaluatedAt"] = Stamp(at);
        }

        body["lastError"] = held.LastError;
        body["firedActual"] = Percentages(thisPeriod, ThresholdKind.Actual);
        body["firedForecast"] = Percentages(thisPeriod, ThresholdKind.Forecast);
        body["alerts"] = new JsonArray([.. alerts.Select(static x => JsonValue.Create(AlertLine(x)))]);

        return body.ToJsonString();

        static JsonArray Percentages(IEnumerable<BudgetAlert> fired, ThresholdKind kind) =>
            new([.. fired.Where(x => x.Kind == kind).Select(static x => x.Percent).Order().Select(static x => JsonValue.Create(x))]);
    }

    /// <summary>One alert as a line of <see cref="StatusResponse" />'s <c>/alerts</c>.</summary>
    /// <param name="alert">An alert the budget fired.</param>
    public static string AlertLine(BudgetAlert alert) {
        ArgumentNullException.ThrowIfNull(alert);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Stamp(alert.FiredAt)} {(alert.Kind == ThresholdKind.Forecast ? "forecast" : "actual")} {alert.Percent}% at {alert.Figure}: {alert.Notification}"
        );
    }

    static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    // ── A body, for tests and the conformance case ───────────────────────────────────────────

    /// <summary>A valid body at <see cref="V2026" />.</summary>
    /// <param name="service">The sending service's resource id path.</param>
    /// <param name="recipients">Who is told.</param>
    /// <param name="amount">The amount.</param>
    /// <param name="actual">Thresholds on the actual cost.</param>
    /// <param name="forecast">Thresholds on the forecast.</param>
    /// <param name="period">The period's spelling.</param>
    /// <param name="scope">The scope's spelling.</param>
    /// <param name="channel">The channel's spelling.</param>
    /// <param name="enabled">Whether it runs.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        string service,
        ImmutableArray<string> recipients,
        decimal amount = 500m,
        decimal[]? actual = null,
        decimal[]? forecast = null,
        string period = "monthly",
        string scope = "resourceGroup",
        string channel = "email",
        bool enabled = true,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["enabled"] = enabled,
                ["amount"] = amount,
                ["period"] = period,
                ["scope"] = scope,
                ["thresholds"] = new JsonObject {
                    ["actual"] = new JsonArray([.. (actual ?? [50m, 80m, 100m]).Select(static x => (JsonNode?)x)]),
                    ["forecast"] = new JsonArray([.. (forecast ?? [100m]).Select(static x => (JsonNode?)x)])
                },
                ["notification"] = new JsonObject {
                    ["service"] = service,
                    ["channel"] = channel,
                    ["recipients"] = new JsonArray([.. recipients.Select(static x => (JsonNode?)x)])
                }
            }
        }.ToJsonString();

    // ── The desired body, read ───────────────────────────────────────────────────────────────

    /// <summary>The <see cref="BudgetSpec" /> a desired body describes.</summary>
    /// <param name="id">The budget, with its GUID resolved.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     <see cref="ErrorCode.InvalidRequestBody" />, targeting the pointer, for what the schema cannot
    ///     refuse: a service path in another tenant, another resource group or of another type, no
    ///     threshold or too many, a recipient count outside 1–20, and a vocabulary word only reachable
    ///     past the schema.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The tenant and group checks are the ones that matter.</b> A budget in tenant A naming
    ///     tenant B's service would send through B's channels and spend B's limits — the argument
    ///     <c>MonitorAlertRules.ToSpec</c> makes. The same holds between two groups of one tenant, which
    ///     that argument stopped short of; <see cref="InSameGroup" /> says why the group is the line.
    ///     <c>BudgetGrain</c> checks both again where it sends.
    /// </remarks>
    public static Result<BudgetSpec> ToSpec(ResourceId id, JsonElement desired) {
        var period = Text(Property(desired, "period"), "monthly") switch {
            "monthly" => BudgetPeriod.Monthly,
            "quarterly" => BudgetPeriod.Quarterly,
            "annually" => BudgetPeriod.Annually,
            _ => BudgetPeriod.Unknown
        };

        if (period == BudgetPeriod.Unknown) {
            return Refuse("The period is not one of " + string.Join(", ", PeriodValues) + ".", "/properties/period");
        }

        var scope = Text(Property(desired, "scope"), "resourceGroup") switch {
            "resourceGroup" => BudgetScope.ResourceGroup,
            "subscription" => BudgetScope.Subscription,
            _ => BudgetScope.Unknown
        };

        if (scope == BudgetScope.Unknown) {
            return Refuse("The scope is not one of " + string.Join(", ", ScopeValues) + ".", "/properties/scope");
        }

        var amount = Number(Property(desired, "amount"));
        if (amount is not > 0m) {
            return Refuse("A budget's amount is a positive number.", "/properties/amount");
        }

        var channel = Text(Member(desired, "notification", "channel"), string.Empty);
        if (!ChannelValues.Contains(channel)) {
            return Refuse(
                "The notification's channel is not one of " + string.Join(", ", ChannelValues) + ".",
                "/properties/notification/channel"
            );
        }

        var servicePath = Text(Member(desired, "notification", "service"), string.Empty).Trim();
        if (!ResourceId.TryParsePath(servicePath, out var service)) {
            return Refuse(
                "The notification's service is not a resource id path. Write the full path of the "
                + "CyberCloud.Communication/services resource — docs/plan/06 § Identifiers.",
                "/properties/notification/service"
            );
        }

        if (service.TenantId != id.TenantId) {
            return Refuse(
                $"The notification's service belongs to tenant {service.TenantId:D} and this budget to tenant "
                + $"{id.TenantId:D}. A budget sends through its own tenant's services only.",
                "/properties/notification/service"
            );
        }

        if (!InSameGroup(service, id.SubscriptionId, id.ResourceGroup)) {
            return Refuse(
                $"The notification's service is in resource group '{service.ResourceGroup}' of subscription "
                + $"{service.SubscriptionId:D}, and this budget is in '{id.ResourceGroup}' of {id.SubscriptionId:D}. "
                + "A budget sends through a service in its own resource group only.",
                "/properties/notification/service"
            );
        }

        if (!string.Equals(service.Type.Namespace, ServiceNamespace, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(service.Type.Type, ServiceType, StringComparison.OrdinalIgnoreCase)) {
            return Refuse(
                $"The notification's service is a {service.Type} resource. It must be a {ServiceNamespace}/{ServiceType} "
                + "resource — the sending service the alert goes through.",
                "/properties/notification/service"
            );
        }

        // ⚠ Refused here and not by the schema, which has no item count — the same honest split
        // MonitorAlertRules.ToSpec records, and the descriptions above say the refusal is at reconcile.
        var thresholds = Numbers(Member(desired, "thresholds", "actual"))
            .Select(static x => new BudgetThreshold { Percent = x, Kind = ThresholdKind.Actual })
            .Concat(
                Numbers(Member(desired, "thresholds", "forecast"))
                    .Select(static x => new BudgetThreshold { Percent = x, Kind = ThresholdKind.Forecast })
            )
            .Distinct()
            .OrderBy(static x => x.Kind)
            .ThenBy(static x => x.Percent)
            .ToImmutableArray();

        if (thresholds.Length == 0) {
            return Refuse("The budget has no threshold. Nobody would ever be told anything.", "/properties/thresholds");
        }

        if (thresholds.Length > MaxThresholds) {
            return Refuse(
                string.Create(CultureInfo.InvariantCulture, $"The budget has {thresholds.Length} thresholds and the most is {MaxThresholds}."),
                "/properties/thresholds"
            );
        }

        var recipients = Strings(Member(desired, "notification", "recipients"))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0)
            .ToImmutableArray();

        if (recipients.Length is 0 or > MaxRecipients) {
            return Refuse(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The notification names {recipients.Length} recipients; a budget tells between 1 and {MaxRecipients}."
                ),
                "/properties/notification/recipients"
            );
        }

        return Result<BudgetSpec>.Success(
            new() {
                BudgetId = id.Id,
                Name = id.Name,
                SubscriptionId = id.SubscriptionId,
                ResourceGroup = id.ResourceGroup,
                Scope = scope,
                Amount = amount.Value,
                Period = period,
                Thresholds = thresholds,
                Notification = new() { ServicePath = servicePath, Channel = channel, Recipients = recipients },
                Enabled = Flag(Property(desired, "enabled"), true)
            }
        );
    }

    /// <summary>Whether a sending service is in the budget's own resource group.</summary>
    /// <param name="service">The service named by the notification.</param>
    /// <param name="subscriptionId">The budget's subscription.</param>
    /// <param name="resourceGroup">The budget's resource group.</param>
    /// <remarks>
    ///     ⚠ <b>The stand-in for asking whether the budget's author may use the service.</b> Nothing on
    ///     the write path checks a <see cref="SchemaFormat.ResourceId" /> reference against its author,
    ///     so a budget naming any service in its tenant let a writer in one group send through another
    ///     group's service, to recipients of their choosing, on that service's spend limits. Creating a
    ///     budget takes <c>write</c> on its group, which creates and configures services there too, so
    ///     confining the reference to the group gives a budget's creator no service they couldn't
    ///     already use. ⚠ Not closed: a contributor on one existing budget, and on nothing else in its
    ///     group, can still point it at a service in that group. That half, and a service elsewhere with
    ///     a grant from its owner, is docs/plan/22 § What is owed, <c>budget-service-grant</c>.
    /// </remarks>
    public static bool InSameGroup(ResourceId service, Guid subscriptionId, string resourceGroup) =>
        service.SubscriptionId == subscriptionId
        && string.Equals(service.ResourceGroup, resourceGroup, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether what the grain holds is what the body asks for.</summary>
    /// <param name="held">The budget as its grain holds it.</param>
    /// <param name="id">The budget.</param>
    /// <param name="desired">The desired body.</param>
    public static bool Matches(BudgetSnapshot held, ResourceId id, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(held);

        return ToSpec(id, desired).TryGetValue(out var wanted) && held.Spec.SameAs(wanted);
    }

    /// <summary>The body spelling of a period.</summary>
    public static string Spell(BudgetPeriod period) =>
        period switch {
            BudgetPeriod.Quarterly => "quarterly",
            BudgetPeriod.Annually => "annually",
            _ => "monthly"
        };

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────

    static Result<BudgetSpec> Refuse(string message, string target) =>
        Result<BudgetSpec>.Failure(ErrorCode.InvalidRequestBody, message, target);

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement? element, string fallback) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? fallback : fallback;

    static bool Flag(JsonElement? element, bool fallback) =>
        element switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };

    static decimal? Number(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var found) ? found : null;

    static IEnumerable<decimal> Numbers(JsonElement? element) {
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            yield break;
        }

        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind is JsonValueKind.Number && item.TryGetDecimal(out var number)) {
                yield return number;
            }
        }
    }

    static IEnumerable<string> Strings(JsonElement? element) {
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            yield break;
        }

        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind is JsonValueKind.String && item.GetString() is { } text) {
                yield return text;
            }
        }
    }
}
