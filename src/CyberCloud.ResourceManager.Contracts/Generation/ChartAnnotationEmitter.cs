using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>An emitted annotation block and everything the registry could not say in it.</summary>
/// <param name="Text">
///     The block, LF-terminated, or <c>""</c> when <paramref name="Problems" /> is not empty.
/// </param>
/// <param name="Problems">
///     One entry per property the block could not describe. ⚠ Non-empty means <b>nothing is written</b>
///     — a partially generated block is a configuration surface that silently lost a constraint, which
///     is the failure ADR-012 exists to remove.
/// </param>
public sealed record ChartAnnotationBlock(string Text, ImmutableArray<string> Problems);

/// <summary>An in-place rewrite of a chart's <c>values.yaml</c>.</summary>
/// <param name="Text">The whole file as it should be, or <c>""</c> when a problem was found.</param>
/// <param name="Problems">Why the rewrite could not be performed. Normally empty.</param>
/// <param name="PreservedInternalLines">
///     How many lines of <c>@internal</c> region were carried through untouched. Reported so a build
///     log can say the number rather than imply it.
/// </param>
public sealed record ChartRewrite(string Text, ImmutableArray<string> Problems, int PreservedInternalLines);

/// <summary>
///     ADR-012's fifth surface: a managed chart's non-<c>@internal</c> <c>@param</c> block.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/02 § ADR-010, <i>Which end authors the schema</i> — DECIDED 2026-08-11:
///         <i>"The C# <c>ResourceSchema</c> is authored. The chart's <c>@param</c> annotations are
///         generated from it and diffed."</i> The block this emits is what <c>Build.Charts</c> writes
///         back into <c>values.yaml</c> and then fails on, exactly as <c>Build.Generate</c> treats a
///         drifted OpenAPI document.
///     </para>
///     <para>
///         ⚠ <b>This is the one derived surface that reads the registry rather than the emitted
///         OpenAPI document, and the reason is a fact the document does not carry.</b>
///         docs/plan/21 § Generation's one hop puts the CLI, the SDK and the forms behind
///         <see cref="DocumentReader" /> so the compatibility gate over the published document covers
///         them. It cannot cover this one: the chart a type renders is
///         <see cref="ResourceTypeRegistration.Chart" />, which is not in the document at all, so
///         there is no pairing to be read back. Emitting from the registry also keeps <i>declaration
///         order</i>, which the document deliberately destroys by sorting <c>properties</c>
///         ordinally — and a <c>values.yaml</c> whose fields were alphabetised is a configuration file
///         nobody can read. The trade is stated rather than hidden: a chart block is an in-repository
///         file and not a published contract, so nothing here needs the protection the one hop buys.
///     </para>
///     <para>
///         ⚠ <b>Only the subtree under <see cref="RootPointer" /> is a chart value.</b>
///         <c>build/Build.Charts.cs</c> already gives a root key of <c>values.yaml</c> the pointer
///         <c>/properties/{name}</c>; that was written before anything compared the two files, and it
///         is the mapping this emitter honours rather than a new one. A root-level property —
///         <c>/location</c>, <c>/tags</c> — is envelope rather than configuration and has no key in a
///         values file.
///     </para>
///     <para>
///         ⚠ <b>A <see cref="SchemaProperty.ReadOnly" /> property is excluded, not lost.</b> A values
///         key is by construction something the chart's caller sets; server-owned state never appears
///         in a values file. The same reasoning drops <c>--provisioning-state</c> from the generated
///         CLI.
///     </para>
///     <para>
///         ⚠ <b>Seven <see cref="SchemaProperty" /> members have no annotation syntax, and this emitter
///         REFUSES rather than dropping them.</b> <see cref="SchemaProperty.Format" />,
///         <see cref="SchemaProperty.Pattern" />, <see cref="SchemaProperty.MinLength" />,
///         <see cref="SchemaProperty.MaxLength" />, <see cref="SchemaProperty.ExampleJson" />,
///         <see cref="SchemaProperty.Nullable" /> and a non-text
///         <see cref="SchemaProperty.ElementKind" /> are named in
///         docs/plan/02 § ADR-010 as the gap that runs registry-to-chart. Each becomes a problem
///         naming the property and the fact, because a constraint that reached the API and not the
///         chart is a chart that renders a cluster the API would have refused. Closing one means a new
///         directive in <c>build/Build.Charts.cs</c>'s <c>Directives</c> table, its emission in
///         <c>PropertyNode</c>, a row in charts/README.md § The annotation format and a case here —
///         four edits, deliberately not made speculatively for a vocabulary no chart yet uses.
///     </para>
/// </remarks>
public static class ChartAnnotationEmitter {
    /// <summary>The pointer whose subtree is a chart's values — <c>build/Build.Charts.cs</c>'s own.</summary>
    public const string RootPointer = "/properties";

    /// <summary>The file, within a chart directory, the block is written into.</summary>
    public const string FileName = "values.yaml";

    /// <summary>
    ///     The annotated block for one api-version's schema.
    /// </summary>
    /// <param name="schema">The authored schema — docs/plan/08 § The provider registry's <c>schema:</c>.</param>
    /// <returns>The block, or the reasons it could not be written.</returns>
    public static ChartAnnotationBlock Emit(ResourceSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);

        var problems = new List<string>();
        var roots = Tree(schema, problems);
        var text = new StringBuilder();

        foreach (var node in roots) {
            Render(node, 0, text, problems);
        }

        problems.Sort(StringComparer.Ordinal);

        return problems.Count > 0
            ? new(string.Empty, [.. problems])
            : new(text.ToString(), []);
    }

    /// <summary>
    ///     Puts a freshly emitted block into a chart's <c>values.yaml</c>, keeping the header comment
    ///     and every <c>@internal</c> key byte-identical.
    /// </summary>
    /// <param name="values">The checked-in file.</param>
    /// <param name="block">What <see cref="Emit" /> produced.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>@internal</c> keys are moved through, never regenerated.</b> Ten of the
    ///         thirty-six rows in charts/managed/postgres/values.yaml are <c>@internal</c>: Helm
    ///         plumbing, seven rows of reconciler-injected identity and an operator escape hatch. None
    ///         of them is in any <c>ResourceSchema</c> and none ever will be — a resource body has no
    ///         place for them — so a generator that rewrote the whole file would eat them. They are
    ///         copied as bytes, not parsed and re-rendered, because re-rendering is how a comment's
    ///         wording or a quoting style quietly changes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every <c>@internal</c> root key must sit after every generated one, and that is
    ///         enforced rather than assumed.</b> An in-place rewrite needs the generated region to be
    ///         one contiguous run; interleaving them would make "where does the new key go" a question
    ///         with no deterministic answer, and a generated file whose ordering depends on the
    ///         previous file's ordering cannot be regenerated from scratch. The one managed chart in
    ///         the tree already satisfies it.
    ///     </para>
    /// </remarks>
    public static ChartRewrite Rewrite(string values, string block) {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(block);

        var lines = Lines(values);
        var regions = RootRegions(lines);
        var problems = new List<string>();

        var lastGenerated = regions.FindLastIndex(x => !x.IsInternal);

        foreach (var stranded in regions.Take(Math.Max(lastGenerated, 0)).Where(x => x.IsInternal)) {
            problems.Add(
                $"{stranded.Line}: '{stranded.Name}' is `@internal` and is followed by "
                + $"'{regions[lastGenerated].Name}' on line {regions[lastGenerated].Line}, which is "
                + "generated. Every `@internal` key sits after every generated one, so that the "
                + "generated block is a single contiguous run this rewrite can replace — see the "
                + "remarks on ChartAnnotationEmitter.Rewrite."
            );
        }

        if (problems.Count > 0) {
            return new(string.Empty, [.. problems], 0);
        }

        var header = regions.Count == 0 ? lines.Count : regions[0].Start;
        var text = new StringBuilder();

        for (var i = 0; i < header; i++) {
            text.Append(lines[i]).Append('\n');
        }

        text.Append(block);

        var preserved = 0;
        var tail = regions.Where(x => x.IsInternal).ToList();

        // ⚠ One blank line between the generated block and the hand-written tail, always. The blank
        // that was there belonged to the last generated region and went with it; without this the two
        // halves of the file would run together and every chart would drift on the run after the
        // first, which is a drift gate that fires unconditionally and is therefore ignored.
        if (tail.Count > 0) {
            text.Append('\n');
        }

        foreach (var region in tail) {
            for (var i = region.Start; i < region.End; i++) {
                text.Append(lines[i]).Append('\n');
                preserved++;
            }
        }

        return new(Trimmed(text.ToString()), [], preserved);
    }

    // ── The tree ──────────────────────────────────────────────────────────────────────────────

    sealed record Node(string Name, SchemaProperty Property, List<Node> Children);

    /// <summary>
    ///     The <see cref="RootPointer" /> subtree, in declaration order at every level.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="ResourceSchema.Properties" /> is a flat list of pointers — see its own remarks
    ///     for why it is not a tree — so a parent is found by pointer rather than by reference. A
    ///     property whose parent was never declared is a problem rather than a silently promoted root:
    ///     a values file with an undeclared object in it is a file <c>helm lint</c> refuses against the
    ///     schema the same registry produced.
    /// </remarks>
    static List<Node> Tree(ResourceSchema schema, List<string> problems) {
        var roots = new List<Node>();
        var byPointer = new Dictionary<string, Node>(StringComparer.Ordinal);

        foreach (var property in schema.Properties) {
            if (!property.JsonPointer.StartsWith(RootPointer + "/", StringComparison.Ordinal)) {
                continue;
            }

            // Server-owned state is not a chart value. Excluding the subtree as well as the key: a
            // read-only object's members are read-only whatever they declare.
            if (property.ReadOnly || HasReadOnlyAncestor(property.JsonPointer, schema)) {
                continue;
            }

            var node = new Node(property.Name, property, []);
            byPointer[property.JsonPointer] = node;

            var parent = property.ParentPointer;

            if (string.Equals(parent, RootPointer, StringComparison.Ordinal)) {
                roots.Add(node);
                continue;
            }

            if (byPointer.TryGetValue(parent, out var found)) {
                found.Children.Add(node);
                continue;
            }

            problems.Add(
                $"'{property.JsonPointer}' sits under '{parent}', which this schema does not declare as "
                + "a property of its own. A chart key needs an object to live in, and an undeclared one "
                + "would be an unnamed level of indentation in values.yaml."
            );
        }

        return roots;
    }

    /// <summary>Whether any ancestor of a pointer is declared <see cref="SchemaProperty.ReadOnly" />.</summary>
    static bool HasReadOnlyAncestor(string jsonPointer, ResourceSchema schema) {
        foreach (var property in schema.Properties) {
            if (property.ReadOnly
                && jsonPointer.StartsWith(property.JsonPointer + "/", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    // ── Rendering one property ────────────────────────────────────────────────────────────────

    static void Render(Node node, int depth, StringBuilder text, List<string> problems) {
        var property = node.Property;
        var pad = new string(' ', depth * 2);
        var before = problems.Count;

        CheckName(node.Name, property.JsonPointer, problems);
        CheckInexpressible(property, problems);

        var type = property.Kind is SchemaKind.Unknown
            ? string.Empty
            : SchemaVocabulary.JsonTypeOf(property.Kind);

        if (type.Length == 0) {
            problems.Add(
                $"'{property.JsonPointer}' declares SchemaKind.Unknown, which has no chart type. The six "
                + "`{type}` tokens are one per SchemaKind member that is not Unknown."
            );
        }

        var description = property.Description.Trim();

        if (description.Length == 0) {
            problems.Add(
                $"'{property.JsonPointer}' has no Description. It is the `@param` line's whole point — "
                + "the CLI help, the SDK doc comment, the portal field label and now the chart's own "
                + "documentation — and build/Build.Charts.cs fails an empty one with its line number."
            );

            description = "?";
        }

        if (description.Contains('\n', StringComparison.Ordinal)) {
            problems.Add(
                $"'{property.JsonPointer}' has a Description spanning more than one line. An annotation "
                + "is one `## @param` line, so a newline would end the block above the key it describes."
            );
        }

        var literal = Literal(node, problems);

        if (problems.Count > before) {
            // Nothing is appended for a property that could not be described. Emit returns "" whenever
            // any problem was found, so this only keeps the builder from carrying a half-written key.
            return;
        }

        text.Append(pad).Append("## @param ").Append(node.Name).Append(" {").Append(type).Append("} ")
            .Append(description).Append('\n');

        if (property.Required) {
            text.Append(pad).Append("## @required\n");
        }

        if (property.Secret) {
            text.Append(pad).Append("## @secret\n");
        }

        if (property.Immutable) {
            text.Append(pad).Append("## @immutable\n");
        }

        if (!property.AllowedValues.IsEmpty) {
            text.Append(pad).Append("## @enum ")
                .Append(string.Join(" | ", property.AllowedValues)).Append('\n');
        }

        if (property.Minimum is not null || property.Maximum is not null) {
            text.Append(pad).Append("## @range ").Append(Bound(property.Minimum))
                .Append("..").Append(Bound(property.Maximum)).Append('\n');
        }

        if (SchemaVocabulary.Of(property.Widget) is { Length: > 0 } widget) {
            text.Append(pad).Append("## @widget ").Append(widget).Append('\n');
        }

        text.Append(pad).Append(node.Name).Append(':');

        if (literal.Length > 0) {
            text.Append(' ').Append(literal);
        }

        text.Append('\n');

        foreach (var child in node.Children) {
            Render(child, depth + 1, text, problems);
        }
    }

    /// <summary>
    ///     ⚠ The key is written from the pointer's last segment and is <b>not</b> re-cased anywhere on
    ///     the way. A <c>resourcegroup</c> where the schema said <c>resourceGroup</c> is one character
    ///     that makes every create 404 with the reason only in a log line; this platform has had that
    ///     bug. What is checked here is that the name is spellable at all in the values subset —
    ///     <c>build/Build.Charts.cs</c>'s <c>KeyLine</c>.
    /// </summary>
    static void CheckName(string name, string jsonPointer, List<string> problems) {
        if (!KeyName.IsMatch(name)) {
            problems.Add(
                $"'{jsonPointer}' has the member name '{name}', which is not a values key. A key is a "
                + "letter or underscore followed by letters, digits and underscores — charts/README.md "
                + "§ The values subset."
            );
        }
    }

    static void CheckInexpressible(SchemaProperty property, List<string> problems) {
        var pointer = property.JsonPointer;

        if (property.Format is not SchemaFormat.None) {
            problems.Add(Missing(pointer, $"Format = SchemaFormat.{property.Format}", "@format"));
        }

        if (property.Pattern.Length > 0) {
            problems.Add(Missing(pointer, "a Pattern", "@pattern"));
        }

        if (property.MinLength is not null || property.MaxLength is not null) {
            problems.Add(Missing(pointer, "a MinLength or MaxLength", "@length"));
        }

        if (property.ExampleJson.Length > 0) {
            problems.Add(Missing(pointer, "an ExampleJson", "@example"));
        }

        if (property.Nullable) {
            problems.Add(Missing(pointer, "Nullable = true", "@nullable"));
        }

        // Text is the one element kind the annotation vocabulary reaches: `@enum` on an array becomes
        // `items: {type: string, enum: [...]}`, and build/Build.Charts.cs hard-codes that string. Any
        // other element kind would be a claim the generated values.schema.json cannot make.
        if (property.Kind is SchemaKind.Array
            && property.ElementKind is not (SchemaKind.Unknown or SchemaKind.Text)) {
            problems.Add(
                Missing(pointer, $"ElementKind = SchemaKind.{property.ElementKind}", "@element")
            );
        }
    }

    static string Missing(string jsonPointer, string fact, string directive) =>
        $"'{jsonPointer}' declares {fact}, and the chart annotation vocabulary has no syntax for it. "
        + $"It is refused rather than dropped: a constraint that reached the API and not the chart is a "
        + $"cluster rendered from values the API would have refused. Closing it means a `{directive}` "
        + "directive — see the remarks on ChartAnnotationEmitter for the four edits that takes.";

    static string Bound(double? value) =>
        value is null ? string.Empty : value.Value.ToString("R", CultureInfo.InvariantCulture);

    // ── Literals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The YAML scalar written on the key's own line.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The default is the value, and there is no <c>@default</c> directive</b> — that is
    ///     charts/README.md § The annotation format's rule and it makes
    ///     <see cref="SchemaProperty.DefaultJson" /> load-bearing here in a way it is nowhere else. A
    ///     values key must carry <i>something</i>: <c>build/Build.Charts.cs</c> refuses <c>null</c>,
    ///     and <c>helm lint</c> would refuse it against the type the same file generates. So an empty
    ///     string, an empty sequence and an empty map are legitimate "unset" spellings and a number or
    ///     a boolean has none — for those, a missing <see cref="SchemaProperty.DefaultJson" /> is a
    ///     problem rather than an invented <c>0</c> or <c>false</c>. An invented default is a value a
    ///     tenant gets without anybody having chosen it.
    /// </remarks>
    static string Literal(Node node, List<string> problems) {
        var property = node.Property;

        if (property.Kind is SchemaKind.Nested) {
            if (node.Children.Count > 0) {
                return string.Empty;
            }

            // charts/README.md § The values subset: an {object} with no members is a free-form map and
            // is written `{}`; anything else is a key whose members were forgotten.
            return "{}";
        }

        if (property.DefaultJson.Length == 0) {
            return property.Kind switch {
                SchemaKind.Text => "\"\"",
                SchemaKind.Array => "[]",
                _ => Undefaulted(property, problems)
            };
        }

        JsonElement parsed;

        try {
            using var document = JsonDocument.Parse(property.DefaultJson);
            parsed = document.RootElement.Clone();
        } catch (JsonException) {
            // SchemaProperty.Incoherences already refuses this at construction, so reaching it means a
            // schema that was never built through ResourceSchema.Of.
            problems.Add(
                $"'{property.JsonPointer}' declares the DefaultJson '{property.DefaultJson}', which is "
                + "not JSON."
            );

            return string.Empty;
        }

        var literal = Scalar(parsed, property, problems);
        CheckAgainstOwnConstraints(property, parsed, problems);

        return literal;
    }

    static string Undefaulted(SchemaProperty property, List<string> problems) {
        problems.Add(
            $"'{property.JsonPointer}' is a {ResourceSchema.Describe(property.Kind)} and declares no "
            + "DefaultJson. Every values key carries a value — a null is refused by the chart reader "
            + "and by helm — and there is no empty spelling of a number or a boolean, so this emitter "
            + "will not invent one. Declare DefaultJson on the SchemaProperty."
        );

        return string.Empty;
    }

    static string Scalar(JsonElement value, SchemaProperty property, List<string> problems) {
        switch (value.ValueKind) {
            case JsonValueKind.True:
                return "true";

            case JsonValueKind.False:
                return "false";

            case JsonValueKind.String:
                return Quoted(value.GetString() ?? string.Empty);

            case JsonValueKind.Number:
                var raw = value.GetRawText();

                if (!IntegerLiteral.IsMatch(raw) && !NumberLiteral.IsMatch(raw)) {
                    problems.Add(
                        $"'{property.JsonPointer}' declares the DefaultJson '{raw}', whose spelling is "
                        + "outside the values subset. Write it as a plain decimal — `1000`, not `1e3` — "
                        + "so that the chart reader and a real YAML parser agree on the value."
                    );

                    return string.Empty;
                }

                return raw;

            case JsonValueKind.Array:
                var members = new List<string>();

                foreach (var element in value.EnumerateArray()) {
                    if (element.ValueKind is JsonValueKind.Array or JsonValueKind.Object) {
                        problems.Add(
                            $"'{property.JsonPointer}' declares a DefaultJson containing a nested "
                            + "sequence or map. A flow sequence holds scalars — charts/README.md § The "
                            + "values subset."
                        );

                        continue;
                    }

                    members.Add(Scalar(element, property, problems));
                }

                return "[" + string.Join(", ", members) + "]";

            default:
                problems.Add(
                    $"'{property.JsonPointer}' declares a DefaultJson of "
                    + $"{value.ValueKind.ToString().ToLowerInvariant()}, which has no values spelling."
                );

                return string.Empty;
        }
    }

    /// <summary>
    ///     A string as the values subset spells it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Quoted unless it is unmistakably a bare scalar. <c>"17"</c> unquoted reads as an integer
    ///     and <c>build/Build.Charts.cs</c> would then fail the chart it just generated, with
    ///     "'version' is declared {string} and its value reads as an integer" — a generator failing the
    ///     build on its own output. The bare form is kept where it is safe because a values file full
    ///     of quotes is a values file people stop reading.
    /// </remarks>
    static string Quoted(string value) =>
        BareScalar.IsMatch(value)
        && value is not ("true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off")
        && !IntegerLiteral.IsMatch(value)
        && !NumberLiteral.IsMatch(value)
            ? value
            : JsonSerializer.Serialize(value);

    /// <summary>
    ///     ⚠ A default outside its own property's <c>@enum</c> or <c>@range</c> is caught here rather
    ///     than by the build that reads the generated file. <c>build/Build.Charts.cs</c> would report it
    ///     against <c>values.yaml</c>, which by then is a generated file — so the author would be sent
    ///     to fix the wrong end. <see cref="SchemaProperty.Incoherences" /> already refuses most of
    ///     these at construction; this is the belt for a schema that reached here another way.
    /// </summary>
    static void CheckAgainstOwnConstraints(
        SchemaProperty property,
        JsonElement value,
        List<string> problems
    ) {
        foreach (var problem in ResourceSchema.ValueProblems(property, value, property.JsonPointer)) {
            problems.Add(
                $"'{property.JsonPointer}' declares a DefaultJson its own constraints reject: "
                + problem.Message
            );
        }
    }

    // ── The values.yaml scan, which is deliberately not a parser ──────────────────────────────

    /// <summary>One root key's lines, from the first line of its annotation block to the last of it.</summary>
    sealed record Region(string Name, int Line, int Start, int End, bool IsInternal);

    /// <summary>
    ///     Splits a <c>values.yaml</c> into its root-level regions.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is not the annotated-values reader and must never become one.</b>
    ///     <c>build/Build.Charts.cs</c> owns that, with the line numbers and the whole cross-check
    ///     table, and a second parser would be a second opinion about what a chart says. All this needs
    ///     to know is where each root key's block begins and whether that block carries
    ///     <c>@internal</c> — enough to move bytes, not enough to interpret them. Everything it gets
    ///     wrong is caught immediately afterwards, because the file it produces is fed straight back
    ///     through the real reader.
    /// </remarks>
    static List<Region> RootRegions(List<string> lines) {
        var starts = new List<(string Name, int Line, int Start, bool IsInternal)>();
        var blockStart = -1;
        var blockIsInternal = false;

        for (var i = 0; i < lines.Count; i++) {
            var line = lines[i];

            if (line.StartsWith("## @", StringComparison.Ordinal)) {
                if (blockStart < 0) {
                    blockStart = i;
                    blockIsInternal = false;
                }

                blockIsInternal |= line.StartsWith("## @internal", StringComparison.Ordinal);
                continue;
            }

            if (line.Length == 0 || line[0] == '#' || line[0] == ' ') {
                // A blank line, an ordinary comment or an indented line ends nothing: an indented key
                // belongs to the root above it, and a blank between a block and its key is a failure
                // build/Build.Charts.cs reports with the line number.
                if (line.Length > 0 && line[0] != ' ') {
                    blockStart = -1;
                }

                continue;
            }

            var key = RootKey.Match(line);

            if (!key.Success) {
                blockStart = -1;
                continue;
            }

            starts.Add((
                key.Groups["key"].Value,
                i + 1,
                blockStart < 0 ? i : blockStart,
                blockIsInternal
            ));

            blockStart = -1;
            blockIsInternal = false;
        }

        var regions = new List<Region>();

        for (var i = 0; i < starts.Count; i++) {
            // ⚠ A region runs to the start of the next one, blank lines included. Trimming them looks
            // tidier and is wrong: the blank line an author put between two `@internal` keys is part of
            // the hand-written region this rewrite promises to carry through untouched, and moving it
            // is a diff in a region nobody edited. Only the end of the file is trimmed, once, in
            // Trimmed.
            var end = i + 1 < starts.Count ? starts[i + 1].Start : lines.Count;

            regions.Add(new(starts[i].Name, starts[i].Line, starts[i].Start, end, starts[i].IsInternal));
        }

        return regions;
    }

    /// <summary>
    ///     The file as lines, with the trailing newline dropped.
    /// </summary>
    /// <remarks>
    ///     ⚠ CRLF is normalised on the way in, exactly as <c>build/Build.Charts.cs</c>'s own
    ///     <c>LineFeeds</c> does, so a Windows checkout does not rewrite every line of every chart.
    /// </remarks>
    /// <summary>
    ///     The file with its trailing blank lines removed and exactly one newline at the end.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>insert_final_newline</c>, and no more than one. An earlier version of
    ///     <c>build/Build.Charts.cs</c> compared a trimmed reading against an untrimmed rendering and
    ///     every chart drifted on every run, including the run immediately after the file was written.
    /// </remarks>
    static string Trimmed(string text) => text.TrimEnd('\n') + "\n";

    static List<string> Lines(string text) {
        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = new List<string>(normalised.Split('\n'));

        if (lines.Count > 0 && lines[^1].Length == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    static readonly Regex KeyName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    static readonly Regex RootKey =
        new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*):[ ]*", RegexOptions.Compiled);

    static readonly Regex IntegerLiteral = new(@"^-?\d+$", RegexOptions.Compiled);

    static readonly Regex NumberLiteral = new(@"^-?\d+\.\d+(?:[eE][+-]?\d+)?$", RegexOptions.Compiled);

    /// <summary>
    ///     A string that needs no quotes: it starts with a letter and holds nothing a YAML parser
    ///     reads as structure.
    /// </summary>
    static readonly Regex BareScalar = new("^[A-Za-z][A-Za-z0-9_./+-]*$", RegexOptions.Compiled);
}
