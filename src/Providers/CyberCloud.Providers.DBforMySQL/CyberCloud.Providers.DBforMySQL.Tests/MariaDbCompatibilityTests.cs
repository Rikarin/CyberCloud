using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.DBforMySQL.Tests;

/// <summary>
///     The compatibility claim, and the surfaces it has to survive on.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This file is this row's central obligation rather than a nicety.</b> docs/plan/12
///         line 310: <i>"Positioned as MySQL-compatible; the same honesty rule as FerretDB applies to
///         the compatibility claim."</i> Line 262, the rule it points at: <i>"⚠ This is a
///         compatibility layer and the product page must say so, with a supported-subset table. …
///         Selling it as 'MongoDB' produces a churn event at the first <c>$lookup</c>. Selling it as
///         'MongoDB-compatible document database, here is exactly what works' produces a happy
///         customer with a smaller use case."</i>
///     </para>
///     <para>
///         ⚠ <b>The failure this guards against is an EDIT, not a bug.</b> Nothing breaks if the
///         summary is shortened to "Managed MySQL" — the type still builds, every other test stays
///         green, the CLI still works, and the platform has quietly started selling an engine it does
///         not run. So the claim is asserted where it lands: in the registry, and in the document
///         every generated client and portal blade is built from.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is against a LITERAL.</b> Comparing
///         <c>registration.Display.Summary</c> to <c>MariaDbProvider.Summary</c> would compare the
///         constant with itself and stay green through any rewrite of it, which is precisely the shape
///         that let a casing sabotage survive on an earlier provider.
///     </para>
/// </remarks>
public sealed class MariaDbCompatibilityTests {
    [Fact]
    public void TheProductSurfaceNamesMariaDbAndCallsItselfCompatibleRatherThanMySql() {
        var summary = Registration().Display.Summary;

        summary.ShouldContain(
            "MariaDB",
            Case.Sensitive,
            "the summary a tenant reads does not name the engine that runs. docs/plan/12 line 310 "
            + "makes the compatibility claim this row's obligation, and a summary that omits MariaDB "
            + "is the sentence ADR-011 forbids for Valkey in one line."
        );

        summary.ShouldContain(
            "MySQL-compatible",
            Case.Sensitive,
            "the summary claims neither compatibility nor its limits."
        );

        // ⚠ THE SABOTAGE THIS TEST EXISTS FOR: `is not MySQL`, spelled out. A summary may say
        // "MySQL-compatible" and still leave a reader believing the server is MySQL — which is the
        // exact reading docs/plan/12 § MongoDB-compatible says produces a churn event.
        summary.ShouldContain("is not", Case.Sensitive, "the summary does not say what this is NOT");
        summary.ShouldContain(
            "supported-subset",
            Case.Sensitive,
            "the summary does not point at the table docs/plan/12 line 262 requires the product page "
            + "to carry."
        );
    }

    [Fact]
    public void TheDisplayNameSaysMariaDbServerAndNotMySqlServer() {
        var display = Registration().Display;

        display.Name.ShouldBe("MariaDB server");
        display.Plural.ShouldBe("MariaDB servers");
        display.Alias.ShouldBe("mariadb");
    }

    [Fact]
    public void TheSupportedSubsetTableSaysBothWhatWorksAndWhatDoesNot() {
        // ⚠ A table listing only absences reads as a warning notice, and a table listing only
        // capabilities is the marketing docs/plan/12 § MongoDB-compatible is arguing against. The
        // FerretDB paragraph's whole point is that the honest version WINS a customer with a smaller
        // use case, which takes both halves.
        var subset = MariaDbServers.SupportedSubset;

        subset.Count(x => x.Supported).ShouldBeGreaterThan(0, "the table says nothing works");
        subset.Count(x => !x.Supported).ShouldBeGreaterThan(
            2,
            "the table names fewer than three limits, which for two engines that diverged in 2012 is "
            + "not a subset table — it is a reassurance."
        );

        foreach (var note in subset) {
            note.Id.ShouldNotBeNullOrWhiteSpace();
            note.Says.Length.ShouldBeGreaterThan(
                40,
                $"'{note.Id}' says too little to act on. A subset row a tenant cannot check their own "
                + "application against is a row that only looks like disclosure."
            );
        }

        subset.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(subset.Length);
    }

    [Theory]
    // ⚠ THE FOUR ROWS A MIGRATION ACTUALLY HITS, PINNED BY ID. Each is a real failure at a real
    // moment: the handshake, the import, the cutover, and the volume somebody hoped to reattach. A
    // future edit that trims the table "because it is long" has to delete one of these by name.
    [InlineData("auth-plugin")]
    [InlineData("collations")]
    [InlineData("replication-interop")]
    [InlineData("data-directory")]
    public void TheRowsThatDecideAMigrationAreInTheTableAndAreMarkedUnsupported(string id) {
        var note = MariaDbServers.SupportedSubset.SingleOrDefault(x => x.Id == id);

        note.Id.ShouldBe(id, $"'{id}' has left the supported-subset table");
        note.Supported.ShouldBeFalse($"'{id}' is claimed as supported, and it is not");
    }

    [Fact]
    public void TheAuthenticationPluginIsHandedBackRatherThanLeftForTheTenantToDiscover() {
        // ⚠ THE SUBSET TABLE'S FIRST ROW, TURNED INTO A VALUE. MySQL 8 defaults its clients to
        // caching_sha2_password, which MariaDB does not implement — so the compatibility claim's first
        // consequence is a handshake failure with a message about a plugin nobody chose. listKeys
        // returning the plugin makes it a setting the caller already has.
        MariaDbServers.AuthenticationPlugin.ShouldBe("mysql_native_password");

        MariaDbServers.ListKeysResponse.Properties
            .ShouldContain(x => x.JsonPointer == "/authenticationPlugin");
    }

    [Fact]
    public void TheEmittedDocumentCarriesTheClaimWhereEveryGeneratedClientCopiesIt() {
        // ⚠ THE SURFACE THAT OUTLIVES THIS REPOSITORY. docs/plan/21 § Generation's one hop makes the
        // OpenAPI document the input to the CLI, the SDK and the portal forms, so a claim that reaches
        // the registry and not the document reaches a reader in none of the three. The document is
        // emitted IN-PROCESS rather than read off disk: openapi/2026-08-01.json is written by a build
        // step that runs AFTER compilation, so a test embedding it would compare this build's registry
        // against the previous build's document.
        var text = Document().ToJsonString();

        text.ShouldContain("MariaDB", Case.Sensitive, "the emitted document never names the engine");
        text.ShouldContain(
            "MySQL-compatible",
            Case.Sensitive,
            "the emitted document does not carry the compatibility claim, so no generated client, "
            + "SDK doc-comment or portal blade repeats it."
        );
    }

    [Fact]
    public void NoDescriptionAnywhereClaimsTheServerIsMySql() {
        // ⚠ THE OTHER DIRECTION, AND THE ONE A "HELPFUL" EDIT TAKES. Every place the word MySQL is
        // legitimate on this type, it is qualified: the wire protocol, the compatibility claim, the
        // 32-character account limit, the resource type's own Azure-parity spelling. An unqualified
        // "MySQL server", "runs MySQL" or "managed MySQL" is the sentence docs/plan/12 § MongoDB-
        // compatible says costs a customer at the first incompatibility.
        var forbidden = new[] { "managed MySQL ", "runs MySQL", "MySQL server", "MySQL database" };

        foreach (var property in MariaDbServers.Schema2026.Properties) {
            foreach (var phrase in forbidden) {
                property.Description.ShouldNotContain(
                    phrase,
                    Case.Sensitive,
                    $"'{property.JsonPointer}' describes the engine as MySQL. It is MariaDB — see "
                    + "MariaDbServers.SupportedSubset."
                );
            }
        }

        foreach (var phrase in forbidden) {
            Registration().Display.Summary.ShouldNotContain(phrase, Case.Sensitive);
        }
    }

    static ResourceTypeRegistration Registration() {
        var registry = ProviderRegistry.Build([new MariaDbProvider()]);
        registry.TryGetType(MariaDbServers.Type, out var registration).ShouldBeTrue();

        return registration;
    }

    /// <summary>The document the generator would write for this provider alone.</summary>
    /// <remarks>
    ///     ⚠ <b>This provider alone.</b> Rule 2 of docs/plan/03 § Assembly graph rules is "no
    ///     <c>Providers.*</c> assembly references another <c>Providers.*</c> assembly, not even
    ///     <c>.Contracts</c>", and a test project taking such a reference would put the edge in the
    ///     graph the gate inspects.
    /// </remarks>
    static JsonObject Document() {
        var registry = ProviderRegistry.Build([new MariaDbProvider()]);
        return OpenApiEmitter.Emit(registry, OpenApiEmitter.ApiVersionsOf(registry).Single());
    }
}
