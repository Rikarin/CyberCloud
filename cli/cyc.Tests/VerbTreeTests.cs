using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     The reader against the real generated tree — <c>generated/cli/2026-08-01.json</c>, embedded in
///     the assembly under test.
/// </summary>
/// <remarks>
///     ⚠ These run against the tree the build embedded rather than a fixture. A fixture would keep
///     passing on the day the emitter changed shape, which is the one day this suite exists for.
/// </remarks>
public sealed class VerbTreeTests {
    [Fact]
    public void ReadsTheEmbeddedTree() {
        var catalog = TestHost.Catalog();

        catalog.ApiVersions.ShouldNotBeEmpty();
        catalog.Select(null).Format.ShouldBe(VerbTreeCatalog.SupportedFormat);
    }

    [Fact]
    public void ReadsFlagsWithAndWithoutChoices() {
        var create = TestHost.Catalog().Select(null).Groups["sample"].Commands["widgets"].Verbs["create"];

        var tier = create.Flags.Single(x => x.Name == "--tier");
        tier.Choices.ShouldBe(["free", "basic", "standard", "premium"]);

        // ⚠ The flag with no `choices` member at all. An absent member must read as an empty list
        // rather than null — CliEmitter § ToJson omits every member that would say `false` or `[]`,
        // so "absent" is the common case and a reader that treated it as null would fail on the
        // first flag of the first verb.
        var cidrs = create.Flags.Single(x => x.Name == "--allowed-cidrs");
        cidrs.Choices.ShouldBeEmpty();
        cidrs.Repeated.ShouldBeTrue();
        cidrs.JsonPointer.ShouldBe("/properties/allowedCidrs");
    }

    [Fact]
    public void CarriesTheGeneratedAlias() {
        var widgets = TestHost.Catalog().Select(null).Groups["sample"].Commands["widgets"];

        // docs/plan/21 § Grammar calls the alias table "the only hand-maintained part of the CLI's
        // surface". It is not: it arrives here, from the registry's shortName.
        widgets.Alias.ShouldBe("widget");
    }

    [Fact]
    public void ExitCodesMatchTheEnum() {
        var codes = TestHost.Catalog().Select(null).ExitCodes;

        codes["ok"].ShouldBe((int)ExitCode.Ok);
        codes["clientError"].ShouldBe((int)ExitCode.ClientError);
        codes["usage"].ShouldBe((int)ExitCode.Usage);
        codes["auth"].ShouldBe((int)ExitCode.Auth);
        codes["serverError"].ShouldBe((int)ExitCode.ServerError);
        codes["timeout"].ShouldBe((int)ExitCode.Timeout);
    }

    [Fact]
    public void UnknownApiVersionNamesWhatIsAvailable() {
        var catalog = TestHost.Catalog();

        var failure = Should.Throw<CycUsageException>(() => catalog.Select("2019-01-01"));

        failure.Message.ShouldContain("2019-01-01");
        failure.Message.ShouldContain(catalog.Newest);
    }

    [Fact]
    public void UnknownFormatIsRefusedRatherThanGuessed() {
        var failure = Should.Throw<CycUsageException>(
            () => VerbTreeCatalog.Parse("""{"format":"99","apiVersion":"2026-08-01"}"""));

        failure.Message.ShouldContain("format '99'");
    }
}
