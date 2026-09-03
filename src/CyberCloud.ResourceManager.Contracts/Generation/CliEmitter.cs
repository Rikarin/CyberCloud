using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     ADR-012's second surface: the <c>cyc</c> verb tree, as a machine-readable description the CLI
///     host consumes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/02 § ADR-012's CLI row is "Verb tree, flags, help, completion", and
///         docs/plan/21 § Grammar fixes the shape: <c>cyc &lt;group&gt; &lt;subgroup...&gt;
///         &lt;verb&gt; [--flags]</c>.
///     </para>
///     <para>
///         ⚠ <b>This emits the <i>description</i>, not the CLI.</b> The <c>cyc</c> host — its
///         <c>System.CommandLine</c> wiring, its <c>--output</c> renderers, its token cache, its
///         <c>cyc rest</c> escape hatch — is hand-written and is somebody else's. The seam between
///         them is this file: a build step produces it, the host reads it, and neither has to know
///         how the other works. A generator that emitted C# command classes instead would fuse the
///         two and make every CLI behaviour change a generator change.
///     </para>
///     <para>
///         ⚠ <b>The alias table is generated, and docs/plan/21 § Grammar calls it "the <i>only</i>
///         hand-maintained part of the CLI's surface".</b> A table listing twenty providers' short
///         names, kept in the CLI, is a table nobody adding the twenty-first provider will find.
///         Declared next to the type (<c>IResourceTypeBuilder.Display</c>) it arrives here for free
///         and a duplicate is a build failure rather than a verb that resolves to one of two things.
///     </para>
/// </remarks>
public static class CliEmitter {
    /// <summary>The directory, under the generated root, this surface is written to.</summary>
    public const string DirectoryName = "cli";

    /// <summary>The schema version of the verb-tree format itself.</summary>
    /// <remarks>
    ///     ⚠ Distinct from the api-version. The <c>cyc</c> host reads this file, so the file's own
    ///     shape is a contract between the generator and the host — and a host that silently accepted
    ///     a format it did not understand would mis-parse the flags rather than say so.
    /// </remarks>
    public const string FormatVersion = "1";

    /// <summary>Emits the verb tree for one api-version's document.</summary>
    /// <param name="document">An emitted OpenAPI document.</param>
    /// <returns>The verb tree.</returns>
    public static JsonObject Emit(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        var version = DocumentReader.VersionOf(document);
        var groups = new JsonObject();

        foreach (var type in DocumentReader.TypesOf(document)) {
            var groupName = GroupOf(type);

            if (groups[groupName] is not JsonObject group) {
                group = new JsonObject {
                    ["name"] = groupName,
                    ["summary"] = "Commands for the " + type.ProviderNamespace + " provider.",
                    ["commands"] = new JsonObject()
                };

                groups[groupName] = group;
            }

            var commands = (JsonObject)group["commands"]!;
            var name = CommandOf(type);

            // ⚠ ADD, NEVER ASSIGN. `commands[name] = …` is an indexer that REPLACES, so two resource
            // types whose kebab-cased command names collide would leave one command in the tree and
            // the other nowhere — a resource type silently absent from the CLI, with nothing in the
            // build saying so. `kafkaClustersTopics` and `kafkaClusters/topics` are exactly that
            // pair: both kebab to `kafka-clusters-topics`. Throwing here makes the collision a build
            // failure at the moment it is created; DerivedSurfaces.CliProblems reports it against a
            // checked-in tree as well, because a generator self-check and a pipeline check catch it
            // at different times and this failure class is one this repository keeps re-finding.
            if (commands.ContainsKey(name)) {
                throw new InvalidOperationException(
                    $"'{groupName} {name}' is the command name of two resource types — "
                    + $"'{type.ResourceType}' is the second. One would silently replace the other in "
                    + "the verb tree and that type would vanish from the CLI. Give one of them a "
                    + "distinct type path, or an IResourceTypeBuilder.Display that kebabs "
                    + "differently — docs/plan/21 § Grammar."
                );
            }

            commands.Add(name, Command(type, version));
        }

        // ⚠ THE SECOND WAY ONE TOKEN COMES TO MEAN TWO THINGS, AND THE CHECK ABOVE CANNOT SEE IT. A
        // command name lands in a JsonObject key, so a duplicate is visible there; a SHORT NAME lands
        // in an `alias` member on a command of a different name, so two of them — or one of them and
        // a sibling's command name, or the group's own key — collide only once System.CommandLine
        // builds the group's token dictionary, which is at `cyc` run time and not here. Three agents
        // have hit the ArgumentException that produces. Derived from the document rather than from a
        // list, because the two lists this replaces went stale twice — CliTokens' own remarks.
        var collisions = CliTokens.Collisions(
            DocumentReader.TypesOf(document).Select(
                x => new CliDeclaration(x.ProviderNamespace, x.TypePath, x.Alias)
            )
        );

        if (collisions.Length > 0) {
            throw new InvalidOperationException(string.Join(" ", collisions));
        }

        return new JsonObject {
            ["format"] = FormatVersion,
            ["apiVersion"] = version,
            // ⚠ Named so a reader can tell which document this was derived from. docs/plan/21
            // § Generation makes the OpenAPI document the source of the other three surfaces, and a
            // derived artifact that does not name its source is one nobody can re-derive.
            ["generatedFrom"] = OpenApiArtifacts.DirectoryName + "/" + version + ".json",
            ["description"] =
                "The cyc verb tree at api-version " + version + ". Generated from the OpenAPI "
                + "document — docs/plan/21 § Generation. Hand edits are overwritten by ./build.sh "
                + "Generate and fail the Generated surfaces gate.",
            ["globalFlags"] = GlobalFlags(version),
            ["exitCodes"] = ExitCodes(),
            ["groups"] = Sorted(groups)
        };
    }

    /// <summary>The file one api-version's verb tree is written to.</summary>
    /// <param name="apiVersion">The api-version.</param>
    public static string FileNameOf(string apiVersion) => apiVersion + ".json";

    // ── The tree ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The top-level group: the provider namespace's last segment, lower-cased.
    /// </summary>
    /// <remarks>
    ///     <c>CyberCloud.DBforPostgreSQL</c> becomes <c>dbforpostgresql</c>, which is exactly what
    ///     docs/plan/21 § Grammar's alias table maps <c>postgres</c> onto. The long form always
    ///     exists; the alias is a second name for it rather than a replacement, so a script written
    ///     against the long form keeps working when an alias is added or removed.
    /// </remarks>
    /// <remarks>
    ///     ⚠ Lower-cased whole, not kebab-cased. A namespace segment is a proper noun with its own
    ///     capitalisation — <c>DBforPostgreSQL</c> — and kebab-casing it on case transitions produces
    ///     <c>dbfor-postgre-sql</c>, which is not a word anybody would type. Azure's CLI spells the
    ///     same thing <c>dbforpostgresql</c> and that is what the alias table maps <c>postgres</c>
    ///     onto.
    ///     <para>
    ///         ⚠ <b>Delegated to <see cref="CliTokens" /> rather than computed here</b>, so the
    ///         collision check and the surface it checks derive the group key the same way. Two copies
    ///         of this two-line rule is how a check comes to disagree with the artifact it is guarding.
    ///     </para>
    /// </remarks>
    static string GroupOf(DocumentType type) => CliTokens.GroupOf(type.ProviderNamespace);

    /// <summary>The command name within a group — the type path, kebab-cased, <c>/</c> to <c>-</c>.</summary>
    static string CommandOf(DocumentType type) => CliTokens.CommandOf(type.TypePath);

    static JsonObject Command(DocumentType type, string version) {
        var verbs = new JsonObject();
        var flags = BodyFlags(type);

        // ⚠ The four verbs are the four HTTP operations, and their names are Azure CLI's rather than
        // HTTP's: `create` and `update` for PUT and PATCH, `show` for GET, `delete` for DELETE. A CLI
        // that spelled them `put` and `get` would be a CLI that made its users learn REST.
        verbs["create"] = Verb(
            "create",
            "Create or replace a " + type.DisplayName + ".",
            "PUT",
            type,
            version,
            [.. flags.Where(x => !x.ReadOnly)],
            longRunning: true
        );

        verbs["update"] = Verb(
            "update",
            "Amend a " + type.DisplayName + ". Only the flags given are changed.",
            "PATCH",
            type,
            version,
            // ⚠ Nothing is required on an update: a merge patch legitimately omits everything it is
            // not changing, and a CLI that demanded every required flag on `update` would make the
            // verb useless.
            [.. flags.Where(x => !x.ReadOnly).Select(x => x with { Required = false })],
            longRunning: true
        );

        verbs["show"] = Verb("show", "Read a " + type.DisplayName + ".", "GET", type, version, [], longRunning: false);

        // ⚠ EMITTED ONLY WHEN THE DOCUMENT DECLARES THE PATH. `cyc … list` with no collection path
        // behind it is a verb whose URL does not exist — and this file's whole premise is that a
        // generated surface cannot claim something the registry does not say. DocumentType's
        // CollectionPath is empty when the document has no such path, which is the fact to branch on
        // rather than an assumption that every type has one.
        if (type.CollectionPath.Length > 0) {
            var list = Verb(
                "list",
                "List the " + type.DisplayPlural + " in a resource group.",
                "GET",
                type,
                version,
                PageFlags(type),
                longRunning: false,
                named: false
            );

            list["path"] = type.CollectionPath;

            // ⚠ PAGED, AND THE HOST ACTS ON IT. docs/plan/07 puts ListObjects at M2, so the platform
            // filters a listing one permission check per member and caps the page
            // (ListRequest.MaxPageSize) — a host that read one page and stopped would silently
            // truncate, and a listing is the one response whose truncation looks exactly like a small
            // result.
            //
            // ⚠ THIS MEMBER USED TO SAY there is deliberately no `pageFlags`, because `--top` and
            // `--skip-token` would have been "two constants in assemblies that cannot see each
            // other": CliFlag could bind a body pointer or a path placeholder and had no query
            // binding, so cyc would have accepted both flags, parsed both and sent neither. Both
            // halves are now built — CliFlag.QueryParameter, and the flags above are read off the
            // document's own declared query parameters rather than named here — so the constants are
            // one constant, in the document. Issue #64.
            list["paged"] = true;

            // ⚠ Host behaviour, so it is named here rather than assumed there — the argument
            // `waitFlags` already makes. `--top` and `--skip-token` are contract: they go on the
            // wire and are in `flags` with a `queryParameter`. `--all` sends nothing; it means "keep
            // following nextLink", which turns one command into N round trips and is therefore the
            // host's decision to implement and the tree's to authorise.
            list["pageFlags"] = new JsonArray { "--all" };

            verbs["list"] = list;
        }

        verbs["delete"] = Verb(
            "delete",
            // ⚠ "Recoverable for N day(s)" is now a statement about what the platform does rather
            // than about what it intends to do — docs/plan/08 § Soft delete is built, so the verb
            // parks the resource and `purge` is what ends the window.
            type.SoftDeleteDays > 0
                ? "Delete a " + type.DisplayName + ". Recoverable for "
                + DocumentReader.Count(type.SoftDeleteDays) + " day(s); purge to end that early."
                : "Delete a " + type.DisplayName + ". Permanent.",
            "DELETE",
            type,
            version,
            [],
            longRunning: true
        );

        foreach (var action in type.Actions) {
            verbs[Kebab(action.Name)] = ActionVerb(type, action, version);
        }

        var command = new JsonObject {
            ["name"] = CommandOf(type),
            ["resourceType"] = type.ResourceType,
            ["title"] = type.DisplayName,
            ["plural"] = type.DisplayPlural,
            ["summary"] = type.Summary,
            ["verbs"] = Sorted(verbs)
        };

        if (type.Alias.Length > 0) {
            // The short form people expect: `cyc postgres server create`. Generated from the type's
            // own declaration rather than hand-maintained in the CLI.
            command["alias"] = type.Alias;
        }

        if (type.Deprecated) {
            command["deprecated"] = true;
        }

        return command;
    }

    static JsonObject Verb(
        string name,
        string summary,
        string method,
        DocumentType type,
        string version,
        ImmutableArray<CliFlag> body,
        bool longRunning,
        bool named = true
    ) {
        var flags = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var flag in Address(type, named).AddRange(body).OrderBy(x => x.Name, StringComparer.Ordinal)) {
            // ⚠ THE SIBLING OF THE `commands[name] = …` BUG, AND IT FAILS THE OTHER WAY ROUND. A
            // JsonObject indexer REPLACES, so a colliding command name left one type out of the tree
            // and the loss was invisible because an object cannot hold one key twice. A JsonArray
            // ADDS, so a colliding FLAG name leaves both in — and the loss is equally invisible,
            // because the host binds by name and whichever it resolves first silently wins.
            //
            // FlagsOf folds a colliding body leaf into its dotted path, and that fold is what can
            // land here: `/properties/servers/name` folds to `--servers-name`, which is exactly the
            // ancestor flag a `servers/databases` command now carries. The rename is checked against
            // the address list and the RESULT of the rename is not, so a body that names its parent
            // type reintroduces the collision the fold exists to remove.
            if (!seen.Add(flag.Name)) {
                throw new InvalidOperationException(
                    $"'{flag.Name}' is the name of two flags on '{CommandOf(type)} {name}'. A verb's "
                    + "flags are a JSON array, so both would be emitted and the host would bind one "
                    + "of them — a body property or an ancestor silently unreachable from the "
                    + "command line. Rename the body property, or give the type a path segment whose "
                    + "kebab form differs — docs/plan/21 § Grammar."
                );
            }

            flags.Add(flag.ToJson());
        }

        var verb = new JsonObject {
            ["name"] = name,
            ["summary"] = summary,
            ["method"] = method,
            ["path"] = type.Path,
            ["apiVersion"] = version,
            ["longRunning"] = longRunning,
            ["flags"] = flags
        };

        if (longRunning) {
            // docs/plan/21 § Decisions: "--wait streams the operation's progress array — this is what
            // makes a nine-minute cluster creation bearable in a terminal."
            verb["waitFlags"] = new JsonArray { "--wait", "--no-wait" };
        }

        return verb;
    }

    static JsonObject ActionVerb(DocumentType type, DocumentAction action, string version) {
        var body = action.Request is null
            ? []
            : FlagsOf(action.Request, string.Empty, Address(type));

        var verb = Verb(
            Kebab(action.Name),
            action.Name + " a " + type.DisplayName + ".",
            "POST",
            type,
            version,
            body,
            action.LongRunning
        );

        verb["action"] = action.Name;
        verb["path"] = type.Path + "/" + action.Name;
        verb["permission"] = action.Permission;

        if (action.Secret) {
            // ⚠ A hint to the host, not a decoration: docs/plan/21 § Decisions bans a plaintext token
            // cache for the same reason a shell history full of key material is a leak.
            verb["secret"] = true;
            verb["summary"] = verb["summary"]!.GetValue<string>()
                              + " ⚠ The response carries secret material — it is always audited and "
                              + "must not be written to a log or a shell history.";
        }

        if (action.Request is null) {
            // Reported rather than papered over. A CLI cannot offer flags for a body nobody described.
            verb["rawBody"] = true;
        }

        return verb;
    }

    // ── Flags ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The flags that address a resource. Every verb takes them and none of them is in the body.
    /// </summary>
    /// <param name="type">The resource type, whose path template supplies the placeholders.</param>
    /// <param name="named">
    ///     Whether the verb addresses one resource. <see langword="false" /> for <c>list</c>, whose
    ///     path ends on the type and therefore has no <c>{resourceName}</c> to fill.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off the path template's own placeholders rather than listed, and until
    ///         2026-08-12 that sentence was in these remarks and was not true.</b> The method returned
    ///         a hard-coded four — <c>--name</c>, <c>--resource-group</c>, <c>--subscription</c>,
    ///         <c>--tenant</c> — so a command for a nested type could name the database and never the
    ///         server. That is a URL the CLI cannot build at all, and nothing said so: the flag list
    ///         was simply four long for every type, and a reader comparing it against the emitted
    ///         <c>path</c> on the same verb would have had to notice a placeholder with no flag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Each address flag now names the placeholder it fills.</b> Without it the binding
    ///         from <c>--servers-name</c> to <c>{serversName}</c> is a naming convention the host has
    ///         to re-derive, and a convention shared across a generator and a hand-written host is a
    ///         convention that drifts. <c>jsonPointer</c> answers the same question for body flags;
    ///         <c>pathPlaceholder</c> is its address-side twin.
    ///     </para>
    ///     <para>
    ///         The two that are not required are <c>--tenant</c> and <c>--subscription</c> when a
    ///         profile supplies them — docs/plan/21 § Decisions gives every setting an env var for CI,
    ///         so they are marked as profile-backed rather than omitted. ⚠ An <i>ancestor</i> flag is
    ///         required, and cannot be profile-backed: a profile names where you are working, and
    ///         which server a database is in is part of the address rather than part of the context.
    ///     </para>
    /// </remarks>
    static ImmutableArray<CliFlag> Address(DocumentType type, bool named = true) {
        var flags = ImmutableArray.CreateBuilder<CliFlag>();

        // ⚠ EVERY VERB BUT `list` NAMES A RESOURCE, AND `list` MUST NOT. Its path ends on the type,
        // so there is no {resourceName} placeholder for a --name to fill; emitting one anyway would
        // give the host a flag that binds to nothing, which is the "a placeholder with no flag"
        // failure the remarks above describe, running in the other direction.
        if (named) {
            flags.Add(
                new("--name", "string", "The resource's name within its group.", Required: true) {
                    PathPlaceholder = DocumentReader.ResourceNamePlaceholder
                }
            );
        }

        flags.Add(
            new(
                "--resource-group",
                "string",
                "The resource group. docs/plan/06 § The hierarchy.",
                Required: true
            ) { PathPlaceholder = DocumentReader.ResourceGroupPlaceholder }
        );

        flags.Add(
            new(
                "--subscription",
                "string",
                "The subscription. Defaults to the current profile.",
                Required: false
            ) {
                Environment = "CYC_SUBSCRIPTION", PathPlaceholder = DocumentReader.SubscriptionPlaceholder
            }
        );

        flags.Add(
            new("--tenant", "string", "The tenant. Defaults to the current profile.", Required: false) {
                Environment = "CYC_TENANT", PathPlaceholder = DocumentReader.TenantPlaceholder
            }
        );

        foreach (var placeholder in DocumentReader.AncestorPlaceholdersOf(type.Path)) {
            flags.Add(
                new(
                    "--" + AncestorFlagName(placeholder),
                    "string",
                    "The name of the parent this "
                    + type.DisplayName
                    + " lives inside. docs/plan/12 § Child resources: a child is addressed "
                    + "'…/{parentType}/{parentName}/{childType}/{childName}', so the parent's name is "
                    + "part of the address rather than part of the body.",
                    Required: true
                ) { PathPlaceholder = placeholder }
            );
        }

        return flags.ToImmutable();
    }

    /// <summary>
    ///     One flag per query parameter the collection <c>GET</c> declares — the paging pair.
    /// </summary>
    /// <param name="type">The resource type, whose collection path item supplies the parameters.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read off the document's declared parameters rather than named here, and that is
    ///         the whole of issue #64's second half.</b> The first half was that <c>CliFlag</c> had no
    ///         query binding, so a <c>--top</c> would have been accepted, parsed and never sent. The
    ///         second is subtler and outlives the fix: <c>$top</c> and <c>$skipToken</c> written here
    ///         would be a second copy of what
    ///         <c>OpenApiEmitter.CollectionParameters</c> writes, and a gateway ignores a query
    ///         parameter it does not recognise. A CLI sending <c>$skip-token</c> at a gateway reading
    ///         <c>$skipToken</c> would answer <c>200</c> with page one, for ever, with nothing
    ///         anywhere saying so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The flag name drops the <c>$</c> and kebabs what is left; the wire name keeps
    ///         it.</b> <c>--$top</c> is not a flag any shell makes easy to type, and
    ///         <see cref="CliFlag.QueryParameter" /> carries the name the request needs, so the two
    ///         never have to be derived from each other.
    ///     </para>
    /// </remarks>
    static ImmutableArray<CliFlag> PageFlags(DocumentType type) {
        var flags = ImmutableArray.CreateBuilder<CliFlag>();

        foreach (var parameter in type.CollectionQuery) {
            var name = "--" + Kebab(parameter.Name.TrimStart('$'));

            // ⚠ THE COLLECTION DECLARES `api-version` TOO, AND IT MUST NOT BECOME A VERB FLAG.
            // Reading the document's query parameters rather than naming two of them is what this
            // method is for, and the first thing it found was a third: every operation declares
            // `api-version`, so `cyc … list` grew a REQUIRED `--api-version` that shadowed the
            // global one of the same name — the verb would have refused every invocation that did
            // not repeat a value the SDK's ApiVersionHandler already puts on the wire. Filtered
            // against the global flags this file emits rather than against the string
            // "api-version", so a global flag added later cannot be shadowed by a provider
            // declaring a query parameter of that name.
            if (GlobalFlagNames.Contains(name)) {
                continue;
            }

            flags.Add(
                new(
                    name,
                    parameter.Type switch {
                        "integer" => "integer",
                        "number" => "number",
                        "boolean" => "switch",
                        _ => "string"
                    },
                    parameter.Description,
                    parameter.Required
                ) { QueryParameter = parameter.Name }
            );
        }

        return flags.ToImmutable();
    }

    /// <summary>The flag name an ancestor placeholder takes — <c>{serversName}</c> is <c>servers-name</c>.</summary>
    /// <remarks>
    ///     ⚠ The placeholder's own text, kebab-cased, and not a singularised or prettified form of it.
    ///     Nothing here knows that the singular of <c>servers</c> is <c>server</c>, and a guess that
    ///     was wrong for one provider would be a flag whose name did not match the URL it fills.
    /// </remarks>
    static string AncestorFlagName(string placeholder) => Kebab(placeholder);

    static ImmutableArray<CliFlag> BodyFlags(DocumentType type) =>
        FlagsOf(type.Body, type.ClusterIdPointer, Address(type));

    /// <summary>
    ///     One flag per value leaf of a body schema.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Named for the leaf, and disambiguated by its parent only when two leaves collide.</b>
    ///         <c>/properties/sku/name</c> is <c>--name</c> at its own level and would collide with the
    ///         resource's own <c>--name</c>, so nesting is folded in: the flag is the dotted path
    ///         under <c>/properties</c>, kebab-cased, whenever the bare name is taken. A CLI that
    ///         always used the full path would spell every flag <c>--properties-sku-name</c>, and one
    ///         that never did would silently drop a field.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A container is not a flag.</b> <c>/properties</c> and <c>/properties/sku</c> carry
    ///         no value; their leaves do.
    ///     </para>
    /// </remarks>
    static ImmutableArray<CliFlag> FlagsOf(
        JsonObject body,
        string clusterIdPointer,
        ImmutableArray<CliFlag> address
    ) {
        var leaves = DocumentReader.LeavesOf(body).Where(x => !x.IsObject).ToList();
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var leaf in leaves) {
            taken[leaf.Name] = taken.GetValueOrDefault(leaf.Name) + 1;
        }

        var flags = ImmutableArray.CreateBuilder<CliFlag>();

        foreach (var leaf in leaves) {
            // ⚠ Reserved by the address flags above. A body property called `name` is not the
            // resource's name and must not share its flag — and for a nested type the reserved set is
            // longer than four, which is why the list is passed in rather than rebuilt from nothing.
            var collides = taken[leaf.Name] > 1
                           || address.Any(x => string.Equals(
                               x.Name,
                               "--" + Kebab(leaf.Name),
                               StringComparison.Ordinal
                           ));

            var name = collides ? PathName(leaf.JsonPointer) : Kebab(leaf.Name);
            var type = DocumentReader.TypeOf(leaf.Schema);
            var values = DocumentReader.EnumOf(leaf.Schema);

            var flag = new CliFlag(
                "--" + name,
                CliType(leaf.Schema),
                DocumentReader.Text(leaf.Schema["description"]),
                leaf.Required
            ) {
                JsonPointer = leaf.JsonPointer,
                Repeated = type == "array",
                Nullable = DocumentReader.IsNullable(leaf.Schema),
                // ⚠ THE ENUM GAP, CASHED IN. A closed set is a flag whose completion is the set and
                // whose invalid value is refused before a request is sent. Without it the CLI could
                // only forward whatever was typed and let the server answer 400.
                Choices = values,
                Secret = DocumentReader.Flag(leaf.Schema["x-cybercloud-secret"]),
                ReadOnly = DocumentReader.Flag(leaf.Schema["readOnly"]),
                Immutable = DocumentReader.Flag(leaf.Schema["x-cybercloud-immutable"]),
                Widget = DocumentReader.Text(leaf.Schema["x-cybercloud-widget"]),
                Default = leaf.Schema["default"]?.DeepClone(),
                Example = leaf.Schema["example"]?.DeepClone()
            };

            if (string.Equals(leaf.JsonPointer, clusterIdPointer, StringComparison.Ordinal)) {
                // ⚠ THE CLUSTER-POINTER GAP, CASHED IN. `requires-cluster: true` alone told the CLI
                // that a cluster was needed and not which flag carries it, so `--cluster` could not
                // exist. Aliasing rather than renaming: the long form still matches the body.
                flag = flag with { Alias = "--cluster" };
            }

            flags.Add(flag);
        }

        return flags.ToImmutable();
    }

    /// <summary>The dotted path under <c>/properties</c>, kebab-cased.</summary>
    static string PathName(string jsonPointer) {
        var segments = jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The `properties` envelope is noise in a flag name: every provider's body has one and no
        // user thinks of it as part of the field's name.
        if (segments.Length > 1 && string.Equals(segments[0], "properties", StringComparison.Ordinal)) {
            segments = segments[1..];
        }

        return string.Join('-', segments.Select(Kebab));
    }

    /// <summary>
    ///     What a flag's value is, in the vocabulary a command-line parser understands.
    /// </summary>
    static string CliType(JsonObject schema) {
        var type = DocumentReader.TypeOf(schema);

        return type switch {
            // A boolean flag is a switch, which is what `--enabled` means on a command line and is
            // not the same thing as `--enabled true`.
            "boolean" => "switch",
            "integer" => "integer",
            "number" => "number",
            "array" => DocumentReader.TypeOf(schema["items"] as JsonObject ?? []) switch {
                "integer" => "integer",
                "number" => "number",
                _ => "string"
            },
            "object" => "keyValue",
            _ => "string"
        };
    }

    // ── The platform envelope, which no provider varies ────────────────────────────────────────

    /// <summary>
    ///     The names <see cref="GlobalFlags" /> takes, which no verb's own flag may shadow.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Read back off the emitted array rather than listed beside it.</b> Two lists is how
    ///     the check comes to disagree with the thing it is checking, and the failure here is
    ///     quiet: a verb flag that shadows a global one is a flag <c>System.CommandLine</c> resolves
    ///     to whichever it finds first.
    /// </remarks>
    static readonly ImmutableHashSet<string> GlobalFlagNames =
        [.. GlobalFlags(string.Empty).Select(x => DocumentReader.Text(x?["name"]))];

    static JsonArray GlobalFlags(string version) =>
        new() {
            new JsonObject {
                ["name"] = "--output",
                ["type"] = "string",
                ["summary"] = "table for humans, json for scripts. tsv because cut exists.",
                ["choices"] = new JsonArray { "table", "json", "yaml", "tsv", "none" },
                ["default"] = "table"
            },
            new JsonObject {
                ["name"] = "--query",
                ["type"] = "string",
                ["summary"] = "A JMESPath expression over the response — docs/plan/21 § Decisions."
            },
            new JsonObject {
                ["name"] = "--api-version",
                ["type"] = "string",
                ["summary"] =
                    "Required on every request and defaulted to the version this tree was generated "
                    + "from. There is no 'latest' — docs/plan/10 § API versioning.",
                ["default"] = version
            }
        };

    /// <summary>
    ///     docs/plan/21 § Decisions: <i>"Documented, stable, so CI can branch on them."</i>
    /// </summary>
    static JsonObject ExitCodes() =>
        new() {
            ["ok"] = 0,
            ["clientError"] = 1,
            ["usage"] = 2,
            ["auth"] = 3,
            ["serverError"] = 4,
            ["timeout"] = 5
        };

    // ── Small shared machinery ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>PascalCase</c> or <c>camelCase</c> to <c>kebab-case</c>, invariantly.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="char.ToLowerInvariant(char)" /> and not <c>ToLower</c>. The Turkish dotless
    ///     <c>ı</c> is the standing example: a build under <c>tr-TR</c> would emit <c>--clusterıd</c>
    ///     and the file would differ from the one CI checked in.
    /// </remarks>
    internal static string Kebab(string value) {
        var built = new StringBuilder(value.Length + 4);

        for (var i = 0; i < value.Length; i++) {
            var current = value[i];

            if (current is '_' or ' ' or '.') {
                built.Append('-');
                continue;
            }

            if (char.IsUpper(current) && i > 0 && !char.IsUpper(value[i - 1])) {
                built.Append('-');
            }

            built.Append(char.ToLowerInvariant(current));
        }

        return built.ToString();
    }

    static JsonObject Sorted(JsonObject value) {
        var sorted = new JsonObject();

        foreach (var member in value.ToList().OrderBy(x => x.Key, StringComparer.Ordinal)) {
            value.Remove(member.Key);
            sorted[member.Key] = member.Value;
        }

        return sorted;
    }
}

/// <summary>One flag in the verb tree.</summary>
/// <param name="Name">The long form, with its <c>--</c>.</param>
/// <param name="Type">What the value is, in a parser's vocabulary.</param>
/// <param name="Summary">The help text — the schema's own description.</param>
/// <param name="Required">Whether the verb refuses without it.</param>
public readonly record struct CliFlag(string Name, string Type, string Summary, bool Required) {
    /// <summary>The body pointer this flag sets, or <c>""</c> for an address flag.</summary>
    public string JsonPointer { get; init; } = string.Empty;

    /// <summary>
    ///     The <c>{…}</c> placeholder in the verb's <c>path</c> this flag fills, or <c>""</c> for a
    ///     body flag.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="JsonPointer" />'s address-side twin, and a nested type is what made it
    ///     necessary.</b> With four fixed address flags the host could hard-code which placeholder
    ///     each one filled. A child's ancestors are per-type — <c>{serversName}</c> for one, a
    ///     different segment for the next — so a host inferring them from the flag NAME would be
    ///     re-deriving a convention this emitter owns, and the two would drift the first time either
    ///     side changed how a segment is kebab-cased.
    /// </remarks>
    public string PathPlaceholder { get; init; } = string.Empty;

    /// <summary>
    ///     The query parameter this flag becomes, <c>$</c> and all, or <c>""</c> when the flag is not
    ///     one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The third binding, and until it existed a paging flag could not be emitted at all.</b>
    ///     <see cref="JsonPointer" /> puts a value in the body and <see cref="PathPlaceholder" /> puts
    ///     one in the URL's path; a <c>$top</c> belongs in neither, so <c>CliEmitter</c> deliberately
    ///     emitted no flag for it rather than emit one the host would accept, parse and never
    ///     send — issue #64. ⚠ The name is the wire name rather than the flag's, because the flag is
    ///     <c>--skip-token</c> and the parameter is <c>$skipToken</c>, and a host recomputing one from
    ///     the other would be re-deriving a convention this emitter owns. A gateway ignores a query
    ///     parameter it does not recognise, so getting that wrong is a <c>200</c> holding page one
    ///     again rather than an error.
    /// </remarks>
    public string QueryParameter { get; init; } = string.Empty;

    /// <summary>A second name for the same flag, or <c>""</c>.</summary>
    public string Alias { get; init; } = string.Empty;

    /// <summary>An environment variable that supplies it, for CI — docs/plan/21 § Decisions.</summary>
    public string Environment { get; init; } = string.Empty;

    /// <summary>Whether the flag may be given more than once.</summary>
    public bool Repeated { get; init; }

    /// <summary>Whether the flag accepts an explicit null.</summary>
    public bool Nullable { get; init; }

    /// <summary>The closed set of values, for validation and completion.</summary>
    public ImmutableArray<string> Choices {
        get => field.IsDefault ? [] : field;
        init => field = value.IsDefault ? [] : value;
    } = [];

    /// <summary>Whether the value is secret. ⚠ Never echoed, never logged.</summary>
    public bool Secret { get; init; }

    /// <summary>
    ///     Whether the server owns the value, so no verb offers a flag for it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Carried on the flag and filtered out per verb rather than dropped when the flag is built,
    ///     because a read-only property is still a <i>column</i> — <c>show</c> and <c>--output table</c>
    ///     have to know it exists even though no verb can set it.
    /// </remarks>
    public bool ReadOnly { get; init; }

    /// <summary>Whether the value may not change after create.</summary>
    public bool Immutable { get; init; }

    /// <summary>The portal widget hint, carried through so one vocabulary serves both surfaces.</summary>
    public string Widget { get; init; } = string.Empty;

    /// <summary>The server's default, or <see langword="null" />.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>An illustrative value, or <see langword="null" />.</summary>
    public JsonNode? Example { get; init; }

    /// <summary>This flag, as the verb tree spells it.</summary>
    /// <remarks>
    ///     ⚠ Members are added only when they say something. A tree in which every flag carries
    ///     <c>"secret": false</c> is a tree three times the size whose diffs are three times as noisy,
    ///     and the host's default for an absent member is the same <see langword="false" />.
    /// </remarks>
    public JsonObject ToJson() {
        var node = new JsonObject {
            ["name"] = Name,
            ["type"] = Type,
            ["required"] = Required
        };

        if (Alias.Length > 0) {
            node["alias"] = Alias;
        }

        if (Summary.Length > 0) {
            node["summary"] = Summary;
        }

        if (JsonPointer.Length > 0) {
            // ⚠ The pointer, not a dotted path: it is what the host builds the request body at and
            // what an error's `target` comes back as, so a failed flag highlights itself.
            node["jsonPointer"] = JsonPointer;
        }

        if (PathPlaceholder.Length > 0) {
            // ⚠ Which `{…}` in the verb's own `path` this flag fills. The host substitutes rather
            // than guessing from the flag's name — see the member's remarks.
            node["pathPlaceholder"] = PathPlaceholder;
        }

        if (QueryParameter.Length > 0) {
            // ⚠ The name on the wire, sigil and all. See the member's remarks.
            node["queryParameter"] = QueryParameter;
        }

        if (Environment.Length > 0) {
            node["env"] = Environment;
        }

        if (Repeated) {
            node["repeated"] = true;
        }

        if (Nullable) {
            node["nullable"] = true;
        }

        if (!Choices.IsEmpty) {
            var choices = new JsonArray();
            foreach (var choice in Choices) {
                choices.Add(choice);
            }

            node["choices"] = choices;
        }

        if (Secret) {
            node["secret"] = true;
        }

        if (Immutable) {
            node["immutable"] = true;
        }

        if (ReadOnly) {
            node["readOnly"] = true;
        }

        if (Widget.Length > 0) {
            node["widget"] = Widget;
        }

        // ⚠ Cloned on every rendering. One CliFlag is rendered into several verbs — `create` and
        // `update` share a flag list — and a JsonNode belongs to exactly one parent, so handing the
        // same instance to the second verb throws "The node already has a parent".
        if (Default is { } fallback) {
            node["default"] = fallback.DeepClone();
        }

        if (Example is { } example) {
            node["example"] = example.DeepClone();
        }

        return node;
    }
}
