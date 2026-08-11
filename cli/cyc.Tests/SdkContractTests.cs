using System.Text.Json.Serialization;
using CyberCloud.Sdk;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     <c>cyc account get-access-token</c> is a contract between two assemblies, and this is the test
///     that holds both ends.
/// </summary>
/// <remarks>
///     ⚠ <c>CyberCloudCliCredential</c> runs exactly <c>cyc account get-access-token --output
///     json</c>, deserialises the output into <c>CliTokenPayload</c>, and branches on the exit code —
///     <b>3 means "not signed in, run cyc login"</b> and anything else means a different problem. Both
///     halves are asserted here, and the payload is round-tripped through the SDK's own type rather
///     than through a copy of its property names.
/// </remarks>
public sealed class SdkContractTests {
    [Fact]
    public async Task TheOutputDeserialisesIntoTheSdksOwnPayloadType() {
        using var host = TestHost.Create();

        var code = await host.RunAsync("account", "get-access-token", "--output", "json");

        code.ShouldBe((int)ExitCode.Ok);

        var payload = JsonSerializer.Deserialize(host.Stdout, CliTokenJsonContext.Default.CliTokenPayload);

        payload.ShouldNotBeNull();
        payload.AccessToken.ShouldBe(TestHost.FixedToken);
        payload.ExpiresOn.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task NotSignedInIsExitThreeSoTheSdkGivesTheRightAdvice() {
        using var host = TestHost.Create(credential: new UnavailableCredential("No sign-in is cached."));

        var code = await host.RunAsync("account", "get-access-token", "--output", "json");

        // ⚠ CyberCloudCliCredential's own comment: "docs/plan/21 § Decisions: exit code 3 is auth.
        // Anything else from the CLI is a different problem and saying 'run cyc login' would be wrong
        // advice." Exit 1 here would make the SDK tell users to sign in when the real problem was
        // something else entirely.
        code.ShouldBe((int)ExitCode.Auth);
        host.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheTokenIsOnStdoutAndNowhereElse() {
        using var host = TestHost.Create();

        await host.RunAsync("account", "get-access-token", "--output", "json", "--verbose");

        // The one command that prints token material, because the SDK parses its output. It still
        // must not be echoed into the trace.
        host.Stdout.ShouldContain(TestHost.FixedToken);
        host.Stderr.ShouldNotContain(TestHost.FixedToken);
    }

    [Fact]
    public void TheSdkExpectsTheExecutableToBeCalledCyc() {
        // ⚠ Not `cc`. `cc` is the POSIX name of the system C compiler and `CC` is the standard make
        // variable, so a CLI called `cc` shadows a toolchain on every Unix box. Both assemblies have
        // to agree, and this is where they are compared. (The SDK's `Executable` constant is
        // internal, so what is checkable from here is the client id it registers under — the same
        // word, and the one that keys the shared token-cache entry.)
        CyberCloudCliCredential.CliClientId.ShouldBe("cyc");
    }

    [Fact]
    public void TheEnvironmentPrefixMatchesTheSdks() {
        // The SDK reads CYC_TENANT_ID / CYC_CLIENT_ID / CYC_CLIENT_SECRET; the CLI's settings derive
        // CYC_* mechanically. One prefix, checked rather than assumed.
        Configuration.CycSettings.EnvironmentPrefix.ShouldBe("CYC_");
        WorkloadIdentityCredential.ClientIdVariable.ShouldStartWith(Configuration.CycSettings.EnvironmentPrefix);
        CyberCloudAuthorityHosts.EnvironmentVariable.ShouldStartWith(Configuration.CycSettings.EnvironmentPrefix);
    }
}

/// <summary>Reads the payload the SDK's <c>CliTokenPayload</c> describes, using that very type.</summary>
[JsonSerializable(typeof(CliTokenPayload))]
sealed partial class CliTokenJsonContext : JsonSerializerContext;
