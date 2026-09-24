using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Core.Policy;

/// <summary>What a matching rule does. The set is closed, and it is Azure Policy's first three.</summary>
public enum PolicyRuleEffect {
    /// <summary>Never assigned. Not an effect.</summary>
    Unknown = 0,

    /// <summary>Refuse the request, naming the assignment and the definition.</summary>
    Deny = 1,

    /// <summary>Let the request through and record the resource as non-compliant.</summary>
    Audit = 2,

    /// <summary>Rewrite the body, then let the request through.</summary>
    Modify = 3
}

/// <summary>How a modify operation treats a field the body already carries.</summary>
public enum PolicyModificationKind {
    /// <summary>Never assigned.</summary>
    Unknown = 0,

    /// <summary>Writes the value only where the body has none — a default the caller can override.</summary>
    Add = 1,

    /// <summary>Writes the value whatever the body carried — a value the caller cannot override.</summary>
    Replace = 2
}

/// <summary>One rewrite a modify rule makes.</summary>
/// <param name="Kind">Whether it may overwrite what the caller sent.</param>
/// <param name="Field">The pointer it writes. ⚠ Always a pointer — a fact such as <c>type</c> is not the body's to change.</param>
/// <param name="Value">The value it writes.</param>
public sealed record PolicyModification(PolicyModificationKind Kind, PolicyField Field, JsonNode? Value) {
    /// <summary>The two operation names, spelled as a rule spells them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["add", "replace"];

    /// <summary>The operation as the rule spells it — <c>add</c> or <c>replace</c>.</summary>
    public string Name => Kind == PolicyModificationKind.Add ? "add" : "replace";

    /// <summary>
    ///     Applies the rewrite to the document the write will send and to the document a condition
    ///     reads, keeping the two in step.
    /// </summary>
    /// <param name="target">
    ///     The document the write sends on — a <c>PUT</c>'s body, or a <c>PATCH</c>'s merge patch.
    /// </param>
    /// <param name="prospective">
    ///     The body as the write would leave the resource. For a <c>PUT</c> it is
    ///     <paramref name="target" /> itself; for a <c>PATCH</c> it is the stored body with the patch
    ///     merged in, which is what <see cref="PolicyModificationKind.Add" /> has to ask "is it set"
    ///     of — a patch omits every field it is not changing.
    /// </param>
    /// <returns>
    ///     Whether the rewrite changed anything, or <see cref="ErrorCode.PolicyViolation" /> when the
    ///     body's shape leaves nowhere to write.
    /// </returns>
    public Result<bool> ApplyTo(JsonObject target, JsonObject prospective) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(prospective);

        var current = Field.Read(prospective);

        if (Kind == PolicyModificationKind.Add && current is not null) {
            return Result<bool>.Success(false);
        }

        if (current is not null && Value is not null && JsonNode.DeepEquals(current, Value)) {
            return Result<bool>.Success(false);
        }

        var written = Field.Write(target, Value);
        if (written.TryGetError(out var error)) {
            return Result<bool>.Failure(error);
        }

        if (!ReferenceEquals(target, prospective)) {
            var mirrored = Field.Write(prospective, Value);
            if (mirrored.TryGetError(out var mirrorError)) {
                return Result<bool>.Failure(mirrorError);
            }
        }

        return Result<bool>.Success(true);
    }

    /// <summary>The rewrite in one line — <c>replace /properties/sku = "gp1"</c> — for the trace.</summary>
    public string Render() => Name + " " + Field.Value + " = " + (Value?.ToJsonString() ?? "null");

    /// <inheritdoc />
    public override string ToString() => Render();
}

/// <summary>
///     A policy definition's <c>policyRule</c>: an <c>if</c> over the resource and a <c>then</c> that
///     says what happens when it holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Where each effect applies is part of the rule, and it is not the same for all three.</b>
///         A rule that reads neither <see cref="PolicyField.Operation" /> nor
///         <see cref="PolicyField.Action" /> is a rule about the resource's <i>shape</i>, and only a
///         create or an update changes a shape — so it is not evaluated on a <c>DELETE</c> or an
///         action. The alternative is the defect Azure's own <c>deny</c> would have if it ran on
///         deletes: "deny any server whose sku is <c>premium</c>" would make every existing premium
///         server undeletable, which is the one thing the tenant writing that rule is trying to get
///         rid of. A <see cref="PolicyRuleEffect.Deny" /> rule that reads either request fact —
///         <c>{ "field": "operation", "equals": "delete" }</c> beside <c>/tags/env equals prod</c>, or
///         <c>{ "field": "action", "equals": "rotateKeys" }</c> alone — has said which request it's
///         about, and it's evaluated on every request. Audit and modify are shape effects and never
///         apply to a delete or an action: there is no body to rewrite and nothing new to be
///         compliant with.
///     </para>
///     <para>
///         ⚠ <b>So a test that can't hold where its effect runs is refused, not stored.</b> An audit
///         or a modify that names <c>delete</c> or <c>action</c>, or reads the <c>action</c> fact —
///         empty on every create and update — would be accepted and never run, and a test of
///         <c>operation</c> against a word outside the four holds on no request at all. Each is the
///         rule that "denies nothing and says nothing" the closed operator set exists to prevent, so
///         <see cref="Parse(JsonElement, string)" /> refuses it, targeting the leaf. Found by the
///         third review of issue #46, which stored <c>{ "field": "action", "equals": "restart" }</c> as a
///         deny that no action ever reached.
///     </para>
///     <para>
///         ⚠ <b>The order across rules is modify, then deny, then audit</b>, and it is Azure's. A
///         deny rule judges the body the write will actually store, so a modify that fixes a
///         violation turns a refusal into a success rather than racing it — and audit records the
///         state the resource is left in, not the state it was asked for in.
///     </para>
/// </remarks>
public sealed class PolicyRule {
    /// <summary>How many operations one modify rule may carry.</summary>
    public const int MaxOperations = 16;

    /// <summary>The three effects, spelled as a rule spells them.</summary>
    public static IReadOnlyList<string> Effects { get; } = ["deny", "audit", "modify"];

    PolicyRule(PolicyCondition condition, PolicyRuleEffect effect, ImmutableArray<PolicyModification> operations) {
        Condition = condition;
        Effect = effect;
        Operations = operations;
        NamesRequest = condition.Fields.Any(static x => IsRequestFact(x.Value));
    }

    /// <summary>The <c>if</c>.</summary>
    public PolicyCondition Condition { get; }

    /// <summary>The <c>then</c>'s effect.</summary>
    public PolicyRuleEffect Effect { get; }

    /// <summary>What a modify rule writes. Empty for the other two effects.</summary>
    public ImmutableArray<PolicyModification> Operations { get; }

    /// <summary>
    ///     Whether the condition reads <see cref="PolicyField.Operation" /> or
    ///     <see cref="PolicyField.Action" />, which is what takes a deny past creates and updates. See
    ///     the remarks.
    /// </summary>
    public bool NamesRequest { get; }

    /// <summary>The effect as a rule spells it.</summary>
    public string EffectName => Spell(Effect);

    /// <summary>
    ///     Whether this rule is evaluated for a request of this kind at all — the remarks carry the
    ///     argument.
    /// </summary>
    /// <param name="operation">One of <see cref="PolicyOperations.All" />.</param>
    public bool AppliesTo(string operation) =>
        PolicyOperations.WritesBody(operation) || (Effect == PolicyRuleEffect.Deny && NamesRequest);

    /// <summary>Whether the <c>if</c> holds.</summary>
    /// <param name="facts">The request and the body.</param>
    public bool Matches(PolicyFacts facts) => Condition.Evaluate(facts);

    /// <summary>The spelling of an effect.</summary>
    /// <param name="effect">The effect.</param>
    public static string Spell(PolicyRuleEffect effect) =>
        effect switch {
            PolicyRuleEffect.Deny => "deny",
            PolicyRuleEffect.Audit => "audit",
            PolicyRuleEffect.Modify => "modify",
            _ => "unknown"
        };

    /// <summary>Parses a <c>policyRule</c> from its JSON text.</summary>
    /// <param name="json">The rule, as a definition stores it.</param>
    /// <param name="target">The pointer the refusal names.</param>
    public static Result<PolicyRule> Parse(string json, string target) {
        ArgumentNullException.ThrowIfNull(json);

        try {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement, target);
        } catch (JsonException exception) {
            return Result<PolicyRule>.Failure(
                ErrorCode.InvalidRequestBody,
                $"The policy rule is not valid JSON: {exception.Message}",
                target
            );
        }
    }

    /// <summary>Parses a <c>policyRule</c>.</summary>
    /// <param name="element">The <c>policyRule</c> member.</param>
    /// <param name="target">The pointer the refusal names — where <paramref name="element" /> sits.</param>
    /// <returns>
    ///     The rule, or an <see cref="ErrorCode.InvalidRequestBody" /> whose target is the offending
    ///     member and whose message names the closed set it was not in.
    /// </returns>
    public static Result<PolicyRule> Parse(JsonElement element, string target) {
        if (element.ValueKind != JsonValueKind.Object) {
            return Invalid("A policy rule is a JSON object with exactly 'if' and 'then'.", target);
        }

        foreach (var member in element.EnumerateObject()) {
            if (member.Name is not ("if" or "then")) {
                return Invalid(
                    $"'{member.Name}' is not a member of a policy rule, which has exactly 'if' and 'then'.",
                    PolicyField.Append(target, member.Name)
                );
            }
        }

        if (!element.TryGetProperty("if", out var when)) {
            return Invalid("A policy rule needs an 'if' — the condition over the resource.", target + "/if");
        }

        if (!element.TryGetProperty("then", out var then) || then.ValueKind != JsonValueKind.Object) {
            return Invalid("A policy rule needs a 'then' object carrying the 'effect'.", target + "/then");
        }

        var condition = PolicyCondition.Parse(when, target + "/if");
        if (condition.TryGetError(out var conditionError)) {
            return Result<PolicyRule>.Failure(conditionError);
        }

        var thenTarget = target + "/then";

        foreach (var member in then.EnumerateObject()) {
            if (member.Name is not ("effect" or "operations")) {
                return Invalid(
                    $"'{member.Name}' is not a member of 'then', which carries 'effect' and — for modify, "
                    + "and only for modify — 'operations'.",
                    PolicyField.Append(thenTarget, member.Name)
                );
            }
        }

        var effect = then.TryGetProperty("effect", out var effectElement) && effectElement.ValueKind == JsonValueKind.String
            ? effectElement.GetString() switch {
                "deny" => PolicyRuleEffect.Deny,
                "audit" => PolicyRuleEffect.Audit,
                "modify" => PolicyRuleEffect.Modify,
                _ => PolicyRuleEffect.Unknown
            }
            : PolicyRuleEffect.Unknown;

        if (effect == PolicyRuleEffect.Unknown) {
            return Invalid(
                "'effect' is one of "
                + string.Join(", ", Effects)
                + " — the set is closed. An effect this platform does not act on would be a rule that "
                + "matches and does nothing.",
                thenTarget + "/effect"
            );
        }

        if (Unreachable(condition.GetValueOrThrow(), effect) is { } unreachable) {
            return Result<PolicyRule>.Failure(unreachable);
        }

        var hasOperations = then.TryGetProperty("operations", out var operationsElement);

        if (effect != PolicyRuleEffect.Modify) {
            return hasOperations
                ? Invalid(
                    $"'operations' belongs to a modify rule and this rule's effect is '{Spell(effect)}'. A deny "
                    + "or audit rule that carried operations would rewrite nothing and read as if it did.",
                    thenTarget + "/operations"
                )
                : Result<PolicyRule>.Success(new(condition.GetValueOrThrow(), effect, []));
        }

        var operations = ParseOperations(hasOperations ? operationsElement : default, thenTarget + "/operations");

        return operations.TryGetError(out var operationsError)
            ? Result<PolicyRule>.Failure(operationsError)
            : Result<PolicyRule>.Success(new(condition.GetValueOrThrow(), effect, operations.GetValueOrThrow()));
    }

    /// <summary>
    ///     The refusal for the first test of a request fact that can't hold on any request this effect
    ///     is evaluated for, or <see langword="null" />. The remarks carry the argument.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Decided by evaluating the leaf on each of the four operations</b>, not by reading its
    ///     operand: <c>equals</c>, <c>in</c> and <c>like</c> compare ignoring case and a glob can
    ///     match two of the four, so asking the leaf is the only reading that can't disagree with how
    ///     it runs. A leaf that holds on all four — <c>exists: true</c>, <c>like: "*"</c> — doesn't
    ///     tell the operations apart and is left alone.
    /// </remarks>
    static Error? Unreachable(PolicyCondition condition, PolicyRuleEffect effect) {
        var shape = Spell(effect);

        foreach (var test in condition.Tests) {
            if (string.Equals(test.Field.Value, PolicyField.Action, StringComparison.Ordinal)) {
                if (effect != PolicyRuleEffect.Deny) {
                    return new(
                        ErrorCode.InvalidRequestBody,
                        $"'action' is empty on every create and update, and those are the only requests a {shape} "
                        + "rule is evaluated for — so this test can't tell them apart and the rule would never "
                        + "see an action. Only a deny rule reaches an action; docs/plan/08 § Policy.",
                        test.Target + "/field"
                    );
                }

                continue;
            }

            if (!string.Equals(test.Field.Value, PolicyField.Operation, StringComparison.Ordinal)) {
                continue;
            }

            var holds = PolicyOperations.All.Where(x => test.Condition.Evaluate(Probe(x))).ToArray();

            if (holds.Length == 0) {
                return new(
                    ErrorCode.InvalidRequestBody,
                    "'operation' is one of "
                    + string.Join(", ", PolicyOperations.All)
                    + ", and this test holds for none of them — so it's false on every request, or under a "
                    + "'not' true on every request.",
                    test.Target
                );
            }

            if (effect != PolicyRuleEffect.Deny
                && holds.Length < PolicyOperations.All.Count
                && holds.FirstOrDefault(static x => !PolicyOperations.WritesBody(x)) is { } unseen) {
                return new(
                    ErrorCode.InvalidRequestBody,
                    $"This test names '{unseen}', and a {shape} rule is evaluated only for a create or an "
                    + "update — there is no body to rewrite or judge on a delete or an action. Name create "
                    + "or update, or make the rule a deny; docs/plan/08 § Policy.",
                    test.Target
                );
            }
        }

        return null;
    }

    /// <summary>The facts of a request that differs from the others only in its operation.</summary>
    static PolicyFacts Probe(string operation) => new("", "", operation, "", new JsonObject());

    static bool IsRequestFact(string field) =>
        string.Equals(field, PolicyField.Operation, StringComparison.Ordinal)
        || string.Equals(field, PolicyField.Action, StringComparison.Ordinal);

    static Result<ImmutableArray<PolicyModification>> ParseOperations(JsonElement element, string target) {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0) {
            return Invalid<ImmutableArray<PolicyModification>>(
                "A modify rule carries a non-empty 'operations' array — each one "
                + """{ "operation": "add" | "replace", "field": "/…", "value": … }.""",
                target
            );
        }

        if (element.GetArrayLength() > MaxOperations) {
            return Invalid<ImmutableArray<PolicyModification>>(
                $"A modify rule carries at most {MaxOperations.ToString(CultureInfo.InvariantCulture)} operations.",
                target
            );
        }

        var built = ImmutableArray.CreateBuilder<PolicyModification>(element.GetArrayLength());
        var index = 0;

        foreach (var item in element.EnumerateArray()) {
            var at = target + "/" + index.ToString(CultureInfo.InvariantCulture);
            index++;

            if (item.ValueKind != JsonValueKind.Object) {
                return Invalid<ImmutableArray<PolicyModification>>("An operation is a JSON object.", at);
            }

            foreach (var member in item.EnumerateObject()) {
                if (member.Name is not ("operation" or "field" or "value")) {
                    return Invalid<ImmutableArray<PolicyModification>>(
                        $"'{member.Name}' is not a member of an operation, which has exactly 'operation', "
                        + "'field' and 'value'.",
                        PolicyField.Append(at, member.Name)
                    );
                }
            }

            var kind = item.TryGetProperty("operation", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() switch {
                    "add" => PolicyModificationKind.Add,
                    "replace" => PolicyModificationKind.Replace,
                    _ => PolicyModificationKind.Unknown
                }
                : PolicyModificationKind.Unknown;

            if (kind == PolicyModificationKind.Unknown) {
                return Invalid<ImmutableArray<PolicyModification>>(
                    "'operation' is one of " + string.Join(", ", PolicyModification.Names) + " — the set is closed.",
                    at + "/operation"
                );
            }

            if (!item.TryGetProperty("field", out var fieldElement) || fieldElement.ValueKind != JsonValueKind.String) {
                return Invalid<ImmutableArray<PolicyModification>>("An operation needs a 'field' pointer.", at + "/field");
            }

            var field = PolicyField.Parse(fieldElement.GetString(), at + "/field");
            if (field.TryGetError(out var fieldError)) {
                return Result<ImmutableArray<PolicyModification>>.Failure(fieldError);
            }

            if (!field.GetValueOrThrow().IsPointer) {
                return Invalid<ImmutableArray<PolicyModification>>(
                    $"'{field.GetValueOrThrow()}' is a fact about the request, and an operation writes a member of "
                    + "the body. Name it with a pointer such as '/tags/costCenter'.",
                    at + "/field"
                );
            }

            if (!item.TryGetProperty("value", out var valueElement)) {
                return Invalid<ImmutableArray<PolicyModification>>("An operation needs a 'value'.", at + "/value");
            }

            built.Add(new(kind, field.GetValueOrThrow(), JsonNode.Parse(valueElement.GetRawText())));
        }

        return Result<ImmutableArray<PolicyModification>>.Success(built.MoveToImmutable());
    }

    static Result<PolicyRule> Invalid(string message, string target) =>
        Result<PolicyRule>.Failure(ErrorCode.InvalidRequestBody, message, target);

    static Result<T> Invalid<T>(string message, string target) where T : notnull =>
        Result<T>.Failure(ErrorCode.InvalidRequestBody, message, target);
}
