using CyberCloud.Registry.Feeds.Host.Authentication;
using CyberCloud.Registry.Feeds.Host.Protocols;
using System.Text;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>The pure parts of the three protocols, at the edges the HTTP suites do not reach.</summary>
public sealed class ProtocolGrammarTests {
    static readonly string[] Unordered = [
        "1.0.0", "1.0.0-rc.1", "1.0.0-beta.11", "1.0.0-beta.2", "1.0.0-alpha", "0.9.9", "1.0.0-beta", "1.0.0-BETA.2"
    ];

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.0.0.5", "1.0.0.5")]
    [InlineData("01.002.0003", "1.2.3")]
    [InlineData("1.0.0-Beta.1+build.7", "1.0.0-Beta.1")]
    [InlineData("  2.1  ", "2.1.0")]
    [InlineData("1", "1.0.0")]
    public void ANuGetVersionNormalisesAsTheClientDoes(string spelled, string normalized) =>
        NuGetVersion.Parse(spelled).GetValueOrThrow().Normalized.ShouldBe(normalized);

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("-1.0")]
    public void AVersionTheClientWouldRefuseIsRefused(string spelled) =>
        NuGetVersion.Parse(spelled).IsFailure.ShouldBeTrue(spelled);

    [Fact]
    public void ANuGetVersionOrdersLikeSemVerTwo() {
        var ordered = Unordered
            .Select(static x => NuGetVersion.Parse(x).GetValueOrThrow())
            .Order()
            .Select(static x => x.Normalized)
            .ToList();

        // ⚠ BETA.2 and beta.2 compare equal — the label compares without case — so they may land in
        // either order beside each other; everything else is fixed.
        ordered.Take(2).ShouldBe(["0.9.9", "1.0.0-alpha"]);
        ordered[2].ShouldBe("1.0.0-beta");
        ordered.Skip(3).Take(2).Select(static x => x.ToLowerInvariant()).ShouldBe(["1.0.0-beta.2", "1.0.0-beta.2"]);
        ordered.Skip(5).ShouldBe(["1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"]);
    }

    [Fact]
    public void ANuspecWithTwoDependencyShapesIsRead() {
        var nuspec = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
              <metadata>
                <id>Cyber.Shapes</id>
                <version>1.0.0-rc.1</version>
                <description>Both shapes.</description>
                <authors>a, b</authors>
                <dependencies>
                  <dependency id="Flat.Dep" version="1.0.0" />
                  <group targetFramework="net8.0">
                    <dependency id="Grouped.Dep" version="[2.0.0, 3.0.0)" />
                  </group>
                  <group targetFramework="netstandard2.0" />
                </dependencies>
              </metadata>
            </package>
            """
        );

        var parsed = Nuspec.Parse(nuspec).GetValueOrThrow();

        parsed.Id.ShouldBe("Cyber.Shapes");
        parsed.IdLower.ShouldBe("cyber.shapes");
        parsed.Version.Normalized.ShouldBe("1.0.0-rc.1");
        parsed.DependencyGroups.Select(static x => x.TargetFramework).ShouldBe(["", "net8.0", "netstandard2.0"]);
        parsed.DependencyGroups[0].Dependencies.Single().Id.ShouldBe("Flat.Dep");
        parsed.DependencyGroups[1].Dependencies.Single().Range.ShouldBe("[2.0.0, 3.0.0)");
        parsed.DependencyGroups[2].Dependencies.ShouldBeEmpty();

        var metadata = Nuspec.MetadataOf(parsed.ToMetadataJson());
        metadata["authors"]!.GetValue<string>().ShouldBe("a, b");
    }

    [Theory]
    [InlineData("<package><metadata><version>1.0</version></metadata></package>", "id")]
    [InlineData("<package><metadata><id>x</id><version>nope</version></metadata></package>", "part")]
    [InlineData("<package><nothing/></package>", "metadata")]
    [InlineData("<not xml", "well-formed")]
    public void ANuspecThatIsWrongIsRefusedNamingWhat(string xml, string named) {
        var refused = Nuspec.Parse(Encoding.UTF8.GetBytes(xml));

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain(named);
    }

    [Theory]
    [InlineData("lodash", true)]
    [InlineData("@cyber/lib", true)]
    [InlineData("my-pkg_1.0", true)]
    [InlineData("Lodash", false)]
    [InlineData(".hidden", false)]
    [InlineData("_private", false)]
    [InlineData("@cyber", false)]
    [InlineData("a/b", false)]
    [InlineData("@cyber/lib/extra", false)]
    [InlineData("", false)]
    public void AnNpmNameFollowsNpmsOwnRule(string name, bool valid) => NpmProtocol.IsPackageName(name).ShouldBe(valid);

    [Theory]
    [InlineData("left-pad", "1.3.0", "left-pad-1.3.0.tgz")]
    [InlineData("@babel/core", "7.24.0", "core-7.24.0.tgz")]
    [InlineData("@cyber/scoped", "0.1.0-rc.1+build.5", "scoped-0.1.0-rc.1+build.5.tgz")]
    public void AnNpmTarballIsNamedAsTheRegistryServesIt(string name, string version, string file) {
        NpmProtocol.TarballNameOf(name, version).ShouldBe(file);
        NpmProtocol.VersionOfTarball(name, file).ShouldBe(version);
    }

    [Theory]
    [InlineData("left-pad", "left-pad.tgz")]
    [InlineData("left-pad", "right-pad-1.0.0.tgz")]
    [InlineData("left-pad", "left-pad-1.0.0.tar.gz")]
    [InlineData("@babel/core", "@babel/core-7.0.0.tgz")]
    [InlineData("core", "core-.tgz")]
    public void ATarballNameThatIsNotThisPackagesCarriesNoVersion(string name, string file) =>
        NpmProtocol.VersionOfTarball(name, file).ShouldBeNull();

    [Theory]
    [InlineData("dXNlcjpzZWNyZXQ=", "secret")] // user:secret
    [InlineData("OnRva2Vu", "token")] // :token — an empty username
    [InlineData("dXNlcjphOmI=", "a:b")] // user:a:b — the first colon is the separator
    [InlineData("bm9jb2xvbg==", null)] // nocolon
    [InlineData("!!!", null)]
    public void ABasicCredentialsPasswordIsWhatFollowsTheFirstColon(string encoded, string? password) =>
        FeedCredentialResolver.BasicPassword(encoded).ShouldBe(password);
}
