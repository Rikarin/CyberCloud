using CyberCloud.Core.Security;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System.Diagnostics.Metrics;

namespace CyberCloud.ServiceDefaults.Logging;

/// <summary>
///     The last thing a log event passes through before it leaves the process. Credential-shaped text
///     in it is replaced, the event is marked, and a counter goes up.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/18 § Platform security, row Secrets asks for <i>"a log-scanning canary that
///         alerts on a key-shaped string in the log pipeline"</i>.
///         <see cref="SecretShapedText" /> is the scanning; this is the attachment point and the
///         alert.
///     </para>
///     <para>
///         ⚠ <b>WHY A SINK WRAPPER AND NOT AN ENRICHER, WHICH IS THE OBVIOUS SHAPE.</b> An
///         <c>ILogEventEnricher</c> runs once per event, before every sink, and can rewrite
///         properties — which handles the common case and none of the others.
///         <see cref="LogEvent.MessageTemplate" /> and <see cref="LogEvent.Exception" /> are both
///         immutable, and an interpolated message and an exception message are two of the three ways
///         a value arrives in a log by accident (the third is the property, which the enricher would
///         have covered). A sink wrapper can build a new event, so it covers all three.
///     </para>
///     <para>
///         ⚠ <b>WHY THIS IS NOT "A CANARY IN THE SAME PROCESS THAT WRITES THE LOG", WHICH PROVES
///         NOTHING.</b> A scanner reading the logs of the process it lives in is a scanner that can
///         only report a leak that has already left. This one sits <i>in front of</i> every sink:
///         nothing is written to stdout and nothing is exported over OTLP until it has been through
///         here, so the finding and the prevention are the same act. The half it genuinely cannot see
///         is a process that logs without going through this pipeline — closed by
///         <c>OrleansApplication</c> clearing every other logging provider, by every sink being
///         declared in code rather than in configuration
///         (<see cref="LogEgress.RefuseConfiguredSinks" />), and by the <c>Log egress</c>
///         architecture gate, which fails the build if any assembly but this one binds a Serilog
///         type.
///     </para>
///     <para>
///         ⚠ <b>IT MUST NEVER LOG.</b> A sink that writes a log line about a log line it did not
///         like feeds itself. The finding leaves as a metric and as a property on the event that
///         carried it, and by no other route.
///     </para>
/// </remarks>
sealed class SecretScrubbingSink : ILogEventSink, IDisposable {
    /// <summary>
    ///     The property added to an event that carried something, naming the rules that fired.
    /// </summary>
    /// <remarks>
    ///     Kept alongside the counter rather than instead of it. The counter is what an alert rule
    ///     watches (docs/plan/16 § Alerts); the property is what turns the alert into the line to go
    ///     and read, without which the responder knows only that <i>something</i> leaked somewhere.
    /// </remarks>
    public const string MarkerProperty = "SecretRedactedBy";

    /// <summary>
    ///     ⚠ Derived from <see cref="ServiceDefaultsExtensions.TelemetrySourcePrefix" /> rather than
    ///     spelled out, because <c>ConfigureOpenTelemetry</c> collects
    ///     <c>AddMeter($"{TelemetrySourcePrefix}.*")</c> and nothing else. A meter named by hand here
    ///     is a counter that increments correctly and is exported nowhere — the alert would then be
    ///     a rule over a series that never appears, which reads as "no leaks" forever.
    /// </summary>
    public static readonly string MeterName = ServiceDefaultsExtensions.TelemetrySourcePrefix + ".Security";

    /// <summary>The counter an alert rule watches. Any non-zero rate is an incident.</summary>
    public const string CounterName = "cybercloud.security.log_secrets_redacted";

    static readonly Meter Meter = new(MeterName);

    static readonly Counter<long> Redactions = Meter.CreateCounter<long>(
        CounterName,
        unit: "{redaction}",
        description: "Credential-shaped runs replaced in a log event before it left the process."
    );

    static readonly MessageTemplateParser TemplateParser = new();

    readonly ILogEventSink inner;

    /// <summary>Wraps <paramref name="inner" />, which is every sink this process writes to.</summary>
    public SecretScrubbingSink(ILogEventSink inner) {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent) {
        ArgumentNullException.ThrowIfNull(logEvent);
        inner.Emit(Scrub(logEvent));
    }

    /// <inheritdoc />
    public void Dispose() => (inner as IDisposable)?.Dispose();

    /// <summary>
    ///     Returns <paramref name="logEvent" /> itself when it is clean, and a rebuilt one when it is
    ///     not.
    /// </summary>
    /// <remarks>
    ///     ⚠ The clean path is the one that matters for cost: it walks the properties, finds nothing,
    ///     and hands back the caller's own event with no allocation. Rebuilding is reserved for the
    ///     events that were going to leak.
    /// </remarks>
    internal static LogEvent Scrub(LogEvent logEvent) {
        var fired = new List<string>();

        var template = logEvent.MessageTemplate;
        if (SecretShapedText.TryRedact(template.Text, out var cleanTemplate, out var templateRules)) {
            // ⚠ Re-parsed rather than carried as literal text. A redaction replaces a run that
            // contains no braces with a marker that contains none either, so every {Property} token
            // in the template survives the round trip and the event still renders against its own
            // properties. A marker pasted in as a literal would break that binding.
            template = TemplateParser.Parse(cleanTemplate);
            fired.AddRange(templateRules);
        }

        var properties = ScrubProperties(logEvent.Properties, fired);
        var exception = ScrubException(logEvent.Exception, fired);

        if (fired.Count == 0) {
            return logEvent;
        }

        foreach (var rule in fired) {
            Redactions.Add(1, new KeyValuePair<string, object?>("rule", rule));
        }

        properties ??= logEvent.Properties.Select(x => new LogEventProperty(x.Key, x.Value)).ToList();
        properties.Add(
            new LogEventProperty(MarkerProperty, new ScalarValue(string.Join(", ", fired.Distinct(StringComparer.Ordinal))))
        );

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            exception,
            template,
            properties,
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default
        );
    }

    /// <summary>
    ///     The properties, or <see langword="null" /> when none of them carried anything.
    /// </summary>
    static List<LogEventProperty>? ScrubProperties(
        IReadOnlyDictionary<string, LogEventPropertyValue> properties,
        List<string> fired
    ) {
        List<LogEventProperty>? scrubbed = null;
        var seen = 0;

        foreach (var property in properties) {
            var clean = ScrubValue(property.Value, fired);

            if (scrubbed is null && !ReferenceEquals(clean, property.Value)) {
                // The first dirty property. Everything before it was clean and is copied across
                // as it stands.
                scrubbed = properties
                    .Take(seen)
                    .Select(x => new LogEventProperty(x.Key, x.Value))
                    .ToList();
            }

            scrubbed?.Add(new LogEventProperty(property.Key, clean));
            seen++;
        }

        return scrubbed;
    }

    /// <summary>
    ///     Walks one property value. Structures, sequences and dictionaries are walked to their
    ///     leaves, because <c>logger.LogInformation("{@Options}", options)</c> is exactly how a
    ///     connection string reaches a log without anybody naming it.
    /// </summary>
    static LogEventPropertyValue ScrubValue(LogEventPropertyValue value, List<string> fired) {
        switch (value) {
            case ScalarValue scalar:
                return ScrubScalar(scalar, fired);

            case SequenceValue sequence: {
                var changed = false;
                var elements = new List<LogEventPropertyValue>(sequence.Elements.Count);

                foreach (var element in sequence.Elements) {
                    var clean = ScrubValue(element, fired);
                    changed |= !ReferenceEquals(clean, element);
                    elements.Add(clean);
                }

                return changed ? new SequenceValue(elements) : sequence;
            }

            case StructureValue structure: {
                var changed = false;
                var members = new List<LogEventProperty>(structure.Properties.Count);

                foreach (var member in structure.Properties) {
                    var clean = ScrubValue(member.Value, fired);
                    changed |= !ReferenceEquals(clean, member.Value);
                    members.Add(new LogEventProperty(member.Name, clean));
                }

                return changed ? new StructureValue(members, structure.TypeTag) : structure;
            }

            case DictionaryValue dictionary: {
                var changed = false;
                var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);

                foreach (var entry in dictionary.Elements) {
                    var key = ScrubScalar(entry.Key, fired);
                    var clean = ScrubValue(entry.Value, fired);
                    changed |= !ReferenceEquals(key, entry.Key) || !ReferenceEquals(clean, entry.Value);
                    entries.Add(KeyValuePair.Create(key, clean));
                }

                return changed ? new DictionaryValue(entries) : dictionary;
            }

            default:
                return value;
        }
    }

    static ScalarValue ScrubScalar(ScalarValue scalar, List<string> fired) {
        if (scalar.Value is null || !CanCarrySecret(scalar.Value)) {
            return scalar;
        }

        var text = scalar.Value as string ?? scalar.Value.ToString();
        if (!SecretShapedText.TryRedact(text, out var clean, out var rules)) {
            return scalar;
        }

        fired.AddRange(rules);
        return new ScalarValue(clean);
    }

    /// <summary>
    ///     Whether a scalar's rendering could contain a credential at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An allow-list of shapes that cannot, rather than a check for <c>string</c>.</b> A
    ///     <see cref="Uri" /> renders <c>redis://user:password@host</c> and is not a string; so does
    ///     an <c>NpgsqlConnectionStringBuilder</c>, and so does any options record somebody passes
    ///     without <c>@</c>. Listing the types whose <c>ToString</c> is a number, a timestamp or an
    ///     identifier keeps those off the scanning path — which is most log properties by count —
    ///     and sends everything else through it, so a type nobody anticipated is scanned rather than
    ///     skipped.
    /// </remarks>
    static bool CanCarrySecret(object value)
        => value is not (bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or char or Guid or DateTime or DateTimeOffset or DateOnly
            or TimeOnly or TimeSpan or Enum);

    /// <summary>
    ///     The exception, or a stand-in whose text is scrubbed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The stand-in keeps the message, the stack and the full rendering, and loses only the
    ///     CLR type.</b> An exception cannot be rewritten in place, and the alternatives are both
    ///     worse: dropping it deletes the diagnostic that was trying to tell somebody what went
    ///     wrong, and keeping it exports the credential that was in its message. The original type's
    ///     name survives inside the scrubbed <c>ToString()</c>, which is what a reader is looking for.
    /// </remarks>
    static Exception? ScrubException(Exception? exception, List<string> fired) {
        if (exception is null) {
            return null;
        }

        var rendered = exception.ToString();
        if (!SecretShapedText.TryRedact(rendered, out var clean, out var rules)) {
            return exception;
        }

        fired.AddRange(rules);

        SecretShapedText.TryRedact(exception.Message, out var message, out _);
        SecretShapedText.TryRedact(exception.StackTrace, out var stack, out _);

        return new RedactedException(
            message ?? exception.Message,
            clean,
            stack ?? exception.StackTrace
        );
    }

    /// <summary>What replaces an exception whose text carried a credential.</summary>
    sealed class RedactedException(string message, string rendered, string? stack) : Exception(message) {
        public override string? StackTrace { get; } = stack;

        public override string ToString() => rendered;
    }
}
