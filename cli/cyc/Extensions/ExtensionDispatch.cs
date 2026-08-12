using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Extensions;

/// <summary>
///     Decides whether a command line <c>cyc</c>'s own parser rejected belongs to an installed
///     extension, and builds what it takes to run one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>AN EXTENSION CANNOT SHADOW A BUILT-IN, AND THAT IS STRUCTURAL RATHER THAN CHECKED.</b>
///         This runs only after <see cref="System.CommandLine.RootCommand" /> has already failed to
///         match the verb, so <c>cyc login</c>, <c>cyc rest</c> and every generated group reach their
///         own code before an extension is even looked for. An extension called <c>login</c> is
///         unreachable rather than dangerous. <see cref="Resolve" /> re-checks the name against
///         <see cref="CommandTree.TopLevelNames" /> anyway, because "unreachable by construction" is
///         a claim worth failing loudly if it stops being true.
///     </para>
///     <para>
///         ⚠ <b>Why the check cannot live where <c>CommandTree.ReservedGroups</c> lives.</b> That one
///         <i>throws</i>, and it runs while the root command is built, so a colliding group takes down
///         <c>cyc --help</c> along with everything else (<c>ReservedGroupTests</c> pins that blast
///         radius). That is the right cost for a generated tree, which is ours and whose collision is
///         a build defect. It is the wrong cost entirely for a name that came out of a directory a
///         user can write: a file called <c>cyc-login</c> must not be able to disable the CLI. So the
///         generated tree fails loudly and an extension is refused quietly — at install, and again on
///         every invocation.
///     </para>
///     <para>
///         ⚠ <b>Nothing is read from disk on the happy path.</b> A parse that succeeded never reaches
///         here, so <c>cyc account get-access-token</c> — which the SDK runs for every token it cannot
///         serve from the cache — costs no directory scan and no index read. The cost is that
///         extensions do not appear in <c>cyc --help</c> or in completion; <c>cyc extension list</c>
///         is where they are discovered.
///     </para>
/// </remarks>
static class ExtensionDispatch {
    /// <summary>
    ///     Works out what to run, or <c>null</c> when the failed parse was about something else.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options, which say which leading flags take a value.</param>
    /// <param name="tree">The verb tree the command surface was built from.</param>
    /// <param name="values">What the global flags said.</param>
    /// <param name="arguments">The raw arguments, exactly as typed.</param>
    /// <returns>The launch, or <c>null</c> when no installed extension answers to the verb.</returns>
    /// <exception cref="CycClientException">
    ///     An extension by that name is installed and cannot be run — the file is gone, it does not
    ///     match the hash recorded at install, or the directory holding it is writable by more than
    ///     its owner. ⚠ Falling through to "unrecognized command" here would be the quiet skip this
    ///     whole design exists to avoid.
    /// </exception>
    public static ExtensionLaunch? Resolve(
        CycHost host,
        GlobalOptions globals,
        VerbTreeDocument tree,
        GlobalValues values,
        string[] arguments) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(values);

        if (FirstOperand(globals, arguments) is not { } operand)
            return null;

        var (name, index) = operand;

        // The re-check. It should be unreachable — the parse would have matched — and it is here so
        // that a change which makes it reachable fails a test rather than shipping a shadowable CLI.
        if (CommandTree.TopLevelNames(tree).Contains(name))
            return null;

        var store = ExtensionStore.Open(host);

        if (store.Find(name) is not { } record) {
            RefuseUnregistered(store, name);

            return null;
        }

        Verify(host, store, record);

        var settings = CycSettings.Resolve(host.Config, host.Environment, values.Profile);

        return new ExtensionLaunch(
            record.Name,
            store.PathFor(record.Name),
            [.. arguments.Skip(index + 1)],
            ExtensionLauncher.EnvironmentFor(record.Name, settings, tree.ApiVersion, values.Output, host.ExecutablePath));
    }

    /// <summary>
    ///     Refuses to run an executable that is sitting in the install directory but was never
    ///     installed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The index is the authority, not the directory listing.</b> Running whatever is present
    ///     would make copying a file into <c>~/.cyc/extensions</c> equivalent to installing it, and the
    ///     install step is where the whole trust decision lives (<see cref="ExtensionStore" />). But
    ///     saying nothing would be worse than either: whoever put the file there believes it is
    ///     installed, and "unrecognized command" sends them hunting for a <c>PATH</c> mechanism that
    ///     does not exist.
    /// </remarks>
    static void RefuseUnregistered(ExtensionStore store, string name) {
        if (!store.Unregistered().Contains(name, StringComparer.OrdinalIgnoreCase))
            return;

        throw new CycClientException(
            $"'{store.PathFor(name)}' exists but no extension named '{name}' is installed, so cyc will not run it. "
            + "cyc runs what its index records, never what happens to be in the directory — install it with "
            + $"'cyc extension add --source {store.PathFor(name)}'.");
    }

    /// <summary>
    ///     Checks that the file about to run is the file that was installed.
    /// </summary>
    /// <remarks>
    ///     ⚠ The hash is re-read on every invocation, not only at install. What that buys and what it
    ///     does not is set out in <see cref="ExtensionStore" />'s remarks: it catches a file replaced
    ///     without the index being rewritten, and it does nothing against someone who can write both.
    ///     The cost is one read of the binary per invocation, checked after the cheap length
    ///     comparison so an obvious mismatch never reads the file at all.
    /// </remarks>
    static void Verify(CycHost host, ExtensionStore store, ExtensionRecord record) {
        if (ExtensionStore.UnsafeDirectories(host) is { Count: > 0 } unsafeDirectories)
            throw new CycClientException(
                $"cyc will not run an extension out of a directory anyone but you can write to: "
                + $"{string.Join(", ", unsafeDirectories)}. Anything writable there runs as you, with your cloud "
                + "credentials. Run 'chmod go-w' on it, then try again.");

        var path = store.PathFor(record.Name);
        var file = new FileInfo(path);

        if (!file.Exists)
            throw new CycClientException(
                $"'{record.Name}' is installed but '{path}' is gone. Reinstall it with 'cyc extension add', "
                + $"or forget it with 'cyc extension remove {record.Name}'.");

        if (file.Length != record.Size || !string.Equals(ExtensionStore.HashOf(path), record.Sha256, StringComparison.Ordinal))
            throw new CycClientException(
                $"'{path}' is not the file that was installed as '{record.Name}' — its contents changed and the index "
                + "was not updated. cyc will not run it. Reinstall it with 'cyc extension add --source … --force' if the "
                + "change was yours.");
    }

    /// <summary>
    ///     The first argument that is not a global flag or a global flag's value, with its index.
    /// </summary>
    /// <param name="globals">The global options, which say which flags take a value.</param>
    /// <param name="arguments">The raw arguments.</param>
    /// <returns>The verb and where it sits, or <c>null</c> when the command line is all flags.</returns>
    /// <remarks>
    ///     ⚠ <b>A hand-written walk rather than <see cref="System.CommandLine.ParseResult" />, because
    ///     the parse this runs after is the one that failed.</b> What is needed is not just the verb
    ///     but <i>where it sits</i>, so everything after it can go to the child in the order it was
    ///     typed — and a <see cref="System.CommandLine.ParseResult" />'s unmatched tokens have already
    ///     lost that. The walk knows only what it has to: a flag written <c>--flag=value</c> carries
    ///     its own value, a flag this host declares with an arity takes the next word, and anything
    ///     else is one token. <c>--</c> ends the flags.
    /// </remarks>
    internal static (string Name, int Index)? FirstOperand(GlobalOptions globals, IReadOnlyList<string> arguments) {
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(arguments);

        var valued = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in globals.All) {
            // ⚠ The MINIMUM, and getting this wrong cost a red test. `--verbose` is an
            // Option<bool>, whose arity is ZeroOrOne rather than Zero — `--verbose true` parses —
            // so a check on the maximum treats it as taking a value and swallows the verb that
            // follows it. Only a flag that must be given a value consumes the next token.
            if (option.Arity.MinimumNumberOfValues == 0)
                continue;

            valued.Add(option.Name);

            foreach (var alias in option.Aliases)
                valued.Add(alias);
        }

        for (var i = 0; i < arguments.Count; i++) {
            var argument = arguments[i];

            if (string.Equals(argument, "--", StringComparison.Ordinal))
                return i + 1 < arguments.Count ? (arguments[i + 1], i + 1) : null;

            if (!argument.StartsWith('-'))
                return (argument, i);

            if (argument.Contains('=', StringComparison.Ordinal))
                continue;

            if (valued.Contains(argument))
                i++;
        }

        return null;
    }
}
