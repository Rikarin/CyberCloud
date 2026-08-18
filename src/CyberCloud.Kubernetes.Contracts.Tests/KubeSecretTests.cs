using Shouldly;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     <see cref="KubeSecret" />, the seam five <c>listKeys</c> handlers read an operator's generated
///     credential through.
/// </summary>
/// <remarks>
///     <para>
///         <b>What is worth asserting here is mostly the refusals.</b> The happy path is one base64
///         decode and every conformance suite that placed an <c>OperatorWritten</c> object drives it.
///         The paths nothing else reaches are the four ways a <c>Secret</c> can fail to carry what a
///         handler asked for — and each of them, if it returned an empty string instead, would put a
///         credential that authenticates nothing into a response whose schema says the field is
///         required.
///     </para>
///     <para>
///         ⚠ <b>The messages are asserted not to carry the value, and that is the assertion this file
///         exists for.</b> Everything else here is behaviour; that one is containment. A failure
///         message quoting what it found would put the credential into whatever logged the failure,
///         which is the leak the whole action path is careful about.
///     </para>
/// </remarks>
public class KubeSecretTests {
    const string Namespace = "cc-t-tenant";
    const string Name = "server-app";
    const string Password = "a-generated-password";

    static ObjectRef Target => KubeSecret.Ref(Namespace, Name);

    static KubeObject Secret(JsonObject? data) {
        var body = new JsonObject {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = new JsonObject { ["name"] = Name, ["namespace"] = Namespace }
        };

        if (data is not null) {
            body["data"] = data;
        }

        return new() { Ref = Target, Json = body.ToJsonString() };
    }

    static JsonObject Encoded(string key, string value) =>
        new() { [key] = Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) };

    [Fact]
    public void TheKindIsTheCoreGroupsSecret() {
        // ⚠ The core group is the EMPTY string, not "core" and not "v1". Three providers wrote this
        // constant separately before it was shared, and a wrong group is a 404 from the API server
        // that reads as "the operator has not created it yet".
        KubeSecret.Kind.Group.ShouldBeEmpty();
        KubeSecret.Kind.Version.ShouldBe("v1");
        KubeSecret.Kind.Kind.ShouldBe("Secret");
        KubeSecret.Kind.Plural.ShouldBe("secrets");

        Target.Namespace.ShouldBe(Namespace);
        Target.Name.ShouldBe(Name);
        Target.IsClusterScoped.ShouldBeFalse("a Secret is namespaced");
    }

    [Fact]
    public void AValueIsDecodedFromBase64() {
        var read = KubeSecret.Value(Secret(Encoded("password", Password)), "password");

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.GetValueOrThrow().ShouldBe(Password);
    }

    [Fact]
    public void TheKeyIsMatchedExactlyAndNotCaseInsensitively() {
        // ⚠ Unlike an action name, which the registry matches case-insensitively. A Secret's data is a
        // map with Kubernetes' own key syntax and the operator that wrote it chose the case; matching
        // loosely here would find `Password` when the handler asked for `password` and hand back
        // whichever the dictionary happened to hold.
        var read = KubeSecret.Value(Secret(Encoded("Password", Password)), "password");

        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public void StringDataIsNotReadBecauseTheApiServerNeverReturnsIt() {
        // ⚠ THE ONE THAT WOULD PASS AGAINST A FIXTURE AND FAIL AGAINST A CLUSTER. `stringData` is
        // write-only: the API server folds it into `data` and drops it, so a helper that fell back to
        // it would work in every test and find nothing in production.
        var body = new JsonObject {
            ["apiVersion"] = "v1",
            ["kind"] = "Secret",
            ["metadata"] = new JsonObject { ["name"] = Name, ["namespace"] = Namespace },
            ["stringData"] = new JsonObject { ["password"] = Password }
        };

        var read = KubeSecret.Value(new() { Ref = Target, Json = body.ToJsonString() }, "password");

        read.IsFailure.ShouldBeTrue();
        read.Error!.Message.ShouldContain("stringData");
    }

    [Fact]
    public void AMissingKeyIsRefusedByName() {
        var read = KubeSecret.Value(Secret(Encoded("username", "app")), "password");

        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.InternalError);
        read.Error.Message.ShouldContain("password");
        read.Error.Message.ShouldContain(Name);
    }

    [Fact]
    public void AValueThatIsNotBase64IsRefusedRatherThanReturnedRaw() {
        var read = KubeSecret.Value(
            new KubeObject {
                Ref = Target,
                Json = new JsonObject {
                    ["data"] = new JsonObject { ["password"] = "not base64 at all!" }
                }.ToJsonString()
            },
            "password"
        );

        read.IsFailure.ShouldBeTrue();
        read.Error!.Message.ShouldContain("base64");
    }

    [Fact]
    public void ABodyThatIsNotJsonIsRefused() {
        var read = KubeSecret.Value(new() { Ref = Target, Json = "not json" }, "password");

        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public void NoRefusalCarriesTheValueItFoundOrTheOneItWasLookingFor() {
        // ⚠ THE CONTAINMENT ASSERTION. Every message above names the secret and the key, which is what
        // a reader needs; none of them may name a value, because these run on the path that exists to
        // move a credential and an error is the one thing on that path that gets logged.
        //
        // The wrong-case case is the sharpest: the value IS present in the object, under a key the
        // helper decided not to read, so a message that quoted "what we did find" would leak it.
        List<Result<string>> refusals = [
            KubeSecret.Value(Secret(Encoded("Password", Password)), "password"),
            KubeSecret.Value(Secret(Encoded("username", "app")), "password"),
            KubeSecret.Value(Secret(null), "password"),
            KubeSecret.Value(
                new KubeObject {
                    Ref = Target,
                    Json = new JsonObject {
                        ["data"] = new JsonObject {
                            ["password"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Password))
                        }
                    }.ToJsonString()
                },
                "missing"
            )
        ];

        foreach (var refusal in refusals) {
            refusal.IsFailure.ShouldBeTrue();

            refusal.Error!.Message.ShouldNotContain(
                Password,
                Case.Insensitive,
                "a refusal from the secret-reading seam quoted the credential"
            );

            refusal.Error.Message.ShouldNotContain(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)),
                Case.Insensitive,
                "a refusal quoted the credential still base64-encoded, which is not concealment"
            );
        }
    }
}
