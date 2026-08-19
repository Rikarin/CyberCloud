using System.Text;
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
///         ⚠ <b>What is different from every other data provider is who makes the value.</b>
///         CloudNativePG, Strimzi and the rest generate a credential at bootstrap and the handler
///         reads it; spotahome generates nothing at all, so <c>ValkeyCacheReconciler</c> mints into
///         the tenant's vault and renders the <c>Secret</c> from what the vault returned. That is also
///         why the <c>auth</c> block is rendered unconditionally rather than only when a resolver
///         supplies something: the alternative is a running, unauthenticated Valkey.
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
    public void TheAuthBlockIsRenderedUnconditionally() {
        // ⚠ THE DECISION, PINNED, BECAUSE THE OBVIOUS "FIX" IS TO DELETE IT. If the named Secret is
        // ever missing the operator will not start the cache — so a reader looking at a resource stuck
        // InProgress has an easy, wrong repair available: drop `spec.auth`, and the cache comes up. It
        // comes up with NO requirepass, reachable by anything in the tenant's namespace.
        // docs/plan/12 § Cross-cutting decisions makes the same trade for external exposure and gives
        // the same reason. The right repair is to find out why the Secret is absent.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        JsonNode.Parse(ValkeyCaches.RedisFailoverJson("unauthenticated", desired.RootElement))!
            ["spec"]!["auth"]!["secretPath"]!.GetValue<string>()
            .ShouldBe("unauthenticated-auth");
    }

    [Fact]
    public void TheCredentialSecretIsAddressedTheWayTheOperatorLooksItUp() {
        // ⚠ THE UPSTREAM READ, WRITTEN DOWN, BECAUSE NOTHING IN THIS REPOSITORY CAN CHECK IT.
        // spotahome's service/k8s/util.go:
        //
        //     secret, err := s.GetSecret(rf.ObjectMeta.Namespace, rf.Spec.Auth.SecretPath)
        //     if password, ok := secret.Data["password"]; ok { return string(password), nil }
        //     return "", fmt.Errorf("secret %q does not have a password field", ...)
        //
        // Three facts follow, and getting any one of them wrong produces a Secret that applies, reads
        // back and converges while the cache never starts: the NAME must be what `secretPath` says,
        // the NAMESPACE is the RedisFailover's own — `secretPath` is a name despite what it is called,
        // and there is no cross-namespace form — and the KEY is `password`.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(Guid.NewGuid()));

        var failover = JsonNode.Parse(ValkeyCaches.RedisFailoverJson("orders", desired.RootElement))!;
        var reference = ValkeyCaches.CredentialSecretRef("prod-ns", "orders");

        reference.Name.ShouldBe(failover["spec"]!["auth"]!["secretPath"]!.GetValue<string>());
        reference.Namespace.ShouldBe("prod-ns");
        reference.Kind.Kind.ShouldBe("Secret");

        var secret = JsonNode.Parse(ValkeyCaches.CredentialSecretJson("orders", "hunter2"))!.AsObject();

        secret["metadata"]!["name"]!.GetValue<string>().ShouldBe(reference.Name);
        secret["type"]!.GetValue<string>().ShouldBe("Opaque");
        secret["data"]!.AsObject().Select(x => x.Key).ShouldBe(["password"]);
        secret["data"]!["password"]!.GetValue<string>().ShouldBe(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2"))
        );
    }

    [Fact]
    public void AGeneratedPasswordSurvivesRedisConfWithoutQuoting() {
        // ⚠ NOT AN ENTROPY TEST — THE ALPHABET IS A PROTOCOL CONSTRAINT. The requirepass reaches the
        // server through redis.conf, where whitespace splits the directive's arguments, `#` starts a
        // comment and `"` opens a quoted token; the operator also passes the same value on an AUTH
        // command line. A generator that reached for punctuation "because it is stronger" would
        // produce a cache that starts with a password nobody can reproduce, intermittently, for
        // whatever fraction of resources drew an unlucky character.
        for (var attempt = 0; attempt < 64; attempt++) {
            var password = ValkeyCaches.GeneratePassword();

            password.Length.ShouldBe(ValkeyCaches.PasswordLength);

            foreach (var symbol in password) {
                char.IsAsciiLetterOrDigit(symbol).ShouldBeTrue(
                    $"'{symbol}' is not alphanumeric, and redis.conf reads it as syntax"
                );
            }
        }

        // And it is a fresh value every call. Two equal draws out of 62^32 is not a flake.
        ValkeyCaches.GeneratePassword().ShouldNotBe(ValkeyCaches.GeneratePassword());
    }

    [Fact]
    public void TheVaultPathIsKeyedOnTheResourceGuidRatherThanItsName() {
        // ⚠ MINT-ONCE MAKES A NAME COLLISION PERMANENT. docs/plan/06 § Identifiers releases the index
        // entry before the resource is gone, deliberately, "so the name is immediately reusable" — so
        // a path built from the name would hand a brand-new cache the password of the cache somebody
        // deleted an hour ago, and cas=0 would then refuse to let the new one mint its own. Forever.
        var first = Address(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
        var second = Address(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002"));

        ValkeyCaches.SecretPath(first).ShouldNotBe(ValkeyCaches.SecretPath(second));

        // ⚠ And the tenant leads, because docs/plan/18 scopes vault policy per tenant. A path that led
        // with the provider would make "everything this tenant owns" unexpressible as a prefix.
        ValkeyCaches.SecretPath(first).ShouldStartWith($"tenants/{first.TenantId:D}/");

        // The reconciler and the handler must reach the same address; the ref is where that is spelled.
        ValkeyCaches.PasswordRef(first).Path.ShouldBe(ValkeyCaches.SecretPath(first));
        ValkeyCaches.PasswordRef(first).Field.ShouldBe(ValkeyCaches.PasswordField);
    }

    /// <summary>Two caches with the SAME name, in one tenant, at two resource GUIDs.</summary>
    static ResourceId Address(Guid id) =>
        new(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "prod",
            ValkeyCaches.Type,
            "sessions",
            id
        );

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
