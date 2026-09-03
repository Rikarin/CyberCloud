using System.Diagnostics;
using System.Text;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     Runs <c>charts/bundle/install.sh</c> — the real installer, not a re-implementation of it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The script is the subject, so the test may not do the script's job.</b>
///         <c>charts/bundle/bundle.yaml</c> § owed,
///         <c>most-of-the-roster-has-never-been-installed</c>, is about <c>install.sh</c>
///         specifically: <i>"a procedure that has been reasoned about and not
///         exercised"</i>. A test that ran <c>helm upgrade --install</c> itself with the same
///         arguments would prove that helm installs cert-manager, which nobody doubted, and would
///         leave the ordering, the flag handling, the bash 3.2 array expansions and the
///         <c>component.yaml</c> reader exactly as unexercised as they were.
///     </para>
///     <para>
///         ⚠ <b><c>--phase 15</c>, and that narrows what the run proves.</b> The script's own usage
///         text says <c>--phase</c> <i>"skips that guarantee and is for repairing one row, not for
///         installing"</i> — the guarantee being the phase barrier. So this exercises the installer's
///         per-component path and NOT its ordering: a defect in the barrier between phases would not
///         be caught here. Installing every phase would mean nineteen operators and three virtual
///         machines in a Testcontainers lane Task #95 capped at four concurrent suites, which is the
///         reason the bundle had no cluster-backed proof at all.
///     </para>
/// </remarks>
public static class BundleInstaller {
    /// <summary>The phase <c>bundle.yaml</c> gives cert-manager. Read from the file, not typed here.</summary>
    public const string CertManagerComponent = "cert-manager";

    /// <summary>
    ///     The component that installs the storage class eleven <c>charts/managed/</c> charts need.
    /// </summary>
    public const string OpenEbsLocalPvComponent = "openebs-localpv";

    /// <summary>
    ///     The operator behind <c>CyberCloud.DBforPostgreSQL/servers</c>, and the first component in
    ///     this bundle whose install makes an <i>operator</i> create a PersistentVolumeClaim.
    /// </summary>
    public const string CloudNativePgComponent = "cloudnative-pg";

    /// <summary>How long the installer gets before the test gives up on it.</summary>
    /// <remarks>
    ///     ⚠ Longer than <c>install.sh</c>'s own <c>--timeout 10m</c> on the helm call, so a helm
    ///     timeout surfaces as helm's message rather than as this harness killing the process. A
    ///     harness that times out first turns every slow install into the same uninformative failure.
    /// </remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(12);

    /// <summary>The repository root — the directory holding <c>CyberCloud.slnx</c>.</summary>
    /// <remarks>
    ///     ⚠ Walked upward to the solution file rather than counted in <c>..</c> segments, which is
    ///     what every other file-reading test in this repository does: the number of segments between
    ///     a test assembly and the root is a property of the artifacts layout, and it changes without
    ///     anybody deciding to change it.
    /// </remarks>
    public static string RepositoryRoot {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException(
                    "No CyberCloud.slnx above " + AppContext.BaseDirectory + ", so charts/bundle/ "
                    + "cannot be found."
                );
        }
    }

    /// <summary>The installer.</summary>
    public static string Script => Path.Combine(RepositoryRoot, "charts", "bundle", "install.sh");

    /// <summary>A component's manifest.</summary>
    /// <param name="component">The component's directory name.</param>
    public static string ComponentFile(string component) =>
        Path.Combine(RepositoryRoot, "charts", "bundle", component, "component.yaml");

    /// <summary>The roster — <c>charts/bundle/bundle.yaml</c>.</summary>
    public static string RosterFile => Path.Combine(RepositoryRoot, "charts", "bundle", "bundle.yaml");

    /// <summary>
    ///     Every component the roster lists, paired with its phase, in the roster's own order.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same narrow reader <c>install.sh</c>'s <c>roster()</c> awk is, mirrored rather
    ///     than shared, for the reason <see cref="Pin" /> gives.</b> Its two rules are easy to get
    ///     subtly wrong and both matter: <c>components:</c> opens the block, and <b>any</b> other
    ///     line beginning with a lower-case letter closes it — which is what stops the reader
    ///     walking on into <c>ordering:</c> and <c>owed:</c>, where the word <c>name</c> appears in
    ///     prose. A reader that closed the block only on a blank line would return rows install.sh
    ///     never sees.
    ///     ⚠ <b>The order is the roster's, and it is the subject rather than the scaffolding.</b>
    ///     <c>bundle.yaml</c>'s header calls the order "a property of the set", and
    ///     <see cref="BundleInstallSelection" /> compares this sequence against what the installer
    ///     prints. Sorting it here would delete the only thing that comparison can find.
    /// </remarks>
    public static IReadOnlyList<(string Phase, string Component)> Roster() {
        var roster = new List<(string, string)>();
        var inside = false;
        string? name = null;

        foreach (var line in File.ReadLines(RosterFile)) {
            if (line.StartsWith("components:", StringComparison.Ordinal)) {
                inside = true;
                continue;
            }

            if (line.Length > 0 && char.IsLower(line[0])) {
                inside = false;
                continue;
            }

            if (!inside) {
                continue;
            }

            if (line.StartsWith("  - name:", StringComparison.Ordinal)) {
                name = line["  - name:".Length..].Trim();
            } else if (line.StartsWith("    phase:", StringComparison.Ordinal) && name is not null) {
                roster.Add((line["    phase:".Length..].Trim(), name));
            }
        }

        return roster;
    }

    /// <summary>
    ///     The value of a top-level scalar in a <c>component.yaml</c>, or <see langword="null" />.
    /// </summary>
    /// <param name="component">The component's directory name.</param>
    /// <param name="key">The key.</param>
    /// <remarks>
    ///     ⚠ <b>The same deliberately narrow reader <c>install.sh</c>'s <c>key()</c> is, and for the
    ///     same reason rather than by copying.</b> The point of reading the pin here is that the
    ///     assertion and the script arrive at the value independently; a YAML library would still be
    ///     an independent path, but it would also accept documents the script's awk cannot, and then
    ///     a component.yaml that this suite reads and the installer silently does not would look
    ///     green. The Bundle gate already rejects anything outside this subset.
    /// </remarks>
    public static string? Pin(string component, string key) {
        foreach (var line in File.ReadLines(ComponentFile(component))) {
            if (line.Length == 0 || !char.IsLetter(line[0])) {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0 || !line.AsSpan(0, colon).SequenceEqual(key)) {
                continue;
            }

            return line[(colon + 1)..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>
    ///     The value of an entry in a <c>component.yaml</c>'s <c>values:</c> block, or
    ///     <see langword="null" /> when the block or the entry is absent.
    /// </summary>
    /// <param name="component">The component's directory name.</param>
    /// <param name="name">The helm value's dotted name, exactly as the block spells it.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="Pin" /> cannot read these and would silently return <see langword="null" />
    ///     for every one of them.</b> Its loop skips any line that does not begin with a letter, and
    ///     every <c>values:</c> entry is indented — so a test that asked <c>Pin</c> for
    ///     <c>hostpathClass.isDefaultClass</c> would get nothing back and, unless it asserted
    ///     non-null, would pass over a component.yaml with the flag deleted. That is the failure this
    ///     method exists to make unavailable.
    ///     ⚠ It mirrors <c>install.sh</c>'s <c>helm_sets()</c> awk, including the part that is easy to
    ///     miss: a top-level line that is not <c>values:</c> ENDS the block, and a comment line —
    ///     which begins with <c>#</c> and so matches neither of awk's patterns — does not. The
    ///     openebs-localpv manifest has a fourteen-line comment directly above its <c>values:</c>
    ///     block and none inside it, but a reader that got that rule backwards would disagree with
    ///     the installer the first time somebody annotated an entry.
    /// </remarks>
    public static string? Value(string component, string name) {
        var inside = false;

        foreach (var line in File.ReadLines(ComponentFile(component))) {
            if (line.Length == 0) {
                continue;
            }

            if (char.IsLetter(line[0])) {
                inside = line.StartsWith("values:", StringComparison.Ordinal);
                continue;
            }

            if (!inside || line.Length < 3 || line[0] != ' ' || line[1] != ' ' || !char.IsLetter(line[2])) {
                continue;
            }

            var entry = line[2..];
            var colon = entry.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0 || !entry.AsSpan(0, colon).SequenceEqual(name)) {
                continue;
            }

            return entry[(colon + 1)..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>What a run of the installer did.</summary>
    /// <param name="ExitCode">Its exit code.</param>
    /// <param name="Output">Standard output and standard error, interleaved in arrival order.</param>
    public sealed record Run(int ExitCode, string Output);

    /// <summary>
    ///     Runs <c>install.sh</c> with the given arguments.
    /// </summary>
    /// <param name="arguments">Everything after the script path.</param>
    /// <param name="kubeconfig">
    ///     A kubeconfig file for the run to act against, or <see langword="null" /> for a run that
    ///     touches no cluster.
    /// </param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <remarks>
    ///     ⚠ <b><c>KUBECONFIG</c> is set to the container's file for every run that has one, and it is
    ///     never left unset for a run that applies.</b> <c>install.sh</c> falls back to the ambient
    ///     kubeconfig, so a bug that dropped the environment here would install cert-manager into
    ///     whatever cluster the developer running the suite was last pointed at. This is the same
    ///     hazard <c>Build.E2E.cs</c> § <c>ExerciseBootstrap</c> guards with an unresolvable context,
    ///     and the guard here is that the applying test passes a path and the dry-run test passes
    ///     none — a dry run executes nothing, so it has nothing to point anywhere.
    /// </remarks>
    public static async Task<Run> RunAsync(
        string arguments,
        string? kubeconfig,
        CancellationToken cancellationToken
    ) {
        var start = new ProcessStartInfo("bash") {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        start.ArgumentList.Add(Script);

        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            start.ArgumentList.Add(argument);
        }

        if (kubeconfig is not null) {
            start.Environment["KUBECONFIG"] = kubeconfig;
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);

        try {
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            Kill(process);
            throw;
        }

        return new(process.ExitCode, output.ToString());
    }

    static void Append(StringBuilder output, string? line) {
        if (line is null) {
            return;
        }

        lock (output) {
            output.AppendLine(line);
        }
    }

    static void Kill(Process process) {
        try {
            process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
            // It exited between the timeout and the kill. Nothing to do and nothing to report.
        }
    }

    /// <summary>
    ///     Whether a command answers on <c>PATH</c>, so a missing tool is a named skip rather than a
    ///     process-start exception.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <remarks>
    ///     ⚠ Checks exactly what the phase under test needs and nothing else. Phase 15 is one
    ///     <c>helm upgrade --install</c>, so <c>kubectl</c> is not required and is not checked — a
    ///     precondition wider than the run is a suite that skips on a machine where it would have
    ///     passed, which is the quieter half of the same failure as one that runs when it should not.
    /// </remarks>
    public static bool OnPath(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(directory => File.Exists(Path.Combine(directory, command)));
}
