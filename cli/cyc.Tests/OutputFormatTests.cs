using CyberCloud.Cli.Output;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>--output table|json|yaml|tsv|none</c> and <c>--query</c> — docs/plan/21 § Decisions.
/// </summary>
public sealed class OutputFormatTests {
    const string Page = """
        [
          {"name":"w1","location":"eu-central","properties":{"tier":"free","replicas":1}},
          {"name":"w2","location":"us-east","properties":{"tier":"premium","replicas":3}}
        ]
        """;

    static string[] Show(params string[] extra) => [
        "sample", "widgets", "show",
        "--name", "w1", "--resource-group", "prod", "--subscription", "s", "--tenant", "t",
        .. extra,
    ];

    [Fact]
    public async Task TableIsTheDefaultAndHasAHeader() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, Page)));

        await host.RunAsync(Show());

        var lines = host.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].ShouldContain("name");
        lines[0].ShouldContain("location");
        lines[2].ShouldContain("w1");
        lines[3].ShouldContain("w2");
    }

    [Fact]
    public async Task TsvHasNoHeaderBecauseCutCountsColumns() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, Page)));

        await host.RunAsync(Show("--output", "tsv"));

        var lines = host.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
        lines[0].Split('\t')[0].ShouldBe("w1");
        lines[1].Split('\t')[1].ShouldBe("us-east");
    }

    [Fact]
    public async Task NoneWritesNothingAtAll() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, Page)));

        (await host.RunAsync(Show("--output", "none"))).ShouldBe((int)ExitCode.Ok);

        host.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task YamlRendersTheNestingATableCannot() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) =>
            Responses.Json(HttpStatusCode.OK, """{"name":"w1","properties":{"tier":"free","enabled":true}}""")));

        await host.RunAsync(Show("--output", "yaml"));

        host.Stdout.ShouldContain("name: w1");
        host.Stdout.ShouldContain("properties:");
        host.Stdout.ShouldContain("  tier: free");
        host.Stdout.ShouldContain("  enabled: true");
    }

    [Fact]
    public void YamlQuotesWhatWouldChangeMeaning() {
        var value = Payload.Of(JsonDocument.Parse("""
            {"a":"no","b":"1.20","c":"","d":"has: colon","e":"plain"}
            """).RootElement);

        using var writer = new StringWriter();
        YamlWriter.Write(writer, value);

        var yaml = writer.ToString();

        // ⚠ The Norway problem, the version-number problem, and the two syntax ones. Each of these
        // reads back as a different type or fails to parse if left bare.
        yaml.ShouldContain("""a: "no" """.TrimEnd());
        yaml.ShouldContain("""b: "1.20" """.TrimEnd());
        yaml.ShouldContain("""c: "" """.TrimEnd());
        yaml.ShouldContain("""d: "has: colon" """.TrimEnd());
        yaml.ShouldContain("e: plain");
    }

    [Fact]
    public async Task QueryProjectsBeforeRendering() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, Page)));

        await host.RunAsync(Show("--query", "[].{name: name, tier: properties.tier}", "--output", "json"));

        using var document = host.StdoutAsJson();
        var first = document.RootElement[0];

        first.GetProperty("name").GetString().ShouldBe("w1");
        first.GetProperty("tier").GetString().ShouldBe("free");
        first.TryGetProperty("location", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task QueryAndTsvIsTheOneLinerPeopleActuallyType() {
        using var host = TestHost.Create(new ScriptedTransport((_, _) => Responses.Json(HttpStatusCode.OK, Page)));

        await host.RunAsync(Show("--query", "[?properties.tier == 'premium'].name", "--output", "tsv"));

        host.Stdout.Trim().ShouldBe("w2");
    }

    /// <summary>
    ///     ⚠ <c>OutputFormats.NameOf</c> is what tells an extension which format <c>cyc</c> settled
    ///     on (docs/plan/21 § Credentials across the process boundary), so a name that does not parse
    ///     back is a child told to render something the parent cannot read. The round trip is the
    ///     cheapest way to keep the two halves honest.
    /// </summary>
    [Fact]
    public void EveryFormatsNameParsesBackToIt() {
        foreach (var format in Enum.GetValues<OutputFormat>())
            OutputFormats.Parse(OutputFormats.NameOf(format)).ShouldBe(format);

        // And the names are exactly the ones --output advertises, so help text and the value an
        // extension receives cannot drift apart.
        Enum.GetValues<OutputFormat>().Select(OutputFormats.NameOf).ShouldBe(OutputFormats.Names, ignoreOrder: true);
    }
}
