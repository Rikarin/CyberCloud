using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Providers.Mail.Dns;

/// <summary>Where <see cref="DnsWireResolver" /> sends its questions, and how long it waits.</summary>
/// <remarks>
///     ⚠ <b>Empty <see cref="Nameservers" /> means the node's own resolvers</b>, read off the network
///     interfaces — in a pod, the cluster DNS, which forwards to whatever the cluster forwards to.
///     That answers what the platform's network sees, which for a tenant zone hosted somewhere public
///     is what a receiver sees too.
/// </remarks>
public sealed record MailDnsOptions {
    /// <summary>The configuration section, beside <see cref="MailPlatformOptions.Section" />.</summary>
    public const string Section = MailPlatformOptions.Section + ":Dns";

    /// <summary>Servers to ask, as <c>address</c> or <c>address:port</c>. Tried in order.</summary>
    public ImmutableArray<string> Nameservers { get; init; } = [];

    /// <summary>How long one question to one server may take.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Whether to ask over TCP from the start rather than after a truncated UDP answer.</summary>
    public bool TcpOnly { get; init; }
}

/// <summary>
///     An RFC 1035 stub resolver over UDP, falling back to TCP when an answer is truncated.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>HAND-WRITTEN, AND THE REASON IS docs/plan/02 § Dependency register.</b> Nothing is
///         admitted without an ADR, .NET's <c>Dns</c> class resolves addresses and nothing else, and
///         a stub resolver asking three record kinds is one message format in one file — the argument
///         <c>SmtpConnection</c> and <c>CyberCloud.ObjectStorage</c>'s SigV4 made first. It is proven
///         against CoreDNS serving a zone file (<c>MailDnsResolverTests</c>) and its parser against
///         the edge cases a real server may send (<c>MailDnsWireTests</c>).
///     </para>
///     <para>
///         ⚠ <b>TXT answers are JOINED, and that is the whole reason DKIM verifies.</b> A 2048-bit
///         key's record is two character-strings of at most 255 bytes each (RFC 1035 § 3.3.14); RFC
///         6376 § 3.6.2.2 says a verifier concatenates them. A resolver that returned each string as a
///         value would report every correctly published DKIM record as a mismatch.
///     </para>
///     <para>
///         ⚠ <b>EDNS(0) with a 1232-byte buffer</b>, the size the DNS flag day of 2020 settled on to
///         avoid IP fragmentation. Without it a server truncates anything over 512 bytes, and a DKIM
///         answer with its question and a CNAME in front of it is close enough to that to make the TCP
///         fallback the common path rather than the rare one.
///     </para>
/// </remarks>
/// <param name="options">Where to ask.</param>
public sealed class DnsWireResolver(MailDnsOptions options) : IMailDnsResolver {
    const ushort TypeCname = 5;
    const ushort TypeMx = 15;
    const ushort TypeTxt = 16;
    const ushort EdnsBufferSize = 1232;

    /// <inheritdoc />
    public async Task<MailDnsAnswer> QueryAsync(string name, string kind, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        ushort type = kind.ToUpperInvariant() switch {
            "TXT" => TypeTxt,
            "MX" => TypeMx,
            "CNAME" => TypeCname,
            _ => 0
        };

        if (type == 0) {
            return Unanswered($"'{kind}' is not a record kind this resolver asks for.");
        }

        var servers = Servers();

        if (servers.Length == 0) {
            return Unanswered(
                "no nameserver is configured under " + MailDnsOptions.Section + " and the node reports none."
            );
        }

        var errors = new List<string>();

        foreach (var server in servers) {
            var id = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            var query = DnsMessage.Query(id, name, type, EdnsBufferSize);

            try {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Timeout);

                var response = options.TcpOnly
                    ? await AskTcpAsync(server, query, timeout.Token)
                    : await AskUdpAsync(server, query, timeout.Token);

                var parsed = DnsMessage.Parse(response, id, type);

                if (parsed.Truncated && !options.TcpOnly) {
                    parsed = DnsMessage.Parse(await AskTcpAsync(server, query, timeout.Token), id, type);
                }

                if (parsed.Answer.Resolved) {
                    return parsed.Answer;
                }

                errors.Add(server + ": " + parsed.Answer.Error);
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                errors.Add(server + ": no answer within " + options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s");
            } catch (SocketException ex) {
                errors.Add(server + ": " + ex.SocketErrorCode);
            } catch (IOException ex) {
                errors.Add(server + ": " + ex.Message);
            }
        }

        return Unanswered(string.Join("; ", errors));
    }

    static MailDnsAnswer Unanswered(string error) => new(false, [], error);

    ImmutableArray<IPEndPoint> Servers() {
        if (!options.Nameservers.IsDefaultOrEmpty) {
            return [.. options.Nameservers.Select(Endpoint).OfType<IPEndPoint>()];
        }

        // ⚠ IPv4 first, and only addresses a socket can reach: an interface's site-local IPv6
        // resolver (fec0::/10) is deprecated and answers nothing on most networks.
        return [
            .. NetworkInterface.GetAllNetworkInterfaces()
                .Where(static x => x.OperationalStatus == OperationalStatus.Up)
                .SelectMany(static x => x.GetIPProperties().DnsAddresses)
                .Where(static x => x.AddressFamily == AddressFamily.InterNetwork || !x.IsIPv6SiteLocal)
                .Distinct()
                .OrderBy(static x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(static x => new IPEndPoint(x, 53))
        ];
    }

    static IPEndPoint? Endpoint(string spelled) =>
        IPEndPoint.TryParse(spelled, out var endpoint)
            ? endpoint.Port == 0 ? new(endpoint.Address, 53) : endpoint
            : null;

    static async Task<byte[]> AskUdpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken) {
        using var udp = new UdpClient(server.AddressFamily);
        await udp.SendAsync(query, server, cancellationToken);

        // ⚠ A datagram from anybody but the server is ignored rather than parsed: a spoofed answer
        // has to get the source address right as well as the random id.
        while (true) {
            var received = await udp.ReceiveAsync(cancellationToken);

            if (received.RemoteEndPoint.Equals(server)) {
                return received.Buffer;
            }
        }
    }

    static async Task<byte[]> AskTcpAsync(IPEndPoint server, byte[] query, CancellationToken cancellationToken) {
        using var tcp = new TcpClient(server.AddressFamily);
        await tcp.ConnectAsync(server, cancellationToken);

        var stream = tcp.GetStream();
        var framed = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.CopyTo(framed, 2);
        await stream.WriteAsync(framed, cancellationToken);

        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, cancellationToken);

        var response = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(response, cancellationToken);

        return response;
    }
}

/// <summary>The DNS message format — building one question and reading the answers to it.</summary>
/// <remarks>
///     Internal to this assembly and visible to its tests, which feed it hand-built answers: a
///     compressed name, a pointer loop, a truncated section, a CNAME in front of the TXT.
/// </remarks>
static class DnsMessage {
    /// <summary>A parsed answer, and whether the server said it cut it short.</summary>
    public readonly record struct Parsed(MailDnsAnswer Answer, bool Truncated);

    /// <summary>A recursive query for one name and type, with an EDNS(0) OPT record.</summary>
    public static byte[] Query(ushort id, string name, ushort type, ushort bufferSize) {
        var message = new List<byte>(64);

        Append16(message, id);
        Append16(message, 0x0100); // RD
        Append16(message, 1); // QDCOUNT
        Append16(message, 0);
        Append16(message, 0);
        Append16(message, 1); // ARCOUNT — the OPT record

        foreach (var label in name.TrimEnd('.').Split('.')) {
            var bytes = Encoding.ASCII.GetBytes(label);

            if (bytes.Length is 0 or > 63) {
                throw new ArgumentException($"'{name}' has a label that is empty or longer than 63 bytes.", nameof(name));
            }

            message.Add((byte)bytes.Length);
            message.AddRange(bytes);
        }

        message.Add(0);
        Append16(message, type);
        Append16(message, 1); // IN

        // OPT: root name, type 41, class = UDP payload size, TTL = extended RCODE and flags, no data.
        message.Add(0);
        Append16(message, 41);
        Append16(message, bufferSize);
        Append16(message, 0);
        Append16(message, 0);
        Append16(message, 0);

        return [.. message];
    }

    /// <summary>Reads the answers of <paramref name="type" /> out of a response to query <paramref name="id" />.</summary>
    public static Parsed Parse(ReadOnlySpan<byte> message, ushort id, ushort type) {
        if (message.Length < 12) {
            return new(new(false, [], "the answer is shorter than a DNS header"), false);
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(message) != id) {
            return new(new(false, [], "the answer's id is not the question's"), false);
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        var truncated = (flags & 0x0200) != 0;
        var rcode = flags & 0x000F;

        if ((flags & 0x8000) == 0) {
            return new(new(false, [], "the message is a question, not an answer"), false);
        }

        // NXDOMAIN is an answer: the name does not exist, so it has no record of any kind.
        if (rcode == 3) {
            return new(new(true, [], string.Empty), truncated);
        }

        if (rcode != 0) {
            return new(new(false, [], "the server answered RCODE " + rcode.ToString(CultureInfo.InvariantCulture)), truncated);
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var offset = 12;
        var values = ImmutableArray.CreateBuilder<string>();

        try {
            for (var i = 0; i < questions; i++) {
                ReadName(message, ref offset);
                offset += 4;
            }

            for (var i = 0; i < answers; i++) {
                ReadName(message, ref offset);

                var recordType = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
                var recordClass = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
                var data = offset + 10;

                if (data + length > message.Length) {
                    throw new FormatException("a record runs past the end of the message");
                }

                if (recordType == type && recordClass == ClassIn) {
                    values.Add(ReadData(message, data, length, type));
                }

                offset = data + length;
            }
        } catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException) {
            // ⚠ A truncated message is expected to run out part-way — that is what TC says — so it is
            // reported as truncated rather than as garbage, and the caller asks again over TCP.
            return truncated
                ? new(new(false, [], "the answer was truncated"), true)
                : new(new(false, [], "the answer is malformed: " + ex.Message), false);
        }

        return new(new(true, values.ToImmutable(), string.Empty), truncated);
    }

    const ushort ClassIn = 1;

    static string ReadData(ReadOnlySpan<byte> message, int offset, int length, ushort type) {
        switch (type) {
            case 16: {
                var text = new StringBuilder();
                var end = offset + length;

                while (offset < end) {
                    var count = message[offset++];

                    if (offset + count > end) {
                        throw new FormatException("a TXT string runs past its record");
                    }

                    text.Append(Encoding.UTF8.GetString(message.Slice(offset, count)));
                    offset += count;
                }

                return text.ToString();
            }
            case 15: {
                var preference = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
                var exchange = offset + 2;

                return preference.ToString(CultureInfo.InvariantCulture) + " " + ReadName(message, ref exchange);
            }
            default: {
                var at = offset;

                return ReadName(message, ref at);
            }
        }
    }

    /// <summary>Reads a possibly compressed name, advancing past it in the record it was found in.</summary>
    /// <remarks>
    ///     ⚠ <b>Pointers are followed a bounded number of times.</b> A pointer that points at itself, or
    ///     two that point at each other, is a loop a naive reader never leaves; RFC 1035 has no rule
    ///     against sending one, so the reader has to have its own.
    /// </remarks>
    static string ReadName(ReadOnlySpan<byte> message, ref int offset) {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;

        while (true) {
            var length = message[position];

            if (length == 0) {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0) {
                if (++jumps > 32) {
                    throw new FormatException("the name's compression pointers loop");
                }

                var target = ((length & 0x3F) << 8) | message[position + 1];

                if (!jumped) {
                    offset = position + 2;
                    jumped = true;
                }

                position = target;
                continue;
            }

            if ((length & 0xC0) != 0) {
                throw new FormatException("the name has a label type RFC 1035 does not define");
            }

            labels.Add(Encoding.ASCII.GetString(message.Slice(position + 1, length)));
            position += 1 + length;
        }

        if (!jumped) {
            offset = position;
        }

        return string.Join('.', labels);
    }

    static void Append16(List<byte> message, ushort value) {
        message.Add((byte)(value >> 8));
        message.Add((byte)value);
    }
}
