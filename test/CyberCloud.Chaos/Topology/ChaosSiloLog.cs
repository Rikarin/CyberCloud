using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace CyberCloud.Chaos.Topology;

/// <summary>
///     Every silo's log, in one file beside the results — <c>silos.log</c> — so a run that
///     violated a row carries its own diagnosis.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The first version of this suite gave the silos no provider at all</b>:
///         <c>ConfigureLogging</c> set a minimum level and nothing listened, so two runs of the
///         review that found rows 3 and 5 not reproducing had nothing to say about why — an
///         operation stuck <c>Running</c> for three minutes after a shard came back, and no line
///         from the silo it was running on. A silo that cannot be asked what it saw is a fault the
///         suite induced and cannot explain.
///     </para>
///     <para>
///         <c>CyberCloud.*</c> at Information, everything else at Warning, appended under a lock:
///         three silos plus the replacements a test starts write to the same file, and the line
///         carries the silo's name so they can be told apart. Not the console — the testing host's
///         stdout is what the test output goes through, and the load suite's finding 4 is what
///         30 000 lines on it does to a p99.
///     </para>
/// </remarks>
sealed class ChaosSiloLog : ILoggerProvider {
    static readonly object Gate = new();

    /// <summary>Where the file goes: beside the results file, or beside the host.</summary>
    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ChaosReport.ResultsPath)!, "silos.log");

    /// <summary>Creates a provider whose lines say "starting" until <see cref="Silo" /> is set.</summary>
    public ChaosSiloLog() {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
    }

    /// <summary>
    ///     How the silo is told apart in the file — its address, stamped by
    ///     <c>ChaosSiloConfigurator</c>'s startup task once the silo knows it.
    /// </summary>
    public string Silo { get; set; } = "starting";

    /// <summary>Appends one line from the test itself, so the faults sit in the same timeline as the silos' reactions.</summary>
    /// <param name="message">What just happened, for example "invariant 5: platform-00 stopped".</param>
    public static void Mark(string message) => Append("test", "MARK", "CyberCloud.Chaos", message, null);

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <inheritdoc />
    public void Dispose() { }

    static void Append(string silo, string level, string category, string message, Exception? exception) {
        var line = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(silo)
            .Append(" [")
            .Append(level)
            .Append("] ")
            .Append(category)
            .Append(": ")
            .Append(message);

        if (exception is not null) {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message.ReplaceLineEndings(" "));
        }

        line.AppendLine();

        lock (Gate) {
            File.AppendAllText(Path, line.ToString());
        }
    }

    sealed class Logger(ChaosSiloLog owner, string category) : ILogger {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (!IsEnabled(logLevel)) {
                return;
            }

            Append(owner.Silo, Level(logLevel), category, formatter(state, exception), exception);
        }

        static string Level(LogLevel level) =>
            level switch {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };
    }
}
