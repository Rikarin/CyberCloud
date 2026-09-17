using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CyberCloud.Communication.Tests.Infrastructure;

/// <summary>
///     An SMTP server that answers from a script — what a relay says when it refuses, which Mailpit
///     never does.
/// </summary>
/// <remarks>
///     <para>
///         <c>SmtpChannelProviderTests</c> proves the happy path against a real server. This exists
///         for the paths a real server will not walk on demand: a greeting that is not <c>220</c>, an
///         <c>EHLO</c> without <c>STARTTLS</c> when the client insists on it, a recipient refused
///         with <c>550</c>, a credential over a connection with no TLS, a relay that stops
///         answering. Each is one property set on this class and one assertion on the client's
///         refusal text, and none needs a container.
///     </para>
///     <para>
///         ⚠ <b>It speaks the grammar and nothing else.</b> One connection at a time, one line per
///         command, replies as configured. It is not a fixture for the protocol's edge cases — a
///         relay that pipelines, a reply split across writes — because the client under test is a
///         submission client to a relay the platform chose, not a general-purpose MTA.
///     </para>
///     <para>
///         <see cref="OfferStartTls" /> answers <c>STARTTLS</c> with <c>220</c> and then presents a
///         self-signed certificate, which the client's default validation refuses — so what that
///         path proves is that the client validates rather than that TLS works. A TLS that works
///         needs a certificate the client trusts, and that is the production relay's.
///     </para>
/// </remarks>
public sealed class ScriptedSmtpServer : IAsyncDisposable {
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stopping = new();
    readonly List<string> commands = [];
    Task? accepting;

    /// <summary>The port to point the client at.</summary>
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The greeting. <c>220</c> is a relay; anything else is a relay saying go away.</summary>
    public string Greeting { get; set; } = "220 scripted ESMTP";

    /// <summary>Whether <c>EHLO</c> advertises <c>STARTTLS</c>.</summary>
    public bool OfferStartTls { get; set; }

    /// <summary>The <c>AUTH</c> mechanisms <c>EHLO</c> advertises, or empty for none.</summary>
    public string AuthMechanisms { get; set; } = string.Empty;

    /// <summary>The reply to <c>RCPT TO</c>. <c>250</c> accepts; <c>550 5.1.1 User unknown</c> is the classic refusal.</summary>
    public string RecipientReply { get; set; } = "250 2.1.5 Ok";

    /// <summary>The reply to <c>EHLO</c>'s first line, when it is not <c>250</c>.</summary>
    public string? RefuseEhloWith { get; set; }

    /// <summary>When set, the server accepts the connection and never says a word.</summary>
    public bool Silent { get; set; }

    /// <summary>Every command line received, in order, across connections.</summary>
    public IReadOnlyList<string> Commands => commands;

    /// <summary>The last message body received after <c>DATA</c>, or empty.</summary>
    public string LastMessage { get; private set; } = string.Empty;

    /// <summary>Starts listening. The port is known after this.</summary>
    public void Start() {
        listener.Start();
        accepting = Task.Run(AcceptAsync, CancellationToken.None);
    }

    async Task AcceptAsync() {
        while (!stopping.IsCancellationRequested) {
            TcpClient client;

            try {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            } catch (OperationCanceledException) {
                return;
            } catch (SocketException) {
                return;
            }

            try {
                await ServeAsync(client);
            } catch (IOException) {
                // The client hung up mid-conversation — a refusal on its side, which is the point.
            } catch (AuthenticationException) {
                // The client refused our self-signed certificate — see OfferStartTls.
            } finally {
                client.Dispose();
            }
        }
    }

    async Task ServeAsync(TcpClient client) {
        var ct = stopping.Token;
        Stream stream = client.GetStream();

        if (Silent) {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return;
        }

        var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        await WriteAsync(stream, Greeting, ct);

        while (await reader.ReadLineAsync(ct) is { } line) {
            lock (commands) {
                commands.Add(line);
            }

            var verb = line.Split(' ', 2)[0].ToUpperInvariant();

            switch (verb) {
                case "EHLO":
                    if (RefuseEhloWith is { } refusal) {
                        await WriteAsync(stream, refusal, ct);
                        break;
                    }

                    var extensions = new StringBuilder("250-scripted\r\n250-SIZE 10485760\r\n");
                    if (OfferStartTls) {
                        extensions.Append("250-STARTTLS\r\n");
                    }

                    if (AuthMechanisms.Length > 0) {
                        extensions.Append("250-AUTH ").Append(AuthMechanisms).Append("\r\n");
                    }

                    extensions.Append("250 8BITMIME");
                    await WriteAsync(stream, extensions.ToString(), ct);
                    break;

                case "STARTTLS":
                    await WriteAsync(stream, "220 2.0.0 Ready to start TLS", ct);
                    var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                    using (var certificate = SelfSigned()) {
                        await tls.AuthenticateAsServerAsync(certificate, false, false);
                    }

                    stream = tls;
                    reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    break;

                case "AUTH":
                    await WriteAsync(stream, "235 2.7.0 Authentication successful", ct);
                    break;

                case "MAIL":
                    await WriteAsync(stream, "250 2.1.0 Ok", ct);
                    break;

                case "RCPT":
                    await WriteAsync(stream, RecipientReply, ct);
                    break;

                case "DATA":
                    await WriteAsync(stream, "354 End data with <CR><LF>.<CR><LF>", ct);
                    var body = new StringBuilder();

                    while (await reader.ReadLineAsync(ct) is { } dataLine && dataLine != ".") {
                        body.Append(dataLine).Append("\r\n");
                    }

                    LastMessage = body.ToString();
                    await WriteAsync(stream, "250 2.0.0 Ok: queued as SCRIPTED01", ct);
                    break;

                case "QUIT":
                    await WriteAsync(stream, "221 2.0.0 Bye", ct);
                    return;

                default:
                    await WriteAsync(stream, "502 5.5.2 Error: command not recognized", ct);
                    break;
            }
        }
    }

    static async Task WriteAsync(Stream stream, string reply, CancellationToken ct) {
        var bytes = Encoding.ASCII.GetBytes(reply + "\r\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    static X509Certificate2 SelfSigned() {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=scripted", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // ⚠ Re-imported through PFX bytes: on Windows, a certificate created in memory has an
        // ephemeral key SslStream cannot use for a server handshake.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await stopping.CancelAsync();
        listener.Dispose();

        if (accepting is not null) {
            try {
                await accepting;
            } catch (OperationCanceledException) {
                // Stopping.
            }
        }

        stopping.Dispose();
    }
}
