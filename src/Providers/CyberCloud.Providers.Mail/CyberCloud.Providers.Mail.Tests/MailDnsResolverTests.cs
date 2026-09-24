using CyberCloud.Providers.Mail.Dns;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     <see cref="DnsWireResolver" /> against a real DNS server: CoreDNS serving the zone file
///     <see cref="MailDnsRecords.ZoneFile" /> writes, which is the file a tenant pastes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE ZONE FILE, THREE CLAIMS.</b> That the zone file this platform hands a tenant is
///         one a real server loads; that the resolver reads every record kind back out of a real
///         server over UDP and over TCP; and that <see cref="MailDnsRecords.Verify" /> finds the
///         records verified — the whole of <c>verify</c>'s path, with the DNS the only part that is
///         not the platform's code.
///     </para>
///     <para>
///         ⚠ <b>One host port for both protocols</b>, because a resolver asks one server and falls back
///         from UDP to TCP on the <i>same</i> address. Two random ports would test two resolvers, and
///         the truncation fallback — the path every DKIM answer near 512 bytes takes without EDNS —
///         would never run.
///     </para>
/// </remarks>
public sealed class MailDnsResolverTests : IAsyncLifetime {
    /// <summary>Pinned like every image in this tree; verified against Docker Hub and pulled on 2026-09-23.</summary>
    public const string Image = "coredns/coredns:1.12.1";

    static readonly MailPlatformOptions Platform = MailHarness.Platform;

    IContainer coredns = null!;
    int port;
    ImmutableArray<MailDnsRecord> records;

    /// <summary>A TXT record too large for one 1232-byte UDP answer, to make the server truncate.</summary>
    static readonly string Big = string.Concat(Enumerable.Repeat("x", 1600));

    public async ValueTask InitializeAsync() {
        using var key = RSA.Create(MailDomains.DkimKeyBits);

        MailDnsRecords.TryRequired("example.com", key.ExportPkcs8PrivateKeyPem(), Platform, out records).ShouldBeTrue();

        var zone = new StringBuilder()
            .Append("$ORIGIN example.com.\n")
            .Append("@ 3600 IN SOA ns.example.com. hostmaster.example.com. 1 7200 3600 1209600 3600\n")
            .Append("@ 3600 IN NS ns.example.com.\n")
            .Append("ns 3600 IN A 127.0.0.1\n")
            .Append(MailDnsRecords.ZoneFile(records))
            .Append("big.example.com. 3600 IN TXT ")
            .Append(string.Join(' ', Big.Chunk(250).Select(static x => "\"" + new string(x) + "\"")))
            .Append('\n')
            .ToString();

        port = FreePort();

        coredns = new ContainerBuilder(Image)
            .WithCommand("-conf", "/Corefile")
            .WithResourceMapping(Encoding.UTF8.GetBytes(".:53 {\n    file /zones/example.com example.com\n    errors\n}\n"), "/Corefile")
            .WithResourceMapping(Encoding.UTF8.GetBytes(zone), "/zones/example.com")
            .WithPortBinding(port.ToString(CultureInfo.InvariantCulture), "53/tcp")
            .WithPortBinding(port.ToString(CultureInfo.InvariantCulture), "53/udp")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("CoreDNS-"))
            .Build();

        await coredns.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() {
        if (coredns is not null) {
            await coredns.DisposeAsync();
        }
    }

    DnsWireResolver Resolver(bool tcpOnly = false) =>
        new(new() { Nameservers = ["127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture)], TcpOnly = tcpOnly });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryRequiredRecordResolvesAndVerifies(bool tcpOnly) {
        var resolver = Resolver(tcpOnly);
        var answers = new Dictionary<MailDnsRecord, MailDnsAnswer>();

        foreach (var record in records) {
            answers[record] = await resolver.QueryAsync(record.Name, record.Kind, TestContext.Current.CancellationToken);
        }

        MailDnsRecords.Verify(records, x => answers[x], out var checks).ShouldBeTrue();

        // ⚠ Every record, not just the three that gate: the MX's exchange and the CNAME's target are
        // names the resolver had to decompress, and the DKIM key is two strings it had to join.
        foreach (var check in checks) {
            check.Status.ShouldBe(MailDnsRecords.Verified, check.Record.Role + ": " + check.Detail + " — found " + string.Join(" | ", check.Found));
        }
    }

    [Fact]
    public async Task ATruncatedUdpAnswerIsAskedAgainOverTcp() {
        // 1600 bytes of TXT cannot fit the 1232-byte buffer the question offers, so CoreDNS sets TC
        // and the resolver has to ask the same server again over TCP to get it.
        var answer = await Resolver().QueryAsync("big.example.com", "TXT", TestContext.Current.CancellationToken);

        answer.Resolved.ShouldBeTrue(answer.Error);
        answer.Values.ShouldBe([Big]);
    }

    [Fact]
    public async Task ANameThatDoesNotExistIsAnAnswerAndNotAFailure() {
        var answer = await Resolver().QueryAsync("_dmarc.nothing-here.example.com", "TXT", TestContext.Current.CancellationToken);

        answer.Resolved.ShouldBeTrue(answer.Error);
        answer.Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerIsUnresolvableWithinTheTimeout() {
        // ⚠ The gate's other failure: nobody answered. It must come back — clause 3 of the reconcile
        // loop is thirty seconds — and it must say it could not ask, not that the record is missing.
        var silent = new DnsWireResolver(
            new() { Nameservers = ["127.0.0.1:" + FreePort().ToString(CultureInfo.InvariantCulture)], Timeout = TimeSpan.FromSeconds(1) }
        );

        var answer = await silent.QueryAsync("example.com", "MX", TestContext.Current.CancellationToken);

        answer.Resolved.ShouldBeFalse();
        answer.Error.ShouldNotBeEmpty();
    }

    /// <summary>A host port nothing is listening on, TCP and UDP both, as of the moment it is asked.</summary>
    static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var chosen = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return chosen;
    }
}
