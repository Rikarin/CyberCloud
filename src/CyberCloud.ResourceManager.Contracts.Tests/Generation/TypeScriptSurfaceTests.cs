using CyberCloud.ResourceManager.Contracts.Generation;
using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The portal's TypeScript client — issue #21, and the two defects its compiler found.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These assertions are about names a compiler would refuse, not about text a reader
///         would like.</b> Nothing in the .NET build compiles TypeScript and
///         <c>portal/eslint.config.mjs</c> ignores <c>libs/api/**</c>, so the real check is
///         <c>pnpm typecheck:api</c>, one toolchain over. What lives here is the part that can be
///         asserted from a document without a Node process — that every type the client names is one
///         the models declare, and that the two shapes which produced an uncompilable .NET SDK
///         produce a compilable client.
///     </para>
///     <para>
///         ⚠ <b>The .NET SDK is asserted here too, and deliberately so.</b> Both defects below were
///         live in <c>generated/sdk/2026-08-01.cs</c> for as long as those emitters had existed —
///         green under every gate in this repository, because nothing here compiles that file. A
///         suite that fixed the TypeScript half and left the C# half unasserted would be the same
///         blindness with one more surface in it.
///     </para>
/// </remarks>
public sealed class TypeScriptSurfaceTests {
    static JsonObject Document =>
        OpenApiEmitter.Emit(Fixtures.Postgres(), ApiVersion.Parse(Fixtures.FirstVersion));

    static ImmutableSortedDictionary<string, string> Client => TypeScriptEmitter.Emit(Document);

    [Fact]
    public void ThePackageIsTheFilesTheReadmeAsksFor() {
        var files = Client;

        files.Keys.ShouldBe([
            "package.json",
            "src/client.ts",
            "src/index.ts",
            "src/models.ts",
            "src/transport.ts",
            "tsconfig.json"
        ]);

        TypeScriptEmitter.Problems(files).ShouldBeEmpty();
    }

    /// <summary>
    ///     ⚠ <b>Every file says it is generated, in its first line.</b>
    /// </summary>
    /// <remarks>
    ///     <c>Build.Architecture</c>'s rule 6 reads the first 2 KB of every file under
    ///     <c>portal/libs/api</c> and fails on one that does not — and a reader who opens an unmarked
    ///     file is a reader who edits it. <c>package.json</c> is the awkward one: JSON has no
    ///     comments, so the banner is a <c>"//"</c> member and it has to be first.
    /// </remarks>
    [Fact]
    public void EveryFileCarriesItsBannerBeforeAnythingElse() {
        foreach (var file in Client) {
            var head = file.Value.Length > 2048 ? file.Value[..2048] : file.Value;

            head.ShouldContain("@generated", customMessage: file.Key);
            head.ShouldContain("DO NOT EDIT", customMessage: file.Key);
        }
    }

    /// <summary>
    ///     ⚠ <b>The client names no type the models do not declare.</b>
    /// </summary>
    /// <remarks>
    ///     This is the check that would have caught both of the .NET SDK's defects, and it is the
    ///     cheapest one that could: an import of a type that is not there fails the portal's build a
    ///     target later, and only once something imports the client at all.
    /// </remarks>
    [Fact]
    public void TheClientImportsNothingTheModelsDoNotExport() {
        var files = Client;
        var models = files["src/models.ts"];

        foreach (var line in files["src/client.ts"].Split('\n')) {
            var name = line.Trim().TrimEnd(',');

            if (name.Length == 0 || name.Contains(' ', StringComparison.Ordinal) || !char.IsAsciiLetterUpper(name[0])) {
                continue;
            }

            models.ShouldContain(
                "export interface " + name + " {",
                customMessage: $"src/client.ts imports '{name}' and src/models.ts does not declare it"
            );
        }
    }

    /// <summary>
    ///     ⚠ <b>Two closed sets whose leaf names are equal declare two identifiers, not one twice.</b>
    /// </summary>
    /// <remarks>
    ///     <c>CyberCloud.Cache/redis</c> has <c>mode</c> at <c>/properties/mode</c> and again at
    ///     <c>/properties/persistence/mode</c>. The name was <c>model + Pascal(leaf.Name)</c> in both
    ///     emitters, so <c>generated/sdk/2026-08-01.cs</c> declared <c>public enum ValkeyCacheMode</c>
    ///     twice and two properties referred to it — <c>CS0101</c>, checked in, and invisible because
    ///     no build here compiles that file. <c>tsc</c> found it in one line as <c>TS2300</c>.
    /// </remarks>
    [Fact]
    public void TwoClosedSetsWithTheSameLeafNameGetDistinctNames() {
        var registry = new FakeRegistry {
            Namespaces = [Fixtures.Namespace],
            Types = [
                new ResourceTypeRegistration {
                    Type = new(Fixtures.Namespace, "servers"),
                    ApiVersions = [new(ApiVersion.Parse(Fixtures.FirstVersion), CollidingEnums())]
                }
            ]
        };

        var document = OpenApiEmitter.Emit(registry, ApiVersion.Parse(Fixtures.FirstVersion));
        var models = TypeScriptEmitter.Emit(document)["src/models.ts"];

        // The top-level one keeps the bare name; only the nested member of the pair moves, which is
        // the same "disambiguate only what collides" rule CliEmitter.FlagsOf applies to flag names.
        Declarations(models, "export type ").ShouldContain("DBforPostgreSQLServersMode");
        Declarations(models, "export type ").ShouldContain("DBforPostgreSQLServersPersistenceMode");
        Declarations(models, "export type ").ShouldBeUnique();

        // …and the same document, through the emitter that shipped the defect.
        Declarations(SdkEmitter.Emit(document), "public enum ").ShouldBeUnique();
    }

    /// <summary>
    ///     ⚠ <b>An action's own closed sets are declared, not merely referenced.</b>
    /// </summary>
    /// <remarks>
    ///     Both emitters rendered an action payload's enum leaf as <c>{payload}{Member}</c> and
    ///     emitted no such type: <c>generated/sdk/2026-08-01.cs</c> carried
    ///     <c>public required ListKeysResultSecurityProtocol SecurityProtocol</c> against a type that
    ///     appeared nowhere in the file — <c>CS0246</c>, checked in, green. The Postgres fixture's
    ///     <c>listKeys</c> declares <c>/keyName</c> as a closed set, so this needs no new fixture.
    /// </remarks>
    [Fact]
    public void AnActionsClosedSetsAreDeclaredOnBothSurfaces() {
        var document = Document;
        var models = TypeScriptEmitter.Emit(document)["src/models.ts"];

        Declarations(models, "export type ").ShouldContain("DBforPostgreSQLServersListKeysContentKeyName");
        Declarations(models, "export type ").ShouldBeUnique();

        var sdk = SdkEmitter.Emit(document);

        sdk.ShouldContain("public enum ListKeysContentKeyName {");
        Declarations(sdk, "public enum ").ShouldBeUnique();
    }

    /// <summary>
    ///     ⚠ <b>A path is built with every segment encoded.</b>
    /// </summary>
    /// <remarks>
    ///     A resource name is caller data. A <c>/</c> in one, interpolated raw, would forge a path —
    ///     the portal would address a different resource, or a scope, and the gateway would answer
    ///     about whatever the forged URL named. There is no version of that which is only cosmetic.
    /// </remarks>
    [Fact]
    public void EveryPathSegmentIsEncoded() {
        var client = Client["src/client.ts"];

        foreach (var line in client.Split('\n')) {
            if (!line.Contains("path: `", StringComparison.Ordinal)) {
                continue;
            }

            // Every `${…}` in a path expression is a call to the encoder and never a bare parameter.
            var scan = line;

            for (var at = scan.IndexOf("${", StringComparison.Ordinal); at >= 0;
                 at = scan.IndexOf("${", at + 2, StringComparison.Ordinal)) {
                scan[at..].ShouldStartWith("${CyberCloudApi.segment(", customMessage: line.Trim());
            }
        }
    }

    /// <summary>
    ///     ⚠ <b>The scope API reaches this surface too — issue #63 and #21 meeting.</b>
    /// </summary>
    /// <remarks>
    ///     The client reads the document, so a path the document gained arrives here with no change
    ///     to this emitter. That is the whole claim of docs/plan/21 § Generation's one hop, and it is
    ///     worth asserting once rather than trusting.
    /// </remarks>
    [Fact]
    public void TheScopeApiReachesTheClientWithNoEmitterChange() {
        var files = Client;

        files["src/models.ts"].ShouldContain("export interface ScopeResource {");
        files["src/models.ts"].ShouldContain("export interface SubscriptionCreateContent {");
        files["src/client.ts"].ShouldContain("createSubscription(");
        files["src/client.ts"].ShouldContain("getTenant(");

        // ⚠ No create for the tenant, on this surface as on the other four.
        files["src/client.ts"].ShouldNotContain("createTenant(");
    }

    /// <summary>
    ///     ⚠ <b>The transport is a seam and never an implementation.</b>
    /// </summary>
    /// <remarks>
    ///     A generated <c>fetch</c> would put the bearer token in a file that is overwritten on every
    ///     build and would give the portal a second HTTP client beside the one it already has —
    ///     portal/README.md § Rules keeps the portal on the same public API as the CLI, with the same
    ///     token.
    /// </remarks>
    [Fact]
    public void NothingGeneratedOpensASocket() {
        var files = Client;

        foreach (var file in files) {
            // ⚠ Asserted against code and never against prose. An earlier version of this test
            // banned the words "Authorization" and "Bearer", and both appear legitimately —
            // `AuthorizationFailed` is one of the platform's own error codes, and the transport's own
            // doc comment says the bearer token is the app's. A check that fails on its subject's
            // documentation is one somebody deletes rather than reads.
            file.Value.ShouldNotContain("fetch(", customMessage: file.Key);
            file.Value.ShouldNotContain("XMLHttpRequest", customMessage: file.Key);
            file.Value.ShouldNotContain("'Authorization'", customMessage: file.Key);
        }

        // The transport is declared and never implemented: no class, no function body, nothing to
        // configure. The portal supplies the one implementation there is.
        files["src/transport.ts"].ShouldNotContain("class ");

        // …and every request the client makes goes through it.
        foreach (var line in files["src/client.ts"].Split('\n')) {
            if (line.Contains("return this.", StringComparison.Ordinal)) {
                line.ShouldContain("this.transport.send<");
            }
        }
    }

    static IEnumerable<string> Declarations(string source, string keyword) =>
        source.Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith(keyword, StringComparison.Ordinal))
            .Select(x => x[keyword.Length..].Split(' ')[0].TrimEnd('{', '=', ' '));

    /// <summary>A body with <c>mode</c> at two depths, both closed.</summary>
    static ResourceSchema CollidingEnums() =>
        ResourceSchema.Of([
            new("/properties", SchemaKind.Nested, Required: true),
            new("/properties/mode", SchemaKind.Text, Description: "The top-level one.") {
                AllowedValues = ["Sentinel", "Standalone"]
            },
            new("/properties/persistence", SchemaKind.Nested),
            new("/properties/persistence/mode", SchemaKind.Text, Description: "The nested one.") {
                AllowedValues = ["None", "RDB", "AOF"]
            }
        ]);
}
