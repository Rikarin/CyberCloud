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
///         reads. <c>Secret</c> would not have saved it: nothing on the write path substitutes a
///         <c>SecretRef</c> before the grain persists the body, so the value would be stored in
///         plaintext — docs/plan/02 § ADR-010 and the remarks on <c>SchemaProperty</c>. All the flag
///         buys is that a read withholds the property, which keeps a secret out of the API's answers
///         and not out of Postgres. So the property is absent, the chart row is <c>@internal</c>, and
///         these tests are what say so out loud.
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

    // ⚠ A FOURTH TEST WAS HERE AND IT IS GONE ON PURPOSE. It projected a desired body through
    // ResourceSchema.Project and asserted nothing was withheld. That method has been deleted: it was
    // the unreachable twin of ResourceGrain.Project, and its secret drop is now
    // ResourceManagerService.ReadablePointers, asserted end to end by
    // CyberCloud.ResourceManager.Tests.WritePathTests.ASecretPropertyIsNeverProjectedBackToTheCaller.
    //
    // What the deleted test actually pinned — this type withholds nothing on a read because it declares
    // no Secret property — is the first test above, which says it more directly.
    // charts/managed/postgres/conformance.yaml's `secrets-never-round-trip` is still satisfied by
    // absence rather than by redaction, which is the stronger of the two and should not be quietly
    // weakened into the other later.
}
