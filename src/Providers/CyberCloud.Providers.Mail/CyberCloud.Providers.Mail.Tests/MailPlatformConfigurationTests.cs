using CyberCloud.Providers.Mail.Application;
using Microsoft.Extensions.Configuration;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The abuse desk's lever, from the configuration an operator writes to the gate a domain renders.
/// </summary>
/// <remarks>
///     ⚠ <b>The #34 review found this parser had never been fed a configuration.</b> The one test of
///     suspension built <see cref="MailPlatformOptions" /> with <c>with { SuspendedTenants = … }</c>
///     and skipped <see cref="MailApplicationModule.PlatformFrom" /> entirely, so a list that bound to
///     empty would have passed every suite — and an empty list is a suspension that does nothing.
/// </remarks>
public sealed class MailPlatformConfigurationTests {
    static readonly string Section = MailPlatformOptions.Section;

    [Fact]
    public async Task ASuspensionWrittenAsAnArrayReachesTheGate() {
        var platform = MailApplicationModule.PlatformFrom(
            Configuration(
                (Section + ":InboundHost", "mx.cybercloud.example"),
                (Section + ":SpfInclude", "_spf.cybercloud.example"),
                (Section + ":MtaStsHost", "mta-sts.cybercloud.example"),
                (Section + ":TlsReportAddress", "tls-reports@cybercloud.example"),
                (Section + ":SuspendedTenants:0", MailHarness.TenantB.ToString("D")),
                (Section + ":SuspendedTenants:1", MailHarness.TenantA.ToString("D"))
            )
        );

        platform.IsConfigured.ShouldBeTrue();
        platform.SuspendedTenants.ShouldBe([MailHarness.TenantB, MailHarness.TenantA]);

        // ⚠ Through to the decision, which is the only reader that matters: suspended before any
        // record is asked about.
        var dns = new ZoneDns([]);
        var decision = await MailDeliverability.DecideAsync(
            MailHarness.TenantA,
            "example.com",
            string.Empty,
            platform,
            dns,
            TestContext.Current.CancellationToken
        );

        decision.Sending.ShouldBe(MailSending.Suspended);
        dns.Asked.ShouldBeEmpty();
    }

    [Fact]
    public void ASuspensionWrittenAsOneCommaSeparatedValueIsReadToo() {
        // ⚠ `CyberCloud__Mail__SuspendedTenants=a,b` — a scalar with no children. The first cut read
        // children only, so this spelling suspended nobody and said nothing.
        var platform = MailApplicationModule.PlatformFrom(
            Configuration((Section + ":SuspendedTenants", MailHarness.TenantA.ToString("D") + ", " + MailHarness.TenantB.ToString("D")))
        );

        platform.SuspendedTenants.ShouldBe([MailHarness.TenantA, MailHarness.TenantB]);
    }

    [Theory]
    [InlineData(":SuspendedTenants:0", "tenant-a")]
    [InlineData(":SuspendedTenants:0", "")]
    [InlineData(":SuspendedTenants", "11111111-1111-4111-8111-111111111111, not-a-tenant")]
    public void AnEntryThatIsNotATenantIdStopsTheHostRatherThanSuspendingNobody(string key, string value) {
        var refused = Should.Throw<InvalidOperationException>(
            () => MailApplicationModule.PlatformFrom(Configuration((Section + key, value)))
        );

        refused.Message.ShouldContain(Section + key);
    }

    [Fact]
    public void NoSectionAtAllIsAnUnconfiguredRegionWithNobodySuspended() {
        var platform = MailApplicationModule.PlatformFrom(Configuration());

        platform.IsConfigured.ShouldBeFalse();
        platform.SuspendedTenants.ShouldBeEmpty();
    }

    static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(static x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();
}
