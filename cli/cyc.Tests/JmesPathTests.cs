using CyberCloud.Cli.Output;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     The <c>--query</c> subset — docs/plan/21 § Decisions: <i>"Azure CLI's convention; a huge
///     productivity feature and a well-specified language."</i>
/// </summary>
/// <remarks>
///     ⚠ The expressions here are the ones a control plane actually gets typed at it. What is
///     deliberately outside the subset is asserted too, because "fails with a clear message" is the
///     other half of the promise.
/// </remarks>
public sealed class JmesPathTests {
    const string Document = """
        {
          "value": [
            {"name":"w1","location":"eu-central","tags":{"env":"prod"},"properties":{"tier":"free","replicas":1,"cidrs":["10.0.0.0/8"]}},
            {"name":"w2","location":"us-east","tags":{"env":"dev"},"properties":{"tier":"premium","replicas":3,"cidrs":["10.1.0.0/16","10.2.0.0/16"]}},
            {"name":"w3","location":"eu-central","tags":{},"properties":{"tier":"premium","replicas":2,"cidrs":[]}}
          ],
          "nextLink": null
        }
        """;

    [Theory]
    // Field access and nesting.
    [InlineData("value[0].name", "\"w1\"")]
    [InlineData("value[-1].name", "\"w3\"")]
    [InlineData("value[0].properties.tier", "\"free\"")]
    [InlineData("value[0].tags.env", "\"prod\"")]
    // A miss is null rather than an error.
    [InlineData("value[0].nope", "null")]
    [InlineData("nextLink", "null")]
    // Projections.
    [InlineData("value[*].name", """["w1","w2","w3"]""")]
    [InlineData("value[].name", """["w1","w2","w3"]""")]
    [InlineData("value[].properties.cidrs[]", """["10.0.0.0/8","10.1.0.0/16","10.2.0.0/16"]""")]
    // Slices.
    [InlineData("value[0:2].name", """["w1","w2"]""")]
    [InlineData("value[::2].name", """["w1","w3"]""")]
    // Filters.
    [InlineData("value[?properties.tier == 'premium'].name", """["w2","w3"]""")]
    [InlineData("value[?properties.replicas > `2`].name", """["w2"]""")]
    [InlineData("value[?location == 'eu-central' && properties.tier == 'premium'].name", """["w3"]""")]
    [InlineData("value[?!tags.env].name", """["w3"]""")]
    // Multiselect.
    [InlineData("value[0].[name, location]", """["w1","eu-central"]""")]
    // Pipes.
    [InlineData("value[*].name | [1]", "\"w2\"")]
    // Functions.
    [InlineData("length(value)", "3")]
    [InlineData("keys(value[0].tags)", """["env"]""")]
    [InlineData("join(', ', value[*].name)", "\"w1, w2, w3\"")]
    [InlineData("sort(value[*].location)", """["eu-central","eu-central","us-east"]""")]
    [InlineData("sort_by(value, &name)[0].name", "\"w1\"")]
    [InlineData("max(value[*].properties.replicas)", "3")]
    [InlineData("starts_with(value[0].name, 'w')", "true")]
    [InlineData("contains(value[*].name, 'w2')", "true")]
    [InlineData("not_null(nextLink, value[0].name)", "\"w1\"")]
    public void Evaluates(string expression, string expected) {
        using var document = JsonDocument.Parse(Document);

        JmesPath.Evaluate(expression, Payload.Of(document.RootElement))
            .ToJson(indented: false)
            .ShouldBe(expected);
    }

    [Fact]
    public void MultiselectHashKeepsDeclarationOrder() {
        using var document = JsonDocument.Parse(Document);

        JmesPath.Evaluate("value[*].{n: name, t: properties.tier}", Payload.Of(document.RootElement))
            .ToJson(indented: false)
            .ShouldBe("""[{"n":"w1","t":"free"},{"n":"w2","t":"premium"},{"n":"w3","t":"premium"}]""");
    }

    [Fact]
    public void AnOrderingComparisonAgainstANonNumberIsNullRatherThanAnError() {
        using var document = JsonDocument.Parse(Document);

        // ⚠ The spec's rule, and it is the one that makes a filter safe over a page where one
        // resource is missing the field.
        JmesPath.Evaluate("value[?name > `2`].name", Payload.Of(document.RootElement))
            .ToJson(indented: false)
            .ShouldBe("[]");
    }

    [Theory]
    [InlineData("value[")]
    [InlineData("value[?")]
    [InlineData("value[0")]
    [InlineData("{a:")]
    [InlineData("value[*].")]
    public void RefusesWhatItCannotParse(string expression) {
        using var document = JsonDocument.Parse(Document);

        Should.Throw<CycUsageException>(() => JmesPath.Evaluate(expression, Payload.Of(document.RootElement)))
            .Message.ShouldContain("--query could not be parsed");
    }

    [Fact]
    public void NamesTheFunctionsItHasWhenAskedForOneItDoesNot() {
        using var document = JsonDocument.Parse(Document);

        var failure = Should.Throw<CycUsageException>(
            () => JmesPath.Evaluate("map(&name, value)", Payload.Of(document.RootElement)));

        failure.Message.ShouldContain("'map' is not a function");
        failure.Message.ShouldContain("length");
    }
}
