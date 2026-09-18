using Kusto.Language;
using Kusto.Language.Parsing;
using Kusto.Language.Symbols;
using Kusto.Language.Syntax;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CyberCloud.ResourceGraph.Query;

/// <summary>
///     What a translation is for: whose tenant, who is reading, and which page.
/// </summary>
/// <param name="TenantId">The tenant whose <c>resource_graph</c> table the statement reads.</param>
/// <param name="AccessSubjects">
///     The caller's subject and every userset it is closed into — <c>user:{id}</c>,
///     <c>group:{id}#member</c>, … — which the access column is matched against. ⚠ Never empty: a
///     caller with no usersets still has their own subject, and an empty array would match no row,
///     which is the right answer for a caller nothing was granted to and a wrong one for a bug.
/// </param>
/// <param name="PageSize">How many rows the page holds. The statement fetches one more.</param>
/// <param name="Offset">How many rows to skip — the continuation's offset.</param>
public sealed record KqlTranslationContext(
    Guid TenantId,
    ImmutableArray<string> AccessSubjects,
    int PageSize,
    long Offset);

/// <summary>
///     Translates the resource graph's KQL subset into one parameterised ClickHouse statement over the
///     caller's <c>resource_graph</c> table. docs/plan/08 § The resource-graph projection.
/// </summary>
/// <remarks>
///     <para>
///         <b>Parse and bind with Microsoft's parser, translate by walking the bound tree.</b>
///         <c>KustoCode.ParseAndAnalyze</c> against <see cref="ResourceGraphSchema.Globals" /> gives a
///         tree in which every name is resolved — a column, a function, or an error the binder has
///         already worded — and every expression carries a type. The translator then admits exactly
///         the node kinds <see cref="KqlSubset" /> lists and refuses every other by name.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Each tabular operator either merges into the current <c>SELECT</c> or opens a new one
///             over it, and the rule is what keeps the SQL readable and the order of rows honest.
///         </b> A
///         <c>where</c>, <c>project</c>, <c>extend</c> or <c>order by</c> merges into a <c>SELECT</c>
///         that has no <c>GROUP BY</c>, <c>DISTINCT</c> or <c>LIMIT</c> yet, with column references
///         inlined to the expressions that define them — so
///         <c>
/// extend x = tolower(name) | where x ==
///         'a'
///         </c> is one <c>SELECT … WHERE lowerUTF8(name) = {p0:String}</c> and never a
///         <c>WHERE</c> that names an alias. Anything after a <c>GROUP BY</c>, <c>DISTINCT</c> or
///         <c>LIMIT</c>, and <c>count</c> always, wraps the statement so far as a derived table. The
///         inlining is also why <c>prefer_column_name_to_alias</c> is set on the request: with an
///         alias spelled the same as a source column (<c>extend name = toupper(name)</c>), ClickHouse
///         would otherwise read the alias inside its own definition and report a cycle.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every literal is a parameter, and the tenant's database is the one identifier
///             interpolated.
///         </b> A string, a number, a date or a tag key from the query text becomes
///         <c>{pN:Type}</c> and a <see cref="SqlParameter" />; the database name is derived from the
///         tenant's GUID by <see cref="ResourceGraphTable.Database" /> and a column name is a KQL
///         identifier the translator has checked against <c>[A-Za-z_][A-Za-z0-9_]*</c>. Nothing the
///         caller typed is concatenated into the statement.
///     </para>
///     <para>
///         <b>Order and paging.</b> KQL's <c>order by</c> defaults to descending, so the translator
///         does too. When the query names no order, every output column is one, so the offset a
///         continuation carries slices one fixed order rather than whichever order the engine picked
///         this time; the tag map sorts by its JSON text for that purpose. <c>LIMIT</c> and
///         <c>OFFSET</c> are numbers this translator computed and are written as numbers.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Four sizes are refused before anything recurses, because a stack overflow is the
///             one exception .NET does not let a process catch (#54 review).
///         </b> The first cut recursed
///         once per pipe and once per nesting level with no limit, and <c>resources</c> followed by
///         eight thousand <c>| where true</c> — 104 KB, a tenth of the gateway's body cap — killed
///         the test host from inside the walk. Microsoft's parser has the same shape: measured on a
///         1 MB thread against 12.4.1, it survives 8,000 pipes, 3,000 <c>and</c>s, 8,000 parentheses
///         and every other bracket-less chain, and overflows between 250 and 500 nested function
///         calls (<c>not(not(not(…)))</c>). So the query is lexed first — the lexer is a loop — and
///         refused when it has more than <see cref="MaxTokens" /> tokens or nests brackets deeper
///         than <see cref="MaxNesting" />, before the parser sees it; and the walk itself refuses
///         more than <see cref="MaxOperators" /> operators and an expression deeper than
///         <see cref="MaxExpressionDepth" />, with the pipe chain and <c>and</c>/<c>or</c> chains
///         walked as loops so that a long query is refused by the count and never by the stack.
///         <c>KqlRefusalTests.ASizeThatWouldOverflowTheStackIsRefusedByItsNumberBeforeTheParserRuns</c>
///         drives each of the four.
///     </para>
/// </remarks>
public static class KqlTranslator {
    static readonly Regex Identifier = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1)
    );

    /// <summary>The placeholder name the access filter binds.</summary>
    public const string AccessParameter = "access";

    /// <summary>
    ///     The most tokens a query may lex to. A two-thousand-member <c>in</c> list fits; a body
    ///     built to exhaust the parser does not.
    /// </summary>
    public const int MaxTokens = 4096;

    /// <summary>
    ///     How deep <c>(</c>, <c>[</c> and <c>{</c> may nest. A real query nests two or three; the
    ///     parser's stack gives out somewhere past two hundred and fifty.
    /// </summary>
    public const int MaxNesting = 32;

    /// <summary>The most tabular operators after the table.</summary>
    public const int MaxOperators = 64;

    /// <summary>
    ///     How deep the walk may recurse into one expression. Brackets are already held to
    ///     <see cref="MaxNesting" /> and <c>and</c>/<c>or</c> chains are flattened, so this catches
    ///     what is left — a tag path a hundred keys long (<c>tags.a.a.a…</c>), the one left-nested
    ///     shape KQL's grammar accepts without a bracket, which no real query writes.
    /// </summary>
    public const int MaxExpressionDepth = 64;

    /// <summary>Translates one query.</summary>
    /// <param name="kql">The query text.</param>
    /// <param name="context">Whose table, who is reading, which page.</param>
    /// <returns>
    ///     The statement, or an <see cref="ErrorCode.InvalidRequestBody" /> failure whose message
    ///     names what was refused — the binder's own sentence for a name it does not know, or the
    ///     operator or function outside the subset followed by the supported list.
    /// </returns>
    public static Result<TranslatedQuery> Translate(string kql, KqlTranslationContext context) {
        ArgumentNullException.ThrowIfNull(kql);
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(kql)) {
            return Refuse(
                "The query is empty. It starts with the table: 'resources | where …'. " + KqlSubset.SupportedSentence
            );
        }

        if (context.AccessSubjects.IsDefaultOrEmpty) {
            throw new ArgumentException(
                "A query needs the caller's subjects for the access filter; none were given.",
                nameof(context)
            );
        }

        if (Size(kql) is { } tooLarge) {
            return Refuse(tooLarge);
        }

        var code = KustoCode.ParseAndAnalyze(kql, ResourceGraphSchema.Globals);
        var bound = code.GetDiagnostics().FirstOrDefault(static x => x.Severity == DiagnosticSeverity.Error);

        // ⚠ THE WALK RUNS EVEN WHEN THE BINDER FOUND AN ERROR, AND WHICH MESSAGE WINS IS DECIDED
        // HERE. The binder's sentence is the right one for a name it does not know ("'foo' does not
        // refer to any known column") and the wrong one for a shape outside the subset: `distinct *`
        // over a table with a dynamic column binds with "the value of type 'dynamic' is not allowed
        // in this context", which sends the reader to the wrong word. So a refusal that names an
        // operator or a function the subset lacks wins over the binder; every other refusal, and a
        // walk that succeeded over a tree with binding errors, defers to the binder — a statement
        // built over unbound names must not run.
        try {
            var translation = new Translation(context);
            var translated = translation.Translate((QueryBlock)code.Syntax);

            return bound is null ? Result<TranslatedQuery>.Success(translated) : Refuse(Bound(bound));
        } catch (KqlRefusedException refused) when (refused.NamesTheSubset || bound is null) {
            return Refuse(refused.Message);
        } catch (KqlRefusedException) {
            return Refuse(Bound(bound!));
        } catch (Exception exception) when (bound is not null
                                            && exception is IndexOutOfRangeException
                                                or InvalidCastException
                                                or NullReferenceException) {
            // ⚠ A tree the binder rejected is not the tree the walk was written for: a `project`
            // that names a column twice has fewer result columns than expressions, and the walk's
            // index into them is off the end. The binder's sentence is the answer; the walk's
            // exception is not.
            return Refuse(Bound(bound));
        }
    }

    /// <summary>
    ///     The refusal for a query too large to parse, or <c>null</c> for one the parser may see:
    ///     the token count against <see cref="MaxTokens" /> and the bracket nesting against
    ///     <see cref="MaxNesting" />, read off the lexer's tokens in one pass.
    /// </summary>
    static string? Size(string kql) {
        var tokens = TokenParser.ParseTokens(kql);

        if (tokens.Length > MaxTokens) {
            return $"The query has {tokens.Length} tokens and the resource graph takes at most {MaxTokens}. "
                + "Split it, or narrow it with a where. "
                + KqlSubset.SupportedSentence;
        }

        var depth = 0;
        var deepest = 0;

        foreach (var token in tokens) {
            switch (token.Kind) {
                case SyntaxKind.OpenParenToken:
                case SyntaxKind.OpenBracketToken:
                case SyntaxKind.OpenBraceToken:
                    deepest = Math.Max(deepest, ++depth);
                    break;

                case SyntaxKind.CloseParenToken:
                case SyntaxKind.CloseBracketToken:
                case SyntaxKind.CloseBraceToken:
                    depth--;
                    break;
            }
        }

        return deepest > MaxNesting
            ? $"The query nests brackets {deepest} deep and the resource graph takes at most {MaxNesting}. "
            + KqlSubset.SupportedSentence
            : null;
    }

    static string Bound(Diagnostic diagnostic) =>
        $"{diagnostic.Message} (at character {diagnostic.Start}). " + KqlSubset.SupportedSentence;

    static Result<TranslatedQuery> Refuse(string message) =>
        Result<TranslatedQuery>.Failure(ErrorCode.InvalidRequestBody, message);

    /// <summary>
    ///     A refusal raised from deep in the walk and turned into a <see cref="Result" /> at the top.
    ///     <see cref="NamesTheSubset" /> is set on a refusal that names an operator, function or shape
    ///     the subset lacks — the ones that should be read before the binder's own diagnostic.
    /// </summary>
    sealed class KqlRefusedException(string message, bool namesTheSubset = false) : Exception(message) {
        public bool NamesTheSubset { get; } = namesTheSubset;
    }

    /// <summary>One expression after translation: its SQL and the type this translator gives it.</summary>
    /// <remarks>
    ///     ⚠ The type is the translator's and not always the binder's: a tag read (<c>tags.env</c>)
    ///     is <c>dynamic</c> to Kusto and a <see cref="KqlType.String" /> here, because the map's
    ///     values are strings and that is what a comparison or <c>tolower</c> should see.
    ///     <see cref="KqlType.Dynamic" /> is reserved for the map itself.
    /// </remarks>
    sealed record SqlExpression(string Sql, KqlType Type);

    /// <summary>A column a <c>SELECT</c> produces: its name, the expression over the stage's source, and its type.</summary>
    sealed record OutputColumn(string Name, string Sql, KqlType Type);

    /// <summary>
    ///     One <c>SELECT</c> in the statement, over the base table or over the previous stage.
    /// </summary>
    sealed class Stage {
        public Stage? Inner { get; init; }

        public required List<OutputColumn> Columns { get; init; }

        public List<string> Where { get; } = [];

        public List<string> GroupBy { get; } = [];

        public bool Distinct { get; set; }

        /// <summary>The order, as SQL over this stage's source, and the KQL node it came from for a wrapping stage to inherit.</summary>
        public List<(string Sql, bool Descending, Expression? Node)> OrderBy { get; } = [];

        public long? Limit { get; set; }

        /// <summary>Whether another operator may merge into this stage or has to wrap it.</summary>
        public bool AcceptsMerge => GroupBy.Count == 0 && !Distinct && Limit is null;

        public OutputColumn? Find(string name) =>
            Columns.Find(x => string.Equals(x.Name, name, StringComparison.Ordinal));
    }

    sealed class Translation(KqlTranslationContext context) {
        readonly List<SqlParameter> parameters = [];
        int literals;
        bool inAggregate;
        int depth;

        public TranslatedQuery Translate(QueryBlock block) {
            var statements = block.Statements;

            if (statements.Count != 1) {
                throw new KqlRefusedException(
                    $"The query has {statements.Count} statements and the subset takes exactly one: the table and its pipe. "
                    + "'let' and a second query are not supported. "
                    + KqlSubset.SupportedSentence
                );
            }

            if (statements[0].Element is not ExpressionStatement statement) {
                throw Refused(statements[0].Element, "statement");
            }

            var stage = Pipeline(statement.Expression);
            stage = Page(stage);

            var columns = stage.Columns
                .Select(static x => new ResourceGraphColumn(x.Name, ResourceGraphSchema.WireName(x.Type)))
                .ToImmutableArray();

            return new() {
                Sql = Render(stage, true),
                Parameters = [.. parameters],
                Columns = columns,
                PageSize = context.PageSize,
                Offset = context.Offset
            };
        }

        // ── Tabular operators ──────────────────────────────────────────────────────────────────

        /// <summary>
        ///     The table and its operators, applied in order. ⚠ A loop and not a recursion: the
        ///     parser hands a pipe back left-nested — <c>((resources | a) | b) | c</c> — so the walk
        ///     goes down the left spine collecting operators and applies them on the way back,
        ///     refusing more than <see cref="MaxOperators" /> before any runs.
        /// </summary>
        Stage Pipeline(Expression expression) {
            var operators = new Stack<QueryOperator>();

            while (expression is PipeExpression pipe) {
                operators.Push(pipe.Operator);
                expression = pipe.Expression;
            }

            if (operators.Count > MaxOperators) {
                throw new KqlRefusedException(
                    $"The query has {operators.Count} operators after the table and the resource graph takes at most {MaxOperators}. "
                    + KqlSubset.SupportedSentence
                );
            }

            if (expression is not NameReference { ReferencedSymbol: TableSymbol } table
                || !string.Equals(table.SimpleName, ResourceGraphSchema.TableName, StringComparison.Ordinal)) {
                throw new KqlRefusedException(
                    $"A query starts with the table 'resources' and this one starts with '{Text(expression)}'. "
                    + KqlSubset.SupportedSentence
                );
            }

            var stage = Base();

            while (operators.Count > 0) {
                stage = Apply(stage, operators.Pop());
            }

            return stage;
        }

        Stage Base() {
            var stage = new Stage {
                Inner = null,
                Columns = ResourceGraphSchema.Columns.Select(static x => new OutputColumn(x.Name, x.Sql, x.Type))
                    .ToList()
            };

            // ⚠ THE TWO FILTERS NO QUERY CAN REMOVE, on the base SELECT before any operator runs:
            // a tombstone is not a resource, and a row the caller may not read is not there. The
            // caller's KQL cannot name either column — the binder does not know them.
            stage.Where.Add("is_deleted = 0");
            stage.Where.Add(
                $"hasAny(access, {Parameter(AccessParameter, "Array(String)", SqlParameter.ArrayOfStrings(context.AccessSubjects))})"
            );

            return stage;
        }

        Stage Apply(Stage stage, QueryOperator op) =>
            op switch {
                FilterOperator filter => Where(stage, filter),
                ProjectOperator project => Project(stage, project),
                ExtendOperator extend => Extend(stage, extend),
                SummarizeOperator summarize => Summarize(stage, summarize),
                SortOperator sort => OrderBy(stage, sort),
                TakeOperator take => Take(stage, take),
                DistinctOperator distinct => Distinct(stage, distinct),
                CountOperator => Count(stage),
                // Named by its keyword — 'mv-expand', 'join', 'top' — and not by the whole clause,
                // because the keyword is what the reader looks up in the supported list.
                _ => throw Refused(op, "operator", op.GetFirstToken()?.Text)
            };

        Stage Where(Stage stage, FilterOperator filter) {
            stage = stage.AcceptsMerge ? stage : Wrap(stage);
            var condition = Scalar(stage, filter.Condition);

            if (condition.Type != KqlType.Bool) {
                throw new KqlRefusedException(
                    $"'where' takes a condition and '{Text(filter.Condition)}' is a {ResourceGraphSchema.WireName(condition.Type)}. "
                    + KqlSubset.SupportedSentence
                );
            }

            stage.Where.Add(condition.Sql);
            return stage;
        }

        Stage Project(Stage stage, ProjectOperator project) {
            stage = stage.AcceptsMerge ? stage : Wrap(stage);
            var resultColumns = ResultColumns(project);
            var columns = new List<OutputColumn>();

            for (var i = 0; i < project.Expressions.Count; i++) {
                var element = project.Expressions[i].Element;
                var name = element is SimpleNamedExpression named ? Declared(named) : resultColumns[i].Name;
                var scalar = Scalar(stage, element is SimpleNamedExpression n ? n.Expression : element);

                if (columns.Exists(x => string.Equals(x.Name, name, StringComparison.Ordinal))) {
                    throw new KqlRefusedException($"'project' names the column '{name}' twice.");
                }

                columns.Add(new(name, scalar.Sql, scalar.Type));
            }

            stage.Columns.Clear();
            stage.Columns.AddRange(columns);
            return stage;
        }

        Stage Extend(Stage stage, ExtendOperator extend) {
            stage = stage.AcceptsMerge ? stage : Wrap(stage);

            // The binder names an unnamed extension (`extend tolower(name)` is `Column1`); those are
            // the result columns this stage did not have before, in order.
            var fresh = new Queue<ColumnSymbol>(ResultColumns(extend).Where(x => stage.Find(x.Name) is null));

            foreach (var separated in extend.Expressions) {
                var element = separated.Element;
                var (name, expression) = element is SimpleNamedExpression named
                    ? (Declared(named), named.Expression)
                    : (fresh.Count > 0 ? fresh.Dequeue().Name : throw Refused(element, "extension"), element);

                var scalar = Scalar(stage, expression);
                var column = new OutputColumn(name, scalar.Sql, scalar.Type);
                var existing = stage.Columns.FindIndex(x => string.Equals(x.Name, name, StringComparison.Ordinal));

                if (existing >= 0) {
                    stage.Columns[existing] = column;
                } else {
                    stage.Columns.Add(column);
                }
            }

            return stage;
        }

        Stage Summarize(Stage stage, SummarizeOperator summarize) {
            stage = stage.AcceptsMerge ? stage : Wrap(stage);

            // The binder's result columns are the by-keys first and the aggregates after, each in
            // declaration order — which is where an unnamed `count()` gets `count_` and an unnamed
            // `min(version)` gets `min_version`.
            var resultColumns = ResultColumns(summarize);
            var keys = summarize.ByClause?.Expressions.Select(static x => x.Element).ToList() ?? [];
            var aggregates = summarize.Aggregates.Select(static x => x.Element).ToList();

            if (keys.Count + aggregates.Count != resultColumns.Count) {
                throw new KqlRefusedException(
                    $"'{Text(summarize)}' produces {resultColumns.Count} columns from {keys.Count} keys and "
                    + $"{aggregates.Count} aggregates, which this translator cannot pair up. Name each "
                    + "aggregate and each key ('total = count()') and try again."
                );
            }

            var columns = new List<OutputColumn>();
            var groupBy = new List<string>();

            for (var i = 0; i < keys.Count; i++) {
                var key = keys[i];
                var name = key is SimpleNamedExpression named ? Declared(named) : resultColumns[i].Name;
                var scalar = Scalar(stage, key is SimpleNamedExpression n ? n.Expression : key);
                columns.Add(new(name, scalar.Sql, scalar.Type));
                groupBy.Add(scalar.Sql);
            }

            for (var i = 0; i < aggregates.Count; i++) {
                var aggregate = aggregates[i];
                var name = aggregate is SimpleNamedExpression named
                    ? Declared(named)
                    : resultColumns[keys.Count + i].Name;
                var scalar = Aggregate(stage, aggregate is SimpleNamedExpression n ? n.Expression : aggregate);
                columns.Add(new(name, scalar.Sql, scalar.Type));
            }

            stage.Columns.Clear();
            stage.Columns.AddRange(columns);
            stage.GroupBy.AddRange(groupBy);
            // A summarize does not preserve the order it was given rows in, and KQL says nothing
            // about the order it produces; an earlier `order by` has nothing to say about this output.
            stage.OrderBy.Clear();
            return stage;
        }

        SqlExpression Aggregate(Stage stage, Expression expression) {
            if (expression is not FunctionCallExpression call) {
                throw new KqlRefusedException(
                    $"'summarize' takes aggregates and '{Text(expression)}' is not one. Aggregates: {string.Join(", ", KqlSubset.Aggregates)}."
                );
            }

            var name = call.Name.SimpleName;
            var arguments = call.ArgumentList.Expressions.Select(static x => x.Element).ToList();

            if (!KqlSubset.AggregateSet.Contains(name)) {
                throw new KqlRefusedException(
                    $"'{name}' is not an aggregate in the resource graph's KQL subset. Aggregates: {string.Join(", ", KqlSubset.Aggregates)}. "
                    + KqlSubset.SupportedSentence
                );
            }

            if (string.Equals(name, "count", StringComparison.Ordinal)) {
                if (arguments.Count != 0) {
                    throw new KqlRefusedException(
                        "'count()' takes no argument in this subset; use dcount(column) for a distinct count."
                    );
                }

                return new("count()", KqlType.Long);
            }

            if (arguments.Count != 1) {
                throw new KqlRefusedException(
                    $"'{name}' takes one argument and '{Text(call)}' gives it {arguments.Count}."
                );
            }

            inAggregate = true;
            SqlExpression argument;

            try {
                argument = Scalar(stage, arguments[0]);
            } finally {
                inAggregate = false;
            }

            return name switch {
                "dcount" => new($"uniqExact({argument.Sql})", KqlType.Long),
                "min" => new($"min({argument.Sql})", argument.Type),
                "max" => new($"max({argument.Sql})", argument.Type),
                "sum" when argument.Type is KqlType.Long or KqlType.Real => new($"sum({argument.Sql})", argument.Type),
                "avg" when argument.Type is KqlType.Long or KqlType.Real => new($"avg({argument.Sql})", KqlType.Real),
                "sum" or "avg" => throw new KqlRefusedException(
                    $"'{name}' takes a number and '{Text(arguments[0])}' is a {ResourceGraphSchema.WireName(argument.Type)}."
                ),
                _ => throw Refused(call, "aggregate")
            };
        }

        Stage OrderBy(Stage stage, SortOperator sort) {
            if (stage.Limit is not null) {
                stage = Wrap(stage);
            }

            stage.OrderBy.Clear();

            foreach (var separated in sort.Expressions) {
                stage.OrderBy.Add(Ordering(stage, separated.Element));
            }

            return stage;
        }

        /// <summary>
        ///     One sort key. ⚠ An element with an <c>asc</c>/<c>desc</c> is an <see cref="OrderedExpression" />
        ///     and one without is the bare expression — and KQL's default is <c>desc</c>.
        /// </summary>
        (string Sql, bool Descending, Expression? Node) Ordering(Stage stage, Expression element) {
            var descending = true;
            var expression = element;

            if (element is OrderedExpression ordered) {
                expression = ordered.Expression;

                if (ordered.Ordering is { } clause) {
                    if (clause.NullsClause is not null) {
                        throw new KqlRefusedException(
                            $"'{Text(clause.NullsClause)}' is not in the resource graph's KQL subset: no column here is nullable, so "
                            + "'nulls first' and 'nulls last' have nothing to order. "
                            + KqlSubset.SupportedSentence,
                            true
                        );
                    }

                    descending = clause.AscOrDescKeyword.Kind != SyntaxKind.AscKeyword;
                }
            }

            var scalar = Scalar(stage, expression);
            return (SortKey(scalar), descending, element);
        }

        Stage Take(Stage stage, TakeOperator take) {
            if (take.Expression is not LiteralExpression {
                    Kind: SyntaxKind.LongLiteralExpression or SyntaxKind.IntLiteralExpression
                } literal
                || literal.LiteralValue is not (long or int)) {
                throw new KqlRefusedException(
                    $"'take' and 'limit' take a whole number and '{Text(take.Expression)}' is not one. "
                    + KqlSubset.SupportedSentence,
                    true
                );
            }

            var count = Convert.ToInt64(literal.LiteralValue, CultureInfo.InvariantCulture);

            if (count < 0) {
                throw new KqlRefusedException($"'take {count}' asks for a negative number of rows.");
            }

            if (stage.Limit is not null) {
                stage = Wrap(stage);
            }

            stage.Limit = count;
            return stage;
        }

        Stage Distinct(Stage stage, DistinctOperator distinct) {
            stage = stage.AcceptsMerge ? stage : Wrap(stage);
            var columns = new List<OutputColumn>();

            foreach (var separated in distinct.Expressions) {
                if (separated.Element is not NameReference reference) {
                    throw new KqlRefusedException(
                        $"'distinct' takes column names and '{Text(separated.Element)}' is not one; 'distinct *' is not in the subset "
                        + "either — name the columns. "
                        + KqlSubset.SupportedSentence,
                        true
                    );
                }

                var column = Column(stage, reference);
                columns.Add(new(reference.SimpleName, column.Sql, column.Type));
            }

            stage.Columns.Clear();
            stage.Columns.AddRange(columns);
            stage.Distinct = true;
            stage.OrderBy.Clear();
            return stage;
        }

        static Stage Count(Stage stage) => new() { Inner = stage, Columns = [new("Count", "count()", KqlType.Long)] };

        /// <summary>
        ///     Opens a new <c>SELECT</c> over the stage so far, carrying its order forward where the
        ///     ordered columns still exist.
        /// </summary>
        Stage Wrap(Stage inner) {
            var stage = new Stage {
                Inner = inner,
                Columns = inner.Columns.Select(static x => new OutputColumn(x.Name, Quote(x.Name), x.Type)).ToList()
            };

            // ⚠ A subquery's ORDER BY is not a promise about the outer query's rows — ClickHouse may
            // read a sorted subquery through several threads — so the order is re-stated on the
            // wrapping SELECT. It can be, only while the columns it names are still there.
            foreach (var (_, _, node) in inner.OrderBy.Where(static x => x.Node is not null)) {
                try {
                    stage.OrderBy.Add(Ordering(stage, node!));
                } catch (KqlRefusedException) {
                    stage.OrderBy.Clear();
                    break;
                }
            }

            return stage;
        }

        /// <summary>
        ///     Makes the final stage pageable: an order when the query has none, and the page's
        ///     <c>LIMIT</c> and <c>OFFSET</c>.
        /// </summary>
        Stage Page(Stage stage) {
            if (stage.Limit is not null) {
                stage = Wrap(stage);
            }

            if (stage.OrderBy.Count == 0) {
                // ⚠ Every output column, so two pages are two slices of one order. Without this the
                // engine returns rows in whatever order its read happened to produce, and an OFFSET
                // into that order skips and repeats rows between requests.
                foreach (var column in stage.Columns) {
                    stage.OrderBy.Add((SortKey(new(column.Sql, column.Type)), false, null));
                }
            }

            stage.Limit = context.PageSize + 1L;
            return stage;
        }

        /// <summary>A sort key for an expression: the tag map sorts by its JSON text, everything else by itself.</summary>
        static string SortKey(SqlExpression scalar) =>
            scalar.Type == KqlType.Dynamic ? $"toJSONString({scalar.Sql})" : scalar.Sql;

        // ── Scalar expressions ─────────────────────────────────────────────────────────────────

        SqlExpression Scalar(Stage stage, Expression expression) {
            if (++depth > MaxExpressionDepth) {
                throw new KqlRefusedException(
                    $"The query has an expression more than {MaxExpressionDepth} levels deep, which the resource graph does not translate. "
                    + KqlSubset.SupportedSentence
                );
            }

            try {
                return Nested(stage, expression);
            } finally {
                depth--;
            }
        }

        SqlExpression Nested(Stage stage, Expression expression) {
            switch (expression) {
                case ParenthesizedExpression parenthesized:
                    return Scalar(stage, parenthesized.Expression);

                case NameReference reference:
                    return Column(stage, reference);

                case LiteralExpression literal:
                    return Literal(literal);

                case CompoundStringLiteralExpression compound:
                    return new(
                        Parameter("String", string.Concat(compound.Tokens.Select(static x => x.ValueText))),
                        KqlType.String
                    );

                case PrefixUnaryExpression {
                    Kind: SyntaxKind.UnaryMinusExpression,
                    Expression: LiteralExpression negated
                } unary:
                    return Literal(negated, true, unary);

                case PathExpression path:
                    return TagRead(
                        stage,
                        path.Expression,
                        path.Selector is NameReference member ? member.SimpleName : throw Refused(path, "tag read"),
                        path
                    );

                case ElementExpression element:
                    return TagRead(
                        stage,
                        element.Expression,
                        element.Selector is BracketedExpression {
                            Expression: LiteralExpression { Kind: SyntaxKind.StringLiteralExpression } bracketed
                        }
                            ? bracketed.LiteralValue as string ?? ""
                            : throw new KqlRefusedException(
                                $"'{Text(element)}' is not in the resource graph's KQL subset: a tag is read as tags.key or tags['key'] "
                                + "with a literal key. "
                                + KqlSubset.SupportedSentence
                            ),
                        element
                    );

                case BinaryExpression binary:
                    return Binary(stage, binary);

                case InExpression inExpression:
                    return In(stage, inExpression);

                case FunctionCallExpression call:
                    return Function(stage, call);

                default:
                    throw Refused(expression, "expression");
            }
        }

        static SqlExpression Column(Stage stage, NameReference reference) {
            if (reference.ReferencedSymbol is not ColumnSymbol) {
                throw new KqlRefusedException(
                    $"'{reference.SimpleName}' is not a column of 'resources'. Columns: "
                    + $"{string.Join(", ", ResourceGraphSchema.Columns.Select(static x => x.Name))}. "
                    + KqlSubset.SupportedSentence
                );
            }

            var column = stage.Find(reference.SimpleName)
                ?? throw new KqlRefusedException(
                    $"'{reference.SimpleName}' is not a column at this point of the query; the columns here are "
                    + $"{string.Join(", ", stage.Columns.Select(static x => x.Name))}."
                );

            return new(column.Sql, column.Type);
        }

        SqlExpression Literal(LiteralExpression literal, bool negate = false, SyntaxNode? whole = null) {
            var value = literal.LiteralValue;

            switch (literal.Kind) {
                case SyntaxKind.StringLiteralExpression when !negate:
                    return new(Parameter("String", value as string ?? ""), KqlType.String);

                case SyntaxKind.LongLiteralExpression or SyntaxKind.IntLiteralExpression when value is long or int: {
                    var number = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    return new(
                        Parameter("Int64", (negate ? -number : number).ToString(CultureInfo.InvariantCulture)),
                        KqlType.Long
                    );
                }

                case SyntaxKind.RealLiteralExpression when value is double real:
                    return new(
                        Parameter("Float64", (negate ? -real : real).ToString("R", CultureInfo.InvariantCulture)),
                        KqlType.Real
                    );

                case SyntaxKind.BooleanLiteralExpression when !negate && value is bool flag:
                    return new(Parameter("Bool", flag ? "true" : "false"), KqlType.Bool);

                case SyntaxKind.DateTimeLiteralExpression when !negate && value is DateTime instant: {
                    // ⚠ The parser hands datetime(2026-09-17T10:00:00Z) back as a LOCAL DateTime —
                    // 12:00 on a laptop in Prague — and one with no zone as Unspecified. KQL's datetime
                    // is always UTC, so Local is converted back and Unspecified is stamped UTC; the
                    // first cut stamped both and bound the golden case two hours late, the same trap
                    // ResourceGraphJson's converter records for the other direction.
                    var utc = instant.Kind switch {
                        DateTimeKind.Local => instant.ToUniversalTime(),
                        DateTimeKind.Utc => instant,
                        _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc)
                    };

                    // ⚠ The parameter's type names the zone too. A bare `DateTime64(3)` is parsed in
                    // the SERVER's time zone, so the UTC value computed above would have landed two
                    // hours off on a ClickHouse running in Europe/Prague while the columns are
                    // `DateTime64(3, 'UTC')` — every suite passed because the containers ran in UTC
                    // (#54 review). ProjectionFixture now starts its ClickHouse in Europe/Prague so
                    // that the suite would find it again.
                    return new(
                        Parameter(SqlParameter.DateTimeType, SqlParameter.DateTime64(new DateTimeOffset(utc))),
                        KqlType.DateTime
                    );
                }

                default:
                    throw new KqlRefusedException(
                        $"'{Text(whole ?? literal)}' is not a literal in the resource graph's KQL subset. Literals: a string, a whole "
                        + "number, a real, true or false, and datetime(…) in UTC. "
                        + KqlSubset.SupportedSentence
                    );
            }
        }

        SqlExpression TagRead(Stage stage, Expression map, string key, SyntaxNode whole) {
            var source = Scalar(stage, map);

            if (source.Type != KqlType.Dynamic) {
                throw new KqlRefusedException(
                    $"'{Text(whole)}' reads a key of '{Text(map)}', which is a {ResourceGraphSchema.WireName(source.Type)} and not a "
                    + "property bag; only tags (and todynamic(…)) has keys. "
                    + KqlSubset.SupportedSentence,
                    true
                );
            }

            // ⚠ A Map read: '' for a key the resource does not carry, which is why isempty(tags.env)
            // is "untagged" and there is no null to test.
            return new($"{source.Sql}[{Parameter("String", key)}]", KqlType.String);
        }

        SqlExpression Binary(Stage stage, BinaryExpression binary) {
            switch (binary.Kind) {
                case SyntaxKind.AndExpression:
                case SyntaxKind.OrExpression: {
                    // ⚠ A chain of one operator — `a and b and c and …` — is left-nested in the tree
                    // and a loop here, so a filter with a hundred terms is a hundred iterations and
                    // not a hundred frames: the walk down the left spine collects the operands, and
                    // each is translated at this depth. The SQL keeps the tree's parentheses, one
                    // pair per binary node, so the golden files did not move.
                    var kind = binary.Kind;
                    var operands = new Stack<Expression>();
                    Expression current = binary;

                    while (current is BinaryExpression { Kind: var k } chain && k == kind) {
                        operands.Push(chain.Right);
                        current = chain.Left;
                    }

                    var op = kind == SyntaxKind.AndExpression ? "AND" : "OR";
                    var sql = Boolean(stage, current).Sql;

                    while (operands.Count > 0) {
                        sql = $"({sql} {op} {Boolean(stage, operands.Pop()).Sql})";
                    }

                    return new(sql, KqlType.Bool);
                }

                case SyntaxKind.EqualExpression:
                case SyntaxKind.NotEqualExpression:
                case SyntaxKind.LessThanExpression:
                case SyntaxKind.LessThanOrEqualExpression:
                case SyntaxKind.GreaterThanExpression:
                case SyntaxKind.GreaterThanOrEqualExpression: {
                    var (left, right) = Comparable(stage, binary);

                    var op = binary.Kind switch {
                        SyntaxKind.EqualExpression => "=",
                        SyntaxKind.NotEqualExpression => "!=",
                        SyntaxKind.LessThanExpression => "<",
                        SyntaxKind.LessThanOrEqualExpression => "<=",
                        SyntaxKind.GreaterThanExpression => ">",
                        _ => ">="
                    };

                    return new($"({left.Sql} {op} {right.Sql})", KqlType.Bool);
                }

                case SyntaxKind.EqualTildeExpression:
                case SyntaxKind.BangTildeExpression: {
                    var (left, right) = Strings(stage, binary);
                    var op = binary.Kind == SyntaxKind.EqualTildeExpression ? "=" : "!=";
                    return new($"(lowerUTF8({left.Sql}) {op} lowerUTF8({right.Sql}))", KqlType.Bool);
                }

                case SyntaxKind.ContainsExpression: {
                    var (left, right) = Strings(stage, binary);
                    return new($"(positionCaseInsensitiveUTF8({left.Sql}, {right.Sql}) > 0)", KqlType.Bool);
                }

                case SyntaxKind.StartsWithExpression: {
                    var (left, right) = Strings(stage, binary);
                    return new($"startsWith(lowerUTF8({left.Sql}), lowerUTF8({right.Sql}))", KqlType.Bool);
                }

                case SyntaxKind.EndsWithExpression: {
                    var (left, right) = Strings(stage, binary);
                    return new($"endsWith(lowerUTF8({left.Sql}), lowerUTF8({right.Sql}))", KqlType.Bool);
                }

                case SyntaxKind.HasExpression: {
                    var left = Scalar(stage, binary.Left);

                    if (left.Type is not (KqlType.String or KqlType.Dynamic)) {
                        throw NotAString(binary.Left, left, "has");
                    }

                    // ⚠ KQL's `has` is a whole-term match, case-insensitive: 'db' matches 'my-db-1' and
                    // not 'mydb'. ClickHouse's hasToken wants a needle with no separator in it and
                    // throws otherwise, so the term is turned into an RE2 pattern here — escaped, so
                    // nothing in it is a metacharacter — and bound as one parameter. The needle has to
                    // be a literal for that; a column on the right has no pattern to build at
                    // translation time.
                    if (binary.Right is not LiteralExpression { Kind: SyntaxKind.StringLiteralExpression } needle
                        || needle.LiteralValue is not string term) {
                        throw new KqlRefusedException(
                            $"'has' takes a string literal on its right and '{Text(binary.Right)}' is not one; use contains for a "
                            + "column-to-column test. "
                            + KqlSubset.SupportedSentence
                        );
                    }

                    // ⚠ On the tag map the term is matched against the map's JSON text — keys and
                    // values both, which is what `tags has 'prod'` means in Azure Resource Graph.
                    // The first cut handed ClickHouse the Map itself, and ClickHouse refused it
                    // ("Illegal type Map(String, String) of argument of function match") for the
                    // most natural tag query there is (#54 review).
                    var pattern = """(?i)(^|[^\p{L}\p{N}_])""" + EscapeRe2(term) + """($|[^\p{L}\p{N}_])""";
                    return new($"match({AsString(left)}, {Parameter("String", pattern)})", KqlType.Bool);
                }

                default:
                    throw Refused(binary, "comparison", binary.Operator.Text);
            }
        }

        SqlExpression Boolean(Stage stage, Expression expression) {
            var scalar = Scalar(stage, expression);

            if (scalar.Type != KqlType.Bool) {
                throw new KqlRefusedException(
                    $"'{Text(expression)}' is a {ResourceGraphSchema.WireName(scalar.Type)} where a condition is needed."
                );
            }

            return scalar;
        }

        (SqlExpression Left, SqlExpression Right) Comparable(Stage stage, BinaryExpression binary) {
            var left = Scalar(stage, binary.Left);
            var right = Scalar(stage, binary.Right);

            if (left.Type == KqlType.Dynamic || right.Type == KqlType.Dynamic) {
                throw new KqlRefusedException(
                    $"'{Text(binary)}' compares the whole tag map; compare one tag — tags.key == 'value' — or its text with tostring(tags)."
                );
            }

            var compatible = left.Type == right.Type
                || (left.Type is KqlType.Long or KqlType.Real && right.Type is KqlType.Long or KqlType.Real);

            if (!compatible) {
                throw new KqlRefusedException(
                    $"'{Text(binary)}' compares a {ResourceGraphSchema.WireName(left.Type)} with a "
                    + $"{ResourceGraphSchema.WireName(right.Type)}; wrap one side in tostring(…) or compare like with like."
                );
            }

            return (left, right);
        }

        (SqlExpression Left, SqlExpression Right) Strings(Stage stage, BinaryExpression binary) {
            var left = Scalar(stage, binary.Left);
            var right = Scalar(stage, binary.Right);
            var op = binary.Operator.Text;

            if (left.Type != KqlType.String) {
                throw NotAString(binary.Left, left, op);
            }

            if (right.Type != KqlType.String) {
                throw NotAString(binary.Right, right, op);
            }

            return (left, right);
        }

        static KqlRefusedException NotAString(Expression expression, SqlExpression scalar, string op) =>
            new(
                $"'{op}' compares strings and '{Text(expression)}' is a {ResourceGraphSchema.WireName(scalar.Type)}; "
                + "wrap it in tostring(…)."
            );

        SqlExpression In(Stage stage, InExpression inExpression) {
            if (inExpression.Kind != SyntaxKind.InExpression) {
                throw Refused(inExpression, "comparison", inExpression.Operator.Text);
            }

            var left = Scalar(stage, inExpression.Left);
            var members = new List<string>();

            foreach (var separated in inExpression.Right.Expressions) {
                var member = Scalar(stage, separated.Element);

                if (member.Type != left.Type
                    && !(left.Type is KqlType.Long or KqlType.Real && member.Type is KqlType.Long or KqlType.Real)) {
                    throw new KqlRefusedException(
                        $"'{Text(inExpression)}' tests a {ResourceGraphSchema.WireName(left.Type)} against a "
                        + $"{ResourceGraphSchema.WireName(member.Type)} ('{Text(separated.Element)}')."
                    );
                }

                members.Add(member.Sql);
            }

            if (members.Count == 0) {
                throw new KqlRefusedException($"'{Text(inExpression)}' tests against an empty list.");
            }

            return new($"({left.Sql} IN ({string.Join(", ", members)}))", KqlType.Bool);
        }

        SqlExpression Function(Stage stage, FunctionCallExpression call) {
            var name = call.Name.SimpleName;
            var arguments = call.ArgumentList.Expressions.Select(static x => x.Element).ToList();

            if (KqlSubset.AggregateSet.Contains(name) && !inAggregate) {
                throw new KqlRefusedException(
                    $"'{name}()' is an aggregate and belongs in 'summarize'; it is not a scalar function. "
                    + KqlSubset.SupportedSentence
                );
            }

            if (!KqlSubset.FunctionSet.Contains(name)) {
                throw Refused(call, "function", name);
            }

            switch (name) {
                case "tolower":
                case "toupper": {
                    var argument = OneString(stage, call, arguments);
                    return new($"{(name == "tolower" ? "lowerUTF8" : "upperUTF8")}({argument.Sql})", KqlType.String);
                }

                case "strcat": {
                    if (arguments.Count < 2) {
                        throw new KqlRefusedException(
                            $"'strcat' takes at least two arguments and '{Text(call)}' gives it {arguments.Count}."
                        );
                    }

                    var parts = arguments.Select(x => Scalar(stage, x)).Select(AsString);
                    return new($"concat({string.Join(", ", parts)})", KqlType.String);
                }

                case "split": {
                    if (arguments.Count is not (2 or 3)) {
                        throw new KqlRefusedException(
                            $"'split' takes a string, a separator and optionally an index; '{Text(call)}' gives it {arguments.Count}."
                        );
                    }

                    var source = Scalar(stage, arguments[0]);
                    var separator = Scalar(stage, arguments[1]);

                    if (source.Type != KqlType.String || separator.Type != KqlType.String) {
                        throw new KqlRefusedException($"'split' takes two strings; '{Text(call)}' does not.");
                    }

                    // ClickHouse's argument order is (separator, string) — the reverse of KQL's.
                    var array = $"splitByString({separator.Sql}, {source.Sql})";

                    if (arguments.Count == 2) {
                        return new(array, KqlType.Dynamic);
                    }

                    var index = Scalar(stage, arguments[2]);

                    if (index.Type != KqlType.Long) {
                        throw new KqlRefusedException(
                            $"'split' takes a whole-number index; '{Text(arguments[2])}' is not one."
                        );
                    }

                    // KQL indexes from 0 and ClickHouse from 1; an index past the end is '' in both.
                    return new($"arrayElement({array}, {index.Sql} + 1)", KqlType.String);
                }

                case "isnotempty":
                case "isempty": {
                    if (arguments.Count != 1) {
                        throw new KqlRefusedException(
                            $"'{name}' takes one argument and '{Text(call)}' gives it {arguments.Count}."
                        );
                    }

                    var argument = Scalar(stage, arguments[0]);
                    var op = name == "isempty" ? "=" : "!=";

                    return argument.Type switch {
                        KqlType.String => new($"({argument.Sql} {op} '')", KqlType.Bool),
                        KqlType.Dynamic => new(
                            $"({(name == "isempty" ? "" : "NOT ")}empty({argument.Sql}))",
                            KqlType.Bool
                        ),
                        _ => throw new KqlRefusedException(
                            $"'{name}' asks whether a string or a tag is empty; '{Text(arguments[0])}' is a "
                            + $"{ResourceGraphSchema.WireName(argument.Type)}, which is never empty here."
                        )
                    };
                }

                case "tostring": {
                    if (arguments.Count != 1) {
                        throw new KqlRefusedException(
                            $"'tostring' takes one argument and '{Text(call)}' gives it {arguments.Count}."
                        );
                    }

                    return new(AsString(Scalar(stage, arguments[0])), KqlType.String);
                }

                case "todynamic": {
                    if (arguments.Count != 1) {
                        throw new KqlRefusedException(
                            $"'todynamic' takes one argument and '{Text(call)}' gives it {arguments.Count}."
                        );
                    }

                    var argument = Scalar(stage, arguments[0]);

                    return argument.Type switch {
                        KqlType.Dynamic => argument,
                        // A flat JSON object of strings, which is the shape a tag map has when it has
                        // been through tostring(); anything else parses to an empty map.
                        KqlType.String => new($"JSONExtract({argument.Sql}, 'Map(String, String)')", KqlType.Dynamic),
                        _ => throw new KqlRefusedException(
                            $"'todynamic' takes a string and '{Text(arguments[0])}' is a {ResourceGraphSchema.WireName(argument.Type)}."
                        )
                    };
                }

                case "not": {
                    if (arguments.Count != 1) {
                        throw new KqlRefusedException(
                            $"'not' takes one condition and '{Text(call)}' gives it {arguments.Count}."
                        );
                    }

                    return new($"NOT {Boolean(stage, arguments[0]).Sql}", KqlType.Bool);
                }

                default:
                    throw Refused(call, "function", name);
            }
        }

        SqlExpression OneString(Stage stage, FunctionCallExpression call, List<Expression> arguments) {
            if (arguments.Count != 1) {
                throw new KqlRefusedException(
                    $"'{call.Name.SimpleName}' takes one argument and '{Text(call)}' gives it {arguments.Count}."
                );
            }

            var argument = Scalar(stage, arguments[0]);

            if (argument.Type != KqlType.String) {
                throw new KqlRefusedException(
                    $"'{call.Name.SimpleName}' takes a string and '{Text(arguments[0])}' is a {ResourceGraphSchema.WireName(argument.Type)}; "
                    + "wrap it in tostring(…)."
                );
            }

            return argument;
        }

        /// <summary>
        ///     The expression as a ClickHouse <c>String</c>: itself for a string, <c>toJSONString</c>
        ///     for the map, the words <c>true</c>/<c>false</c> for a bool — ClickHouse's own
        ///     <c>toString</c> of a <c>Bool</c> is <c>1</c>/<c>0</c>, and KQL's is the words — and
        ///     <c>toString</c> otherwise.
        /// </summary>
        static string AsString(SqlExpression scalar) =>
            scalar.Type switch {
                KqlType.String => scalar.Sql,
                KqlType.Dynamic => $"toJSONString({scalar.Sql})",
                KqlType.Bool => $"if({scalar.Sql}, 'true', 'false')",
                _ => $"toString({scalar.Sql})"
            };

        // ── Helpers ────────────────────────────────────────────────────────────────────────────

        string Parameter(string clickHouseType, string value) => Parameter($"p{literals++}", clickHouseType, value);

        string Parameter(string name, string clickHouseType, string value) {
            var parameter = new SqlParameter(name, clickHouseType, value);
            parameters.Add(parameter);
            return parameter.Placeholder;
        }

        static string Declared(SimpleNamedExpression named) {
            var name = named.Name.SimpleName;

            if (!Identifier.IsMatch(name)) {
                throw new KqlRefusedException(
                    $"'{name}' is not a column name this subset accepts: letters, digits and underscores, not starting with a digit."
                );
            }

            return name;
        }

        static IReadOnlyList<ColumnSymbol> ResultColumns(QueryOperator op) =>
            op.ResultType is TableSymbol table
                ? table.Columns
                : throw new KqlRefusedException($"'{Text(op)}' has no result the binder could type.");

        static string Quote(string identifier) {
            if (!Identifier.IsMatch(identifier)) {
                throw new KqlRefusedException(
                    $"'{identifier}' is not a column name this subset accepts: letters, digits and underscores, not starting with a digit."
                );
            }

            return "`" + identifier + "`";
        }

        static string Text(SyntaxNode node) => node.ToString(IncludeTrivia.Minimal).Trim();

        static KqlRefusedException Refused(SyntaxNode node, string what, string? token = null) =>
            new(
                $"'{token ?? Text(node)}' is not in the resource graph's KQL subset (an {what} it does not translate). "
                + KqlSubset.SupportedSentence,
                true
            );

        /// <summary>Escapes a term for RE2 so that it matches itself and nothing more.</summary>
        static string EscapeRe2(string term) {
            var built = new StringBuilder(term.Length + 8);

            foreach (var character in term) {
                if (char.IsLetterOrDigit(character) || character == '_' || character == ' ' || character == '-') {
                    built.Append(character);
                } else {
                    built.Append('\\').Append(character);
                }
            }

            return built.ToString();
        }

        // ── Rendering ──────────────────────────────────────────────────────────────────────────

        string Render(Stage stage, bool top) {
            var sql = new StringBuilder();
            sql.Append("SELECT ");

            if (stage.Distinct) {
                sql.Append("DISTINCT ");
            }

            // ⚠ A comparison is a UInt8 to ClickHouse and renders as 1/0 in JSON; toBool makes a
            // bool column the true/false a KQL reader expects. Only on the way out — inside an
            // expression a UInt8 is what AND, OR and NOT take — and a no-op on a column that is
            // already Bool.
            sql.AppendJoin(
                ", ",
                stage.Columns.Select(static x => $"{(x.Type == KqlType.Bool ? $"toBool({x.Sql})" : x.Sql)} AS {Quote(x.Name)}"
                )
            );
            sql.Append(" FROM ");

            if (stage.Inner is null) {
                sql.Append(ResourceGraphTable.Qualified(context.TenantId)).Append(" FINAL");
            } else {
                sql.Append('(').Append(Render(stage.Inner, false)).Append(')');
            }

            if (stage.Where.Count > 0) {
                sql.Append(" WHERE ").AppendJoin(" AND ", stage.Where);
            }

            if (stage.GroupBy.Count > 0) {
                sql.Append(" GROUP BY ").AppendJoin(", ", stage.GroupBy);
            }

            if (stage.OrderBy.Count > 0) {
                sql.Append(" ORDER BY ")
                    .AppendJoin(", ", stage.OrderBy.Select(static x => x.Sql + (x.Descending ? " DESC" : " ASC")));
            }

            if (stage.Limit is { } limit) {
                sql.Append(" LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));

                if (top && context.Offset > 0) {
                    sql.Append(" OFFSET ").Append(context.Offset.ToString(CultureInfo.InvariantCulture));
                }
            }

            return sql.ToString();
        }
    }
}
