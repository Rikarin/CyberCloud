namespace CyberCloud.Cli.Configuration;

/// <summary>
///     docs/plan/21 § Decisions, the telemetry row, in full: <i>"<b>Opt-in, off by default, and asked
///     once.</b> Opt-out telemetry in a developer tool is a trust cost that is never worth the
///     data."</i>
/// </summary>
/// <remarks>
///     <para>
///         Three properties, each of which the implementation has to earn:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Off by default</b> — <see cref="IsEnabled" /> answers <c>false</c> for a missing
///             setting, an unreadable one and an unrecognised one. There is no spelling of the config
///             file that means "on" by accident.
///         </item>
///         <item>
///             <b>Opt-in</b> — the question's default answer is no, and the answer is written down
///             either way.
///         </item>
///         <item>
///             <b>Asked once</b> — and only where a person can answer. On a non-interactive stderr the
///             answer is recorded as <c>off</c> without asking: a CI job cannot say yes, and a prompt
///             it cannot answer is a hung build.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Nothing is sent anywhere by this build.</b> There is no endpoint, no payload and no
///         background upload; what exists is the consent record that any future collection must check
///         first. Turning it on today changes one line in <c>~/.cyc/config</c> and nothing else, which
///         is the honest state of the feature.
///     </para>
/// </remarks>
static class TelemetryConsent {
    /// <summary>The setting's key in <c>~/.cyc/config</c> — and so <c>CYC_TELEMETRY</c> in the environment.</summary>
    public const string Key = "telemetry";

    /// <summary>Whether the user has said yes.</summary>
    /// <param name="settings">The resolved settings.</param>
    public static bool IsEnabled(CycSettings settings) {
        ArgumentNullException.ThrowIfNull(settings);

        return string.Equals(settings.Get(Key), "on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the question has been put.</summary>
    /// <param name="settings">The resolved settings.</param>
    public static bool WasAsked(CycSettings settings) {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Get(Key) is { Length: > 0 };
    }

    /// <summary>Writes the answer.</summary>
    /// <param name="host">The host, for the state directory.</param>
    /// <param name="settings">The resolved settings.</param>
    /// <param name="enabled">Whether telemetry is on.</param>
    public static void Record(CycHost host, CycSettings settings, bool enabled) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);

        settings.File
            .Set(settings.Profile, Key, enabled ? "on" : "off")
            .Write(Path.Combine(host.StateDirectory, "config"));
    }

    /// <summary>
    ///     Asks, once, if there is a person to ask.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="settings">The resolved settings.</param>
    /// <param name="interactive">Whether stdin and stderr belong to a terminal.</param>
    /// <param name="readAnswer">Reads the answer — <see cref="Console.ReadLine" /> in production.</param>
    /// <remarks>
    ///     ⚠ Failures are swallowed. A read-only home directory is a reason not to record the answer
    ///     and not a reason to fail the command somebody actually ran.
    /// </remarks>
    public static void EnsureAsked(CycHost host, CycSettings settings, bool interactive, Func<string?> readAnswer) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(readAnswer);

        if (WasAsked(settings))
            return;

        try {
            if (!interactive) {
                Record(host, settings, enabled: false);

                return;
            }

            host.Console.Note(string.Empty);
            host.Console.Note("cyc can send anonymous usage telemetry. It is off unless you turn it on, and");
            host.Console.Note("this is the only time you will be asked. Send telemetry? [y/N] ");

            var answer = readAnswer();
            var yes = answer is { Length: > 0 } && (answer[0] is 'y' or 'Y');

            Record(host, settings, yes);
            host.Console.Note(yes ? "Telemetry is on. Turn it off with 'cyc config telemetry off'." : "Telemetry stays off.");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Nowhere to write the answer. Ask again next time rather than failing this run.
        }
    }
}
