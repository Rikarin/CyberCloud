using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The chart's templates against the constants the reconciler renders from: images, ports, mount
///     paths, keys, and the relay gate's closed spelling.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST CUT'S DEFECTS WERE IN BOTH COPIES, AND THAT IS WHAT THIS FILE IS FOR.</b>
///         The milter on Rspamd's normal worker, three images nothing built, a key mounted where
///         Rspamd could not read it — each was written once in <see cref="MailDomains" /> and once in
///         <c>charts/managed/mail/templates</c>, and the chart had them all. Nothing generates a Helm
///         template from a schema (<c>MailSizingTests</c> says so for the helpers), so every fact
///         here is two hand-maintained copies held together by an assertion.
///     </para>
///     <para>
///         ⚠ <b>Containment, not equality.</b> The chart is Go templating around the same text, so
///         this asserts the values the daemons read appear in it — a changed port or path in either
///         copy fails here — and does not pretend a template and a C# builder produce equal bytes.
///     </para>
/// </remarks>
public sealed class MailChartTests {
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void TheStatefulSetRunsTheImagesAndMountsThePathsTheReconcilerDoes() {
        var set = Embedded("mail.statefulset.yaml");
        var helpers = Embedded("mail.helpers.tpl");

        foreach (var version in MailDomains.Versions) {
            helpers.ShouldContain(MailDomains.DovecotImage(version));
        }

        set.ShouldContain(MailDomains.RspamdImage);
        set.ShouldContain(MailDomains.PostfixImage);
        set.ShouldContain("fsGroup: " + Text(MailDomains.MailGroupId));

        foreach (var path in new[] {
                     MailDomains.ConfigDirectory, MailDomains.UsersDirectory, MailDomains.DkimDirectory, "/srv/mail",
                     "/run/dovecot", "/etc/rspamd/local.d"
                 }) {
            set.ShouldContain("mountPath: " + path);
        }

        // ⚠ The key projected under a name Rspamd cannot mistake for base64 — MailDomains.DkimKeyFile.
        set.ShouldContain("key: " + MailDomains.DkimPrivateKeyField);
        set.ShouldContain("path: " + MailDomains.DkimKeyFile);

        // ⚠ The sender rule, under the one Lua name Rspamd loads from local.d — MailDomains.RspamdSenderRule.
        set.ShouldContain("key: " + MailDomains.RspamdSenderKey + "\n                path: rspamd.lua");

        foreach (var port in new[] {
                     MailDomains.LmtpContainerPort, MailDomains.ImapContainerPort, MailDomains.SieveContainerPort,
                     MailDomains.SmtpPort, MailDomains.SubmissionPort, MailDomains.MetricsPort
                 }) {
            set.ShouldContain("containerPort: " + Text(port));
        }
    }

    [Fact]
    public void TheServiceMapsTheConventionalPortsOntoTheSameContainerPorts() {
        var service = Embedded("mail.service.yaml");

        foreach (var (port, target) in new[] {
                     (MailDomains.SmtpPort, MailDomains.SmtpPort), (MailDomains.ImapPort, MailDomains.ImapContainerPort),
                     (MailDomains.SubmissionPort, MailDomains.SubmissionPort), (MailDomains.SievePort, MailDomains.SieveContainerPort)
                 }) {
            service.ShouldContain("port: " + Text(port) + "\n      targetPort: " + Text(target));
        }

        // ⚠ No LMTP on the Service: it skips Postfix, and with it the alias map, forwarding, the
        // catch-all and the spam filter — the #34 review's finding. MailDomains.SmtpPort.
        service.ShouldNotContain("targetPort: " + Text(MailDomains.LmtpContainerPort));
        service.ShouldContain("type: ClusterIP");
        service.ShouldNotContain("LoadBalancer\n");
    }

    [Fact]
    public void TheConfigMapCarriesEveryKeyAndTheValuesTheDaemonsDial() {
        var map = Embedded("mail.configmap.yaml");
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        foreach (var key in new[] {
                     MailDomains.DovecotConfKey, MailDomains.PostfixMainCfKey, MailDomains.PostfixStartKey,
                     MailDomains.RspamdProxyKey, MailDomains.RspamdDkimKey, MailDomains.RspamdActionsKey,
                     MailDomains.RspamdSenderKey
                 }) {
            map.ShouldContain("  " + key + ": |");
        }

        // ⚠ Both halves of the sender lock, which the chart would otherwise install without.
        map.ShouldContain("smtpd_sender_login_maps = texthash:/etc/postfix/senders");

        foreach (var line in MailDomains.RspamdSenderRule().Split('\n').Where(static x => x.Length > 0)) {
            map.ShouldContain(line, Case.Sensitive, "the chart's rspamd-sender.lua is missing a line the reconciler renders");
        }

        // The ports and paths the three containers find each other on.
        map.ShouldContain("smtpd_milters = inet:127.0.0.1:" + Text(MailDomains.MilterPort));
        map.ShouldContain("bind_socket = \"127.0.0.1:" + Text(MailDomains.MilterPort) + "\";");
        map.ShouldContain("virtual_transport = lmtp:inet:127.0.0.1:" + Text(MailDomains.LmtpContainerPort));
        map.ShouldContain("smtpd_sasl_path = inet:127.0.0.1:" + Text(MailDomains.AuthContainerPort));
        map.ShouldContain("path = \"" + MailDomains.DkimDirectory + "/" + MailDomains.DkimKeyFile + "\";");
        map.ShouldContain(MailDomains.UsersDirectory + "/%{user | username | lower}.passwd");
        map.ShouldContain(MailDomains.UsersDirectory + "/%Ln.passwd");

        // ⚠ Every script line the reconciler renders is in the chart's copy of it.
        foreach (var line in MailDomains.PostfixStartScript(body.RootElement).Split('\n')
                     .Where(static x => x.Length > 0 && !x.StartsWith("domain=", StringComparison.Ordinal) && !x.StartsWith("catch_all=", StringComparison.Ordinal))) {
            map.ShouldContain(line, Case.Sensitive, "the chart's postfix-start.sh is missing a line the reconciler renders");
        }

        // ⚠ A chart install cannot ask the DNS, so it renders the gate the reconciler renders for a
        // domain nothing has verified — held, and forwards deferred.
        map.ShouldContain("is held until its SPF, DKIM and DMARC records verify}, reject");
        map.ShouldContain("defer_transports = smtp");
    }

    [Fact]
    public void TheMailboxSecretIsEmptyAndNamesTheDomain() {
        var users = Embedded("mail.secret-users.yaml");

        users.ShouldContain("-mail-users");
        users.ShouldContain(MailMailboxes.DomainAnnotation + ":");
        users.ShouldNotContain("\ndata:");
        users.ShouldNotContain("\nstringData:");
    }

    static string Embedded(string name) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"'{name}' is not an embedded resource of this assembly. It is declared in "
                + "CyberCloud.Providers.Mail.Tests.csproj with a LogicalName."
            );

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
