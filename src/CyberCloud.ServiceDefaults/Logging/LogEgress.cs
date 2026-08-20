using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace CyberCloud.ServiceDefaults.Logging;

/// <summary>
///     The rules that make "every log line leaves through one scrubbed path" true rather than
///     intended.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/18 § Platform security, row Secrets. A scanner in front of one sink is not a
///         control; it is a control over that sink. Three things together make the claim hold, and
///         all three are here or one call away:
///     </para>
///     <list type="number">
///         <item>
///             <see cref="ScrubbingSecrets" /> wraps <b>every</b> sink the process writes to, as one
///             unit, rather than being applied per sink.
///         </item>
///         <item>
///             <see cref="RefuseConfiguredSinks" /> makes a sink declared in configuration a startup
///             failure, because such a sink is added outside the wrapper and would export
///             unredacted.
///         </item>
///         <item>
///             <c>OrleansApplication.ConfigureHost</c> clears every other logging provider, so
///             Serilog is the only one an <c>ILogger</c> call reaches.
///         </item>
///     </list>
/// </remarks>
static class LogEgress {
    /// <summary>The configuration sections that create a sink.</summary>
    /// <remarks>
    ///     Both, not just <c>WriteTo</c>. <c>AuditTo</c> is the synchronous, throw-on-failure
    ///     spelling of the same thing and creates a sink by the same route — a control that checked
    ///     only the common spelling would be the narrower-question failure with a tick beside it.
    /// </remarks>
    static readonly string[] SinkSections = ["Serilog:WriteTo", "Serilog:AuditTo"];

    /// <summary>
    ///     Wraps everything <paramref name="configure" /> declares in a
    ///     <see cref="SecretScrubbingSink" />.
    /// </summary>
    /// <param name="to">The <c>WriteTo</c> configuration this wrapper is attached to.</param>
    /// <param name="configure">Declares the real sinks. All of them go behind the one wrapper.</param>
    /// <remarks>
    ///     <see cref="LevelAlias.Minimum" /> on the wrapper because the wrapper is not a filter: the
    ///     level decisions belong to the pipeline in front of it and to the sinks behind it, and a
    ///     minimum level here would silently mean "events below this level are not scrubbed", which
    ///     is the opposite of what a level on a scrubber suggests.
    /// </remarks>
    public static LoggerConfiguration ScrubbingSecrets(
        this LoggerSinkConfiguration to,
        Action<LoggerSinkConfiguration> configure
    ) {
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(configure);

        var wrapped = LoggerSinkConfiguration.Wrap(inner => new SecretScrubbingSink(inner), configure);

        return to.Sink(wrapped, LevelAlias.Minimum);
    }

    /// <summary>
    ///     Refuses to start when configuration declares a sink.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <exception cref="InvalidOperationException">A sink is declared in configuration.</exception>
    /// <remarks>
    ///     ⚠ <b>This is a real constraint on operators and it is the price of the control.</b>
    ///     <c>ReadFrom.Configuration</c> adds sinks to the same collection the wrapper was attached
    ///     to, not inside it, so a <c>Serilog:WriteTo</c> entry in a values override — the natural
    ///     way to add a file or a second collector in a cluster — would write unredacted while every
    ///     test and every gate stayed green. Levels, overrides, enrichers, filters and destructuring
    ///     all still come from configuration; only the sinks moved into code.
    /// </remarks>
    public static void RefuseConfiguredSinks(IConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var section in SinkSections) {
            if (!configuration.GetSection(section).Exists()) {
                continue;
            }

            throw new InvalidOperationException(
                $"Configuration declares a Serilog sink at '{section}', and this host refuses to start "
                + "with one. Sinks are declared in code, in CyberCloud.ServiceDefaults.OrleansApplication, "
                + "so that SecretScrubbingSink wraps every one of them — docs/plan/18 § Platform security, "
                + "row Secrets. A sink configured here is added outside that wrapper and would export log "
                + "events with credentials still in them. Move the sink into ConfigureHost. Serilog's "
                + "MinimumLevel, Override, Enrich, Filter and Destructure sections are unaffected and are "
                + "still read from configuration."
            );
        }
    }
}
