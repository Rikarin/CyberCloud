using CyberCloud.Cli.Configuration;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     docs/plan/21 § Decisions: <i>"Config | <c>~/.cyc/config</c> with named profiles; every setting
///     also an env var (<c>CYC_SUBSCRIPTION</c>, …) for CI."</i>
/// </summary>
public sealed class ConfigurationTests {
    const string TwoProfiles = """
        default = work

        [work]
        subscription = sub-work
        tenant = contoso

        [lab]
        subscription = sub-lab
        tenant = fabrikam
        endpoint = https://api.lab.internal/
        """;

    [Fact]
    public void EverySettingHasAMechanicalEnvironmentVariable() {
        // ⚠ A rule rather than a table. A table of setting-to-variable would be a second source that
        // goes stale the first time somebody adds a setting.
        CycSettings.VariableFor("subscription").ShouldBe("CYC_SUBSCRIPTION");
        CycSettings.VariableFor("tenant").ShouldBe("CYC_TENANT");
        CycSettings.VariableFor("resource-group").ShouldBe("CYC_RESOURCE_GROUP");
        CycSettings.VariableFor("endpoint").ShouldBe("CYC_ENDPOINT");
    }

    [Fact]
    public void TheFlagBeatsTheEnvironmentWhichBeatsTheProfile() {
        var file = CycConfigFile.Parse(TwoProfiles);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CYC_SUBSCRIPTION"] = "sub-env" };

        var settings = CycSettings.Resolve(file, environment, profileFlag: null);

        settings.Profile.ShouldBe("work");
        settings.Get("subscription", flagValue: "sub-flag").ShouldBe("sub-flag");
        settings.Get("subscription").ShouldBe("sub-env");
        settings.Get("tenant").ShouldBe("contoso");
    }

    [Fact]
    public void ProfileSelectionFollowsFlagThenEnvironmentThenTheFilesOwnDefault() {
        var file = CycConfigFile.Parse(TwoProfiles);
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        CycSettings.Resolve(file, empty, profileFlag: "lab").Get("tenant").ShouldBe("fabrikam");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CYC_PROFILE"] = "lab" };
        CycSettings.Resolve(file, environment, profileFlag: null).Get("tenant").ShouldBe("fabrikam");

        CycSettings.Resolve(file, empty, profileFlag: null).Get("tenant").ShouldBe("contoso");
    }

    [Fact]
    public void AProfileCanOverrideTheEndpoint() {
        var settings = CycSettings.Resolve(
            CycConfigFile.Parse(TwoProfiles),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            profileFlag: "lab");

        settings.Endpoint.ShouldBe(new Uri("https://api.lab.internal/"));
    }

    [Fact]
    public void AnEndpointThatIsNotAUrlIsAUsageError() {
        var settings = CycSettings.Resolve(
            CycConfigFile.Parse("[default]\nendpoint = not a url\n"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            profileFlag: null);

        Should.Throw<CycUsageException>(() => settings.Endpoint);
    }

    [Fact]
    public void RoundTripsThroughTheFile() {
        var written = CycConfigFile.Parse(TwoProfiles).Set("lab", "output", "json").Render();
        var read = CycConfigFile.Parse(written);

        read.DefaultProfile.ShouldBe("work");
        read.Value("lab", "output").ShouldBe("json");
        read.Value("work", "subscription").ShouldBe("sub-work");
    }

    [Fact]
    public async Task TheProfileSuppliesTheSubscriptionAndTenantAVerbNeeds() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));

        using var host = TestHost.Create(transport, config: TwoProfiles);

        // Neither --subscription nor --tenant is on the command line.
        var code = await host.RunAsync("sample", "widgets", "show", "--name", "w1", "--resource-group", "prod", "--output", "none");

        code.ShouldBe((int)ExitCode.Ok);
        transport.Requests[0].Uri.AbsolutePath.ShouldBe("/tenants/contoso/subscriptions/sub-work/resourceGroups/prod/providers/CyberCloud.Sample/widgets/w1");
    }

    [Fact]
    public async Task TheEnvironmentSuppliesThemForCi() {
        var transport = new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, "{}"));

        using var host = TestHost.Create(
            transport,
            environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["CYC_SUBSCRIPTION"] = "sub-ci",
                ["CYC_TENANT"] = "tenant-ci",
            });

        await host.RunAsync("sample", "widgets", "show", "--name", "w1", "--resource-group", "prod", "--output", "none");

        transport.Requests[0].Uri.AbsolutePath.ShouldStartWith("/tenants/tenant-ci/subscriptions/sub-ci/");
    }

    [Fact]
    public async Task AMissingAddressValueNamesAllThreePlacesItCouldComeFrom() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("sample", "widgets", "show", "--name", "w1", "--resource-group", "prod", "--tenant", "t");

        code.ShouldBe((int)ExitCode.Usage);
        host.Stderr.ShouldContain("--subscription");
        host.Stderr.ShouldContain("CYC_SUBSCRIPTION");
        host.Stderr.ShouldContain("~/.cyc/config");
    }

    [Fact]
    public async Task ConfigSetWritesTheCurrentProfile() {
        using var host = TestHost.Create(config: TwoProfiles);

        (await host.RunAsync("config", "set", "output", "json", "--output", "none")).ShouldBe((int)ExitCode.Ok);

        var written = CycConfigFile.Read(Path.Combine(host.StateDirectory, "config"));
        written.Value("work", "output").ShouldBe("json");
    }

    [Fact]
    public async Task ConfigGetSaysWhereTheValueCameFrom() {
        using var host = TestHost.Create(
            config: TwoProfiles,
            environment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CYC_SUBSCRIPTION"] = "sub-env" });

        await host.RunAsync("config", "get", "subscription", "--output", "json");

        using var document = host.StdoutAsJson();

        document.RootElement.GetProperty("value").GetString().ShouldBe("sub-env");
        document.RootElement.GetProperty("source").GetString().ShouldBe("CYC_SUBSCRIPTION");
    }
}
