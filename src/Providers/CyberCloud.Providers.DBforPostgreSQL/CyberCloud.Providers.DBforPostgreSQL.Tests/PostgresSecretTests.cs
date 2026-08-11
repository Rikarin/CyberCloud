using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforPostgreSQL.Tests;

/// <summary>
///     The one credential this type has, and the four places it must not appear.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Non-negotiables, the "Secrets never reach grain state" row; docs/plan/12
///         § Cross-cutting decisions, Credentials: <i>"Generated at create, written to the tenant's
///         Vault path, never in grain state."</i> <c>CC1005</c> is the compile-time half and polices
///         <c>[Id]</c>-annotated members named <c>*Password</c>/<c>*Secret</c>/<c>*Token</c>/
///         <c>*Key</c>; nothing in this provider declares one, and a <c>[SuppressMessage]</c> here
///         would be a failure rather than a fix.
///     </para>
///     <para>
///         ⚠ <b>The analyzer is not the interesting half on this type, because the risk here is not a
///         field.</b> <c>charts/managed/postgres/values.yaml</c> carries a <c>bootstrap.password</c>
///         row, and a provider that mirrored it as a body property would have put a plaintext
///         password into the resource grain's desired state through a JSON string that no analyzer
///         reads. <c>SchemaProperty.Secret</c>'s own remarks say the value <i>"never reaches grain
///         state — it is replaced by a <c>SecretRef</c> before step 8"</i>, and no such substitution
///         exists anywhere in <c>src/CyberCloud.ResourceManager</c> — the only thing
///         <c>Secret</c> does today is make <c>ResourceSchema.Project</c> drop the property on a
///         <i>read</i>. So the property is absent, the chart row is <c>@internal</c>, and these tests
///         are what say so out loud.
///     </para>
/// </remarks>
public sealed class PostgresSecretTests {
    [Fact]
    public void NoPropertyInTheBodyIsASecretAndNothingIsNamedLikeOne() {
        foreach (var property in PostgresServers.Schema2026.Properties) {
            property.Secret.ShouldBeFalse(
                $"'{property.JsonPointer}' is declared Secret, and the write path does not substitute a "
                + "SecretRef before the grain writes desired state — so the value would be persisted."
            );

            foreach (var word in new[] { "password", "secret", "token", "key" }) {
                property.Name.Contains(word, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"'{property.JsonPointer}' is named like a credential. CC1005 bans the shape on "
                    + "[Id]-annotated members; a JSON pointer is the same hazard with no analyzer "
                    + "watching it."
                );
            }
        }
    }

    [Fact]
    public void ABodyThatCarriesABootstrapPasswordIsRefusedRatherThanStoredOrDropped() {
        // ⚠ THE SABOTAGE, AND IT IS THE TEST. A caller who has read the chart will send this. The
        // schema must refuse it — RejectsUnknownProperties defaults to refusing — because the two
        // alternatives are both bad: storing it puts a plaintext password in durable state, and
        // dropping it silently tells the caller a password took when it did not.
        var body = JsonNode.Parse(PostgresServers.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["bootstrap"]!.AsObject()["password"] = "hunter2";

        using var document = JsonDocument.Parse(body.ToJsonString());
        var validated = PostgresServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        validated.Error.Target.ShouldBe("/properties/bootstrap/password");
    }

    [Fact]
    public void TheRenderedClusterNamesACredentialSecretAndCarriesNoPasswordValue() {
        // CloudNativePG reads the owner's password out of a Secret named in the CR, so the CR itself
        // never holds one — docs/plan/12 § The pattern, once, piece 5. This asserts the rendering
        // takes that seam rather than inlining anything.
        using var desired = JsonDocument.Parse(PostgresServers.Body(Guid.NewGuid()));

        var rendered = PostgresServers.ClusterJson("credentials", desired.RootElement);
        var initdb = JsonNode.Parse(rendered)!["spec"]!["bootstrap"]!["initdb"]!.AsObject();

        initdb["secret"]!["name"]!.GetValue<string>()
            .ShouldBe(PostgresServers.CredentialSecretName("credentials"));

        initdb.ContainsKey("password").ShouldBeFalse();
        rendered.Contains("password", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            "the rendered Cluster mentions a password. The only legal mention is a Secret reference "
            + "by name, and that is spelled 'secret'."
        );
    }

    [Fact]
    public void AProjectionOfTheDesiredBodyIsTheWholeBodyBecauseThereIsNothingToWithhold() {
        // ⚠ The corollary worth pinning: ResourceSchema.Project drops Secret properties on a read, and
        // this type has none — so a GET returns everything it stored, and "secrets never round-trip"
        // holds because none went in. charts/managed/postgres/conformance.yaml's
        // `secrets-never-round-trip` assertion is satisfied by absence rather than by redaction, which
        // is the stronger of the two and should not be quietly weakened into the other later.
        var body = JsonNode.Parse(PostgresServers.Body(Guid.NewGuid()))!.AsObject();
        var projected = PostgresServers.Schema2026.Project(body);

        // ⚠ Compared with members sorted. Project rebuilds the object in the SCHEMA's declaration
        // order rather than the body's, so a raw text comparison would be asserting that Body happens
        // to be written in schema order — which is true today and is not the property under test.
        Sorted(projected).ShouldBe(Sorted(body));
    }

    static string Sorted(JsonNode? value) => Sort(value)?.ToJsonString() ?? "null";

    static JsonNode? Sort(JsonNode? value) {
        if (value is not JsonObject map) {
            return value?.DeepClone();
        }

        var sorted = new JsonObject();
        foreach (var member in map.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            sorted[member.Key] = Sort(member.Value);
        }

        return sorted;
    }
}
