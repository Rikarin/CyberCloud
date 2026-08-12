using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     The two credentials this type has, and the places they must not appear.
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
///         ⚠ <b>The analyzer is not the interesting half, because the risk is not a field.</b>
///         <c>charts/managed/mariadb/values.yaml</c> carries a <c>credentials</c> block with two
///         <c>@secret</c> rows in it, and a provider that mirrored them as body properties would have
///         put two plaintext passwords into the resource grain's desired state through JSON strings no
///         analyzer reads. <c>Secret</c> would not have saved it: nothing on the write path
///         substitutes a <c>SecretRef</c> before the grain persists the body.
///     </para>
///     <para>
///         ⚠ <b>What is different from BOTH neighbours is what happens when the value is missing, and
///         it is a difference this provider CHOSE rather than inherited.</b> CloudNativePG generates
///         its own password when the referenced <c>Secret</c> is absent; spotahome generates nothing,
///         so a Valkey cache does not come up. mariadb-operator does neither by itself — it generates
///         only when the reference says <c>generate: true</c>, and that field's own default is
///         <c>false</c>. See <see cref="TheCredentialReferencesAskTheOperatorToGenerate" />.
///     </para>
/// </remarks>
public sealed class MariaDbSecretTests {
    [Fact]
    public void NoPropertyInTheBodyIsASecretAndNothingIsNamedLikeOne() {
        foreach (var property in MariaDbServers.Schema2026.Properties) {
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
    public void ABodyThatCarriesACredentialsPasswordIsRefusedRatherThanStoredOrDropped() {
        // ⚠ THE SABOTAGE, AND IT IS THE TEST. A caller who has read the chart will send this. The
        // schema must refuse it — RejectsUnknownProperties defaults to refusing — because the two
        // alternatives are both bad: storing it puts a plaintext password in durable state, and
        // dropping it silently tells the caller a password took when it did not.
        var body = JsonNode.Parse(MariaDbServers.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["credentials"] = new JsonObject { ["rootPassword"] = "hunter2" };

        using var document = JsonDocument.Parse(body.ToJsonString());
        var validated = MariaDbServers.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        validated.Error.Target.ShouldBe("/properties/credentials");
    }

    [Fact]
    public void TheRenderedServerNamesTwoCredentialSecretsAndCarriesNoPasswordValue() {
        // The operator reads both passwords out of Secrets named in the CR, so the CR itself never
        // holds one — docs/plan/12 § The pattern, once, piece 5. This asserts the rendering takes that
        // seam rather than inlining anything.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(Guid.NewGuid()));

        var rendered = MariaDbServers.ServerJson("credentials", desired.RootElement);
        var spec = JsonNode.Parse(rendered)!["spec"]!.AsObject();

        spec["rootPasswordSecretKeyRef"]!["name"]!.GetValue<string>()
            .ShouldBe(MariaDbServers.RootSecretName("credentials"));

        spec["passwordSecretKeyRef"]!["name"]!.GetValue<string>()
            .ShouldBe(MariaDbServers.PasswordSecretName("credentials"));

        // ⚠ The only legal mentions of the word in this document are the two `…SecretKeyRef` field
        // names and the `password` KEY inside them. Nothing may carry a value.
        foreach (var block in new[] { "rootPasswordSecretKeyRef", "passwordSecretKeyRef" }) {
            var reference = spec[block]!.AsObject();

            reference["key"]!.GetValue<string>().ShouldBe("password");
            reference.ContainsKey("value").ShouldBeFalse();
            reference.Count.ShouldBe(3, $"'{block}' grew a member — check it is not a value");
        }
    }

    [Fact]
    public void TheCredentialReferencesAskTheOperatorToGenerate() {
        // ⚠ THE ONE LINE THAT DECIDES WHETHER THIS DATABASE EVER STARTS, AND THE CHECK THAT WAS WORTH
        // READING THE OPERATOR'S SOURCE FOR RATHER THAN ASSUMING.
        //
        // `MariaDB.SetDefaults` (api/v1alpha1/mariadb_types.go) fills in spec.rootPasswordSecretKeyRef
        // — carrying `Generate: true` — ONLY when the field is the zero value. `GeneratedSecretKeyRef`
        // has a `generate` bool whose own default is FALSE. So rendering the reference at all REPLACES
        // the operator's generous default, and a reference written without the flag is a server that
        // waits forever for a Secret nothing in this platform writes: piece 5 does not exist.
        //
        // The failure mode is silent in every direction — the CR is valid, the operator is content, the
        // apply succeeds, `Matches` is satisfied, and the pods never become ready.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(Guid.NewGuid()));

        var spec = JsonNode.Parse(MariaDbServers.ServerJson("generated", desired.RootElement))!["spec"]!
            .AsObject();

        spec["rootPasswordSecretKeyRef"]!["generate"]!.GetValue<bool>().ShouldBeTrue(
            "the root credential reference does not ask the operator to generate. Its default is "
            + "false, and nothing in this platform writes the Secret, so the server never starts."
        );

        spec["passwordSecretKeyRef"]!["generate"]!.GetValue<bool>().ShouldBeTrue(
            "the application credential reference does not ask the operator to generate."
        );
    }

    [Fact]
    public void RootIsNeverPasswordless() {
        // ⚠ THE FIELD THAT TURNS A MANAGED DATABASE INTO AN OPEN ONE, PINNED. `rootEmptyPassword: true`
        // is a legal MariaDB CRD value and is exactly the "fix" available to a future reader looking at
        // a server stuck waiting for a credential. It comes up instantly, reachable by anything in the
        // tenant's namespace with no password at all. docs/plan/12: "a managed database on a public IP
        // with a weak password is the single most common cloud breach" — which applies inside a
        // namespace too, and no password is weaker than a weak one.
        using var desired = JsonDocument.Parse(MariaDbServers.Body(Guid.NewGuid()));

        var rendered = MariaDbServers.ServerJson("open", desired.RootElement);

        JsonNode.Parse(rendered)!["spec"]!["rootEmptyPassword"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void TheListKeysResponseIsTheOnlyPlaceAPasswordIsDeclaredAndItIsMarkedSecret() {
        // The action's response is the one shape in this provider that carries a credential, and
        // ResourceManagerService audits every `secret: true` action call. Declaring the property
        // without the flag would put the value on a surface that is neither audited nor masked.
        var password = MariaDbServers.ListKeysResponse.Properties.Single(x => x.JsonPointer == "/password");

        password.Secret.ShouldBeTrue();

        MariaDbServers.ListKeysResponse.Properties
            .Where(x => x.JsonPointer != "/password")
            .ShouldAllBe(x => !x.Secret);

        // ⚠ And it is the APPLICATION account's password, not root's. A credential with GRANT OPTION
        // over every schema is not the one an application connects with, and an API that handed it out
        // would make the safe choice the harder one. Root's password exists — the operator generates
        // it — and this action does not carry it.
        MariaDbServers.ListKeysResponse.Properties
            .ShouldNotContain(x => x.JsonPointer.Contains("root", StringComparison.OrdinalIgnoreCase));
    }
}
