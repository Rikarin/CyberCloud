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
///         pipes, and eleven functions. What is not here — <c>map</c>, <c>merge</c>, <c>reverse</c>,
///         arithmetic, <c>let</c> bindings — fails with the expression, the offset and the word that
///         was not understood, which is a better answer than a silently empty result.
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
    sealed class ProjectionNode(Node source, Node projected) : Node {
        public override Payload Evaluate(Payload current) {
            var value = source.Evaluate(current);

            if (!value.IsArray)
                return Payload.Missing;

            var results = new List<Payload>();

            foreach (var item in value.Items) {
                var result = projected.Evaluate(item);

                if (!result.IsMissing)
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

    sealed class FunctionNode(string name, IReadOnlyList<Node> arguments) : Node {
        public override Payload Evaluate(Payload current) {
            var values = arguments.Select(x => x.Evaluate(current)).ToList();

            switch (name) {
                case "length":
                    Arity(1, values.Count);

                    return values[0].IsMissing ? Payload.Missing : Payload.Number(values[0].Count);

                case "keys":
                    Arity(1, values.Count);

                    return values[0].IsObject
                        ? Payload.Array([.. values[0].Members.Select(x => Payload.Text(x.Key))])
                        : Payload.Missing;

                case "values":
                    Arity(1, values.Count);

                    return values[0].IsObject
                        ? Payload.Array([.. values[0].Members.Select(x => x.Value)])
                        : Payload.Missing;

                case "to_string":
                    Arity(1, values.Count);

                    return Payload.Text(values[0].ValueKind == JsonValueKind.String
                        ? values[0].AsString()!
                        : values[0].ToJson(indented: false));

                case "join": {
                    Arity(2, values.Count);

                    var separator = values[0].AsString()
                        ?? throw new CycUsageException("join()'s first argument must be a string.");

                    return Payload.Text(string.Join(separator, values[1].Items.Select(x => x.AsString() ?? x.ToCell())));
                }

                case "contains":
                    Arity(2, values.Count);

                    return Payload.Boolean(values[0].ValueKind == JsonValueKind.String
                        ? values[0].AsString()!.Contains(values[1].AsString() ?? values[1].ToCell(), StringComparison.Ordinal)
                        : values[0].Items.Any(x => x.SameAs(values[1])));

                case "starts_with":
                    Arity(2, values.Count);

                    return Payload.Boolean(
                        (values[0].AsString() ?? string.Empty).StartsWith(values[1].AsString() ?? string.Empty, StringComparison.Ordinal));

                case "ends_with":
                    Arity(2, values.Count);

                    return Payload.Boolean(
                        (values[0].AsString() ?? string.Empty).EndsWith(values[1].AsString() ?? string.Empty, StringComparison.Ordinal));

                case "sort":
                    Arity(1, values.Count);

                    return values[0].IsArray
                        ? Payload.Array([.. values[0].Items.OrderBy(x => x.ToCell(), StringComparer.Ordinal)])
                        : Payload.Missing;

                case "sort_by": {
                    Arity(2, values.Count);

                    if (arguments[1] is not ExpressionReferenceNode reference)
                        throw new CycUsageException("sort_by()'s second argument must be an expression reference, as in sort_by(@, &name).");

                    return values[0].IsArray
                        ? Payload.Array([.. values[0].Items.OrderBy(x => reference.Inner.Evaluate(x).ToCell(), StringComparer.Ordinal)])
                        : Payload.Missing;
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

                    var numbers = values[0].Items.Select(x => x.AsNumber()).Where(x => x is not null).Select(x => x!.Value).ToList();

                    if (numbers.Count == 0)
                        return Payload.Missing;

                    return Payload.Number(name == "min" ? numbers.Min() : numbers.Max());
                }

                default:
                    throw new CycUsageException(
                        $"'{name}' is not a function cyc's --query understands. Available: {string.Join(", ", Functions)}.");
            }
        }

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

            return new ProjectionNode(source, ParseChain(new CurrentNode(), root: false));
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
