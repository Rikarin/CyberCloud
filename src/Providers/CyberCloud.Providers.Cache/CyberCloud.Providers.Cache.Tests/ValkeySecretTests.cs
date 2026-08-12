using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Tests;

/// <summary>
///     The one credential this type has, and the places it must not appear.
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
///         <c>charts/managed/valkey/values.yaml</c> carries an <c>auth.password</c> row, and a provider
///         that mirrored it as a body property would have put a plaintext password into the resource
///         grain's desired state through a JSON string no analyzer reads. <c>Secret</c> would not have
///         saved it: nothing on the write path substitutes a <c>SecretRef</c> before the grain persists
///         the body — <c>SchemaProperty</c>'s own remarks now say so outright, ending <i>"until the
///         substitution exists, don't mark a body property Secret"</i>. All the flag buys is that a
///         read withholds the property, which keeps a secret out of the API's answers and not out of
///         PostgreSQL.
///     </para>
///     <para>
///         ⚠ <b>What is different from the first provider is what happens when the value is
///         missing.</b> CloudNativePG generates its own password when the referenced <c>Secret</c> is
///         absent; spotahome generates nothing, so this cache does not come up. That is the trade
///         <c>ValkeyCaches.RedisFailoverJson</c> argues for and it is the reason the <c>auth</c> block
///         is rendered unconditionally rather than only when a resolver supplies something: the
///         alternative is a running, unauthenticated Valkey.
///     </para>
/// </remarks>
public sealed class ValkeySecretTests {
    [Fact]
    public void NoPropertyInTheBodyIsASecretAndNothingIsNamedLikeOne() {
        foreach (var property in ValkeyCaches.Schema2026.Properties) {
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
    public void ABodyThatCarriesAnAuthPasswordIsRefusedRatherThanStoredOrDropped() {
        // ⚠ THE SABOTAGE, AND IT IS THE TEST. A caller who has read the chart will send this. The
        // schema must refuse it — RejectsUnknownProperties defaults to refusing — because the two
        // alternatives are both bad: storing it puts a plaintext password in durable state, and
        // dropping it silently tells the caller a password took when it did not.
        var body = JsonNode.Parse(ValkeyCaches.Body(Guid.NewGuid()))!.AsObject();
        body["properties"]!.AsObject()["auth"] = new JsonObject { ["password"] = "hunter2" };

        using var document = JsonDocument.Parse(body.ToJsonString());
        var validated = ValkeyCaches.Schema2026.Validate(document.RootElement);

        validated.IsFailure.ShouldBeTrue();
        validated.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        validated.Error.Target.ShouldBe("/properties/auth");
    }

    [Fact]
    public void TheRenderedFailoverNamesACredentialSecretAndCarriesNoPasswordValue() {
        // spotahome reads `requirepass` out of a Secret named in the CR, so the CR itself never holds
        // one — docs/plan/12 § The pattern, once, piece 5. This asserts the rendering takes that seam
        // rather than inlining anything.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        var rendered = ValkeyCaches.RedisFailoverJson("credentials", desired.RootElement);
        var auth = JsonNode.Parse(rendered)!["spec"]!["auth"]!.AsObject();

        auth["secretPath"]!.GetValue<string>().ShouldBe(ValkeyCaches.CredentialSecretName("credentials"));
        auth.ContainsKey("password").ShouldBeFalse();

        // ⚠ `secretPath` is the only legal mention of the word in the document, so this checks the
        // whole rendering rather than the one block a reader would look at.
        rendered.Contains("password", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            "the rendered RedisFailover mentions a password. The only legal mention is a Secret "
            + "reference by name, and that is spelled 'secretPath'."
        );
    }

    [Fact]
    public void TheAuthBlockIsRenderedEvenThoughNothingWritesTheSecretYet() {
        // ⚠ THE DECISION, PINNED, BECAUSE THE OBVIOUS "FIX" IS TO DELETE IT. With no OpenBao
        // integration the named Secret does not exist and the operator will not start the cache — so a
        // future reader looking at a resource stuck InProgress has an easy, wrong repair available:
        // drop `spec.auth`, and the cache comes up. It comes up with NO requirepass, reachable by
        // anything in the tenant's namespace. docs/plan/12 § Cross-cutting decisions makes the same
        // trade for external exposure and gives the same reason.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        JsonNode.Parse(ValkeyCaches.RedisFailoverJson("unauthenticated", desired.RootElement))!
            ["spec"]!["auth"]!["secretPath"]!.GetValue<string>()
            .ShouldBe("unauthenticated-auth");
    }

    [Fact]
    public void TheListKeysResponseIsTheOnlyPlaceAPasswordIsDeclaredAndItIsMarkedSecret() {
        // The action's response is the one shape in this provider that carries a credential, and
        // ResourceManagerService audits every `secret: true` action call. Declaring the property
        // without the flag would put the value on a surface that is neither audited nor masked.
        var password = ValkeyCaches.ListKeysResponse.Properties.Single(x => x.JsonPointer == "/password");

        password.Secret.ShouldBeTrue();

        ValkeyCaches.ListKeysResponse.Properties
            .Where(x => x.JsonPointer != "/password")
            .ShouldAllBe(x => !x.Secret);
    }
}
