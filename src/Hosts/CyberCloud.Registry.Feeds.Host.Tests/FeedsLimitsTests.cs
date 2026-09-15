using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The server's body limits against the host's own cap, read back off the running host.
/// </summary>
/// <remarks>
///     ⚠ <b>The cap was a number nobody could reach, and this suite could not see it.</b> The fixture
///     lowers <c>MaxArtifactBytes</c> to 64 KiB so an oversized push is a small file, and every push
///     it ever sent was under Kestrel's default of 30,000,000 bytes — so the shipped 256 MiB cap
///     answered <c>413</c> from Kestrel, with a message naming neither the cap nor its key, and no
///     test noticed. The review of #29 measured it. A push of 40 MiB through this fixture would prove
///     the same thing at the price of 40 MiB per run; the limits are read from the same options the
///     server reads instead, which is what makes them a statement about the composition rather than
///     about a probe.
/// </remarks>
[Collection(FeedsHostSuite.Name)]
public sealed class FeedsLimitsTests(FeedsHostFixture host) {
    [Fact]
    public void KestrelAdmitsTwiceTheCap() {
        var kestrel = host.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        var options = host.Services.GetRequiredService<FeedsOptions>();

        options.MaxArtifactBytes.ShouldBe(FeedsHostFixture.MaxArtifactBytes);
        kestrel.Limits.MaxRequestBodySize.ShouldBe(options.MaxRequestBodyBytes);
        kestrel.Limits.MaxRequestBodySize.ShouldBe(2 * FeedsHostFixture.MaxArtifactBytes);
    }

    [Fact]
    public void TheMultipartReaderAdmitsTwiceTheCap() {
        // dotnet nuget push is multipart, and ReadFormAsync has its own limit that Kestrel's does
        // not override — 128 MiB by default, under the shipped 256 MiB cap.
        var form = host.Services.GetRequiredService<IOptions<FormOptions>>().Value;
        var options = host.Services.GetRequiredService<FeedsOptions>();

        form.MultipartBodyLengthLimit.ShouldBe(options.MaxRequestBodyBytes);
    }

    [Fact]
    public void TheShippedCapIsAboveBothServerDefaults() {
        // The defaults the review measured: Kestrel 30,000,000 and FormOptions 134,217,728. A
        // deployment that never raised them would refuse the shipped cap's own message.
        var shipped = new FeedsOptions();

        shipped.MaxArtifactBytes.ShouldBe(256L * 1024 * 1024);
        shipped.MaxRequestBodyBytes.ShouldBeGreaterThan(30_000_000L);
        shipped.MaxRequestBodyBytes.ShouldBeGreaterThan(new FormOptions().MultipartBodyLengthLimit);
        shipped.MaxRequestBodyBytes.ShouldBeGreaterThan(new KestrelServerLimits().MaxRequestBodySize!.Value);
    }
}
