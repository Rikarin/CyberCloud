using CyberCloud.Communication.Contracts;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Monitor/workspaces/alertRules</c>: a condition over
///     the workspace's metrics or logs, a severity, an action group, and the evaluator that runs it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § Alerts · M2:
///         <i>
///             "a query, a threshold, a duration, a severity, an action
///             group"
///         </i>. All five are here, under <c>/properties</c>, and the document's
///         <c>CyberCloud.Monitor/alertRules</c> is spelled as a child of <c>workspaces</c> because a
///         rule's query has no meaning without a store to run against, and the store is the
///         workspace — a top-level rule would carry the workspace as a property that could name
///         another tenant's.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE FIRST CLUSTERLESS TYPE IN A CLUSTER-BACKED FAMILY, AND NOTHING IN THE TREE HAD
///             THAT SHAPE.
///         </b> The workspace declares <c>RequiresCluster</c> and applies three objects; this type
///         declares neither a cluster nor a chart and converges a grain — the sending module's
///         four types were the first clusterless family, and this is the first clusterless
///         <i>child of a cluster-backed parent</i>. What it means for the conformance suite: the
///         harness creates the workspace ancestor against its fake cluster and then reads this type
///         through an <c>IConvergedModule</c>, the same run.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The action group names the sending service by RESOURCE ID PATH, and this is the
///             first property in the catalogue with <c>SchemaFormat.ResourceId</c>.
///         </b> docs/plan/03
///         § Assembly graph rules, rule 2: cross-provider traffic goes
///         <i>"through CyberCloud.ResourceManager by resource id"</i>. Every family before this one
///         either needed no other provider's resource or recorded that it wanted one and could not
///         reach it (module-layering.txt lists three). Here the reference is to a
///         <c>CyberCloud.Communication/services</c> resource, whose grain the sending module keys on
///         a GUID derived from that very path — so the evaluator reaches the service with no index
///         read and no assembly reference to the other provider. The path is checked three ways at
///         reconcile time, and each refusal names the pointer: it must parse, it must be in the
///         rule's own tenant, and it must be that one type.
///     </para>
///     <para>
///         ⚠ <b>docs/plan/16 § Alerts' three mandatory limits, and where each one is.</b>
///         <i>
///             "Query cost limits, a per-workspace concurrent-evaluation cap and a max look-back are
///             mandatory from day one."
///         </i> The look-back is the schema's — <see cref="MaxLookbackSeconds" />
///         — and is refused at the API. The concurrency cap is the evaluator's activation, one per
///         workspace, evaluating rules one at a time (<see cref="IAlertEvaluatorGrain" />). Query
///         cost is bounded three ways: the query's length (<see cref="MaxQueryLength" />), the
///         rules a workspace may carry (<c>IAlertEvaluatorGrain.MaxRules</c>), and a timeout per
///         query (<c>IAlertEvaluatorGrain.QueryTimeout</c>). What none of these does is inspect
///         the query — a MetricsQL cost estimate is the store's to make, and the seam that would ask
///         it is owed.
///     </para>
/// </remarks>
public static class MonitorAlertRules {
    // ── Identity ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The type path, under <see cref="MonitorWorkspaces.TypePath" />.</summary>
    public const string TypePath = MonitorWorkspaces.TypePath + "/alertRules";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(MonitorWorkspaces.ProviderNamespace, TypePath);

    /// <summary><c>POST …/alertRules/{name}/listInstances</c>. Every firing, oldest first.</summary>
    public const string ListInstancesAction = "listInstances";

    // ── The vocabulary, as the body spells it ────────────────────────────────────────────────

    /// <summary>The body spellings of <see cref="AlertSignal" />.</summary>
    public static ImmutableArray<string> SignalValues { get; } = ["metrics", "logs"];

    /// <summary>The body spellings of <see cref="AlertSeverity" />.</summary>
    public static ImmutableArray<string> SeverityValues { get; } = ["critical", "error", "warning", "informational"];

    /// <summary>The body spellings of <see cref="AlertOperator" />.</summary>
    public static ImmutableArray<string> OperatorValues { get; } = [
        "greaterThan", "greaterOrEqual", "lessThan", "lessOrEqual", "equal", "notEqual"
    ];

    /// <summary>The body spellings of <see cref="ChannelKind" /> — the sending module's five.</summary>
    /// <remarks>
    ///     ⚠ Spelled here rather than taken from the other provider's <c>ChannelKinds</c>, because
    ///     rule 2 forbids naming that assembly. Same five words; a sixth channel is a change to both.
    /// </remarks>
    public static ImmutableArray<string> ChannelValues { get; } = ["sms", "whatsapp", "email", "push", "voice"];

    /// <summary>
    ///     The provider namespace and type an action group's service must be. Literals, because
    ///     rule 2 forbids binding the other provider's constants — and compared case-insensitively,
    ///     because the namespace is case-preserving.
    /// </summary>
    public const string ServiceNamespace = "CyberCloud.Communication";

    /// <summary>See <see cref="ServiceNamespace" />.</summary>
    public const string ServiceType = "services";

    // ── The limits ────────────────────────────────────────────────────────────────────────────

    /// <summary>The longest query a rule may carry, in characters.</summary>
    public const int MaxQueryLength = 4096;

    /// <summary>The shortest evaluation interval — Orleans' reminder floor.</summary>
    public const int MinIntervalSeconds = 60;

    /// <summary>The longest evaluation interval. An hour.</summary>
    public const int MaxIntervalSeconds = 3600;

    /// <summary>The shortest look-back. One evaluation interval.</summary>
    public const int MinLookbackSeconds = 60;

    /// <summary>
    ///     The longest look-back. A day — docs/plan/16 § Alerts' <i>"max look-back"</i>, spelled as
    ///     a schema maximum so the API refuses it rather than the evaluator.
    /// </summary>
    public const int MaxLookbackSeconds = 86_400;

    /// <summary>The longest <c>for</c>. A day.</summary>
    public const int MaxForSeconds = 86_400;

    /// <summary>The most recipients one action group carries.</summary>
    public const int MaxRecipients = 20;

    /// <summary>The evaluation interval a body that names none gets.</summary>
    public const int DefaultIntervalSeconds = 60;

    /// <summary>The look-back a body that names none gets.</summary>
    public const int DefaultLookbackSeconds = 300;

    // ── The body shape ───────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="MonitorWorkspaces.V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the rule is evaluated in — the workspace's."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The rule's own settings."),
                new(
                    "/properties/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether the rule is evaluated. Off keeps the rule and its history and "
                    + "stops the clock; an open alert is resolved without a notification."
                ) { DefaultJson = "true" },
                new(
                    "/properties/severity",
                    SchemaKind.Text,
                    true,
                    Description: "How loud: critical pages somebody, error is looked at today, warning "
                    + "this week, informational is worth knowing."
                ) { AllowedValues = SeverityValues, ExampleJson = "\"warning\"" },

                // ── The condition ────────────────────────────────────────────────────────────
                new("/properties/condition", SchemaKind.Nested, Description: "What is asked, and what answer counts."),
                new(
                    "/properties/condition/signal",
                    SchemaKind.Text,
                    true,
                    Description: "Which store the query runs against: metrics is MetricsQL over the "
                    + "workspace's VictoriaMetrics account, logs is SQL over its ClickHouse database."
                ) { AllowedValues = SignalValues, ExampleJson = "\"metrics\"" },
                new(
                    "/properties/condition/query",
                    SchemaKind.Text,
                    true,
                    Description: "The query, verbatim. It must produce numbers; the rule fires when any of "
                    + "them satisfies the operator against the threshold."
                ) {
                    MinLength = 1,
                    MaxLength = MaxQueryLength,
                    ExampleJson = "\"max(rate(http_requests_errors_total[5m]))\""
                },
                new(
                    "/properties/condition/operator",
                    SchemaKind.Text,
                    true,
                    Description: "How a value is compared with the threshold."
                ) { AllowedValues = OperatorValues, ExampleJson = "\"greaterThan\"" },
                new(
                    "/properties/condition/threshold",
                    SchemaKind.Number,
                    true,
                    Description: "The number the value is compared with."
                ) { ExampleJson = "5" },
                new(
                    "/properties/condition/lookbackSeconds",
                    SchemaKind.WholeNumber,
                    Description: "How far back the query may read, in seconds. Capped at a day: the "
                    + "look-back is the query's cost, and the cap is the platform's, not the tenant's."
                ) { Minimum = MinLookbackSeconds, Maximum = MaxLookbackSeconds, DefaultJson = "300" },

                // ── The schedule ─────────────────────────────────────────────────────────────
                new(
                    "/properties/evaluation",
                    SchemaKind.Nested,
                    Description: "How often, and how long before it counts."
                ),
                new(
                    "/properties/evaluation/intervalSeconds",
                    SchemaKind.WholeNumber,
                    Description: "How often the condition is evaluated, in seconds. A multiple of 60: the "
                    + "evaluator ticks once a minute and a rule at 300 is evaluated on every fifth tick. "
                    + "Anything else is refused when the rule is reconciled."
                ) { Minimum = MinIntervalSeconds, Maximum = MaxIntervalSeconds, DefaultJson = "60" },
                new(
                    "/properties/evaluation/forSeconds",
                    SchemaKind.WholeNumber,
                    Description: "How long the condition must hold before the rule fires, in seconds. Zero "
                    + "fires on the first evaluation that meets it; 300 ignores anything shorter than "
                    + "five minutes."
                ) { Minimum = 0, Maximum = MaxForSeconds, DefaultJson = "0" },

                // ── The action group ─────────────────────────────────────────────────────────
                new("/properties/actionGroup", SchemaKind.Nested, Description: "Who is told, and how."),
                new(
                    "/properties/actionGroup/service",
                    SchemaKind.Text,
                    true,
                    Description: "The CyberCloud.Communication/services resource the notification is sent "
                    + "through, as its full resource id path. It must be in this tenant."
                ) {
                    Format = SchemaFormat.ResourceId,
                    MaxLength = 512,
                    ExampleJson = "\"/tenants/11111111-1111-4111-8111-111111111111/subscriptions/"
                        + "33333333-3333-4333-8333-333333333333/resourceGroups/prod/providers/"
                        + "CyberCloud.Communication/services/alerts\""
                },
                new(
                    "/properties/actionGroup/channel",
                    SchemaKind.Text,
                    true,
                    Description: "Which of that service's channels carries it. The service must have the "
                    + "channel configured and enabled, or every notification is refused by name."
                ) { AllowedValues = ChannelValues, ExampleJson = "\"email\"" },
                new(
                    "/properties/actionGroup/recipients",
                    SchemaKind.Array,
                    true,
                    Description: "Where it goes — addresses or E.164 numbers, one send each, every one "
                    + "checked against the service's suppression list before dispatch. At least one and "
                    + "at most 20; a list outside that is refused when the rule is reconciled."
                ) {
                    ElementKind = SchemaKind.Text,
                    MinLength = 1,
                    MaxLength = 320,
                    ExampleJson = """["oncall@example.com"]"""
                },
                new(
                    "/properties/actionGroup/notifyOnResolve",
                    SchemaKind.Boolean,
                    Description: "Whether the recipients are told when the condition stops holding, as well "
                    + "as when it starts."
                ) { DefaultJson = "true" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    /// <summary>What a <c>listInstances</c> returns.</summary>
    /// <remarks>
    ///     Text lines, for the reason the sending module's <c>listSuppressions</c> gives — the schema
    ///     has no array of objects. <see cref="InstanceLine" /> renders
    ///     <c>{state} {severity} fired {firedAt} resolved {resolvedAt} value {value}: {summary} | {notification}</c>.
    /// </remarks>
    public static ResourceSchema ListInstancesResponse { get; } =
        ResourceSchema.Of(
            [
                new("/count", SchemaKind.WholeNumber, true, Description: "How many firings the rule keeps."),
                new("/open", SchemaKind.WholeNumber, true, Description: "How many of them are still firing."),
                new(
                    "/state",
                    SchemaKind.Text,
                    true,
                    Description: "Where the rule is now: ok, pending or firing."
                ),
                new(
                    "/instances",
                    SchemaKind.Array,
                    true,
                    Description: "Every firing, oldest first, one line each: "
                    + "'{state} {severity} fired {firedAt} resolved {resolvedAt} value {value}: {summary} | {notification}'."
                ) { ElementKind = SchemaKind.Text }
            ]
        );

    // ── A body, for tests and the conformance case ───────────────────────────────────────────

    /// <summary>A valid body at <see cref="MonitorWorkspaces.V2026" />.</summary>
    /// <param name="service">The sending service's resource id path.</param>
    /// <param name="recipients">Who is told.</param>
    /// <param name="threshold">The number.</param>
    /// <param name="query">The query.</param>
    /// <param name="signal">Which store.</param>
    /// <param name="op">The comparison.</param>
    /// <param name="severity">How loud.</param>
    /// <param name="channel">Which channel.</param>
    /// <param name="intervalSeconds">How often.</param>
    /// <param name="forSeconds">How long before it counts.</param>
    /// <param name="lookbackSeconds">How far back.</param>
    /// <param name="enabled">Whether it runs.</param>
    /// <param name="notifyOnResolve">Whether a resolve is notified.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        string service,
        ImmutableArray<string> recipients,
        double threshold = 5,
        string query = "max(rate(http_requests_errors_total[5m]))",
        string signal = "metrics",
        string op = "greaterThan",
        string severity = "warning",
        string channel = "email",
        int intervalSeconds = DefaultIntervalSeconds,
        int forSeconds = 0,
        int lookbackSeconds = DefaultLookbackSeconds,
        bool enabled = true,
        bool notifyOnResolve = true,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["enabled"] = enabled,
                ["severity"] = severity,
                ["condition"] = new JsonObject {
                    ["signal"] = signal,
                    ["query"] = query,
                    ["operator"] = op,
                    ["threshold"] = threshold,
                    ["lookbackSeconds"] = lookbackSeconds
                },
                ["evaluation"] = new JsonObject { ["intervalSeconds"] = intervalSeconds, ["forSeconds"] = forSeconds },
                ["actionGroup"] = new JsonObject {
                    ["service"] = service,
                    ["channel"] = channel,
                    ["recipients"] = new JsonArray([.. recipients.Select(static x => (JsonNode?)x)]),
                    ["notifyOnResolve"] = notifyOnResolve
                }
            }
        }.ToJsonString();

    // ── The evaluator's address, which is the workspace's ────────────────────────────────────

    /// <summary>
    ///     The id of the evaluator grain that holds a rule — one per workspace, derived from the
    ///     workspace's address.
    /// </summary>
    /// <param name="id">The rule's address, or the workspace's. Either derives the same id.</param>
    /// <remarks>
    ///     Walks up to the <c>workspaces</c> level first, so a rule's reconciler and anything
    ///     addressing the workspace directly agree by construction. The construction is
    ///     <c>CommunicationGrainKeys.ResourceIdFor</c>'s — RFC 9562 § 5.5 with SHA-256, version 8 —
    ///     over a domain string of this family's own, so a workspace and a sending service at
    ///     coincidentally similar paths cannot derive one grain.
    /// </remarks>
    public static Guid EvaluatorIdFor(ResourceId id) {
        var workspace = id;
        while (workspace.Parent is { } parent) {
            workspace = parent;
        }

        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"cybercloud.monitor.alerts\n{workspace.TenantId:N}\n{workspace.CanonicalPath}"
        );

        Span<byte> digest = stackalloc byte[32];
        _ = SHA256.HashData(Encoding.UTF8.GetBytes(material), digest);

        Span<byte> guid = stackalloc byte[16];
        digest[..16].CopyTo(guid);

        // RFC 9562 § 4.1–4.2: version 8 ("custom") in the high nibble of octet 6, the variant in
        // the top bits of octet 8. bigEndian so the stamped octets are the ones the GUID prints.
        guid[6] = (byte)((guid[6] & 0x0F) | 0x80);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);

        return new(guid, true);
    }

    /// <summary>The workspace a rule sits under — its address, with no GUID.</summary>
    /// <param name="id">The rule's address.</param>
    public static ResourceId WorkspaceOf(ResourceId id) {
        var workspace = id;
        while (workspace.Parent is { } parent) {
            workspace = parent;
        }

        return workspace;
    }

    // ── The desired body, read ───────────────────────────────────────────────────────────────

    /// <summary>The <see cref="AlertRuleSpec" /> a desired body describes.</summary>
    /// <param name="id">The rule, with its GUID resolved.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>
    ///     <c>InvalidRequestBody</c>, targeting the pointer, for an action group whose service path
    ///     does not parse, is in another tenant, or is not a <c>CyberCloud.Communication/services</c>
    ///     resource — and for a vocabulary word the schema would have refused, which is reachable only
    ///     past the schema and is refused rather than defaulted, because the default would be a rule
    ///     that never fires.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The tenant check is the one that matters, and it cannot be the schema's.</b>
    ///     <c>SchemaFormat.ResourceId</c> proves the path parses; it cannot know whose tenant the
    ///     body belongs to. A rule in tenant A naming tenant B's service would send through B's
    ///     channels and spend B's limits, and the sending module's own tenant separation would not
    ///     catch it — the evaluator calls <c>IMessageSender</c> with the tenant it chooses.
    /// </remarks>
    public static Result<AlertRuleSpec> ToSpec(ResourceId id, JsonElement desired) {
        var signal = ParseSignal(Text(Member(desired, "condition", "signal"), string.Empty));
        if (signal == AlertSignal.Unknown) {
            return Refuse(
                "The condition's signal is not one of " + string.Join(", ", SignalValues) + ".",
                "/properties/condition/signal"
            );
        }

        var op = ParseOperator(Text(Member(desired, "condition", "operator"), string.Empty));
        if (op == AlertOperator.Unknown) {
            return Refuse(
                "The condition's operator is not one of " + string.Join(", ", OperatorValues) + ".",
                "/properties/condition/operator"
            );
        }

        var severity = ParseSeverity(Text(Property(desired, "severity"), string.Empty));
        if (severity == AlertSeverity.Unknown) {
            return Refuse(
                "The severity is not one of " + string.Join(", ", SeverityValues) + ".",
                "/properties/severity"
            );
        }

        var channel = ParseChannel(Text(Member(desired, "actionGroup", "channel"), string.Empty));
        if (channel == ChannelKind.Unknown) {
            return Refuse(
                "The action group's channel is not one of " + string.Join(", ", ChannelValues) + ".",
                "/properties/actionGroup/channel"
            );
        }

        var servicePath = Text(Member(desired, "actionGroup", "service"), string.Empty).Trim();
        if (!ResourceId.TryParsePath(servicePath, out var service)) {
            return Refuse(
                "The action group's service is not a resource id path. Write the full path of the "
                + "CyberCloud.Communication/services resource — docs/plan/06 § Identifiers.",
                "/properties/actionGroup/service"
            );
        }

        if (service.TenantId != id.TenantId) {
            return Refuse(
                $"The action group's service belongs to tenant {service.TenantId:D} and this rule to "
                + $"tenant {id.TenantId:D}. A rule sends through its own tenant's services only.",
                "/properties/actionGroup/service"
            );
        }

        if (!string.Equals(service.Type.Namespace, ServiceNamespace, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(service.Type.Type, ServiceType, StringComparison.OrdinalIgnoreCase)) {
            return Refuse(
                $"The action group's service is a {service.Type} resource. It must be a "
                + $"{ServiceNamespace}/{ServiceType} resource — the sending service the notification goes through.",
                "/properties/actionGroup/service"
            );
        }

        // ⚠ THREE REFUSALS THE SCHEMA CANNOT MAKE, AND WHY THEY ARE HERE AND NOT THERE. The
        // schema vocabulary has no item count and no multiple-of — SchemaProperty's bounds are
        // per element on an array, and Minimum/Maximum are inclusive ends — so an empty recipient
        // list, twenty-one recipients and an interval of 90 all pass ResourceSchema.Validate and
        // land here, where the operation fails with the pointer rather than the PUT. Adding either
        // constraint to the vocabulary is a registry change with four emitters and the
        // compatibility gate behind it, and it is the right fix; until it lands, this is the
        // honest one, and the schema descriptions say the refusal happens at reconcile.
        var intervalSeconds = Whole(Member(desired, "evaluation", "intervalSeconds"), DefaultIntervalSeconds);
        if (intervalSeconds % MinIntervalSeconds != 0) {
            return Refuse(
                $"The evaluation interval is {intervalSeconds} seconds and must be a multiple of {MinIntervalSeconds}: "
                + "the evaluator ticks once a minute, and a rule at 90 would be evaluated every 120 seconds "
                + "and called 90.",
                "/properties/evaluation/intervalSeconds"
            );
        }

        var recipients = Strings(Member(desired, "actionGroup", "recipients"))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0)
            .ToImmutableArray();

        if (recipients.Length == 0) {
            return Refuse(
                "The action group names no recipient. Nobody would be told.",
                "/properties/actionGroup/recipients"
            );
        }

        if (recipients.Length > MaxRecipients) {
            return Refuse(
                $"The action group names {recipients.Length} recipients and the most is {MaxRecipients}. "
                + "A distribution list belongs on the sending side.",
                "/properties/actionGroup/recipients"
            );
        }

        var workspace = WorkspaceOf(id);

        return Result<AlertRuleSpec>.Success(
            new() {
                RuleId = id.Id,
                Name = id.Name,
                Workspace = workspace.Name,
                WorkspacePath = workspace.CanonicalPath,
                Enabled = Flag(Property(desired, "enabled"), true),
                Severity = severity,
                Condition = new() {
                    Signal = signal,
                    Query = Text(Member(desired, "condition", "query"), string.Empty),
                    Operator = op,
                    Threshold = Number(Member(desired, "condition", "threshold"), 0d),
                    Lookback = TimeSpan.FromSeconds(
                        Whole(Member(desired, "condition", "lookbackSeconds"), DefaultLookbackSeconds)
                    )
                },
                Interval = TimeSpan.FromSeconds(intervalSeconds),
                For = TimeSpan.FromSeconds(Whole(Member(desired, "evaluation", "forSeconds"), 0)),
                ActionGroup = new() {
                    ServicePath = servicePath,
                    ServiceId = CommunicationGrainKeys.ResourceIdFor(service.TenantId, service.CanonicalPath),
                    Channel = channel,
                    Recipients = recipients,
                    NotifyOnResolve = Flag(Member(desired, "actionGroup", "notifyOnResolve"), true)
                }
            }
        );
    }

    /// <summary>Whether what the evaluator holds is what the body asks for.</summary>
    /// <param name="held">The snapshot the evaluator holds for the rule.</param>
    /// <param name="id">The rule.</param>
    /// <param name="desired">The desired body.</param>
    public static bool Matches(AlertRuleSnapshot held, ResourceId id, JsonElement desired) {
        ArgumentNullException.ThrowIfNull(held);

        return ToSpec(id, desired).TryGetValue(out var wanted) && held.Spec.SameAs(wanted);
    }

    // ── Spellings, both ways ─────────────────────────────────────────────────────────────────

    /// <summary>The body spelling of a signal.</summary>
    public static string Spell(AlertSignal signal) => signal == AlertSignal.Logs ? "logs" : "metrics";

    /// <summary>The body spelling of a severity.</summary>
    public static string Spell(AlertSeverity severity) =>
        severity switch {
            AlertSeverity.Critical => "critical",
            AlertSeverity.Error => "error",
            AlertSeverity.Warning => "warning",
            _ => "informational"
        };

    /// <summary>The body spelling of an operator, and the symbol a notification prints.</summary>
    public static string Spell(AlertOperator op) =>
        op switch {
            AlertOperator.GreaterThan => "greaterThan",
            AlertOperator.GreaterOrEqual => "greaterOrEqual",
            AlertOperator.LessThan => "lessThan",
            AlertOperator.LessOrEqual => "lessOrEqual",
            AlertOperator.Equal => "equal",
            _ => "notEqual"
        };

    /// <summary>The symbol a notification prints for an operator.</summary>
    public static string Symbol(AlertOperator op) =>
        op switch {
            AlertOperator.GreaterThan => ">",
            AlertOperator.GreaterOrEqual => ">=",
            AlertOperator.LessThan => "<",
            AlertOperator.LessOrEqual => "<=",
            AlertOperator.Equal => "=",
            _ => "!="
        };

    /// <summary>The body spelling of a state.</summary>
    public static string Spell(AlertRuleState state) =>
        state switch {
            AlertRuleState.Pending => "pending",
            AlertRuleState.Firing => "firing",
            _ => "ok"
        };

    /// <summary>The body spelling of a channel — the sending module's five words.</summary>
    public static string Spell(ChannelKind channel) =>
        channel switch {
            ChannelKind.Sms => "sms",
            ChannelKind.WhatsApp => "whatsapp",
            ChannelKind.Email => "email",
            ChannelKind.Push => "push",
            ChannelKind.Voice => "voice",
            _ => string.Empty
        };

    /// <summary>A signal from its body spelling, or <see cref="AlertSignal.Unknown" />.</summary>
    public static AlertSignal ParseSignal(string spelled) =>
        spelled switch {
            "metrics" => AlertSignal.Metrics,
            "logs" => AlertSignal.Logs,
            _ => AlertSignal.Unknown
        };

    /// <summary>A severity from its body spelling, or <see cref="AlertSeverity.Unknown" />.</summary>
    public static AlertSeverity ParseSeverity(string spelled) =>
        spelled switch {
            "critical" => AlertSeverity.Critical,
            "error" => AlertSeverity.Error,
            "warning" => AlertSeverity.Warning,
            "informational" => AlertSeverity.Informational,
            _ => AlertSeverity.Unknown
        };

    /// <summary>An operator from its body spelling, or <see cref="AlertOperator.Unknown" />.</summary>
    public static AlertOperator ParseOperator(string spelled) =>
        spelled switch {
            "greaterThan" => AlertOperator.GreaterThan,
            "greaterOrEqual" => AlertOperator.GreaterOrEqual,
            "lessThan" => AlertOperator.LessThan,
            "lessOrEqual" => AlertOperator.LessOrEqual,
            "equal" => AlertOperator.Equal,
            "notEqual" => AlertOperator.NotEqual,
            _ => AlertOperator.Unknown
        };

    /// <summary>A channel from its body spelling, or <see cref="ChannelKind.Unknown" />.</summary>
    public static ChannelKind ParseChannel(string spelled) =>
        spelled switch {
            "sms" => ChannelKind.Sms,
            "whatsapp" => ChannelKind.WhatsApp,
            "email" => ChannelKind.Email,
            "push" => ChannelKind.Push,
            "voice" => ChannelKind.Voice,
            _ => ChannelKind.Unknown
        };

    // ── What the actions and the observation render ──────────────────────────────────────────

    /// <summary>One instance, as <see cref="ListInstancesResponse" />'s <c>/instances</c> spells it.</summary>
    public static string InstanceLine(AlertInstance instance) {
        ArgumentNullException.ThrowIfNull(instance);

        var notification = instance.ResolveNotification.Length > 0
            ? $"fire {instance.FireNotification}; resolve {instance.ResolveNotification}"
            : $"fire {instance.FireNotification}";

        return $"{(instance.IsOpen ? "firing" : "resolved")} {Spell(instance.Severity)} fired {Stamp(instance.FiredAt)} "
            + $"resolved {(instance.ResolvedAt is { } at ? Stamp(at) : "-")} value {Number(instance.Value)}: "
            + $"{instance.Summary} | {notification}";
    }

    /// <summary>The <see cref="ListInstancesResponse" /> body for a rule.</summary>
    public static string InstancesJson(AlertRuleSnapshot rule) {
        ArgumentNullException.ThrowIfNull(rule);

        var instances = rule.Instances.IsDefault ? [] : rule.Instances;

        return new JsonObject {
            ["count"] = instances.Length,
            ["open"] = instances.Count(static x => x.IsOpen),
            ["state"] = Spell(rule.State),
            ["instances"] = new JsonArray([.. instances.Select(static x => (JsonNode?)InstanceLine(x))])
        }.ToJsonString();
    }

    /// <summary>The one-line text a fire or resolve notification carries.</summary>
    /// <param name="spec">The rule.</param>
    /// <param name="value">The value that fired it, or the last one seen when it resolved.</param>
    /// <param name="at">When.</param>
    /// <param name="fired">Whether this is the fire text or the resolve text.</param>
    public static string Summary(AlertRuleSpec spec, double value, DateTimeOffset at, bool fired) {
        ArgumentNullException.ThrowIfNull(spec);

        var what = fired ? "FIRING" : "RESOLVED";

        return $"[{Spell(spec.Severity)}] {spec.Name} on workspace {spec.Workspace} {what} at {Stamp(at)}: "
            + $"{spec.Condition.Query} = {Number(value)} {Symbol(spec.Condition.Operator)} {Number(spec.Condition.Threshold)}";
    }

    /// <summary>An RFC 3339 stamp, invariant.</summary>
    public static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A number, invariant, with no more digits than a person reads.</summary>
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────

    static Result<AlertRuleSpec> Refuse(string message, string target) =>
        Result<AlertRuleSpec>.Failure(ErrorCode.InvalidRequestBody, message, target);

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
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

    static long Whole(JsonElement? element, long fallback) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var found) ? found : fallback;

    static double Number(JsonElement? element, double fallback) =>
        element is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var found) ? found : fallback;

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
