namespace CyberCloud.Cli;

/// <summary>
///     docs/plan/21 § Decisions: <i>"Update check | Once a day, non-blocking, never auto-installs."</i>
/// </summary>
/// <remarks>
///     <para>
///         All three words are load-bearing and each is implemented rather than intended:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Once a day</b> — a stamp file in <c>~/.cyc</c> holds the last check. It is written
///             before the check runs, so a feed that is down does not turn into a check on every
///             invocation.
///         </item>
///         <item>
///             <b>Non-blocking</b> — <see cref="Start" /> returns a task nothing awaits. The command
///             runs and the process exits on its own schedule; a half-finished check is abandoned,
///             which costs a stamp and nothing else. ⚠ It is never awaited even at exit: a CLI that
///             waited on a network call it did not need is a CLI that hangs behind a corporate proxy.
///         </item>
///         <item>
///             <b>Never auto-installs</b> — there is no code path here that writes an executable,
///             spawns a package manager or downloads anything. It prints one line to stderr naming the
///             version and stops.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>There is no release feed yet, so by default this checks nothing.</b> The mechanism is
///         wired and the probe is injected: <c>CYC_UPDATE_FEED</c> names a URL returning
///         <c>{"version": "…"}</c>, and without it the check records the stamp and returns. Building
///         the machinery around a feed that does not exist is what would have to be undone later;
///         building the machinery and leaving the URL to configuration is not.
///     </para>
/// </remarks>
static class UpdateCheck {
    /// <summary>The environment variable naming the release feed.</summary>
    public const string FeedVariable = "CYC_UPDATE_FEED";

    /// <summary>How long between checks.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromDays(1);

    /// <summary>
    ///     Starts a check if one is due.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="currentVersion">This build's version.</param>
    /// <param name="probe">Asks the feed for the newest version, or <c>null</c> to use the environment's.</param>
    /// <returns>The task, which the caller does not await.</returns>
    public static Task Start(CycHost host, string currentVersion, Func<CancellationToken, Task<string?>>? probe = null) {
        ArgumentNullException.ThrowIfNull(host);

        if (host.Environment.ContainsKey("CYC_NO_UPDATE_CHECK"))
            return Task.CompletedTask;

        if (!IsDue(host))
            return Task.CompletedTask;

        Stamp(host);

        probe ??= _ => Task.FromResult<string?>(null);

        return RunAsync(host, currentVersion, probe);
    }

    static async Task RunAsync(CycHost host, string currentVersion, Func<CancellationToken, Task<string?>> probe) {
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2), host.Time);

            if (await probe(deadline.Token).ConfigureAwait(false) is not { Length: > 0 } newest)
                return;

            if (string.Equals(newest, currentVersion, StringComparison.Ordinal))
                return;

            // ⚠ One line, on stderr, naming the command to run. Not a download, not a prompt, not a
            // banner around the answer the user asked for.
            host.Console.Note($"cyc {newest} is available; you have {currentVersion}. See the release notes to upgrade.");
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException) {
            // An update check that fails is not news. Nothing about the command the user ran depends
            // on it, so it dies quietly by design.
        }
    }

    /// <summary>Whether a day has passed since the last check.</summary>
    /// <param name="host">The host.</param>
    public static bool IsDue(CycHost host) {
        ArgumentNullException.ThrowIfNull(host);

        var path = StampPath(host);

        try {
            if (!File.Exists(path))
                return true;

            var text = File.ReadAllText(path).Trim();

            return !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var last)
                || host.Time.GetUtcNow() - last >= Interval;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static void Stamp(CycHost host) {
        try {
            Directory.CreateDirectory(host.StateDirectory);
            File.WriteAllText(StampPath(host), host.Time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // A read-only home directory means the check runs every time. That is a nuisance, not a
            // failure, and it is not worth an error on a command that was about something else.
        }
    }

    static string StampPath(CycHost host) => Path.Combine(host.StateDirectory, "update-check");
}
