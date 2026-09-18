using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace CyberCloud.Communication.Providers.Smtp;

/// <summary>One reply from the relay: the three-digit code and every text line it came with.</summary>
/// <param name="Code">The reply code — <c>250</c>, <c>354</c>, <c>550</c>.</param>
/// <param name="Lines">The text after the code on each line of a multi-line reply, in order.</param>
readonly record struct SmtpReply(int Code, IReadOnlyList<string> Lines) {
    /// <summary>A <c>2yz</c> reply — the command did what was asked.</summary>
    public bool IsCompleted => Code is >= 200 and < 300;

    /// <summary>A <c>3yz</c> reply — the relay wants more, which is <c>DATA</c>'s <c>354</c> and <c>AUTH</c>'s <c>334</c>.</summary>
    public bool IsIntermediate => Code is >= 300 and < 400;

    /// <summary>A <c>5yz</c> reply — permanent. Retrying the same command gets the same answer.</summary>
    public bool IsPermanentFailure => Code is >= 500 and < 600;

    /// <summary>The whole reply on one line, for a refusal a person reads.</summary>
    public override string ToString() =>
        string.Concat(Code.ToString(CultureInfo.InvariantCulture), " ", string.Join(" / ", Lines));
}

/// <summary>
///     The wire half of SMTP — RFC 5321's command and reply grammar over a TCP socket, with
///     <c>STARTTLS</c>, and nothing about what to say.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Hand-written, like <c>CyberCloud.ObjectStorage</c>'s SigV4 client, and for the same
///             reason.
///         </b> docs/plan/02 § Dependency register admits nothing without an ADR, and what this
///         module needs of SMTP fits in a file: read a reply, send a line, upgrade to TLS, send a
///         body with dot-stuffing. A full client library brings IMAP, POP, S/MIME and a MIME object
///         model the platform would carry in every silo image to use one per cent of.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The line reader is this class's own and not a <c>StreamReader</c>, because of
///             <c>STARTTLS</c>.
///         </b> A <c>StreamReader</c> reads ahead into a buffer it owns; after the <c>220</c> that
///         answers <c>STARTTLS</c>, the next bytes on the socket are the TLS handshake, and a reader
///         that had swallowed them into its buffer leaves <c>SslStream</c> waiting on a server hello
///         it will never see. This reader pulls what it needs and <see cref="UpgradeToTlsAsync" />
///         refuses to proceed with anything buffered.
///     </para>
///     <para>
///         ⚠ <b>Dot-stuffing happens here.</b> RFC 5321 § 4.5.2: a body line beginning with
///         <c>.</c> gets a second <c>.</c> on the wire, because a lone <c>.</c> on a line is the end
///         of <c>DATA</c>. A message whose template ends a paragraph with "." on its own line would
///         otherwise be truncated there and the rest of the body sent to the relay as commands.
///     </para>
/// </remarks>
sealed class SmtpConnection : IAsyncDisposable {
    // A reply line is at most 512 characters including CRLF (RFC 5321 § 4.5.3.1.5). Twice that is
    // room for a relay that ignores the limit and for the whole of an EHLO in one read.
    const int BufferSize = 4096;

    readonly TcpClient tcp;
    readonly string host;
    readonly byte[] buffer = new byte[BufferSize];
    Stream stream;
    int bufferStart;
    int bufferEnd;

    SmtpConnection(TcpClient tcp, Stream stream, string host) {
        this.tcp = tcp;
        this.stream = stream;
        this.host = host;
    }

    /// <summary>Whether the connection is under TLS — after implicit TLS or a successful upgrade.</summary>
    public bool IsEncrypted => stream is SslStream;

    /// <summary>Opens the socket and, for implicit TLS, negotiates before a byte is read.</summary>
    /// <param name="host">The relay.</param>
    /// <param name="port">Its port.</param>
    /// <param name="implicitTls">Whether to negotiate TLS before reading the greeting.</param>
    /// <param name="cancellationToken">Cancels the connect and the handshake.</param>
    public static async Task<SmtpConnection> ConnectAsync(
        string host,
        int port,
        bool implicitTls,
        CancellationToken cancellationToken
    ) {
        var tcp = new TcpClient { NoDelay = true };

        try {
            await tcp.ConnectAsync(host, port, cancellationToken);
            var connection = new SmtpConnection(tcp, tcp.GetStream(), host);

            if (implicitTls) {
                await connection.UpgradeToTlsAsync(cancellationToken);
            }

            return connection;
        } catch {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Reads one complete reply, however many lines it spans.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="IOException">The relay closed the connection or answered something that is not SMTP.</exception>
    public async Task<SmtpReply> ReadReplyAsync(CancellationToken cancellationToken) {
        var lines = new List<string>(1);
        var code = 0;

        while (true) {
            var line = await ReadLineAsync(cancellationToken);

            // `250-EHLO line` continues, `250 last line` ends. Both are at least "nnn".
            if (line.Length < 3
                || !int.TryParse(
                    line.AsSpan(0, 3),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var lineCode
                )) {
                throw new IOException($"The relay answered a line that is not an SMTP reply: \"{line}\".");
            }

            if (code != 0 && lineCode != code) {
                throw new IOException(
                    $"The relay changed its reply code mid-reply, from {code} to {lineCode}: \"{line}\"."
                );
            }

            code = lineCode;
            lines.Add(line.Length > 4 ? line[4..] : string.Empty);

            if (line.Length < 4 || line[3] != '-') {
                return new(code, lines);
            }
        }
    }

    /// <summary>Sends one command line and reads its reply.</summary>
    /// <param name="command">The command without its CRLF, for example <c>EHLO example.com</c>.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public async Task<SmtpReply> SendCommandAsync(string command, CancellationToken cancellationToken) {
        await WriteAsync(string.Concat(command, MailMessages.CrLf), cancellationToken);
        return await ReadReplyAsync(cancellationToken);
    }

    /// <summary>
    ///     Sends a message's bytes after a <c>354</c>, dot-stuffed and terminated, and reads the
    ///     relay's verdict.
    /// </summary>
    /// <param name="message">The message, CRLF line endings, as <see cref="MailMessages.Compose" /> makes it.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public async Task<SmtpReply> SendDataAsync(string message, CancellationToken cancellationToken) {
        var stuffed = new StringBuilder(message.Length + 8);

        foreach (var line in message.Split(MailMessages.CrLf)) {
            if (line.Length > 0 && line[0] == '.') {
                stuffed.Append('.');
            }

            stuffed.Append(line).Append(MailMessages.CrLf);
        }

        // Every line above ended with CRLF, so the terminator is a dot on a line of its own.
        stuffed.Append('.').Append(MailMessages.CrLf);

        await WriteAsync(stuffed.ToString(), cancellationToken);
        return await ReadReplyAsync(cancellationToken);
    }

    /// <summary>Wraps the socket in TLS, validating the relay's certificate against <c>host</c>.</summary>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <exception cref="InvalidOperationException">Bytes were buffered ahead of the handshake, or TLS is already up.</exception>
    public async Task UpgradeToTlsAsync(CancellationToken cancellationToken) {
        if (IsEncrypted) {
            throw new InvalidOperationException("The connection is already under TLS.");
        }

        if (bufferEnd > bufferStart) {
            // See the class remarks: this is the STARTTLS trap, made loud.
            throw new InvalidOperationException(
                $"{(bufferEnd - bufferStart).ToString(CultureInfo.InvariantCulture)} byte(s) were read ahead of the TLS "
                + "handshake, which means the relay sent more than one reply to STARTTLS or the reader over-read. "
                + "Refusing to start TLS over bytes SslStream would never see."
            );
        }

        var tls = new SslStream(stream, false);

        // ⚠ The default certificate validation — chain and host name — and nothing relaxed. A relay
        // with a certificate the silo does not trust is refused, because "the relay" is otherwise
        // whoever is on the path; Mailpit on a laptop uses SmtpSecurity.None and never reaches here.
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = host },
            cancellationToken
        );

        stream = tls;
    }

    async Task WriteAsync(string text, CancellationToken cancellationToken) {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Reads up to and excluding the next CRLF, pulling from the socket only when the buffer is empty.</summary>
    async Task<string> ReadLineAsync(CancellationToken cancellationToken) {
        var line = new StringBuilder(80);

        while (true) {
            if (bufferStart >= bufferEnd) {
                bufferStart = 0;
                bufferEnd = await stream.ReadAsync(buffer, cancellationToken);

                if (bufferEnd == 0) {
                    throw new IOException(
                        line.Length == 0
                            ? "The relay closed the connection before answering."
                            : $"The relay closed the connection mid-reply after \"{line}\"."
                    );
                }
            }

            while (bufferStart < bufferEnd) {
                var b = buffer[bufferStart++];

                if (b == (byte)'\n') {
                    if (line.Length > 0 && line[^1] == '\r') {
                        line.Length--;
                    }

                    return line.ToString();
                }

                if (line.Length >= 2 * BufferSize) {
                    throw new IOException("The relay sent a reply line longer than any SMTP reply can be.");
                }

                // Replies are ASCII; anything above it is shown as-is so an error text survives.
                line.Append((char)b);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        await stream.DisposeAsync();
        tcp.Dispose();
    }
}
