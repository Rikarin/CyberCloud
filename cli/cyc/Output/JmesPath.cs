using System.Diagnostics;

namespace CyberCloud.Cli.Output;

/// <summary>
///     <c>--query</c> — docs/plan/21 § Decisions: <i>"Azure CLI's convention; a huge productivity
///     feature and a well-specified language."</i>
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A documented subset, not the whole language, and it says so when it hits the
///         edge.</b> What is here covers what people actually type at a control plane: field access,
///         indexing and slicing, list and object projections, filters, multiselect lists and hashes,
///         pipes, and the thirteen functions in <see cref="Functions" />. What is not here —
///         <c>map</c>, <c>merge</c>, <c>reverse</c>, arithmetic, <c>let</c> bindings — fails with the
///         expression, the offset and the word that was not understood, which is a better answer than
///         a silently empty result.
///     </para>
///     <para>
///         ⚠ <b>Where this subset returns nothing, it returns nothing for a reason the spec gives.</b>
///         A miss is <c>null</c>, a projection over a non-array is <c>null</c>, a filter that matches
///         nothing is <c>[]</c>, and an ordering comparison between different types is <c>null</c> —
///         all four are the language rather than shortcuts. What is <i>not</i> allowed is inventing an
///         answer: a function handed a value outside its signature raises a usage error, because a
///         wrong answer that typechecks is never investigated. See <see cref="FunctionNode" />.
///     </para>
///     <para>
///         ⚠ <b>Written here rather than taken from a package for the same reason as
///         <see cref="YamlWriter" />:</b> every JMESPath library on NuGet evaluates against
///         <c>Newtonsoft.Json</c> or reflects over <c>object</c>, and this project's
///         <c>IsAotCompatible</c> makes either an IL2026. It evaluates over <see cref="Payload" />,
///         which is a closed union.
///     </para>
///     <para>
///         ⚠ <b>A projection swallows misses, and that is the language rather than a bug here.</b>
///         <c>[*].nope</c> is an empty array, not an error — which is what makes
///         <c>--query "[].{name:name, tier:properties.tier}"</c> work across resources that do not all
///         have a tier.
///     </para>
/// </remarks>
static class JmesPath {
    /// <summary>Compiles and runs an expression.</summary>
    /// <param name="expression">The expression, as typed after <c>--query</c>.</param>
    /// <param name="input">The document to run it against.</param>
    /// <exception cref="CycUsageException">The expression does not parse, or uses something outside the subset.</exception>
    public static Payload Evaluate(string expression, Payload input) {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(input);

        var parser = new Parser(expression);
        var node = parser.ParseExpression();
        parser.ExpectEnd();

        return node.Evaluate(input);
    }

    // ── Nodes ──────────────────────────────────────────────────────────────────────────────────

    abstract class Node {
        public abstract Payload Evaluate(Payload current);
    }

    sealed class CurrentNode : Node {
        public override Payload Evaluate(Payload current) => current;
    }

    sealed class LiteralNode(Payload value) : Node {
        public override Payload Evaluate(Payload current) => value;
    }

    sealed class FieldNode(Node source, string name) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            return value.IsObject ? value.Member(name) : Payload.Missing;
        }
    }

    sealed class IndexNode(Node source, int index) : Node {
        public override Payload Evaluate(Payload current) => source.Evaluate(current).At(index);
    }

    sealed class SliceNode(Node source, int? start, int? stop, int step) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            if (!value.IsArray)
                return Payload.Missing;

            var items = value.Items.ToList();
            var length = items.Count;
            var from = Clamp(start ?? (step > 0 ? 0 : length - 1), length, step);
            var to = Clamp(stop ?? (step > 0 ? length : -1), length, step);
            var taken = new List<Payload>();

            if (step > 0) {
                for (var i = from; i < to; i += step)
                    taken.Add(items[i]);
            } else {
                for (var i = from; i > to; i += step)
                    taken.Add(items[i]);
            }

            return Payload.Array(taken);
        }

        static int Clamp(int index, int length, int step) {
            if (index < 0)
                index += length;

            var lower = step > 0 ? 0 : -1;
            var upper = step > 0 ? length : length - 1;

            return Math.Clamp(index, lower, upper);
        }
    }

    /// <summary>
    ///     Applies <paramref name="projected" /> to every element the left produces, dropping the
    ///     misses. Built by the parser whenever <c>[]</c>, <c>[*]</c>, <c>[?…]</c> or <c>.*</c> is
    ///     followed by more of the chain.
    /// </summary>
    /// <remarks>
    ///     ⚠ <paramref name="flatten" /> is what makes <c>a[].b[]</c> mean what JMESPath says it
    ///     means. A trailing <c>[]</c> flattens the <i>projection</i> rather than each element of it,
    ///     so <c>value[].properties.cidrs[]</c> is one list of CIDRs and not a list of lists. Without
    ///     it the expression parses, runs and quietly answers the wrong shape — which is worse than
    ///     refusing it.
    /// </remarks>
    sealed class ProjectionNode(Node source, Node projected, bool flatten) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            if (!value.IsArray)
                return Payload.Missing;

            var results = new List<Payload>();

            foreach (var item in value.Items) {
                var result = projected.Evaluate(item);

                if (result.IsMissing)
                    continue;

                if (flatten && result.IsArray)
                    results.AddRange(result.Items);
                else
                    results.Add(result);
            }

            return Payload.Array(results);
        }
    }

    /// <summary>Every element of an array, or nothing when the value is not one.</summary>
    sealed class ListNode(Node source) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            return value.IsArray ? Payload.Array([.. value.Items]) : Payload.Missing;
        }
    }

    /// <summary>Every value of an object — the <c>.*</c> projection.</summary>
    sealed class ValuesNode(Node source) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            return value.IsObject ? Payload.Array([.. value.Members.Select(x => x.Value)]) : Payload.Missing;
        }
    }

    /// <summary>
    ///     <c>[]</c> — one level of flattening, then a projection.
    /// </summary>
    sealed class FlattenNode(Node source) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            if (!value.IsArray)
                return Payload.Missing;

            var flattened = new List<Payload>();

            foreach (var item in value.Items) {
                if (item.IsArray)
                    flattened.AddRange(item.Items);
                else
                    flattened.Add(item);
            }

            return Payload.Array(flattened);
        }
    }

    sealed class FilterNode(Node source, Node predicate) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            if (!value.IsArray)
                return Payload.Missing;

            return Payload.Array([.. value.Items.Where(item => predicate.Evaluate(item).IsTruthy)]);
        }
    }

    sealed class MultiSelectListNode(IReadOnlyList<Node> parts) : Node {
        public override Payload Evaluate(Payload current) {
            if (current.IsMissing)
                return Payload.Missing;

            return Payload.Array([.. parts.Select(x => Coalesce(x.Evaluate(current)))]);
        }
    }

    sealed class MultiSelectHashNode(IReadOnlyList<KeyValuePair<string, Node>> parts) : Node {
        public override Payload Evaluate(Payload current) {
            if (current.IsMissing)
                return Payload.Missing;

            return Payload.Object([
                .. parts.Select(x => new KeyValuePair<string, Payload>(x.Key, Coalesce(x.Value.Evaluate(current)))),
            ]);
        }
    }

    sealed class ComparisonNode(Node left, string comparator, Node right) : Node {
        public override Payload Evaluate(Payload current) {
            var a = left.Evaluate(current);
            var b = right.Evaluate(current);

            if (comparator is "==" or "!=") {
                var same = a.SameAs(b);

                return Payload.Boolean(comparator == "==" ? same : !same);
            }

            // ⚠ Ordering comparisons are numbers only, and a mismatch is `null` rather than an error.
            // The spec says so, and it is what makes `[?replicas > `2`]` safe over a page where one
            // resource is missing the field.
            if (a.AsNumber() is not { } x || b.AsNumber() is not { } y)
                return Payload.Missing;

            return Payload.Boolean(comparator switch {
                "<" => x < y,
                "<=" => x <= y,
                ">" => x > y,
                _ => x >= y,
            });
        }
    }

    sealed class AndNode(Node left, Node right) : Node {
        public override Payload Evaluate(Payload current) {
            var a = left.Evaluate(current);

            return a.IsTruthy ? right.Evaluate(current) : a;
        }
    }

    sealed class OrNode(Node left, Node right) : Node {
        public override Payload Evaluate(Payload current) {
            var a = left.Evaluate(current);

            return a.IsTruthy ? a : right.Evaluate(current);
        }
    }

    sealed class NotNode(Node inner) : Node {
        public override Payload Evaluate(Payload current) => Payload.Boolean(!inner.Evaluate(current).IsTruthy);
    }

    sealed class PipeNode(Node left, Node right) : Node {
        public override Payload Evaluate(Payload current) => right.Evaluate(left.Evaluate(current));
    }

    /// <summary>An <c>&amp;expression</c> — the second argument of <c>sort_by</c>.</summary>
    sealed class ExpressionReferenceNode(Node inner) : Node {
        public Node Inner => inner;

        public override Payload Evaluate(Payload current) => inner.Evaluate(current);
    }

    /// <summary>
    ///     A call to one of <see cref="Functions" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An argument of the wrong type is a usage error, and an <i>absent</i> argument is
    ///         not.</b> JMESPath's own rule is that every one of these functions raises
    ///         <c>invalid-type</c> when handed something outside its signature, and the rule matters
    ///         here for a reason a query language rarely gets credit for: the alternative is a wrong
    ///         answer that looks right. <c>length(nextLink)</c> over a <c>null</c> answered <c>0</c> —
    ///         indistinguishable from an empty page, so <c>cyc … --query "length(value)"</c> against an
    ///         api-version that spells the field differently reported "no resources" instead of
    ///         "wrong query". <c>starts_with(name, `3`)</c> coerced the number to <c>""</c> and every
    ///         string starts with <c>""</c>, so the filter matched every element.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A <see cref="Payload.Missing" /> argument propagates instead, and that is
    ///         deliberate.</b> <c>[?starts_with(name, 'w')]</c> over a page where one resource has no
    ///         <c>name</c> must not fail the whole query — the same property
    ///         <see cref="ComparisonNode" /> preserves for ordering comparisons, and the reason
    ///         <c>--query "[].{name:name, tier:properties.tier}"</c> works across resources that do not
    ///         all have a tier. Missing is falsy, so such an element simply does not match. An
    ///         explicit <c>null</c> is a <i>present</i> value of the wrong type and is refused.
    ///     </para>
    /// </remarks>
    sealed class FunctionNode(string name, IReadOnlyList<Node> arguments) : Node {
        public override Payload Evaluate(Payload current) {
            var values = arguments.Select(x => x.Evaluate(current)).ToList();

            // ⚠ The name is checked before anything else, because the propagation rule below returns
            // rather than throws: without this, `map(&name, value)` — a function this subset does not
            // have — would answer null instead of naming the ones it does.
            if (!Functions.Contains(name, StringComparer.Ordinal))
                throw new CycUsageException(
                    $"'{name}' is not a function cyc's --query understands. Available: {string.Join(", ", Functions)}.");

            // ⚠ `not_null` is the one function whose whole job is to be handed absent values, and an
            // &expression reference is a function rather than a value — sort_by(value, &name)
            // evaluates its reference against each element, never against the document.
            if (name is not "not_null") {
                for (var i = 0; i < values.Count; i++) {
                    if (arguments[i] is not ExpressionReferenceNode && values[i].IsMissing)
                        return Payload.Missing;
                }
            }

            switch (name) {
                case "length":
                    Arity(1, values.Count);

                    // ⚠ Payload.Count answers 0 for a number, a boolean and a null, so the type test
                    // has to come first — that 0 was the defect this rule exists for.
                    if (values[0].ValueKind is not (JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object))
                        throw TypeError(0, "a string, an array or an object", values[0]);

                    return Payload.Number(values[0].Count);

                case "keys":
                    Arity(1, values.Count);

                    if (!values[0].IsObject)
                        throw TypeError(0, "an object", values[0]);

                    return Payload.Array([.. values[0].Members.Select(x => Payload.Text(x.Key))]);

                case "values":
                    Arity(1, values.Count);

                    if (!values[0].IsObject)
                        throw TypeError(0, "an object", values[0]);

                    return Payload.Array([.. values[0].Members.Select(x => x.Value)]);

                case "to_string":
                    Arity(1, values.Count);

                    // Every type is legal here: to_string() is the escape hatch that makes a number or
                    // an object printable, so it is the one function with no signature to violate.
                    return Payload.Text(values[0].ValueKind == JsonValueKind.String
                        ? values[0].AsString()!
                        : values[0].ToJson(indented: false));

                case "join": {
                    Arity(2, values.Count);

                    var separator = values[0].AsString() ?? throw TypeError(0, "a string", values[0]);

                    if (!values[1].IsArray)
                        throw TypeError(1, "an array of strings", values[1]);

                    var parts = new List<string>();

                    foreach (var item in values[1].Items) {
                        // ⚠ Not ToCell(). Falling back to it joined the JSON encoding of each object
                        // into one comma-riddled string that no `cut` pipeline could read back.
                        parts.Add(item.AsString() ?? throw TypeError(1, "an array of strings", item));
                    }

                    return Payload.Text(string.Join(separator, parts));
                }

                case "contains":
                    Arity(2, values.Count);

                    if (values[0].ValueKind == JsonValueKind.String) {
                        var needle = values[1].AsString() ?? throw TypeError(1, "a string when the subject is one", values[1]);

                        return Payload.Boolean(values[0].AsString()!.Contains(needle, StringComparison.Ordinal));
                    }

                    if (!values[0].IsArray)
                        throw TypeError(0, "a string or an array", values[0]);

                    return Payload.Boolean(values[0].Items.Any(x => x.SameAs(values[1])));

                case "starts_with":
                case "ends_with": {
                    Arity(2, values.Count);

                    // ⚠ Both arguments, and the second is the one that mattered: coercing a non-string
                    // to "" made starts_with() true for every subject, because every string starts
                    // with the empty one.
                    var subject = values[0].AsString() ?? throw TypeError(0, "a string", values[0]);
                    var affix = values[1].AsString() ?? throw TypeError(1, "a string", values[1]);

                    return Payload.Boolean(name == "starts_with"
                        ? subject.StartsWith(affix, StringComparison.Ordinal)
                        : subject.EndsWith(affix, StringComparison.Ordinal));
                }

                case "sort":
                    Arity(1, values.Count);

                    if (!values[0].IsArray)
                        throw TypeError(0, "an array", values[0]);

                    return Payload.Array([.. Ordered([.. values[0].Items], x => x)]);

                case "sort_by": {
                    Arity(2, values.Count);

                    if (arguments[1] is not ExpressionReferenceNode reference)
                        throw new CycUsageException("sort_by()'s second argument must be an expression reference, as in sort_by(@, &name).");

                    if (!values[0].IsArray)
                        throw TypeError(0, "an array", values[0]);

                    return Payload.Array([.. Ordered([.. values[0].Items], reference.Inner.Evaluate)]);
                }

                case "not_null":
                    foreach (var value in values) {
                        if (!value.IsMissing && value.ValueKind != JsonValueKind.Null)
                            return value;
                    }

                    return Payload.Missing;

                case "min":
                case "max": {
                    Arity(1, values.Count);

                    if (!values[0].IsArray)
                        throw TypeError(0, "an array of numbers or an array of strings", values[0]);

                    var sorted = Ordered([.. values[0].Items], x => x);

                    // The spec's answer for an empty array, and the reason `max(value[*].replicas)` on
                    // a page with no resources is null rather than an error.
                    return sorted.Count == 0 ? Payload.Missing : sorted[name == "min" ? 0 : ^1];
                }

                default:
                    // Unreachable: the check at the top of this method rejects any name that is not
                    // in Functions. Reaching it means Functions lists a name this switch does not
                    // implement, which is a mistake in this file rather than in anybody's query.
                    throw new UnreachableException(
                        $"'{name}' is listed in {nameof(Functions)} but has no implementation.");
            }
        }

        /// <summary>
        ///     Sorts by the key <paramref name="key" /> projects, as JMESPath orders values.
        /// </summary>
        /// <remarks>
        ///     ⚠ <b>Numbers compare as numbers.</b> Ordering by the rendered cell put <c>100</c> before
        ///     <c>2</c>, so <c>sort_by(value, &amp;properties.replicas)</c> returned a list that looked
        ///     sorted and was not — the worst shape a defect in a query language can take. The spec
        ///     admits <c>array[number]</c> and <c>array[string]</c> and nothing else, so a mixed array
        ///     is refused rather than ordered by a rule nobody could predict.
        /// </remarks>
        List<Payload> Ordered(List<Payload> items, Func<Payload, Payload> key) {
            var keys = items.Select(key).ToList();

            if (keys.TrueForAll(x => x.AsNumber() is not null))
                return [.. items.Select((x, i) => (Item: x, Key: keys[i].AsNumber()!.Value)).OrderBy(x => x.Key).Select(x => x.Item)];

            if (keys.TrueForAll(x => x.AsString() is not null))
                return [
                    .. items
                        .Select((x, i) => (Item: x, Key: keys[i].AsString()!))
                        .OrderBy(x => x.Key, StringComparer.Ordinal)
                        .Select(x => x.Item),
                ];

            throw new CycUsageException(
                $"{name}() orders an array of numbers or an array of strings, and this one holds "
                + $"{string.Join(", ", keys.Select(Describe).Distinct(StringComparer.Ordinal))}.");
        }

        CycUsageException TypeError(int index, string expected, Payload actual)
            => new($"{name}()'s argument {index + 1} must be {expected}, and this one is {Describe(actual)}.");

        void Arity(int expected, int actual) {
            if (expected != actual)
                throw new CycUsageException($"{name}() takes {expected} argument(s), not {actual}.");
        }
    }

    /// <summary>The functions the subset implements, named in the error when one is not.</summary>
    static readonly string[] Functions = [
        "contains", "ends_with", "join", "keys", "length", "max", "min", "not_null", "sort", "sort_by",
        "starts_with", "to_string", "values",
    ];

    static Payload Coalesce(Payload value) => value.IsMissing ? Payload.Null : value;

    /// <summary>Names a value's type for an <c>invalid-type</c> message, in the words the message needs.</summary>
    static string Describe(Payload value)
        => value.ValueKind switch {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.Array => "an array",
            JsonValueKind.Object => "an object",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Null => "null",
            _ => "absent",
        };

    // ── Parser ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A recursive-descent parser over the expression text.
    /// </summary>
    /// <remarks>
    ///     ⚠ Projections are built by recursion rather than by precedence climbing: when a step
    ///     produces a projection, the <i>rest</i> of the chain is parsed into its right-hand side. That
    ///     is what makes <c>a[].b.c</c> mean "b.c of each" instead of "c of the array of b".
    /// </remarks>
    sealed class Parser(string text) {
        int position;

        public void ExpectEnd() {
            SkipSpace();

            if (position < text.Length)
                throw Error($"unexpected '{text[position]}'");
        }

        public Node ParseExpression() => ParsePipe();

        Node ParsePipe() {
            var left = ParseOr();

            while (Peek('|') && !PeekAt(1, '|')) {
                position++;
                left = new PipeNode(left, ParseOr());
            }

            return left;
        }

        Node ParseOr() {
            var left = ParseAnd();

            while (Match("||"))
                left = new OrNode(left, ParseAnd());

            return left;
        }

        Node ParseAnd() {
            var left = ParseNot();

            while (Match("&&"))
                left = new AndNode(left, ParseNot());

            return left;
        }

        Node ParseNot() {
            SkipSpace();

            if (Peek('!') && !PeekAt(1, '=')) {
                position++;

                return new NotNode(ParseNot());
            }

            return ParseComparison();
        }

        Node ParseComparison() {
            var left = ParseChain(new CurrentNode(), root: true);

            SkipSpace();

            foreach (var comparator in Comparators) {
                if (!Match(comparator))
                    continue;

                return new ComparisonNode(left, comparator, ParseChain(new CurrentNode(), root: true));
            }

            return left;
        }

        static readonly string[] Comparators = ["==", "!=", "<=", ">=", "<", ">"];

        Node ParseChain(Node current, bool root) {
            var node = root ? ParsePrimary(current) : current;

            while (true) {
                SkipSpace();

                if (Peek('.')) {
                    position++;
                    SkipSpace();

                    if (Peek('*')) {
                        position++;

                        return Project(new ValuesNode(node));
                    }

                    if (Peek('[')) {
                        node = ParseBracket(node, out var projection);

                        if (projection)
                            return Project(node);

                        continue;
                    }

                    if (Peek('{')) {
                        node = ParseMultiSelectHash(node);

                        continue;
                    }

                    node = new FieldNode(node, ParseIdentifier());

                    continue;
                }

                if (Peek('[')) {
                    node = ParseBracket(node, out var projection);

                    if (projection)
                        return Project(node);

                    continue;
                }

                return node;
            }
        }

        /// <summary>Wraps a projection around whatever remains of the chain.</summary>
        Node Project(Node source) {
            SkipSpace();

            // Nothing after the projection: the projection itself is the answer.
            if (position >= text.Length || text[position] is ']' or ')' or ',' or '}' || Peek('|'))
                return source;

            var projected = ParseChain(new CurrentNode(), root: false);

            return new ProjectionNode(source, projected, flatten: projected is FlattenNode);
        }

        Node ParsePrimary(Node current) {
            SkipSpace();

            if (position >= text.Length)
                throw Error("the expression ends where a value was expected");

            var character = text[position];

            switch (character) {
                case '@':
                    position++;

                    return current;

                case '*':
                    position++;

                    return new ValuesNode(current);

                case '&':
                    position++;

                    return new ExpressionReferenceNode(ParseChain(new CurrentNode(), root: true));

                case '(': {
                    position++;
                    var inner = ParseExpression();
                    Expect(')');

                    return inner;
                }

                case '[': {
                    var node = ParseBracket(current, out var projection);

                    return projection ? Project(node) : node;
                }

                case '{':
                    return ParseMultiSelectHash(current);

                case '`':
                    return new LiteralNode(ParseJsonLiteral());

                case '\'':
                    return new LiteralNode(Payload.Text(ParseRawString()));

                case '"':
                    return new FieldNode(current, ParseQuotedIdentifier());
            }

            if (character is '-' || char.IsAsciiDigit(character))
                return new LiteralNode(Payload.Number(ParseNumber()));

            var name = ParseIdentifier();

            SkipSpace();

            if (!Peek('('))
                return new FieldNode(current, name);

            position++;

            var arguments = new List<Node>();
            SkipSpace();

            if (!Peek(')')) {
                do {
                    arguments.Add(ParseExpression());
                    SkipSpace();
                } while (Match(","));
            }

            Expect(')');

            return new FunctionNode(name, arguments);
        }

        /// <summary>One bracket step. <paramref name="projection" /> says whether the rest of the chain applies element-wise.</summary>
        Node ParseBracket(Node source, out bool projection) {
            Expect('[');
            SkipSpace();
            projection = false;

            // ⚠ `[a, b]` is a multiselect list, `[0]` is an index and `[?…]` is a filter — three
            // meanings for one bracket, told apart by the first character after it. Anything that
            // starts an expression rather than a subscript opens a multiselect.
            if (position < text.Length && (char.IsLetter(text[position]) || text[position] is '_' or '@' or '"' or '\'' or '`' or '{')) {
                var parts = new List<Node>();

                do {
                    parts.Add(ParseChain(new CurrentNode(), root: true));
                    SkipSpace();
                } while (Match(","));

                Expect(']');

                return Rooted(source, new MultiSelectListNode(parts));
            }

            if (Peek(']')) {
                position++;
                projection = true;

                return new FlattenNode(source);
            }

            if (Peek('*')) {
                position++;
                Expect(']');
                projection = true;

                return new ListNode(source);
            }

            if (Peek('?')) {
                position++;
                var predicate = ParseExpression();
                Expect(']');
                projection = true;

                return new FilterNode(source, predicate);
            }

            // A slice or an index. Both start with an optional number.
            int? first = Peek(':') ? null : ParseNumber();

            SkipSpace();

            if (Peek(']')) {
                position++;

                return new IndexNode(source, (int)first!.Value);
            }

            Expect(':');
            SkipSpace();

            int? second = Peek(']') || Peek(':') ? null : ParseNumber();
            var step = 1;

            SkipSpace();

            if (Match(":")) {
                SkipSpace();

                if (!Peek(']'))
                    step = (int)ParseNumber();
            }

            Expect(']');

            if (step == 0)
                throw Error("a slice step of 0 does not terminate");

            projection = true;

            return new SliceNode(source, (int?)first, (int?)second, step);
        }

        Node ParseMultiSelectHash(Node source) {
            Expect('{');

            var parts = new List<KeyValuePair<string, Node>>();

            do {
                SkipSpace();
                var key = Peek('"') ? ParseQuotedIdentifier() : ParseIdentifier();
                SkipSpace();
                Expect(':');
                parts.Add(new KeyValuePair<string, Node>(key, ParseChain(new CurrentNode(), root: true)));
                SkipSpace();
            } while (Match(","));

            Expect('}');

            return Rooted(source, new MultiSelectHashNode(parts));
        }

        /// <summary>
        ///     Runs a multiselect against whatever the chain has produced so far. When nothing has,
        ///     the multiselect is the whole expression and evaluates against the input directly.
        /// </summary>
        static Node Rooted(Node source, Node selector)
            => source is CurrentNode ? selector : new PipeNode(source, selector);

        int ParseNumber() {
            SkipSpace();
            var start = position;

            if (Peek('-'))
                position++;

            while (position < text.Length && char.IsAsciiDigit(text[position]))
                position++;

            if (position == start)
                throw Error("a number was expected");

            return int.Parse(text.AsSpan(start, position - start), CultureInfo.InvariantCulture);
        }

        string ParseIdentifier() {
            SkipSpace();
            var start = position;

            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_')) {
                position++;
            }

            if (position == start)
                throw Error("an identifier was expected");

            return text[start..position];
        }

        string ParseQuotedIdentifier() {
            Expect('"');
            var start = position;

            while (position < text.Length && text[position] != '"')
                position++;

            if (position >= text.Length)
                throw Error("a quoted identifier is not closed");

            var name = text[start..position];
            position++;

            return name;
        }

        string ParseRawString() {
            Expect('\'');
            var value = new StringBuilder();

            while (position < text.Length && text[position] != '\'') {
                if (text[position] == '\\' && position + 1 < text.Length)
                    position++;

                value.Append(text[position++]);
            }

            if (position >= text.Length)
                throw Error("a raw string is not closed");

            position++;

            return value.ToString();
        }

        Payload ParseJsonLiteral() {
            Expect('`');
            var start = position;

            while (position < text.Length && text[position] != '`') {
                if (text[position] == '\\' && position + 1 < text.Length)
                    position++;

                position++;
            }

            if (position >= text.Length)
                throw Error("a `literal` is not closed");

            var json = text[start..position].Replace("\\`", "`", StringComparison.Ordinal);
            position++;

            try {
                // Parsed and kept: JsonDocument owns the memory, and the payload is read before the
                // process exits. Disposing it here would invalidate the element the payload wraps.
                return Payload.Of(JsonDocument.Parse(json).RootElement);
            } catch (JsonException) {
                // A bare word inside backticks is a string in JMESPath's older spelling.
                return Payload.Text(json);
            }
        }

        void SkipSpace() {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }

        bool Peek(char character) {
            SkipSpace();

            return position < text.Length && text[position] == character;
        }

        bool PeekAt(int offset, char character)
            => position + offset < text.Length && text[position + offset] == character;

        bool Match(string token) {
            SkipSpace();

            if (position + token.Length > text.Length || !text.AsSpan(position, token.Length).SequenceEqual(token))
                return false;

            position += token.Length;

            return true;
        }

        void Expect(char character) {
            SkipSpace();

            if (position >= text.Length || text[position] != character)
                throw Error($"'{character}' was expected");

            position++;
        }

        CycUsageException Error(string what)
            => new($"--query could not be parsed at offset {position}: {what}. Expression: {text}");
    }
}
