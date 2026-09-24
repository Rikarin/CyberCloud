using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     What <see cref="MailDomains" /> and <see cref="MailMailboxes" /> render, run by the images the
///     pod runs — Dovecot 2.3 and 2.4 and Rspamd — rather than read.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CUT OF THIS TYPE WAS NEVER RUN BY ANYTHING, AND IT HAD FOUR DEFECTS ONLY
///         RUNNING FINDS.</b> A milter on Rspamd's HTTP port, a catch-all in a map Postfix does not
///         consult for a virtual domain, an antivirus socket nothing served, and a DKIM key mounted
///         where Rspamd's user could not read it. Each rendered a document every test agreed with.
///         So each rendering here is handed to the real daemon, which is the only reader whose opinion
///         counts.
///     </para>
///     <para>
///         ⚠ <b>Postfix is not here; it is <c>MailDeliveryOnK3sTests</c>'</b>, because its image is
///         built from this repository and that suite builds it once for the whole pod. These run
///         without a cluster, in the per-PR lane, against images pulled by tag.
///     </para>
/// </remarks>
public sealed class MailDataPlaneTests {
    const string Password = "correct horse battery staple";

    [Theory]
    [InlineData("2.3")]
    [InlineData("2.4")]
    public async Task DovecotAcceptsTheRenderedConfigDeliversAndSignsInOnlyWithTheRightPassword(string version) {
        var token = TestContext.Current.CancellationToken;
        using var body = JsonDocument.Parse(
            MailDomains.Body(MailHarness.ClusterId, version: version, mailboxQuota: "1Gi")
        );

        var alice = Guid.Parse("55555555-5555-4555-8555-000000000001");
        var aliceLine = MailMailboxes.PasswdLine(
            "alice@example.com",
            Sha512Crypt.Hash(Password, Sha512Crypt.SaltFor(alice), Sha512Crypt.MailboxRounds),
            "100Mi"
        );

        var archiveLine = MailMailboxes.PasswdLine("archive@example.com", null, string.Empty);

        await using var dovecot = new ContainerBuilder(MailDomains.DovecotImage(version))
            .WithCommand("dovecot", "-F", "-c", MailDomains.ConfigDirectory + "/" + MailDomains.DovecotConfKey)
            .WithResourceMapping(Encoding.UTF8.GetBytes(MailDomains.DovecotConf(body.RootElement)), MailDomains.ConfigDirectory + "/" + MailDomains.DovecotConfKey)
            .WithResourceMapping(Encoding.UTF8.GetBytes(aliceLine), MailDomains.UsersDirectory + "/" + MailMailboxes.PasswdKey("alice"))
            .WithResourceMapping(Encoding.UTF8.GetBytes(archiveLine), MailDomains.UsersDirectory + "/" + MailMailboxes.PasswdKey("archive"))
            // ⚠ Owned by vmail, as a volume under the pod's fsGroup is. Docker's default tmpfs is root's
            // and 0755, and Dovecot 2.3 — whose master runs as root and whose mail processes do not —
            // then fails every delivery with "missing +w perm: /srv/mail" and a 451 at the client.
            .WithCreateParameterModifier(static x => x.HostConfig!.Tmpfs = new Dictionary<string, string> {
                ["/srv/mail"] = "uid=1000,gid=1000,mode=0770",
                ["/run/dovecot"] = "mode=0777"
            })
            .WithPortBinding(MailDomains.ImapContainerPort, true)
            .WithPortBinding(MailDomains.LmtpContainerPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("starting up for imap, lmtp"))
            .Build();

        await dovecot.StartAsync(token);

        var host = dovecot.Hostname;

        // ── Delivery, as the shared inbound pool will make it ─────────────────────────────────
        using (var lmtp = await MailWire.ConnectAsync(host, dovecot.GetMappedPublicPort(MailDomains.LmtpContainerPort), token)) {
            await lmtp.ExpectAsync("220", token);
            await lmtp.CommandAsync("LHLO test", "250", token);
            await lmtp.CommandAsync("MAIL FROM:<sender@elsewhere.example>", "250", token);
            await lmtp.CommandAsync("RCPT TO:<alice@example.com>", "250", token);

            // ⚠ An address with no passwd file is refused at RCPT — the lookup is per login, by file.
            await lmtp.CommandAsync("RCPT TO:<nobody@example.com>", "550", token);
            await lmtp.CommandAsync("DATA", "354", token);
            await lmtp.CommandAsync("Subject: delivered\r\n\r\nhello\r\n.", "250", token);
        }

        var imapPort = dovecot.GetMappedPublicPort(MailDomains.ImapContainerPort);

        // ── Sign-in: the right password, a wrong one, and a mailbox that has none ─────────────
        (await MailWire.ImapLoginAsync(host, imapPort, "alice@example.com", Password, token)).ShouldBeTrue();
        (await MailWire.ImapLoginAsync(host, imapPort, "alice@example.com", "wrong", token)).ShouldBeFalse();

        // ⚠ The login is the whole address. The local part alone, or the right password under
        // another domain, is not this mailbox.
        (await MailWire.ImapLoginAsync(host, imapPort, "alice@other.example", Password, token)).ShouldBeFalse();

        // ⚠ THE ONE THAT MATTERS MOST: a mailbox with no password is not open to every password.
        foreach (var attempt in new[] { "!", "", Password }) {
            (await MailWire.ImapLoginAsync(host, imapPort, "archive@example.com", attempt, token))
                .ShouldBeFalse($"the password-less mailbox accepted '{attempt}'");
        }

        // ── What alice sees ─────────────────────────────────────────────────────────────────────
        using var imap = await MailWire.ConnectAsync(host, imapPort, token);
        await imap.ExpectAsync("* OK", token);
        await imap.CommandAsync($"a1 LOGIN alice@example.com \"{Password}\"", "a1 OK", token);

        // ⚠ 100Mi from the mailbox, not the domain's 1Gi — the per-mailbox quota reaches the major.
        var quota = await imap.CommandAsync("a2 GETQUOTAROOT INBOX", "a2 OK", token);
        Regex.IsMatch(quota, @"STORAGE \d+ 102400\)").ShouldBeTrue(quota);

        await imap.CommandAsync("a3 SELECT INBOX", "a3 OK", token);
        var message = await imap.CommandAsync("a4 FETCH 1 BODY[]", "a4 OK", token);
        message.ShouldContain("Subject: delivered");
    }

    [Fact]
    public async Task RspamdLoadsTheRenderedConfigurationAndTheKeyFileItNames() {
        // ⚠ The DKIM key path is the trap this test exists for: a path Rspamd can read as base64 is
        // taken for an inline key, and it signs with ed25519 under nonsense — MailDomains.DkimKeyFile.
        // configtest reads every local.d file this type renders.
        var token = TestContext.Current.CancellationToken;
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var pem = MailDomains.GenerateCredentials()[MailDomains.DkimPrivateKeyField];

        await using var rspamd = new ContainerBuilder(MailDomains.RspamdImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(MailDomains.RspamdWorkerProxy()), "/etc/rspamd/local.d/worker-proxy.inc")
            .WithResourceMapping(Encoding.UTF8.GetBytes(MailDomains.RspamdDkimSigning(body.RootElement)), "/etc/rspamd/local.d/dkim_signing.conf")
            .WithResourceMapping(Encoding.UTF8.GetBytes(MailDomains.RspamdActions(body.RootElement)), "/etc/rspamd/local.d/actions.conf")
            .WithResourceMapping(Encoding.UTF8.GetBytes(MailDomains.RspamdSenderRule()), "/etc/rspamd/local.d/rspamd.lua")
            .WithResourceMapping(Encoding.UTF8.GetBytes(pem), MailDomains.DkimDirectory + "/" + MailDomains.DkimKeyFile)
            .WithResourceMapping(Encoding.UTF8.GetBytes(Message("CEO <ceo@victim.example>")), "/tmp/forged.eml")
            .WithResourceMapping(Encoding.UTF8.GetBytes(Message("Alice <Alice@Example.com>")), "/tmp/honest.eml")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("prepare to fork process rspamd_proxy"))
            .Build();

        await rspamd.StartAsync(token);

        var test = await rspamd.ExecAsync(["rspamadm", "configtest"], token);

        test.ExitCode.ShouldBe(0, test.Stdout + test.Stderr);

        // The milter worker is on the port Postfix dials, and it is the proxy, not the normal worker.
        var dump = await rspamd.ExecAsync(["rspamadm", "configdump", "worker"], token);
        dump.Stdout.ShouldContain(MailDomains.MilterPort.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // ── The sender rule: the header half of the #34 review's forgery ─────────────────────────
        //
        // ⚠ alice, signed in, with alice on the envelope and the CEO of another domain in From: —
        // the message a receiver would have shown with SPF passing. Rejected, by this rule and by name.
        // The same message from a client that did not sign in is inbound mail and is not this rule's.
        var forged = await ScanAsync(rspamd, "/tmp/forged.eml", "alice@example.com", token);
        forged.ShouldContain("Action: reject", Case.Sensitive, forged);
        forged.ShouldContain(MailDomains.SenderRuleSymbol, Case.Sensitive, forged);

        var inbound = await ScanAsync(rspamd, "/tmp/forged.eml", null, token);
        inbound.ShouldNotContain(MailDomains.SenderRuleSymbol, Case.Sensitive, inbound);

        // ⚠ Case-insensitive, as addresses' domains are: Alice@Example.com is the envelope's sender.
        var honest = await ScanAsync(rspamd, "/tmp/honest.eml", "alice@example.com", token);
        honest.ShouldNotContain(MailDomains.SenderRuleSymbol, Case.Sensitive, honest);
        honest.ShouldNotContain("Action: reject", Case.Sensitive, honest);
    }

    /// <summary>What <c>rspamc</c> reports for one message, as the milter would hand it over.</summary>
    /// <param name="rspamd">The running container.</param>
    /// <param name="path">The message file inside it.</param>
    /// <param name="user">The SASL login, or <see langword="null" /> for a client that did not sign in.</param>
    /// <param name="token">The test's token.</param>
    static async Task<string> ScanAsync(IContainer rspamd, string path, string? user, CancellationToken token) {
        string[] command = user is null
            ? ["rspamc", "--connect", "127.0.0.1:11333", "--from", "alice@example.com", "--rcpt", "bob@example.com", path]
            : ["rspamc", "--connect", "127.0.0.1:11333", "--user", user, "--from", "alice@example.com", "--rcpt", "bob@example.com", path];

        var scanned = await rspamd.ExecAsync(command, token);

        scanned.ExitCode.ShouldBe(0, scanned.Stdout + scanned.Stderr);

        return scanned.Stdout;
    }

    static string Message(string from) =>
        "From: " + from + "\r\nTo: bob@example.com\r\nSubject: a scan\r\nDate: Thu, 24 Sep 2026 12:00:00 +0000\r\n"
        + "Message-ID: <" + Guid.NewGuid().ToString("N") + "@example.com>\r\n\r\nhello\r\n";
}

/// <summary>A line-oriented TCP conversation — SMTP, LMTP and IMAP are all this.</summary>
/// <remarks>
///     ⚠ Hand-written in the test, deliberately, rather than the production <c>SmtpConnection</c>:
///     that client refuses <c>AUTH</c> without TLS, which is the right refusal for the platform's own
///     relay and the wrong one for a back end whose TLS is the unbuilt front door's. What is under
///     test is the server; the client only has to speak the protocol.
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

    /// <summary>Reads until a line starts with <paramref name="prefix" />, returning everything read.</summary>
    public async Task<string> ExpectAsync(string prefix, CancellationToken token) {
        var read = new StringBuilder();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (true) {
            var line = await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("the server closed the connection after: " + read);

            read.Append(line).Append('\n');

            if (line.StartsWith(prefix, StringComparison.Ordinal)) {
                // A multi-line SMTP reply continues with "250-"; only "250 " ends it.
                if (prefix.Length == 3 && line.Length > 3 && line[3] == '-') {
                    continue;
                }

                return read.ToString();
            }

            // A refusal that is not what was expected ends the wait with what the server said.
            if (prefix.Length == 3 && line.Length >= 4 && char.IsDigit(line[0]) && line[3] == ' ') {
                throw new InvalidOperationException($"expected {prefix}, the server answered: {line}");
            }

            if (prefix.Contains(' ', StringComparison.Ordinal)
                && line.StartsWith(prefix.Split(' ')[0] + " ", StringComparison.Ordinal)) {
                throw new InvalidOperationException($"expected {prefix}, the server answered: {line}");
            }
        }
    }

    public async Task<string> CommandAsync(string command, string expect, CancellationToken token) {
        await writer.WriteLineAsync(command.AsMemory(), token);
        return await ExpectAsync(expect, token);
    }

    /// <summary>Whether an IMAP <c>LOGIN</c> succeeds, on a connection of its own.</summary>
    public static async Task<bool> ImapLoginAsync(string host, int port, string user, string password, CancellationToken token) {
        using var imap = await ConnectAsync(host, port, token);
        await imap.ExpectAsync("* OK", token);

        try {
            await imap.CommandAsync($"l1 LOGIN {user} \"{password}\"", "l1 OK", token);
            return true;
        } catch (InvalidOperationException) {
            return false;
        }
    }

    public void Dispose() {
        reader.Dispose();
        writer.Dispose();
        tcp.Dispose();
    }
}
