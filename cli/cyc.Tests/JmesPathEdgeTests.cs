using CyberCloud.Cli.Output;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     The shapes a hand-written query engine gets wrong: an empty result set, a null field, a
///     projection over a non-array, a filter that matches nothing, and a function handed the wrong
///     type.
/// </summary>
/// <remarks>
///     ⚠ <b>Every case here is one where returning <i>something</i> is worse than refusing.</b>
///     <see cref="JmesPathTests" /> covers the expressions people mean to type; this covers the ones
///     they type by accident, because that is where a wrong answer survives — a query that answers
///     <c>0</c>, <c>false</c> or a plausibly-ordered list is never investigated, and one that answers
///     an error is fixed in the next keystroke.
/// </remarks>
public sealed class JmesPathEdgeTests {
    const string Document = """
        {
          "value": [
            {"name":"w1","location":"eu-central","properties":{"tier":"free","replicas":10}},
            {"name":"w2","location":"us-east","properties":{"tier":"premium","replicas":9}},
            {"name":"w3","location":"eu-central","properties":{"tier":"premium","replicas":100}}
          ],
          "empty": [],
          "flag": true,
          "count": 7,
          "nextLink": null
        }
        """;

    [Theory]
    // A filter that matches nothing is an empty array, and stays an array through the projection
    // after it — a script doing `| length` must not be handed null.
    [InlineData("value[?properties.tier == 'nope']", "[]")]
    [InlineData("value[?properties.tier == 'nope'].name", "[]")]
    [InlineData("value[?properties.replicas > `1000`].name", "[]")]
    // An empty result set stays empty through every operator that accepts one.
    [InlineData("empty", "[]")]
    [InlineData("empty[*].name", "[]")]
    [InlineData("empty[]", "[]")]
    [InlineData("empty[0:2]", "[]")]
    [InlineData("length(empty)", "0")]
    [InlineData("sort(empty)", "[]")]
    [InlineData("join(',', empty)", "\"\"")]
    // ⚠ The spec's answer for min/max of an empty array, and the reason it is null rather than an
    // error: "no resources yet" is a normal state of a control plane, not a broken query.
    [InlineData("max(empty)", "null")]
    // A null field reads as null, and so does a member of one — not an error and not a crash.
    [InlineData("nextLink", "null")]
    [InlineData("nextLink.name", "null")]
    [InlineData("nextLink.a.b.c", "null")]
    // A projection over a non-array is null, which is what the spec says and what makes an
    // over-applied [*] visible instead of silently empty.
    [InlineData("nextLink[*].name", "null")]
    [InlineData("count[*]", "null")]
    [InlineData("value[0].name[*]", "null")]
    [InlineData("value[0][?a == 'b']", "null")]
    // An index past the end is a miss, not an exception.
    [InlineData("value[99].name", "null")]
    [InlineData("value[-99].name", "null")]
    [InlineData("value[5:9].name", "[]")]
    // A multiselect over an element that lacks the field fills null rather than dropping the key,
    // so every row of `--output table` has the same columns.
    [InlineData("value[*].{n: name, z: nope}", """[{"n":"w1","z":null},{"n":"w2","z":null},{"n":"w3","z":null}]""")]
    public void Evaluates(string expression, string expected) {
        Run(expression).ShouldBe(expected);
    }

    /// <summary>
    ///     ⚠ <b>The defect this class was written for.</b> Ordering by the rendered cell put
    ///     <c>100</c> between <c>10</c> and <c>9</c>, so the answer looked sorted and was not.
    /// </summary>
    [Theory]
    [InlineData("sort(value[*].properties.replicas)", "[9,10,100]")]
    [InlineData("sort_by(value, &properties.replicas)[*].name", """["w2","w1","w3"]""")]
    [InlineData("max(value[*].properties.replicas)", "100")]
    [InlineData("min(value[*].properties.replicas)", "9")]
    // Strings still order as strings.
    [InlineData("sort(value[*].name)", """["w1","w2","w3"]""")]
    [InlineData("max(value[*].name)", "\"w3\"")]
    public void OrdersNumbersAsNumbers(string expression, string expected) {
        Run(expression).ShouldBe(expected);
    }

    /// <summary>
    ///     A function handed a present value outside its signature is refused rather than answered.
    /// </summary>
    [Theory]
    // ⚠ length() of a scalar answered 0 — indistinguishable from an empty page, so a query with a
    // misspelled field reported "no resources" instead of "wrong query".
    [InlineData("length(nextLink)", "must be a string, an array or an object")]
    [InlineData("length(count)", "must be a string, an array or an object")]
    [InlineData("length(flag)", "must be a string, an array or an object")]
    // ⚠ starts_with() coerced a non-string to "" and every string starts with "", so the filter
    // matched every element it was meant to narrow.
    [InlineData("starts_with(value[0].name, `3`)", "must be a string")]
    [InlineData("starts_with(count, 'w')", "must be a string")]
    [InlineData("ends_with(nextLink, 'w')", "must be a string")]
    // ⚠ join() fell back to the JSON encoding of each element, producing one comma-riddled string
    // that no `cut` pipeline could read back.
    [InlineData("join(',', value)", "must be an array of strings")]
    [InlineData("join(',', count)", "must be an array of strings")]
    [InlineData("join(`3`, value[*].name)", "must be a string")]
    [InlineData("contains(count, `7`)", "must be a string or an array")]
    [InlineData("keys(count)", "must be an object")]
    [InlineData("values(value)", "must be an object")]
    [InlineData("sort(count)", "must be an array")]
    [InlineData("sort_by(nextLink, &name)", "must be an array")]
    [InlineData("max(count)", "must be an array")]
    // A mixed array has no ordering the spec defines, so it is refused rather than ordered by a rule
    // nobody could predict.
    [InlineData("sort(value[*].properties)", "orders an array of numbers or an array of strings")]
    public void RefusesAnArgumentOfTheWrongType(string expression, string expected) {
        using var document = JsonDocument.Parse(Document);

        Should.Throw<CycUsageException>(() => JmesPath.Evaluate(expression, Payload.Of(document.RootElement)))
            .Message.ShouldContain(expected);
    }

    /// <summary>
    ///     ⚠ <b>An absent argument is not a wrong one.</b> A filter over a page where one resource is
    ///     missing the field must narrow the page rather than fail the command — the property
    ///     <c>--query "[].{name:name, tier:properties.tier}"</c> depends on.
    /// </summary>
    [Theory]
    [InlineData("value[?starts_with(nope, 'w')]", "[]")]
    [InlineData("value[?length(nope) > `0`]", "[]")]
    [InlineData("starts_with(nope, 'w')", "null")]
    [InlineData("length(nope)", "null")]
    [InlineData("join(',', nope)", "null")]
    [InlineData("not_null(nope, nextLink, value[0].name)", "\"w1\"")]
    public void AnAbsentArgumentPropagatesRatherThanFailing(string expression, string expected) {
        Run(expression).ShouldBe(expected);
    }

    static string Run(string expression) {
        using var document = JsonDocument.Parse(Document);

        return JmesPath.Evaluate(expression, Payload.Of(document.RootElement)).ToJson(indented: false);
    }
}
