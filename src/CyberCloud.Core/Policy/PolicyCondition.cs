using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Core.Policy;

/// <summary>
///     One node of a policy rule's <c>if</c> — a JSON-Logic-shaped condition over the resource body.
///     docs/plan/01 § A: <i>"JSON-Logic-shaped conditions over the resource body, evaluated in the write
///     path before the provider is called"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The operator set is closed and a word outside it is refused by name.</b> Three
///         combinators — <c>allOf</c>, <c>anyOf</c>, <c>not</c> — and a leaf that is a <c>field</c>
///         plus exactly one of <c>equals</c>, <c>in</c>, <c>like</c> or <c>exists</c>. Nothing else
///         parses. A rule that silently ignored an operator it did not know would be a deny policy
///         that denies nothing, and nobody finds out until the audit.
///     </para>
///     <para>
///         ⚠ <b>The cost is bounded at parse time, not trusted at evaluation time.</b> A condition is
///         at most <see cref="MaxDepth" /> deep and <see cref="MaxNodes" /> nodes wide, a combinator
///         takes at most <see cref="MaxBranches" /> branches and <c>in</c> at most
///         <see cref="MaxValues" /> values. Evaluation runs on every write in the tenant, inside the
///         request, so a rule that could be made arbitrarily expensive would be a way for one scope's
///         owner to slow every write beneath it.
///     </para>
///     <para>
///         <b>Comparison is Azure's:</b> strings compare ignoring case, numbers compare by value, and a
///         field that is absent is not equal to anything — so <c>not</c> over <c>equals</c> is true
///         for a body that lacks the field, and a rule that means "the field is set to something else"
///         has to say <c>exists</c> as well.
///     </para>
/// </remarks>
public abstract class PolicyCondition {
    /// <summary>How deep a condition may nest.</summary>
    public const int MaxDepth = 16;

    /// <summary>How many nodes one condition may hold, combinators and leaves together.</summary>
    public const int MaxNodes = 256;

    /// <summary>How many branches one <c>allOf</c> or <c>anyOf</c> may hold.</summary>
    public const int MaxBranches = 64;

    /// <summary>How many values one <c>in</c> may hold.</summary>
    public const int MaxValues = 256;

    /// <summary>The combinators, spelled as a rule spells them.</summary>
    public static IReadOnlyList<string> Combinators { get; } = ["allOf", "anyOf", "not"];

    /// <summary>The leaf operators, spelled as a rule spells them.</summary>
    public static IReadOnlyList<string> Operators { get; } = ["equals", "in", "like", "exists"];

    /// <summary>Whether the facts satisfy this condition.</summary>
    /// <param name="facts">The request and the body.</param>
    public abstract bool Evaluate(PolicyFacts facts);

    /// <summary>Every field this condition reads, in document order.</summary>
    public abstract IEnumerable<PolicyField> Fields { get; }

    /// <summary>
    ///     Every leaf test, in document order, with where it sits in the rule — so
    ///     <see cref="PolicyRule" /> can refuse a test that can't hold on any request its effect is
    ///     evaluated for.
    /// </summary>
    internal abstract IEnumerable<PolicyTest> Tests { get; }

    /// <summary>
    ///     Parses a condition.
    /// </summary>
    /// <param name="element">The <c>if</c> member.</param>
    /// <param name="target">The pointer the refusal names — where <paramref name="element" /> sits in the body.</param>
    /// <returns>The condition, or an <see cref="ErrorCode.InvalidRequestBody" /> naming the offending node.</returns>
    public static Result<PolicyCondition> Parse(JsonElement element, string target) {
        var budget = new Budget();
        return Parse(element, target, 1, budget);
    }

    static Result<PolicyCondition> Parse(JsonElement element, string target, int depth, Budget budget) {
        if (depth > MaxDepth) {
            return Invalid(
                $"The condition nests deeper than {MaxDepth.ToString(CultureInfo.InvariantCulture)} levels. The "
                + "limit bounds what one rule can cost on every write beneath its scope.",
                target
            );
        }

        if (++budget.Nodes > MaxNodes) {
            return Invalid(
                $"The condition has more than {MaxNodes.ToString(CultureInfo.InvariantCulture)} nodes. The limit "
                + "bounds what one rule can cost on every write beneath its scope; split it into two "
                + "definitions.",
                target
            );
        }

        if (element.ValueKind != JsonValueKind.Object) {
            return Invalid(
                $"A condition is a JSON object and this is a JSON {Kind(element.ValueKind)}. It is one of "
                + ClosedSet()
                + ".",
                target
            );
        }

        var members = element.EnumerateObject().ToArray();
        var hasField = members.Any(static x => string.Equals(x.Name, "field", StringComparison.Ordinal));

        foreach (var member in members) {
            if (string.Equals(member.Name, "field", StringComparison.Ordinal)
                || Combinators.Contains(member.Name, StringComparer.Ordinal)
                || Operators.Contains(member.Name, StringComparer.Ordinal)) {
                continue;
            }

            return Invalid(
                $"'{member.Name}' is not an operator. A condition is {ClosedSet()} — the set is closed, "
                + "and a rule that named an operator this platform does not evaluate would deny or audit "
                + "nothing without saying so.",
                PolicyField.Append(target, member.Name)
            );
        }

        if (!hasField) {
            if (members.Length != 1 || !Combinators.Contains(members[0].Name, StringComparer.Ordinal)) {
                return Invalid(
                    $"A condition without a 'field' is exactly one of allOf, anyOf or not, and this one has "
                    + $"[{string.Join(", ", members.Select(static x => x.Name))}]. A leaf operator — "
                    + $"{string.Join(", ", Operators)} — needs a 'field' beside it.",
                    target
                );
            }

            var only = members[0];
            var at = target + "/" + only.Name;

            return only.Name switch {
                "not" => Not(only.Value, at, depth, budget),
                _ => Combine(only.Name, only.Value, at, depth, budget)
            };
        }

        var operators = members.Where(static x => !string.Equals(x.Name, "field", StringComparison.Ordinal)).ToArray();

        if (operators.Length != 1 || !Operators.Contains(operators[0].Name, StringComparer.Ordinal)) {
            return Invalid(
                "A condition with a 'field' carries exactly one of "
                + string.Join(", ", Operators)
                + $" beside it, and this one has [{string.Join(", ", operators.Select(static x => x.Name))}]. "
                + "Combine two tests with allOf or anyOf.",
                target
            );
        }

        var fieldElement = members.First(static x => string.Equals(x.Name, "field", StringComparison.Ordinal)).Value;
        if (fieldElement.ValueKind != JsonValueKind.String) {
            return Invalid("'field' is a string.", target + "/field");
        }

        var field = PolicyField.Parse(fieldElement.GetString(), target + "/field");
        if (field.TryGetError(out var fieldError)) {
            return Result<PolicyCondition>.Failure(fieldError);
        }

        var op = operators[0];
        var operandTarget = target + "/" + op.Name;

        return op.Name switch {
            "equals" => Equal(field.GetValueOrThrow(), op.Value, operandTarget),
            "in" => In(field.GetValueOrThrow(), op.Value, operandTarget),
            "like" => Like(field.GetValueOrThrow(), op.Value, operandTarget),
            _ => Exists(field.GetValueOrThrow(), op.Value, operandTarget)
        };
    }

    static Result<PolicyCondition> Not(JsonElement operand, string target, int depth, Budget budget) {
        var inner = Parse(operand, target, depth + 1, budget);
        return inner.TryGetError(out var error)
            ? Result<PolicyCondition>.Failure(error)
            : Result<PolicyCondition>.Success(new NotCondition(inner.GetValueOrThrow()));
    }

    static Result<PolicyCondition> Combine(string name, JsonElement operand, string target, int depth, Budget budget) {
        if (operand.ValueKind != JsonValueKind.Array || operand.GetArrayLength() == 0) {
            return Invalid($"'{name}' takes a non-empty array of conditions.", target);
        }

        if (operand.GetArrayLength() > MaxBranches) {
            return Invalid(
                $"'{name}' has {operand.GetArrayLength().ToString(CultureInfo.InvariantCulture)} branches and the limit is "
                + $"{MaxBranches.ToString(CultureInfo.InvariantCulture)}.",
                target
            );
        }

        var branches = ImmutableArray.CreateBuilder<PolicyCondition>(operand.GetArrayLength());
        var index = 0;

        foreach (var item in operand.EnumerateArray()) {
            var parsed = Parse(item, target + "/" + index.ToString(CultureInfo.InvariantCulture), depth + 1, budget);
            if (parsed.TryGetError(out var error)) {
                return Result<PolicyCondition>.Failure(error);
            }

            branches.Add(parsed.GetValueOrThrow());
            index++;
        }

        return Result<PolicyCondition>.Success(
            string.Equals(name, "allOf", StringComparison.Ordinal)
                ? new AllOfCondition(branches.MoveToImmutable())
                : new AnyOfCondition(branches.MoveToImmutable())
        );
    }

    static Result<PolicyCondition> Equal(PolicyField field, JsonElement operand, string target) =>
        IsScalar(operand)
            ? Result<PolicyCondition>.Success(new EqualsCondition(field, JsonNode.Parse(operand.GetRawText())!, Leaf(target)))
            : Invalid("'equals' compares against a string, a number or a boolean.", target);

    static Result<PolicyCondition> In(PolicyField field, JsonElement operand, string target) {
        if (operand.ValueKind != JsonValueKind.Array || operand.GetArrayLength() == 0) {
            return Invalid("'in' takes a non-empty array of strings, numbers or booleans.", target);
        }

        if (operand.GetArrayLength() > MaxValues) {
            return Invalid(
                $"'in' has {operand.GetArrayLength().ToString(CultureInfo.InvariantCulture)} values and the limit is "
                + $"{MaxValues.ToString(CultureInfo.InvariantCulture)}.",
                target
            );
        }

        var values = ImmutableArray.CreateBuilder<JsonNode>(operand.GetArrayLength());
        var index = 0;

        foreach (var item in operand.EnumerateArray()) {
            if (!IsScalar(item)) {
                return Invalid(
                    "Each value 'in' compares against is a string, a number or a boolean.",
                    target + "/" + index.ToString(CultureInfo.InvariantCulture)
                );
            }

            values.Add(JsonNode.Parse(item.GetRawText())!);
            index++;
        }

        return Result<PolicyCondition>.Success(new InCondition(field, values.MoveToImmutable(), Leaf(target)));
    }

    static Result<PolicyCondition> Like(PolicyField field, JsonElement operand, string target) =>
        operand.ValueKind == JsonValueKind.String && operand.GetString()!.Length > 0
            ? Result<PolicyCondition>.Success(new LikeCondition(field, operand.GetString()!, Leaf(target)))
            : Invalid("'like' takes a non-empty string, where '*' matches any run of characters.", target);

    static Result<PolicyCondition> Exists(PolicyField field, JsonElement operand, string target) =>
        operand.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? Result<PolicyCondition>.Success(new ExistsCondition(field, operand.GetBoolean(), Leaf(target)))
            : Invalid("'exists' takes true or false.", target);

    /// <summary>Whether two JSON scalars are equal the way a condition compares them.</summary>
    /// <param name="left">The body's value. <see langword="null" /> when absent, which equals nothing.</param>
    /// <param name="right">The rule's value.</param>
    internal static bool ScalarEquals(JsonNode? left, JsonNode right) {
        if (left is not JsonValue leftValue || right is not JsonValue rightValue) {
            return false;
        }

        var leftKind = leftValue.GetValueKind();
        var rightKind = rightValue.GetValueKind();

        if (leftKind == JsonValueKind.String && rightKind == JsonValueKind.String) {
            return string.Equals(leftValue.GetValue<string>(), rightValue.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }

        if (leftKind == JsonValueKind.Number && rightKind == JsonValueKind.Number) {
            return leftValue.TryGetValue<decimal>(out var l) && rightValue.TryGetValue<decimal>(out var r)
                ? l == r
                : leftValue.GetValue<double>().Equals(rightValue.GetValue<double>());
        }

        return leftKind is JsonValueKind.True or JsonValueKind.False && leftKind == rightKind;
    }

    static bool IsScalar(JsonElement element) =>
        element.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    /// <summary>The leaf's own pointer, from the pointer to its operator's operand.</summary>
    static string Leaf(string operandTarget) => operandTarget[..operandTarget.LastIndexOf('/')];

    static string ClosedSet() =>
        "one of allOf, anyOf or not, or a 'field' with exactly one of " + string.Join(", ", Operators);

    static string Kind(JsonValueKind kind) => kind.ToString().ToLowerInvariant();

    static Result<PolicyCondition> Invalid(string message, string target) =>
        Result<PolicyCondition>.Failure(ErrorCode.InvalidRequestBody, message, target);

    sealed class Budget {
        public int Nodes { get; set; }
    }

    sealed class AllOfCondition(ImmutableArray<PolicyCondition> branches) : PolicyCondition {
        public override bool Evaluate(PolicyFacts facts) => branches.All(x => x.Evaluate(facts));

        public override IEnumerable<PolicyField> Fields => branches.SelectMany(static x => x.Fields);

        internal override IEnumerable<PolicyTest> Tests => branches.SelectMany(static x => x.Tests);
    }

    sealed class AnyOfCondition(ImmutableArray<PolicyCondition> branches) : PolicyCondition {
        public override bool Evaluate(PolicyFacts facts) => branches.Any(x => x.Evaluate(facts));

        public override IEnumerable<PolicyField> Fields => branches.SelectMany(static x => x.Fields);

        internal override IEnumerable<PolicyTest> Tests => branches.SelectMany(static x => x.Tests);
    }

    sealed class NotCondition(PolicyCondition inner) : PolicyCondition {
        public override bool Evaluate(PolicyFacts facts) => !inner.Evaluate(facts);

        public override IEnumerable<PolicyField> Fields => inner.Fields;

        internal override IEnumerable<PolicyTest> Tests => inner.Tests;
    }

    /// <summary>A test of one field: the node a <c>field</c> and its operator make.</summary>
    abstract class LeafCondition(PolicyField subject, string target) : PolicyCondition {
        protected PolicyField Subject => subject;

        public override IEnumerable<PolicyField> Fields => [subject];

        internal override IEnumerable<PolicyTest> Tests => [new(subject, target, this)];
    }

    sealed class EqualsCondition(PolicyField subject, JsonNode value, string target) : LeafCondition(subject, target) {
        public override bool Evaluate(PolicyFacts facts) => ScalarEquals(facts.Resolve(Subject), value);
    }

    sealed class InCondition(PolicyField subject, ImmutableArray<JsonNode> values, string target) : LeafCondition(subject, target) {
        public override bool Evaluate(PolicyFacts facts) {
            var actual = facts.Resolve(Subject);
            return actual is not null && values.Any(x => ScalarEquals(actual, x));
        }
    }

    sealed class ExistsCondition(PolicyField subject, bool expected, string target) : LeafCondition(subject, target) {
        public override bool Evaluate(PolicyFacts facts) => (facts.Resolve(Subject) is not null) == expected;
    }

    /// <summary><c>like</c>: <c>*</c> matches any run of characters, including none; case is ignored.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a regular expression, and deliberately so.</b> A pattern here is evaluated on every
    ///     write beneath the assignment, and a regular expression is the classic way to make one
    ///     comparison cost seconds. A glob has one metacharacter and a matcher that is linear in the
    ///     text times the number of stars.
    /// </remarks>
    sealed class LikeCondition(PolicyField subject, string pattern, string target) : LeafCondition(subject, target) {
        readonly string[] parts = pattern.Split('*');

        public override bool Evaluate(PolicyFacts facts) =>
            facts.Resolve(Subject) is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && Matches(value.GetValue<string>());

        bool Matches(string text) {
            if (parts.Length == 1) {
                return string.Equals(text, parts[0], StringComparison.OrdinalIgnoreCase);
            }

            if (!text.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            var position = parts[0].Length;

            for (var i = 1; i < parts.Length - 1; i++) {
                if (parts[i].Length == 0) {
                    continue;
                }

                var found = text.IndexOf(parts[i], position, StringComparison.OrdinalIgnoreCase);
                if (found < 0) {
                    return false;
                }

                position = found + parts[i].Length;
            }

            var last = parts[^1];
            return text.Length - position >= last.Length
                && text.EndsWith(last, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>One leaf of a condition, with the pointer to it in the definition.</summary>
/// <param name="Field">The field the test reads.</param>
/// <param name="Target">The leaf's pointer, for a refusal's <c>target</c>: <c>/properties/policyRule/if/allOf/1</c>.</param>
/// <param name="Condition">The leaf itself, which evaluates on its own.</param>
internal readonly record struct PolicyTest(PolicyField Field, string Target, PolicyCondition Condition);
