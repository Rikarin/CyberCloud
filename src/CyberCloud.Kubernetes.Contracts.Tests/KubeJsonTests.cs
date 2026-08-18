using Shouldly;
using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     The two ways a read-back object differs from the applied one, checked against the shapes that
///     produced them.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both directions cost a measured bug, and neither was a style argument.</b> The
///         <i>removal</i> direction is <c>omitempty</c>: <c>NetworkPolicySpec.Ingress</c> drops an
///         empty list, so the rule that spells "deny all ingress" comes back with no key, and
///         <c>CyberCloud.Terminal/consoles</c> converged in two fake harnesses and hung forever
///         against k3s. The <i>addition</i> direction is a CRD's <c>+kubebuilder:default</c>, which
///         put fields in a stored OpenSearch object that nobody applied and turned an
///         equality-instead-of-containment comparison red only against a real cluster.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is written as the difference between the two spellings</b>, not
///         merely as "the helper works". The spelling these replace — <c>is JsonArray { Count: 0 }</c>
///         and a deep-equality comparison — is true of what a provider applies and false of what a
///         cluster returns, which is why it passes every harness and fails in production.
///     </para>
/// </remarks>
public sealed class KubeJsonTests {
    [Fact]
    public void AnAbsentCollectionAndAnEmptyOneAreTheSameStatement() {
        // ⚠ THE WHOLE POINT. A built-in type returns the absent form and a custom resource returns
        // the empty one, for the same rendered intent — so a comparison that accepts only one of them
        // is wrong against half the catalogue.
        KubeJson.IsAbsentOrEmpty(null).ShouldBeTrue("absent is what omitempty leaves behind");
        KubeJson.IsAbsentOrEmpty(new JsonArray()).ShouldBeTrue("empty is what a custom resource keeps");
        KubeJson.IsAbsentOrEmpty(new JsonObject()).ShouldBeTrue("an empty map is dropped like an empty list");
    }

    [Fact]
    public void AListThatGrewAnEntryIsStillRefused() {
        // ⚠ THE HALF THAT MAKES ACCEPTING BOTH FORMS SAFE. Tolerating absent-or-empty would be a
        // weakening if it also tolerated a rule somebody added — a NetworkPolicy that gained an
        // ingress rule is a different security posture, and this is what still says so.
        KubeJson.IsAbsentOrEmpty(new JsonArray("one")).ShouldBeFalse();
        KubeJson.IsAbsentOrEmpty(new JsonObject { ["k"] = "v" }).ShouldBeFalse();
    }

    [Fact]
    public void AScalarIsNotACollectionAndIsNotTreatedAsAnEmptyOne() {
        // ⚠ A `false`, a zero and an empty string are all values a comparison may legitimately
        // require. omitempty drops them too — see this type's remarks — but that is a fact about
        // specific Go fields, and treating every falsy scalar as "absent" here would make a whole
        // class of correct comparison unfalsifiable.
        KubeJson.IsAbsentOrEmpty(JsonValue.Create(false)).ShouldBeFalse();
        KubeJson.IsAbsentOrEmpty(JsonValue.Create(0)).ShouldBeFalse();
        KubeJson.IsAbsentOrEmpty(JsonValue.Create(string.Empty)).ShouldBeFalse();
    }

    [Fact]
    public void AFieldTheServerAddedDoesNotBreakTheComparison() {
        // ⚠ THE OpenSearch BUG, IN ONE ASSERTION. A CRD's +kubebuilder:default puts `protocol` in the
        // stored object; nobody applied it. Equality fails, containment holds — and the Docker-free
        // harness cannot tell them apart, because it derives its CRD stub from `Objects` and a derived
        // stub has no defaults.
        var actual = JsonNode.Parse("""{"spec":{"port":9200,"protocol":"TCP","replicas":3}}""");
        var expected = JsonNode.Parse("""{"spec":{"port":9200,"replicas":3}}""");

        KubeJson.Contains(actual, expected).ShouldBeTrue("the server added a defaulted field");
        JsonNode.DeepEquals(actual, expected).ShouldBeFalse("which is exactly what equality refuses");
    }

    [Fact]
    public void AFieldTheServerRemovedDoesNotBreakTheComparison() {
        // The other direction, on the same helper: an expectation of "no entries" is satisfied by the
        // absent key omitempty leaves.
        var actual = JsonNode.Parse("""{"spec":{"policyTypes":["Ingress","Egress"]}}""");
        var expected = JsonNode.Parse("""{"spec":{"policyTypes":["Ingress","Egress"],"ingress":[]}}""");

        KubeJson.Contains(actual, expected).ShouldBeTrue("an empty expectation accepts an absent key");
    }

    [Fact]
    public void AValueTheServerChangedIsRefused() {
        // ⚠ THE CALIBRATION. Containment that accepted anything would pass the two tests above and be
        // worthless; this is what says it still compares.
        var actual = JsonNode.Parse("""{"spec":{"replicas":1}}""");
        var expected = JsonNode.Parse("""{"spec":{"replicas":3}}""");

        KubeJson.Contains(actual, expected).ShouldBeFalse();
    }

    [Fact]
    public void AMissingFieldIsRefusedRatherThanTreatedAsExtra() {
        var actual = JsonNode.Parse("""{"spec":{"replicas":3}}""");
        var expected = JsonNode.Parse("""{"spec":{"replicas":3,"version":"2.13"}}""");

        KubeJson.Contains(actual, expected).ShouldBeFalse("containment is about what is required, not optional");
    }

    [Fact]
    public void AListIsComparedPositionallyAndMayBeLongerButNotReordered() {
        // ⚠ POSITIONAL, AND THAT IS DELIBERATE. A container's args and an ordered rule chain are
        // different programs when reordered, so a set-wise match would bless a real defect. Longer is
        // allowed for the same reason a map may have extra keys: the server appends.
        var applied = JsonNode.Parse("""{"args":["serve","--tls"]}""");

        KubeJson.Contains(JsonNode.Parse("""{"args":["serve","--tls","--v=2"]}"""), applied)
            .ShouldBeTrue("the server appended an argument");

        KubeJson.Contains(JsonNode.Parse("""{"args":["--tls","serve"]}"""), applied)
            .ShouldBeFalse("reordered args are a different command line");

        KubeJson.Contains(JsonNode.Parse("""{"args":["serve"]}"""), applied)
            .ShouldBeFalse("a dropped argument is not containment");
    }

    [Fact]
    public void NestingIsFollowedAllTheWayDown() {
        var actual = JsonNode.Parse(
            """{"spec":{"template":{"spec":{"clusterName":"prod","extra":true}}}}"""
        );

        KubeJson.Contains(actual, JsonNode.Parse("""{"spec":{"template":{"spec":{"clusterName":"prod"}}}}"""))
            .ShouldBeTrue();

        KubeJson.Contains(actual, JsonNode.Parse("""{"spec":{"template":{"spec":{"clusterName":"dev"}}}}"""))
            .ShouldBeFalse("a wrong clusterName three levels down is still wrong");
    }
}
