using CyberCloud.ResourceManager.Contracts.Generation;
using System.Text;

namespace CyberCloud.ResourceManager.Contracts.Tests.Generation;

/// <summary>
///     The two gates docs/plan/23 § The architecture gates asks for, exercised end to end against a
///     real directory: <b>Generated surfaces</b> ("regenerate byte-identically") and <b>OpenAPI
///     compatibility</b> ("a breaking change fails").
/// </summary>
/// <remarks>
///     ⚠ These write to a temporary directory rather than to <c>openapi/</c>. A test that regenerated
///     the checked-in surface would repair the exact drift the gate exists to catch, and would pass
///     for that reason.
/// </remarks>
public sealed class OpenApiArtifactsTests : IDisposable {
    readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "cybercloud-openapi-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose() {
        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }
    }

    GenerationReport Generate(IProviderRegistry registry, bool write = true) =>
        OpenApiArtifacts.Generate(registry, directory, write);

    string FileFor(string version) => Path.Combine(directory, version + ".json");

    // ── What a run produces ────────────────────────────────────────────────────────────────────

    [Fact]
    public void OneDocumentPerApiVersionPlusTheIndex() {
        var report = Generate(Fixtures.Postgres());

        report.Providers.ShouldBe(1);
        report.ResourceTypes.ShouldBe(2);
        report.ApiVersions.ShouldBe(2);

        report.Documents.Select(x => x.FileName)
            .ShouldBe(["2026-08-01.json", "2027-01-01.json", "index.json"]);

        File.Exists(FileFor(Fixtures.FirstVersion)).ShouldBeTrue();
        File.Exists(FileFor(Fixtures.SecondVersion)).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "index.json")).ShouldBeTrue();
    }

    [Fact]
    public void AnEmptyRegistryStillWritesTheIndexAndSaysItInspectedNothing() {
        // ⚠ The vacuous-pass case. "The generator found no provider" and "the generator did not run"
        // must be different states on disk — Build.Architecture.cs draws the same distinction with
        // GateStatus.Vacuous rather than printing a confident tick.
        var report = Generate(Fixtures.Empty);

        report.Providers.ShouldBe(0);
        report.ResourceTypes.ShouldBe(0);
        report.ApiVersions.ShouldBe(0);
        report.Documents.Select(x => x.FileName).ShouldBe(["index.json"]);
        File.Exists(Path.Combine(directory, "index.json")).ShouldBeTrue();

        // ⚠ The first run is NOT clean, because a document that is not checked in yet counts as drift
        // — that is what makes a missing generated file fail the gate rather than pass it silently.
        // The second run, against what the first wrote, is.
        report.Documents.Single().Published.ShouldBeFalse();
        report.IsClean.ShouldBeFalse();
        Generate(Fixtures.Empty).IsClean.ShouldBeTrue();
    }

    [Fact]
    public void CheckModeWritesNothing() {
        // The Architecture gate runs in this mode: a gate that repaired what it was inspecting would
        // be permanently green.
        Generate(Fixtures.Postgres(), write: false);

        Directory.Exists(directory).ShouldBeFalse();
    }

    // ── Drift ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RegeneratingIsByteIdenticalAndReportsNoDrift() {
        Generate(Fixtures.Postgres());
        var second = Generate(Fixtures.Postgres());

        second.Documents.ShouldAllBe(x => !x.Drifted);
        second.IsClean.ShouldBeTrue();
    }

    [Fact]
    public void OneEditedCharacterIsDriftAndTheReportNamesTheFile() {
        Generate(Fixtures.Postgres());

        var path = FileFor(Fixtures.FirstVersion);
        var edited = File.ReadAllText(path, Encoding.UTF8).Replace("Cyber Cloud", "Cyber Clouds", StringComparison.Ordinal);
        File.WriteAllText(path, edited, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var checkOnly = Generate(Fixtures.Postgres(), write: false);

        checkOnly.IsClean.ShouldBeFalse();
        checkOnly.Documents.Single(x => x.FileName == "2026-08-01.json").Drifted.ShouldBeTrue();
        checkOnly.Documents.Single(x => x.FileName == "index.json").Drifted.ShouldBeFalse();

        // ⚠ And a writing run repairs the file AND still reports the drift, which is what makes the
        // fix `git add` rather than hand-copying the generator's output out of a log.
        var writing = Generate(Fixtures.Postgres());
        writing.Documents.Single(x => x.FileName == "2026-08-01.json").Drifted.ShouldBeTrue();

        Generate(Fixtures.Postgres()).IsClean.ShouldBeTrue();
    }

    [Fact]
    public void ATrailingNewlineIsPartOfTheContract() {
        Generate(Fixtures.Postgres());

        var path = FileFor(Fixtures.FirstVersion);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);

        Generate(Fixtures.Postgres(), write: false)
            .Documents.Single(x => x.FileName == "2026-08-01.json")
            .Drifted.ShouldBeTrue();
    }

    // ── Compatibility, in both directions ──────────────────────────────────────────────────────

    [Fact]
    public void AddingAnOptionalPropertyIsCompatible() {
        // docs/plan/21 § OpenAPI: "adding an optional field is fine".
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var widened = Fixtures.PostgresWith(
            ResourceSchema.Of([
                .. Fixtures.ServerSchema().Properties,
                new SchemaProperty("/properties/backupDays", SchemaKind.WholeNumber, Description: "Retention.")
            ])
        );

        var report = Generate(widened, write: false);

        report.Documents.SelectMany(x => x.BreakingChanges).ShouldBeEmpty();

        // It is still drift, and still has to be regenerated and committed — the two gates ask
        // different questions and this is the case where they disagree.
        report.Documents.Single(x => x.FileName == "2026-08-01.json").Drifted.ShouldBeTrue();
    }

    [Fact]
    public void RemovingARequiredPropertyIsABreakingChange() {
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var narrowed = Fixtures.PostgresWith(
            ResourceSchema.Of([
                .. Fixtures.ServerSchema().Properties
                    .Where(x => !string.Equals(x.JsonPointer, "/properties/sku/vcpu", StringComparison.Ordinal))
            ])
        );

        var breaking = Generate(narrowed, write: false)
            .Documents.Single(x => x.FileName == "2026-08-01.json")
            .BreakingChanges;

        breaking.ShouldNotBeEmpty();
        breaking.ShouldContain(x => x.Rule == OpenApiCompatibility.Removed);
        breaking.ShouldContain(x => x.JsonPointer.Contains("vcpu", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingAnOptionalPropertyIsAlsoABreakingChange() {
        // "removing anything" — the rule has no optional-property exemption, and the reason is that
        // a client generated against the old document has a member for it.
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var narrowed = Fixtures.PostgresWith(
            ResourceSchema.Of([
                .. Fixtures.ServerSchema().Properties
                    .Where(x => !string.Equals(x.JsonPointer, "/properties/storageGb", StringComparison.Ordinal))
            ])
        );

        Generate(narrowed, write: false)
            .Documents.Single(x => x.FileName == "2026-08-01.json")
            .BreakingChanges
            .ShouldContain(x => x.Rule == OpenApiCompatibility.Removed);
    }

    [Fact]
    public void MakingAnOptionalPropertyRequiredIsABreakingChange() {
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var narrowed = Fixtures.PostgresWith(
            ResourceSchema.Of([
                .. Fixtures.ServerSchema().Properties
                    .Select(x => string.Equals(x.JsonPointer, "/properties/storageGb", StringComparison.Ordinal)
                        ? x with { Required = true }
                        : x)
            ])
        );

        Generate(narrowed, write: false)
            .Documents.Single(x => x.FileName == "2026-08-01.json")
            .BreakingChanges
            .ShouldContain(x => x.Rule == OpenApiCompatibility.RequiredAdded);
    }

    [Fact]
    public void NarrowingATypeIsABreakingChange() {
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var narrowed = Fixtures.PostgresWith(
            ResourceSchema.Of([
                .. Fixtures.ServerSchema().Properties
                    .Select(x => string.Equals(x.JsonPointer, "/properties/storageGb", StringComparison.Ordinal)
                        ? x with { Kind = SchemaKind.Text }
                        : x)
            ])
        );

        Generate(narrowed, write: false)
            .Documents.Single(x => x.FileName == "2026-08-01.json")
            .BreakingChanges
            .ShouldContain(x => x.Rule == OpenApiCompatibility.Changed
                && x.JsonPointer.Contains("storageGb", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingAWholeResourceTypeIsABreakingChange() {
        Generate(Fixtures.Postgres());

        var report = Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()), write: false);

        report.Documents.Single(x => x.FileName == "2026-08-01.json")
            .BreakingChanges
            .ShouldContain(x => x.Rule == OpenApiCompatibility.Removed
                && x.JsonPointer.Contains("databases", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingProseIsNotABreakingChange() {
        // A typo fix in a description should not need a new api-version.
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var reworded = Fixtures.PostgresWith(
            ResourceSchema.Of([
                .. Fixtures.ServerSchema().Properties
                    .Select(x => string.Equals(x.JsonPointer, "/properties/storageGb", StringComparison.Ordinal)
                        ? x with { Description = "Storage, in gigabytes." }
                        : x)
            ])
        );

        var report = Generate(reworded, write: false);

        report.Documents.SelectMany(x => x.BreakingChanges).ShouldBeEmpty();
        report.Documents.Single(x => x.FileName == "2026-08-01.json").Drifted.ShouldBeTrue();
    }

    [Fact]
    public void ANewApiVersionBreaksNothing() {
        Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()));

        var report = Generate(Fixtures.Postgres(), write: false);

        // 2027-01-01 is new here, and a version that was never published cannot be broken.
        report.Documents.Single(x => x.FileName == "2027-01-01.json").Published.ShouldBeFalse();
        report.Documents.Single(x => x.FileName == "2027-01-01.json").BreakingChanges.ShouldBeEmpty();
    }

    [Fact]
    public void AnUnreadablePublishedDocumentIsReportedRatherThanOverwrittenSilently() {
        Generate(Fixtures.Postgres());
        File.WriteAllText(FileFor(Fixtures.FirstVersion), "{ this is not json");

        Generate(Fixtures.Postgres(), write: false)
            .Documents.Single(x => x.FileName == "2026-08-01.json")
            .BreakingChanges
            .ShouldContain(x => x.Rule == OpenApiCompatibility.Unreadable);
    }

    // ── A vanished api-version — docs/plan/08 § The provider registry ──────────────────────────

    [Fact]
    public void ADocumentTheRegistryNoLongerProducesIsStaleAndIsNotDeleted() {
        Generate(Fixtures.Postgres());

        // Drop the second api-version from the registry. Its document is still checked in.
        var report = Generate(Fixtures.PostgresWith(Fixtures.ServerSchema()), write: true);

        report.Stale.ShouldBe(["2027-01-01.json"]);
        report.IsClean.ShouldBeFalse();

        // ⚠ Not deleted. A published contract vanishing inside a build step is the failure the
        // 12-month notice window exists to prevent.
        File.Exists(FileFor(Fixtures.SecondVersion)).ShouldBeTrue();
    }
}
