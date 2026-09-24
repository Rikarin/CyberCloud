using System.Net.Sockets;
using System.Text;

namespace CyberCloud.Providers.Mail.ClusterConformance;

/// <summary>A line-oriented TCP conversation — SMTP submission and IMAP are both this.</summary>
/// <remarks>
///     ⚠ Hand-written in the test rather than the platform's <c>SmtpConnection</c>, which refuses
///     <c>AUTH</c> without TLS — the right refusal for the platform's own relay, and the wrong one for
///     a back end whose TLS belongs to the unbuilt shared front door. What is under test is the
///     server; the client only has to speak the protocol, and to keep every byte it was sent.
/// </remarks>
sealed class MailWire : IDisposable {
    readonly TcpClient tcp;
    readonly StreamReader reader;
    readonly StreamWriter writer;

    MailWire(TcpClient tcp) {
        this.tcp = tcp;
        var stream = tcp.GetStream();
        reader = new(stream, Encoding.UTF8);
        writer = new(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
    }

    public static async Task<MailWire> ConnectAsync(string host, int port, CancellationToken token) {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, token);
        return new(tcp);
    }

    /// <summary>
    ///     Reads until a line starts with <paramref name="expect" />, returning every line read, CRLF-joined.
    ///     A final reply that is not the expected one throws with what the server said.
    /// </summary>
    public async Task<string> ExpectAsync(string expect, CancellationToken token) {
        var read = new StringBuilder();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        var tag = expect.Contains(' ', StringComparison.Ordinal) ? expect.Split(' ')[0] + " " : null;

        while (true) {
            var line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("the server closed the connection after: " + read);

            read.Append(line).Append("\r\n");

            var smtpFinal = expect.Length == 3 && line.Length >= 4 && char.IsAsciiDigit(line[0]) && line[3] == ' ';

            if (line.StartsWith(expect, StringComparison.Ordinal) && (expect.Length != 3 || smtpFinal)) {
                return read.ToString();
            }

            if (smtpFinal || (tag is not null && line.StartsWith(tag, StringComparison.Ordinal))) {
                // The message is the server's reply line, exactly, so a caller can read the code off it.
                throw new InvalidOperationException(line);
            }
        }
    }

    public async Task<string> CommandAsync(string command, string expect, CancellationToken token) {
        await writer.WriteLineAsync(command.AsMemory(), token);
        return await ExpectAsync(expect, token);
    }

    /// <summary>The <c>AUTH PLAIN</c> argument for a user and password.</summary>
    public static string Plain(string user, string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + user + "\0" + password));

    public void Dispose() {
        reader.Dispose();
        writer.Dispose();
        tcp.Dispose();
    }
}
