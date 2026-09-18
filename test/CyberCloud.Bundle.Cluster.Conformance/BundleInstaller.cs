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
///         specifically:
///         <i>
///             "a procedure that has been reasoned about and not
///             exercised"
///         </i>. A test that ran <c>helm upgrade --install</c> itself with the same
///         arguments would prove that helm installs cert-manager, which nobody doubted, and would
///         leave the ordering, the flag handling, the bash 3.2 array expansions and the
///         <c>component.yaml</c> reader exactly as unexercised as they were.
///     </para>
///     <para>
///         ⚠ <b><c>--phase 15</c>, and that narrows what the run proves.</b> A selector narrows a
///         run to what it selects, so this exercises the installer's per-component path and NOT its
///         ordering: a defect in the barrier between phases would not be caught here. Installing
///         every phase would mean nineteen operators and three virtual machines in a lane exactly
///         one suite wide, which is the reason the bundle had no cluster-backed proof at all.
///         ⚠ "in a lane narrower than one suite" until issue #81. It is not narrower than one:
///         <c>build/Build.Test.cs</c> § <c>ClusterBackedSuiteDegree</c> is 1, so the lane is one
///         suite exactly, and a lane narrower than that would run nothing at all.
///     </para>
///     <para>
///         ⚠
///         <b>
///             That paragraph used to quote the script's usage text —
///             <i>
///                 "skips that guarantee and
///                 is for repairing one row, not for installing"
///             </i> — and the quotation had been stale since
///             2026-09-03
///         </b>, when <c>--component</c> landed and that sentence was rewritten around it.
///         #74 rewrote the same text again on 2026-09-05, so the quotation is not re-quoted: it is
///         removed. What this paragraph needs is a property of the script's BEHAVIOUR — a selector
///         narrows a run — and quoting prose to establish behaviour is how a citation goes stale
///         without anything going red. <c>charts/bundle/bundle.yaml</c> § owed,
///         <c>a-selector-that-matched-nothing-reported-success</c>, keeps the usage text's history.
///     </para>
///     <para>
///         ⚠
///         <b>
///             That sentence read "in a Testcontainers lane Task #95 capped at four concurrent
///             suites" until the #77 review, and both halves of it had moved
///         </b> — the same stale
///         citation the sibling <c>.csproj</c> and <c>charts/bundle/README.md</c> were corrected for
///         on 2026-09-05, in a third copy nobody looked for. The container cap is derived from the
///         host rather than the literal four, and since #77 a suite that holds a <em>cluster</em> —
///         this one does — waits on a second cap of its own: <c>build/Build.Test.cs</c>
///         § <c>ClusterBackedSuiteDegree</c>, which is <b>1</b>.
///         ⚠
///         <b>
///             That last clause read "The number is deliberately not repeated here;
///             build/README.md § 'The cluster degree is 1' is where it lives" until issue #81, and the
///             same sentence had already said "at one" three words earlier.
///         </b> It cannot not be
///         repeated: the section it points at carries the number in its own heading. So the number is
///         stated once, plainly, and build/README.md § "The cluster degree is 1" is where it is
///         ARGUED rather than merely written down — that it is the invariant fifteen of the seventeen
///         cluster-backed assemblies already keep among themselves through <c>ClusterSlot</c>. ⚠ What
///         would make this stale is that constant moving, and unlike <c>ContainerBackedSuiteDegree</c>
///         beside it, it is neither derived from the host nor overridable, so it moves only by
///         somebody editing the line.
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

    /// <summary>
    ///     How long the installer gets for a run that selects one <c>install: helm</c> component
    ///     before the test gives up on it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Longer than <c>install.sh</c>'s own <c>--timeout 10m</c> on the helm call, so a helm
    ///     timeout surfaces as helm's message rather than as this harness killing the process. A
    ///     harness that times out first turns every slow install into the same uninformative failure.
    ///     ⚠
    ///     <b>
    ///         #74 added a SECOND timeout inside the script and this number was left to be
    ///         reconsidered on 2026-09-05, which is recorded here rather than acted on.
    ///     </b> The
    ///     <c>manifest:</c> branch now runs
    ///     <c>kubectl wait --for=condition=Established --timeout=5m crd --all</c> after EVERY
    ///     manifest apply — six of the nineteen rows, up from the two that declare a
    ///     <c>manifestExtra</c> — so the worst case of a run is no longer bounded by helm's 10 m
    ///     alone but by <c>10 m × (helm rows selected) + 5 m × (manifest rows selected)</c>.
    ///     ⚠
    ///     <b>
    ///         Twelve minutes bounded every run this assembly made until 2026-09-17, and that was
    ///         COUNTED rather than assumed — and then a fourth call site made the count wrong.
    ///     </b> Three call sites ran the installer for real rather than <c>--dry-run</c>:
    ///     <c>--phase 15</c> (cert-manager), <c>--phase 25</c> (openebs-localpv) and one
    ///     <c>--component</c> pair (openebs-localpv, cloudnative-pg). The THREE distinct components
    ///     between them all declare <c>install: helm</c>, so none of the three runs reaches the
    ///     manifest branch. The pair was already two helm rows under one 12 m bound — 20 m of helm
    ///     timeouts that this harness would have cut off at 12 — and the docs/plan/24 § Phase 2 story
    ///     (<see cref="M1StoryOnAFreshCluster" />) selects THREE helm rows in one run. A constant
    ///     cannot be honest about a selection it does not know, so the bound is now
    ///     <see cref="BudgetFor" />, which reads the selected components' own <c>install:</c> and
    ///     <c>waitFor:</c> blocks and adds up exactly the timeouts the script would spend. This
    ///     constant is what that arithmetic charges ONE helm row, and the three older call sites
    ///     still pay it once or twice.
    ///     ⚠ <b>What the arithmetic charges a manifest row, so the next person does not discover it
    ///     as a harness timeout.</b> The row <c>charts/bundle/README.md</c> names as next installs a
    ///     <c>manifest:</c> component: a <c>--phase 40</c> run is three manifest rows and one helm row,
    ///     so 3 × 5 m + 10 m = 25 m of establishment waits, and a full install is 30 m of them alone.
    ///     <c>charts/bundle/bundle.yaml</c> § owed, <c>the-manifest-path-waits-for-nothing</c>,
    ///     carries why that wait is cluster-wide and what it costs.
    ///     ⚠ <b>And since 2026-09-15 a manifest row also waits for its <c>waitFor:</c> entries, 10 m
    ///     each</b>, so the same <c>--phase 40</c> is bounded by 3 × 5 m + 5 × 10 m + 10 m. Even one
    ///     manifest row alone — kubevirt, measured at 1 m 40 s to <c>Deployed</c> on a warm cache and
    ///     about seven minutes on a cold one — would not have fitted under twelve minutes with margin.
    ///     <see cref="BudgetFor" /> reads the <c>waitFor:</c> block so that it does now.
    /// </remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(12);

    /// <summary>The helm branch's <c>--wait --timeout 10m</c>, charged per helm row.</summary>
    static readonly TimeSpan HelmTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The manifest branch's <c>kubectl wait --for=condition=Established --timeout=5m</c>.</summary>
    static readonly TimeSpan EstablishedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The manifest branch's <c>kubectl wait --timeout=10m</c>, charged per <c>waitFor:</c> entry.</summary>
    static readonly TimeSpan WaitForTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     What <see cref="Budget" /> adds on top of one helm timeout: the process start, the chart
    ///     fetch, the roster read and the shutdown — everything a run spends that is not a wait.
    /// </summary>
    static readonly TimeSpan Margin = Budget - HelmTimeout;

    /// <summary>
    ///     How long a run of the installer that selects exactly these components gets: the sum of
    ///     every timeout <c>install.sh</c> can spend on them, plus <see cref="Margin" />.
    /// </summary>
    /// <param name="components">The components the run selects, by directory name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read out of each <c>component.yaml</c>, not typed per call site</b>, so the bound
    ///         moves when a component's kind or its <c>waitFor:</c> block does. The rates are the
    ///         script's own: <c>install: helm</c> and <c>helm-archive</c> pay one
    ///         <see cref="HelmTimeout" /> — two when the helm row declares a <c>chartCrds</c> chart,
    ///         which the script installs first with its own <c>--wait</c>; <c>install: manifest</c>
    ///         pays one <see cref="EstablishedTimeout" /> and one <see cref="WaitForTimeout" /> per
    ///         <c>waitFor:</c> entry; <c>install: file</c> waits for nothing, by the script's own
    ///         argument, and pays nothing here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A budget, not a prediction.</b> The three helm rows the story test selects were
    ///         measured together at under three minutes on this host with a warm image cache
    ///         (<see cref="M1StoryOnAFreshCluster" />); this method answers 32 m for them. The gap
    ///         is deliberate and is the same argument <see cref="Budget" /> makes for one row: the
    ///         harness must never be the first to give up, or a slow install reports as a killed
    ///         process rather than as helm's own message naming the resource that never became ready.
    ///     </para>
    /// </remarks>
    public static TimeSpan BudgetFor(IEnumerable<string> components) {
        ArgumentNullException.ThrowIfNull(components);

        var waits = TimeSpan.Zero;

        foreach (var component in components) {
            switch (Pin(component, "install")) {
                case "helm":
                    waits += HelmTimeout;

                    if (!string.IsNullOrEmpty(Pin(component, "chartCrds"))) {
                        waits += HelmTimeout;
                    }

                    break;
                case "helm-archive":
                    waits += HelmTimeout;
                    break;
                case "manifest":
                    waits += EstablishedTimeout + WaitForTimeout * WaitFor(component).Count;
                    break;
                case "file":
                    break;
                default:
                    throw new InvalidOperationException(
                        $"charts/bundle/{component}/component.yaml declares `install: "
                        + $"{Pin(component, "install")}`, which is not one of the four kinds "
                        + "install.sh installs, so this harness cannot say how long the script would "
                        + "wait for it. charts/bundle/README.md § What a component owes."
                    );
            }
        }

        return waits + Margin;
    }

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
    ///     ⚠
    ///     <b>
    ///         The same narrow reader <c>install.sh</c>'s <c>roster()</c> awk is, mirrored rather
    ///         than shared, for the reason <see cref="Pin" /> gives.
    ///     </b> Its two rules are easy to get
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
    ///     ⚠
    ///     <b>
    ///         The same deliberately narrow reader <c>install.sh</c>'s <c>key()</c> is, and for the
    ///         same reason rather than by copying.
    ///     </b> The point of reading the pin here is that the
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
    ///     ⚠
    ///     <b>
    ///         <see cref="Pin" /> cannot read these and would silently return <see langword="null" />
    ///         for every one of them.
    ///     </b> Its loop skips any line that does not begin with a letter, and
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

    /// <summary>
    ///     The entries of a component's <c>images:</c> block, digest included, in file order.
    /// </summary>
    /// <param name="component">The component's directory name.</param>
    /// <remarks>
    ///     The same narrow reader <c>install.sh</c>'s <c>recorded()</c> awk is, for the reason
    ///     <see cref="Pin" /> gives: the block opens at <c>images:</c>, every <c>  - </c> line under
    ///     it is an entry, and any other line ends it.
    /// </remarks>
    public static IReadOnlyList<string> Images(string component) => Sequence(component, "images");

    /// <summary>
    ///     The entries of a component's <c>waitFor:</c> block — one <c>kubectl wait</c> argument
    ///     list each — in file order. Empty for a component that declares none.
    /// </summary>
    /// <param name="component">The component's directory name.</param>
    public static IReadOnlyList<string> WaitFor(string component) => Sequence(component, "waitFor");

    /// <summary>
    ///     The entries of one top-level block sequence, the way <c>install.sh</c>'s <c>recorded()</c>
    ///     and <c>waits()</c> awk read them: the block opens at <c>&lt;key&gt;:</c>, every <c>  - </c>
    ///     line under it is an entry, an indented comment is skipped, and the next top-level key
    ///     ends it.
    /// </summary>
    static List<string> Sequence(string component, string key) {
        var entries = new List<string>();
        var inside = false;

        foreach (var line in File.ReadLines(ComponentFile(component))) {
            if (line.StartsWith(key + ":", StringComparison.Ordinal)) {
                inside = true;
                continue;
            }

            if (line.Length > 0 && char.IsLetter(line[0])) {
                inside = false;
            }

            if (!inside || !line.StartsWith("  - ", StringComparison.Ordinal)) {
                continue;
            }

            entries.Add(line[4..].Trim().Trim('"'));
        }

        return entries;
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
    /// <param name="budget">
    ///     How long the run gets, defaulting to <see cref="Budget" /> — one helm row's worth. A run
    ///     that selects more than one installing component passes <see cref="BudgetFor" />.
    /// </param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         <c>KUBECONFIG</c> is set to the container's file for every run that has one, and it is
    ///         never left unset for a run that applies.
    ///     </b> <c>install.sh</c> falls back to the ambient
    ///     kubeconfig, so a bug that dropped the environment here would install cert-manager into
    ///     whatever cluster the developer running the suite was last pointed at. This is the same
    ///     hazard <c>Build.E2E.cs</c> § <c>ExerciseBootstrap</c> guards with an unresolvable context,
    ///     and the guard here is that the applying test passes a path and the dry-run test passes
    ///     none — a dry run executes nothing, so it has nothing to point anywhere.
    /// </remarks>
    public static Task<Run> RunAsync(
        string arguments,
        string? kubeconfig,
        CancellationToken cancellationToken,
        TimeSpan? budget = null
    ) => RunAsync(Script, arguments, kubeconfig, cancellationToken, budget: budget);

    /// <summary>
    ///     Runs an <c>install.sh</c> that is not the checked-in one — a copy of <c>charts/bundle/</c>
    ///     with one <c>component.yaml</c> sabotaged — with the given arguments.
    /// </summary>
    /// <param name="script">
    ///     The installer to run. Its sibling <c>bundle.yaml</c>, <c>oci.sh</c> and component
    ///     directories are what it reads, so pass the copy's <c>install.sh</c> and not the
    ///     repository's.
    /// </param>
    /// <param name="arguments">Everything after the script path.</param>
    /// <param name="kubeconfig">
    ///     A kubeconfig file for the run to act against, or <see langword="null" /> for a run that
    ///     touches no cluster.
    /// </param>
    /// <param name="cancellationToken">The test's token.</param>
    /// <param name="environment">
    ///     Variables to set for the run — what a person exports before <c>install.sh</c>, which
    ///     is how <c>substitute.sh</c>'s <c>${VAR:=default}</c> pass is overridden. Empty by default.
    /// </param>
    /// <param name="budget">How long the run gets; <see cref="Budget" /> when omitted.</param>
    /// <remarks>
    ///     ⚠ <b>Forward slashes on Windows, and it is not cosmetic.</b> <c>install.sh</c> finds its
    ///     directory with <c>dirname "${BASH_SOURCE[0]}"</c>, and a path handed to Git's bash with
    ///     backslashes makes <c>dirname</c> answer <c>.</c> — so the script would read whatever
    ///     <c>bundle.yaml</c> sits in the working directory, which for this harness is the
    ///     repository root, where there is none. With slashes the MSYS runtime resolves
    ///     <c>C:/…</c> and the script reads the copy it was pointed at. The same rule applies to
    ///     every path in <paramref name="arguments" />, which is split on spaces and passed as is.
    /// </remarks>
    public static async Task<Run> RunAsync(
        string script,
        string arguments,
        string? kubeconfig,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? budget = null
    ) {
        var start = new ProcessStartInfo(Bash ?? "bash") {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        start.ArgumentList.Add(OperatingSystem.IsWindows() ? script.Replace('\\', '/') : script);

        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            start.ArgumentList.Add(argument);
        }

        if (kubeconfig is not null) {
            start.Environment["KUBECONFIG"] = kubeconfig;
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget ?? Budget);

        try {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
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
            // ⚠ '\n' and not AppendLine, which is "\r\n" on Windows. Every assertion in this assembly
            // locates a component by the exact bytes "\n  <name>\n", and the first run of this
            // harness on Windows (2026-09-15, once Bash resolved) failed all nineteen of them on the
            // carriage return before reaching anything about the installer.
            output.Append(line).Append('\n');
        }
    }

    /// <summary>
    ///     Runs a command that is not the installer — <c>helm template</c>, <c>kubectl apply</c>,
    ///     <c>kubectl exec</c> — feeds it <paramref name="input" />, and returns what it said.
    /// </summary>
    /// <param name="command">The command, resolved on <c>PATH</c>.</param>
    /// <param name="arguments">Its arguments, one per entry and never split on spaces.</param>
    /// <param name="input">Standard input, or <see langword="null" /> to leave it closed.</param>
    /// <param name="kubeconfig">
    ///     A kubeconfig file for the command to act against, or <see langword="null" /> for one that
    ///     touches no cluster.
    /// </param>
    /// <param name="token">The test's token.</param>
    /// <remarks>
    ///     ⚠ Standard output and standard error are interleaved into one string, exactly as
    ///     <see cref="RunAsync(string,string,string?,CancellationToken,TimeSpan?)" /> does it and for
    ///     the same reason: a failure report that separates a tool's diagnosis from the line it was
    ///     diagnosing is a report nobody can read. Lines end in <c>'\n'</c> for the reason
    ///     <see cref="Append" /> gives.
    /// </remarks>
    public static async Task<(int ExitCode, string Output)> CaptureAsync(
        string command,
        IReadOnlyList<string> arguments,
        string? input,
        string? kubeconfig,
        CancellationToken token
    ) {
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(command) {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments) {
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

        if (input is not null) {
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(token).ConfigureAwait(false);

        return (process.ExitCode, output.ToString());
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
    /// <param name="command">The command, without an extension.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ Checks exactly what the phase under test needs and nothing else. Phase 15 is one
    ///         <c>helm upgrade --install</c>, so <c>kubectl</c> is not required and is not checked —
    ///         a precondition wider than the run is a suite that skips on a machine where it would
    ///         have passed, which is the quieter half of the same failure as one that runs when it
    ///         should not.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>bash</c> is answered by <see cref="Bash" /> and not by a <c>PATH</c> walk,
    ///             because on Windows the walk finds the wrong bash.
    ///         </b> Until issue #17 this
    ///         method did <c>File.Exists(dir/"bash")</c>, which never matches <c>bash.exe</c>, so
    ///         every test in this assembly skipped on Windows — #74's closing comment records it as
    ///         "the assembly skips wholesale on Windows". Adding <c>.exe</c> would have made it
    ///         worse rather than better: measured on 2026-09-15, <c>Get-Command bash</c> resolves to
    ///         <c>C:\WINDOWS\system32\bash.exe</c>, the WSL launcher, which runs a Linux distribution
    ///         that may not exist and cannot see a <c>C:\</c> checkout as the path this harness
    ///         passes. Other commands do get the <c>.exe</c> probe, since <c>helm.exe</c> and
    ///         <c>kubectl.exe</c> are the same programs by another name.
    ///     </para>
    /// </remarks>
    public static bool OnPath(string command) =>
        string.Equals(command, "bash", StringComparison.Ordinal)
            ? Bash is not null
            : PathDirectories.Any(directory =>
                File.Exists(Path.Combine(directory, command))
                || (OperatingSystem.IsWindows() && File.Exists(Path.Combine(directory, command + ".exe")))
            );

    /// <summary>
    ///     The bash that can run <c>install.sh</c>, or <see langword="null" /> when the machine has
    ///     none.
    /// </summary>
    /// <remarks>
    ///     On Windows this is Git for Windows' bash, found beside the <c>git.exe</c> on <c>PATH</c>
    ///     (<c>Git\cmd\git.exe</c> sits next to <c>Git\bin\bash.exe</c>), and never the
    ///     <c>bash.exe</c> a <c>PATH</c> walk finds first — see <see cref="OnPath" /> for why.
    ///     Everywhere else it is <c>bash</c> if <c>PATH</c> has one.
    /// </remarks>
    public static string? Bash {
        get {
            if (!OperatingSystem.IsWindows()) {
                return PathDirectories.Any(directory => File.Exists(Path.Combine(directory, "bash"))) ? "bash" : null;
            }

            foreach (var directory in PathDirectories) {
                if (!File.Exists(Path.Combine(directory, "git.exe"))) {
                    continue;
                }

                var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));

                if (root is null) {
                    continue;
                }

                var bash = Path.Combine(root, "bin", "bash.exe");

                if (File.Exists(bash)) {
                    return bash;
                }
            }

            return null;
        }
    }

    static string[] PathDirectories =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
