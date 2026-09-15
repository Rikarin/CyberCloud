using CyberCloud.Registry.Feeds.Host.Protocols;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>The Maven path grammar on its own, for the shapes a client library will not send.</summary>
public sealed class MavenPathTests {
    [Theory]
    [InlineData("io/cybercloud/../../escape/x.jar")]
    [InlineData("./x.jar")]
    [InlineData("/io/x.jar")]
    [InlineData("io/x.jar/")]
    [InlineData("io//x.jar")]
    [InlineData("")]
    [InlineData("io/cyber cloud/x.jar")]
    public void APathThatCouldStepOutsideAPrefixOrIsNotAFileIsRefused(string path) =>
        MavenProtocol.IsRepositoryPath(path).ShouldBeFalse();

    [Theory]
    [InlineData("io/cybercloud/widget/1.0.0/widget-1.0.0.jar")]
    [InlineData("io/cybercloud/widget/maven-metadata.xml")]
    [InlineData("org/x/y/1.0-SNAPSHOT/y-1.0-20260915.101010-1.jar.sha1")]
    [InlineData("com/example/lib_a+b/1.0/lib_a+b-1.0.pom")]
    public void ARepositoryPathIsAccepted(string path) => MavenProtocol.IsRepositoryPath(path).ShouldBeTrue();

    [Theory]
    [InlineData("io/x/1.0/x-1.0.jar", false)]
    [InlineData("io/x/1.0/x-1.0.jar.sha1", true)]
    [InlineData("io/x/1.0/x-1.0.jar.asc", true)]
    [InlineData("io/x/maven-metadata.xml", true)]
    [InlineData("io/x/maven-metadata.xml.md5", true)]
    [InlineData("io/x/1.0-SNAPSHOT/x-1.0-20260915.1-1.jar", true)]
    public void OnlyAReleaseArtifactIsImmutable(string path, bool replaceable) =>
        MavenProtocol.IsReplaceable(path).ShouldBe(replaceable);
}
