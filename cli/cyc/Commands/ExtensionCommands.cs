using System.CommandLine;
using CyberCloud.Cli.Execution;
using CyberCloud.Cli.Extensions;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc extension</c> — docs/plan/21 § Extensions, the out-of-process model.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This group is the whole of the trust boundary's user interface.</b>
///         <see cref="ExtensionStore" /> explains the boundary; here it turns into three commands.
///         <c>add</c> is the moment a user decides to give a program their cloud credentials, so it is
///         where the warning goes — once, where it is actionable, rather than on every invocation
///         where it would be trained away.
///     </para>
///     <para>
///         ⚠ <b>Exit codes split by whose mistake it was.</b> A bad name, a name that shadows a
///         built-in, a source file that is not there — <see cref="ExitCode.Usage" />, because the
///         command line can be corrected. A refusal on integrity grounds — a changed binary, a
///         world-writable directory, a file nobody installed — <see cref="ExitCode.ClientError" />,
///         because the command line was right and the machine is not.
///     </para>
/// </remarks>
static class ExtensionCommands {
    /// <summary>Builds the command.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree, which names the groups an extension may not shadow.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        return new Command(
            "extension",
            "Install and list out-of-process extensions. An extension runs as you, with your credentials — see 'add'.") {
            Add(host, globals, tree),
            List(host, globals, tree),
            Remove(host, globals, tree),
        };
    }

    /// <summary>
    ///     <c>cyc extension add</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>--source</c> is a local file and nothing else.</b> No registry, no index, no URL:
    ///     docs/plan/21 names no extension feed, and inventing a download path would put a general
    ///     HTTP client into a CLI whose stated position is that <c>CyberCloud.Sdk</c> owns HTTP
    ///     (cli/README.md § It owns no HTTP and no OAuth). Fetching is the caller's job — <c>curl</c>,
    ///     <c>gh release download</c>, a package manager — and the refusal below says so rather than
    ///     failing with a file-not-found for a string that starts with <c>https</c>.
    /// </remarks>
    static Command Add(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var source = new Option<string>("--source") {
            Description = "The executable to install. A local path — cyc does not download extensions.",
            Required = true,
        };

        var name = new Option<string>("--name") {
            Description = "The verb it answers to. Defaults to the file name with its 'cyc-' prefix removed.",
        };

        var force = new Option<bool>("--force") {
            Description = "Replace an extension of the same name.",
        };

        var command = new Command("add", "Install an extension from a local executable.") { source, name, force };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var path = parse.GetRequiredValue(source);

            if (Uri.TryCreate(path, UriKind.Absolute, out var url) && !url.IsFile)
                throw new CycUsageException(
                    $"'{path}' is a URL and cyc does not download extensions — it owns no general-purpose HTTP client, "
                    + "by design. Fetch the executable yourself, then point --source at the file.");

            if (!File.Exists(path))
                throw new CycUsageException($"'{path}' is not a file, so there is nothing to install.");

            var verb = parse.GetValue(name) ?? DeriveName(path);

            if (!ExtensionStore.IsLegalName(verb))
                throw new CycUsageException(
                    $"'{verb}' cannot be an extension name. Names are lower-case letters, digits and hyphens, start with a "
                    + "letter and are at most 64 characters — the name becomes both a verb and a file name, so anything else "
                    + "is a path waiting to escape ~/.cyc/extensions. Pass --name to choose one.");

            AssertDoesNotShadow(tree, verb);

            var store = ExtensionStore.Open(host);

            if (ExtensionStore.UnsafeDirectories(host) is { Count: > 0 } unsafeDirectories)
                throw new CycClientException(
                    "cyc will not install an extension into a directory anyone but you can write to: "
                    + $"{string.Join(", ", unsafeDirectories)}. Run 'chmod go-w' on it, then try again.");

            if (store.Find(verb) is not null && !parse.GetValue(force))
                throw new CycUsageException($"'{verb}' is already installed. Pass --force to replace it.");

            var installed = store.Install(verb, path);
            var record = installed.Find(verb)!;

            // ⚠ Said once, at the only moment it can change anybody's mind. `krew` prints "not
            // audited for security" and `gh` says extensions are "not verified, signed, or endorsed
            // by GitHub"; neither sandboxes and neither does this. What is different here is that the
            // sentence names the specific thing being handed over.
            invocation.Console.Note(
                $"Installed '{verb}'. ⚠ 'cyc {verb}' now runs {installed.PathFor(verb)} as you. It is not sandboxed, not "
                + "audited and not signed, and it can read your access token by running 'cyc account get-access-token' "
                + $"exactly as you can. Remove it with 'cyc extension remove {verb}'.");

            invocation.Render(Payload.Object([
                new KeyValuePair<string, Payload>("name", Payload.Text(verb)),
                new KeyValuePair<string, Payload>("path", Payload.Text(installed.PathFor(verb))),
                new KeyValuePair<string, Payload>("sha256", Payload.Text(record.Sha256)),
                new KeyValuePair<string, Payload>("source", Payload.Text(record.Source ?? path)),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     <c>cyc extension list</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Nothing installed prints an empty list; something broken never prints as though it
    ///     worked.</b> Every entry carries a <c>state</c>, and anything that is not <c>ok</c> also
    ///     gets a line on stderr, because a script reading <c>--output json</c> and a person reading a
    ///     table need the same news by different routes. An unreadable index is not an empty list
    ///     either — <see cref="ExtensionStore.Open" /> throws instead.
    /// </remarks>
    static Command List(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var command = new Command("list", "Every installed extension, and whether cyc would run it.");

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var store = ExtensionStore.Open(host);
            var unsafeDirectories = ExtensionStore.UnsafeDirectories(host);
            var reserved = CommandTree.TopLevelNames(tree);
            var rows = new List<Payload>();

            foreach (var directory in unsafeDirectories)
                invocation.Console.Note($"⚠ '{directory}' is writable by more than you, so cyc will run no extension at all.");

            foreach (var record in store.Records.OrderBy(x => x.Name, StringComparer.Ordinal)) {
                var state = StateOf(store, record, reserved, unsafeDirectories.Count > 0);

                if (!string.Equals(state, "ok", StringComparison.Ordinal))
                    invocation.Console.Note($"⚠ '{record.Name}' is {state}: 'cyc {record.Name}' will not run.");

                rows.Add(Payload.Object([
                    new KeyValuePair<string, Payload>("name", Payload.Text(record.Name)),
                    new KeyValuePair<string, Payload>("state", Payload.Text(state)),
                    new KeyValuePair<string, Payload>("path", Payload.Text(store.PathFor(record.Name))),
                    new KeyValuePair<string, Payload>("sha256", Payload.Text(record.Sha256)),
                    new KeyValuePair<string, Payload>("source", record.Source is null ? Payload.Null : Payload.Text(record.Source)),
                    new KeyValuePair<string, Payload>(
                        "installed",
                        Payload.Text(record.Installed.ToString("O", CultureInfo.InvariantCulture))),
                ]));
            }

            foreach (var stray in store.Unregistered()) {
                invocation.Console.Note(
                    $"⚠ '{store.PathFor(stray)}' is in the extensions directory but was never installed, so it will not run. "
                    + $"Install it with 'cyc extension add --source {store.PathFor(stray)}'.");

                rows.Add(Payload.Object([
                    new KeyValuePair<string, Payload>("name", Payload.Text(stray)),
                    new KeyValuePair<string, Payload>("state", Payload.Text("unregistered")),
                    new KeyValuePair<string, Payload>("path", Payload.Text(store.PathFor(stray))),
                    new KeyValuePair<string, Payload>("sha256", Payload.Null),
                    new KeyValuePair<string, Payload>("source", Payload.Null),
                    new KeyValuePair<string, Payload>("installed", Payload.Null),
                ]));
            }

            invocation.Render(Payload.Array(rows));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    static Command Remove(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        var name = new Argument<string>("name") { Description = "The extension's verb." };
        var command = new Command("remove", "Uninstall an extension and delete its executable.") { name };

        command.SetAction(parse => {
            var invocation = CycRunner.Bind(host, globals, tree, parse);
            var verb = parse.GetRequiredValue(name);
            var store = ExtensionStore.Open(host);

            if (!store.Remove(verb)) {
                var installed = store.Records.Count == 0
                    ? "Nothing is installed."
                    : $"Installed: {string.Join(", ", store.Records.Select(x => x.Name).Order(StringComparer.Ordinal))}.";

                throw new CycUsageException($"No extension named '{verb}' is installed. {installed}");
            }

            invocation.Console.Note($"Removed '{verb}'.");

            invocation.Render(Payload.Object([
                new KeyValuePair<string, Payload>("name", Payload.Text(verb)),
                new KeyValuePair<string, Payload>("removed", Payload.Boolean(value: true)),
            ]));

            return (int)ExitCode.Ok;
        });

        return command;
    }

    /// <summary>
    ///     Refuses a name the host or the generated tree already owns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Refusing at install is a courtesy; the invocation path is the guarantee.</b> A name
    ///     checked here can still be shadowed later — the generated tree grows a <c>postgres</c> group
    ///     in a release after somebody installed <c>cyc-postgres</c>, and from that day the built-in
    ///     wins. <see cref="ExtensionDispatch" /> makes that outcome safe rather than surprising, and
    ///     <c>cyc extension list</c> reports the extension as <c>shadowed</c> so it is visible instead
    ///     of merely silent.
    /// </remarks>
    static void AssertDoesNotShadow(VerbTreeDocument tree, string verb) {
        if (!CommandTree.TopLevelNames(tree).Contains(verb))
            return;

        throw new CycUsageException(
            $"'{verb}' is a command cyc already has, so an extension by that name could never run: cyc's own parser "
            + "matches the built-in first. Choose another name with --name.");
    }

    /// <summary>
    ///     What <c>cyc &lt;name&gt;</c> would do with this entry today.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="record">The index entry.</param>
    /// <param name="reserved">The names the host and the generated tree own.</param>
    /// <param name="unsafeDirectory">Whether a directory on the way to the file is writable by others.</param>
    static string StateOf(ExtensionStore store, ExtensionRecord record, IReadOnlySet<string> reserved, bool unsafeDirectory) {
        if (unsafeDirectory)
            return "unsafe-directory";

        if (reserved.Contains(record.Name))
            return "shadowed";

        var file = new FileInfo(store.PathFor(record.Name));

        if (!file.Exists)
            return "missing";

        if (file.Length != record.Size || !string.Equals(ExtensionStore.HashOf(file.FullName), record.Sha256, StringComparison.Ordinal))
            return "modified";

        if (!OperatingSystem.IsWindows() && (file.UnixFileMode & UnixFileMode.UserExecute) == 0)
            return "not-executable";

        return "ok";
    }

    /// <summary>
    ///     The verb a source file implies — <c>/build/cyc-shell</c> means <c>shell</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ A file without the prefix returns its own name, which <see cref="ExtensionStore.IsLegalName" />
    ///     then judges. Guessing harder — stripping extensions, lower-casing — would install
    ///     <c>MyTool.exe</c> under a name its author never chose, and <c>--name</c> already exists for
    ///     exactly that case.
    /// </remarks>
    static string DeriveName(string path) {
        var file = Path.GetFileName(path);

        return file.StartsWith(ExtensionStore.FilePrefix, StringComparison.Ordinal)
            ? file[ExtensionStore.FilePrefix.Length..]
            : file;
    }
}
