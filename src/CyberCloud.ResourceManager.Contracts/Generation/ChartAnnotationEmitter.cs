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
///         ⚠ <b>Two <see cref="SchemaProperty" /> members still have no annotation syntax, and this
///         emitter REFUSES rather than dropping them.</b> <see cref="SchemaProperty.Nullable" /> and a
///         non-text <see cref="SchemaProperty.ElementKind" /> are what is left of the seven
///         docs/plan/02 § ADR-010 named as the gap that runs registry-to-chart. Each becomes a problem
///         naming the property and the fact, because a constraint that reached the API and not the
///         chart is a chart that renders a cluster the API would have refused.
///     </para>
///     <para>
///         ⚠ <b>The other five closed on 2026-08-12, and closing them cost more edits than the note
///         that stood here predicted.</b> That note said four — the <c>Directives</c> table in
///         <c>build/Build.Charts.cs</c>, its emission in <c>PropertyNode</c>, a row in
///         charts/README.md § The annotation format, and a case here. It was written before any
///         directive had been added, so it was a guess, and it missed the three that carry the risk:
///         the <b>parse case in <c>TakeAnnotation</c></b> and the field on <c>ValueAnnotation</c> (the
///         allow-list only decides that a verb is <i>spelled</i> correctly; without a case the argument
///         is read and thrown away, which is a directive the reader accepts and the emitter's fact
///         never reaches), the <b>cross-check in <c>Validate</c></b> (a <c>@pattern</c> on a
///         <c>{boolean}</c>, or the chart's own default failing its own constraint, which
///         <c>helm lint</c> would then reject), and a case in
///         <see cref="CheckUnspellable" /> for the arguments the directive's transport cannot carry.
///         The <c>Subset</c> checker in <c>ChartAnnotationTests</c> holds a fourth copy of the
///         directive table. Nine sites in four files, not four.
///     </para>
///     <para>
///         ⚠ <b><c>@pattern</c> carries the pattern UNANCHORED and every consumer anchors it.</b>
///         <see cref="SchemaProperty.Pattern" /> is a whole-value match — <c>ResourceSchema</c> tests it
///         as <c>^(?:…)$</c> — whereas JSON Schema's <c>pattern</c> keyword and
///         <see cref="Regex.IsMatch(string, string)" /> are both a <i>search</i>. So a bare
///         <c>\d+Gi</c> in a <c>values.schema.json</c> would accept <c>xxx20Gixxx</c>, which the API
///         refuses: the chart would be strictly more permissive than the surface it is generated from,
///         which is the exact failure this emitter refuses constraints to prevent.
///         <c>build/Build.Charts.cs</c>'s <c>PropertyNode</c> anchors on the way into the schema, in
///         the same three characters and for the same stated reason as
///         <see cref="OpenApiEmitter" />. The annotation itself stays bare so that the line in
///         <c>values.yaml</c> and the <c>Pattern</c> in C# are the same string and drift between them
///         is visible.
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
    ///         ⚠ <b>The <c>@internal</c> keys are moved through, never regenerated.</b> Eleven of the
    ///         thirty-six rows in charts/managed/postgres/values.yaml are <c>@internal</c>: Helm
    ///         plumbing, seven rows of reconciler-injected identity, an operator escape hatch and the
    ///         owning role's password. None of them is in any <c>ResourceSchema</c> and none ever will
    ///         be — a resource body has no place for them — so a generator that rewrote the whole file
    ///         would eat them. They are copied as bytes, not parsed and re-rendered, because
    ///         re-rendering is how a comment's wording or a quoting style quietly changes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>AT EVERY DEPTH, and that is a correction rather than a description.</b> Until
    ///         2026-08-12 this walked root keys only, and the first real pair proved what that costs:
    ///         <c>bootstrap.password</c> is <c>@internal</c> and sits <i>inside</i> <c>bootstrap:</c>,
    ///         which is a generated key — so the whole region was replaced and the row was deleted,
    ///         while <c>build/Build.Charts.cs</c> printed "The <c>@internal</c> rows were not touched".
    ///         A generator that eats a hand-written row is bad; one that eats it and reports otherwise
    ///         is the failure this file's own header calls "a diff nobody reads". The merge below is
    ///         therefore recursive: at each level the generated keys are written in the registry's
    ///         order, then the <c>@internal</c> keys of that same level are appended from the original
    ///         bytes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every <c>@internal</c> key must sit after every generated one AT ITS OWN LEVEL, and
    ///         that is enforced rather than assumed.</b> An in-place rewrite needs each level's
    ///         generated region to be one contiguous run; interleaving them would make "where does the
    ///         new key go" a question with no deterministic answer, and a generated file whose ordering
    ///         depends on the previous file's ordering cannot be regenerated from scratch. The one
    ///         managed chart in the tree already satisfies it at both levels it uses.
    ///     </para>
    /// </remarks>
    public static ChartRewrite Rewrite(string values, string block) {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(block);

        var original = Lines(values);
        var problems = new List<string>();
        var preserved = new Counter();

        var merged = Merge(Lines(block), original, 0, problems, preserved);

        if (problems.Count > 0) {
            return new(string.Empty, [.. problems], 0);
        }

        var regions = Regions(original, 0);
        var header = regions.Count == 0 ? original.Count : regions[0].Start;
        var text = new StringBuilder();

        for (var i = 0; i < header; i++) {
            text.Append(original[i]).Append('\n');
        }

        foreach (var line in merged) {
            text.Append(line).Append('\n');
        }

        return new(Trimmed(text.ToString()), [], preserved.Value);
    }

    /// <summary>How many original lines were carried through, across the whole recursion.</summary>
    sealed class Counter {
        public int Value { get; set; }
    }

    /// <summary>
    ///     One level of the file: the generated keys in the registry's order, then that level's
    ///     <c>@internal</c> keys from the original bytes.
    /// </summary>
    /// <param name="generated">The emitted lines for this level's subtree.</param>
    /// <param name="original">The checked-in lines for the same subtree.</param>
    /// <param name="indent">The column this level's keys sit at.</param>
    /// <param name="problems">Appended to when the two cannot be merged.</param>
    /// <param name="preserved">Counts the original lines carried through.</param>
    static List<string> Merge(
        List<string> generated,
        List<string> original,
        int indent,
        List<string> problems,
        Counter preserved
    ) {
        var generatedRegions = Regions(generated, indent);
        var originalRegions = Regions(original, indent);
        var output = new List<string>();

        var lastGenerated = originalRegions.FindLastIndex(x => !x.IsInternal);

        foreach (var stranded in originalRegions.Take(Math.Max(lastGenerated, 0)).Where(x => x.IsInternal)) {
            problems.Add(
                $"{stranded.Line}: '{stranded.Name}' is `@internal` and is followed by "
                + $"'{originalRegions[lastGenerated].Name}' on line {originalRegions[lastGenerated].Line}, "
                + "which is generated. Every `@internal` key sits after every generated one at its own "
                + "level, so that the generated keys are a single contiguous run this rewrite can "
                + "replace — see the remarks on ChartAnnotationEmitter.Rewrite."
            );
        }

        if (problems.Count > 0) {
            return output;
        }

        var byName = new Dictionary<string, Region>(StringComparer.Ordinal);

        foreach (var region in originalRegions) {
            byName[region.Name] = region;
        }

        // ⚠ Everything above this level's first key, verbatim. One level down that is the parent's own
        // `## @param` block and its `key:` line — the lines that make the members below it members of
        // anything. Dropping them is not a formatting slip: it reparents every child onto whatever key
        // preceded it, which `helm lint` reports as a nil pointer three templates away from the cause.
        var header = generatedRegions.Count == 0 ? generated.Count : generatedRegions[0].Start;

        for (var i = 0; i < header; i++) {
            output.Add(generated[i]);
        }

        foreach (var region in generatedRegions) {
            var lines = Slice(generated, region.Start, region.End);

            // Only descend where there is something to carry. A generated object with no `@internal`
            // member below it is emitted exactly as the registry wrote it, and re-splicing an
            // untouched subtree would be a chance to change bytes for no reason.
            if (byName.TryGetValue(region.Name, out var match) && HasInternalBelow(original, match, indent)) {
                lines = Merge(
                    lines,
                    Slice(original, match.Start, match.End),
                    indent + 2,
                    problems,
                    preserved
                );
            }

            output.AddRange(lines);
        }

        var tail = originalRegions.Where(x => x.IsInternal).ToList();

        // ⚠ One blank line between the generated keys and the hand-written tail, at the top level
        // only, because that is the shape the file already has: root keys are separated by a blank
        // line and members of an object are not. Without it the two halves would run together and the
        // chart would drift on the run after the first, which is a gate that fires unconditionally and
        // is therefore ignored.
        if (tail.Count > 0 && indent == 0 && output.Count > 0) {
            output.Add(string.Empty);
        }

        foreach (var region in tail) {
            for (var i = region.Start; i < region.End; i++) {
                output.Add(original[i]);
                preserved.Value++;
            }
        }

        return output;
    }

    /// <summary>Whether a region holds an <c>@internal</c> annotation below its own level.</summary>
    static bool HasInternalBelow(List<string> lines, Region region, int indent) {
        for (var i = region.Start; i < region.End; i++) {
            var line = lines[i];
            var depth = line.Length - line.TrimStart(' ').Length;

            if (depth > indent && line.TrimStart(' ').StartsWith("## @internal", StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    static List<string> Slice(List<string> lines, int start, int end) =>
        lines.GetRange(start, end - start);

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
        CheckUnspellable(property, problems);

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

        // ⚠ A blank line between root keys, and none between an object's members — the shape the one
        // managed chart was hand-written in, kept rather than flattened. It is not decoration: a
        // values.yaml is read by people choosing what to set, and thirty-six annotated rows with no
        // separation is a wall of text. The reader allows it because a blank line only fails BETWEEN a
        // block and its key, never before the block.
        if (depth == 0 && text.Length > 0) {
            text.Append('\n');
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

        if (property.MinLength is not null || property.MaxLength is not null) {
            text.Append(pad).Append("## @length ").Append(Length(property.MinLength))
                .Append("..").Append(Length(property.MaxLength)).Append('\n');
        }

        // ⚠ Bare, not anchored. See the remarks on this class: the annotation is the same string as
        // SchemaProperty.Pattern so that drift between them is visible, and build/Build.Charts.cs
        // anchors it into `^(?:…)$` on the way into values.schema.json, where the keyword is a search.
        if (property.Pattern.Length > 0) {
            text.Append(pad).Append("## @pattern ").Append(property.Pattern).Append('\n');
        }

        if (SchemaVocabulary.Of(property.Format) is { Length: > 0 } format) {
            text.Append(pad).Append("## @format ").Append(format).Append('\n');
        }

        if (property.ExampleJson.Length > 0) {
            text.Append(pad).Append("## @example ").Append(Compact(property.ExampleJson)).Append('\n');
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

    /// <summary>
    ///     The facts whose <i>spelling</i> the vocabulary cannot carry, as opposed to the members it
    ///     has no word for at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Every one of these was found by reading <c>build/Build.Charts.cs</c>'s own regular
    ///     expressions rather than its documentation.</b> A one-sided bound is the sharp one:
    ///     <see cref="SchemaProperty" /> lets a property declare a <see cref="SchemaProperty.Minimum" />
    ///     with no <see cref="SchemaProperty.Maximum" />, and <c>@range</c>'s pattern requires both —
    ///     so the obvious emission, <c>## @range 1..</c>, is "malformed `@range`" against a file this
    ///     emitter had just written. charts/README.md says <c>@range &lt;min&gt;..&lt;max&gt;</c> and
    ///     says nothing about whether either side may be empty; the regex is the answer.
    /// </remarks>
    static void CheckUnspellable(SchemaProperty property, List<string> problems) {
        var pointer = property.JsonPointer;

        if (property.Minimum is null != (property.Maximum is null)) {
            problems.Add(
                $"'{pointer}' declares a one-sided numeric bound, and `@range` takes both — its pattern "
                + "is `<min>..<max>` and `1..` is a malformed directive, not an open range. Declare the "
                + "other bound, or grow `RangeDirective` in build/Build.Charts.cs to accept an open "
                + "end and teach PropertyNode to emit only the keyword that was given."
            );
        }

        foreach (var allowed in property.AllowedValues) {
            if (allowed.Contains('|', StringComparison.Ordinal)
                || allowed.Trim().Length != allowed.Length
                || allowed.Length == 0) {
                problems.Add(
                    $"'{pointer}' declares the allowed value '{allowed}', which `@enum` cannot spell: "
                    + "members are separated by `|` and are trimmed, so a member carrying a pipe or "
                    + "leading space would come back as two members or as a different string."
                );
            }
        }

        // `@secret` is a string's directive and `@widget` renders one scalar field —
        // build/Build.Charts.cs refuses both elsewhere. SchemaProperty.Incoherences does not, so a
        // registry can legitimately hold what the chart cannot say.
        if (property.Secret && property.Kind is not SchemaKind.Text) {
            problems.Add(
                $"'{pointer}' is Secret and is a {ResourceSchema.Describe(property.Kind)}. `@secret` "
                + "applies to a string, and build/Build.Charts.cs refuses it anywhere else."
            );
        }

        if (property.Widget is not WidgetHint.None
            && property.Kind is SchemaKind.Nested or SchemaKind.Array) {
            problems.Add(
                $"'{pointer}' declares WidgetHint.{property.Widget} and is a "
                + $"{ResourceSchema.Describe(property.Kind)}. `@widget` renders one scalar field, and "
                + "build/Build.Charts.cs refuses it on an object or an array."
            );
        }

        CheckPatternIsSpellable(property, problems);

        // ⚠ `@length` accepts an open end where `@range` does not — `1..` and `..63` are legal
        // arguments — so a one-sided MinLength or MaxLength needs no refusal here. The asymmetry is
        // deliberate and is written down in charts/README.md § The annotation format: `@range` is the
        // older directive with a published pattern, and widening it is a change to a surface that
        // already has authors. What no spelling reaches is a NEGATIVE bound: the argument is a run of
        // digits on each side, so a `-1` would come back as a malformed directive against a file this
        // emitter had just written.
        if (property.MinLength is { } shortest && shortest < 0) {
            problems.Add(NegativeLength(pointer, nameof(SchemaProperty.MinLength), shortest));
        }

        if (property.MaxLength is { } longest && longest < 0) {
            problems.Add(NegativeLength(pointer, nameof(SchemaProperty.MaxLength), longest));
        }

        // A SchemaFormat member that SchemaVocabulary has no word for. It cannot happen today — every
        // member has one — and it is exactly what a new member added without touching the vocabulary
        // would look like, at which point `## @format ` is a directive with an empty argument.
        if (property.Format is not SchemaFormat.None
            && SchemaVocabulary.Of(property.Format) is { Length: 0 }) {
            problems.Add(
                $"'{pointer}' declares SchemaFormat.{property.Format}, which SchemaVocabulary has no "
                + "name for, so `@format` has nothing to write. Give the member a name in "
                + "SchemaVocabulary.Of(SchemaFormat) and add it to build/Build.Charts.cs's FormatNames."
            );
        }

        // `format: password` is what `@secret` already puts on a property — OpenApiEmitter uses the
        // same three keywords — so a property that is both would emit two `format` keywords into one
        // values.schema.json node and the second would win silently.
        if (property.Secret && property.Format is not SchemaFormat.None) {
            problems.Add(
                $"'{pointer}' is Secret and declares SchemaFormat.{property.Format}. `@secret` already "
                + "means `format: password`, so the two directives write the same keyword and one of "
                + "them would be lost. build/Build.Charts.cs refuses the pair for the same reason."
            );
        }

        if (property.ExampleJson.Length > 0 && Compact(property.ExampleJson) is { Length: 0 }) {
            problems.Add(
                $"'{pointer}' declares the ExampleJson '{property.ExampleJson}', which is not JSON, so "
                + "`@example` has nothing to write. SchemaProperty.Incoherences already refuses this at "
                + "construction, so reaching it means a schema that was never built through "
                + "ResourceSchema.Of."
            );
        }
    }

    static string NegativeLength(string pointer, string member, int value) =>
        $"'{pointer}' declares {member} {value.ToString(CultureInfo.InvariantCulture)}, and `@length` "
        + "spells each end as digits. A negative length is not a length, and emitting one would produce "
        + "a malformed directive in a file this emitter had just written.";

    /// <summary>
    ///     Whether a <see cref="SchemaProperty.Pattern" /> survives the trip through a <c>## @</c> line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the check the whole <c>@pattern</c> directive stands on, and it is about the
    ///         transport rather than the regular expression.</b> The argument reaches
    ///         <c>build/Build.Charts.cs</c> through a hand-written reader that does <c>TrimEnd</c> on the
    ///         line, <c>Trim</c> on the directive body and <c>Trim</c> again on the argument — three
    ///         separate trims — so <b>a pattern with leading or trailing whitespace comes back as a
    ///         different pattern</b>, silently, and the chart would then validate a set of strings the
    ///         API does not. That is precisely the "constraint that reached the API and not the chart"
    ///         failure, wearing a disguise: the constraint reaches the chart, and means something else
    ///         when it gets there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is deliberately NOT refused is the interesting half.</b> A <c>#</c>, a
    ///         <c>|</c>, a <c>:</c>, a quote, a <c>{</c> or a <c>}</c> are all safe and all appear in
    ///         real patterns — <c>CyberCloud.DBforPostgreSQL/servers</c>'s own quantity pattern carries
    ///         thirteen pipes. The line is a comment, so YAML never reads it as structure; the reader
    ///         takes the whole rest of the line verbatim; and only <c>@enum</c> splits on <c>|</c>.
    ///         Refusing them would be a vocabulary that cannot spell the first pattern anybody wrote.
    ///     </para>
    /// </remarks>
    static void CheckPatternIsSpellable(SchemaProperty property, List<string> problems) {
        if (property.Pattern.Length == 0) {
            return;
        }

        var pattern = property.Pattern;

        if (pattern.Trim().Length != pattern.Length) {
            problems.Add(
                $"'{property.JsonPointer}' declares a Pattern with leading or trailing whitespace, and "
                + "`@pattern` cannot spell one: build/Build.Charts.cs trims the line, the directive body "
                + "and the argument, so the pattern would come back without it and the chart would "
                + "accept a different set of strings from the API. Anchor the whitespace with `\\s` or "
                + "drop it."
            );
        }

        foreach (var character in pattern) {
            if (!char.IsControl(character)) {
                continue;
            }

            problems.Add(
                $"'{property.JsonPointer}' declares a Pattern containing the control character "
                + $"U+{(int)character:X4}. An annotation is one line: a newline would end the block "
                + "above the key it describes, and a tab is a build failure in the values subset. Write "
                + "it as an escape — `\\n`, `\\t` — so the pattern is one line of printable text."
            );

            return;
        }
    }

    /// <summary>
    ///     The members the vocabulary has no word for at all.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="SchemaProperty.Nullable" /> and a non-text
    ///     <see cref="SchemaProperty.ElementKind" /> were deliberately left open on 2026-08-12 when the
    ///     other five closed.</b> Neither has a user: no property of any type in the tree declares
    ///     either, so a <c>@nullable</c> or an <c>@element</c> would be a directive written from a
    ///     guess about what it should mean, tested only against a schema invented to test it, and
    ///     wired through four files' worth of tables to serve nobody. Both are harder than the five
    ///     that closed, and in a way that shows why guessing is the wrong move: a nullable values key
    ///     collides head-on with charts/README.md § The values subset's "no null values" rule and with
    ///     the reader's own refusal of <c>null</c>, so <c>@nullable</c> is a question about the subset
    ///     before it is a directive; and an <c>@element</c> has to decide what a non-text array
    ///     element means for the hard-coded <c>items: {type: string}</c> that <c>@enum</c> already
    ///     emits. The refusal is the honest state: the fact is named, the build is red, and nothing is
    ///     dropped.
    /// </remarks>
    static void CheckInexpressible(SchemaProperty property, List<string> problems) {
        var pointer = property.JsonPointer;

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

    static string Length(int? value) =>
        value is null ? string.Empty : value.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     A JSON literal on one line, in one spelling.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Re-serialised rather than copied, and that is what makes <c>@example</c> safe where
    ///     <c>@pattern</c> needed a check.</b> <see cref="SchemaProperty.ExampleJson" /> is a string of
    ///     JSON whose whitespace nobody constrains — a pretty-printed one carries newlines, which would
    ///     end the annotation block above the key it describes. Parsing and re-writing it compactly
    ///     makes the emitted form single-line by construction, and JSON escapes every control character
    ///     it could otherwise carry. It also makes the bytes a function of the <i>value</i> rather than
    ///     of how somebody spelled it, which is what the byte-for-byte drift gate needs.
    ///     <para>
    ///         The encoder is the relaxed one, matching <c>build/Build.Charts.cs</c>'s <c>Render</c>: an
    ///         em dash in an example stays an em dash rather than becoming <c>—</c> in a file
    ///         people read. Safe for the reason the name warns about — a values file is never
    ///         interpolated into HTML.
    ///     </para>
    /// </remarks>
    static string Compact(string json) {
        try {
            using var document = JsonDocument.Parse(json);

            return JsonSerializer.Serialize(
                document.RootElement,
                CompactJson
            );
        } catch (JsonException) {
            return string.Empty;
        }
    }

    static readonly JsonSerializerOptions CompactJson = new() {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
    ///     Splits a run of values lines into the regions of one indentation level.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is not the annotated-values reader and must never become one.</b>
    ///         <c>build/Build.Charts.cs</c> owns that, with the line numbers and the whole cross-check
    ///         table, and a second parser would be a second opinion about what a chart says. All this
    ///         needs to know is where each key's block begins and whether that block carries
    ///         <c>@internal</c> — enough to move bytes, not enough to interpret them. Everything it
    ///         gets wrong is caught immediately afterwards, because the file it produces is fed
    ///         straight back through the real reader.
    ///     </para>
    ///     <para>
    ///         ⚠ Keys deeper than <paramref name="indent" /> belong to the region above them and are
    ///         never regions here; a region runs to the start of the next one at the same indent, blank
    ///         lines included, because a blank an author put between two keys is part of the
    ///         hand-written region this rewrite promises to carry through untouched.
    ///     </para>
    /// </remarks>
    static List<Region> Regions(List<string> lines, int indent) {
        var starts = new List<(string Name, int Line, int Start, bool IsInternal)>();
        var blockStart = -1;
        var blockIsInternal = false;

        for (var i = 0; i < lines.Count; i++) {
            var line = lines[i];
            var depth = line.Length - line.TrimStart(' ').Length;
            var trimmed = line.TrimStart(' ');

            if (trimmed.StartsWith("## @", StringComparison.Ordinal)) {
                if (depth != indent) {
                    continue;
                }

                if (blockStart < 0) {
                    blockStart = i;
                    blockIsInternal = false;
                }

                blockIsInternal |= trimmed.StartsWith("## @internal", StringComparison.Ordinal);
                continue;
            }

            if (trimmed.Length == 0 || trimmed[0] == '#' || depth != indent) {
                // A blank line, a deeper line or a deeper comment ends nothing: a deeper key belongs to
                // the region above it, and a blank between a block and its key is a failure
                // build/Build.Charts.cs reports with the line number. An ordinary comment at this
                // level does break a pending block.
                if (trimmed.Length > 0 && trimmed[0] == '#' && depth == indent) {
                    blockStart = -1;
                }

                continue;
            }

            var key = RootKey.Match(trimmed);

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
