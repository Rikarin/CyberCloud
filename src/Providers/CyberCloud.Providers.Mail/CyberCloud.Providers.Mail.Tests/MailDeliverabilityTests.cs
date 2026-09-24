using CyberCloud.ResourceManager.Conformance;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     doc 17 § Deliverability's gate: what verifies, what does not, and what the rendered Postfix
///     does about it.
/// </summary>
/// <remarks>
///     ⚠ The zone here is a table (<see cref="ZoneDns" />), because what is under test is the decision
///     and the rendering. That a real resolver reads a real zone the same way is
///     <c>MailDnsResolverTests</c>', and that the rendered gate refuses a real SMTP client is
///     <c>MailDeliveryOnK3sTests</c>'.
/// </remarks>
public sealed class MailDeliverabilityTests {
    static readonly string Pem = NewPem();

    static string NewPem() {
        using var rsa = RSA.Create(MailDomains.DkimKeyBits);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    static ImmutableArray<MailDnsRecord> Required() {
        MailDnsRecords.TryRequired("example.com", Pem, MailHarness.Platform, out var records).ShouldBeTrue();
        return records;
    }

    [Fact]
    public void AZoneHoldingExactlyTheRequiredRecordsVerifiesEveryOne() {
        var records = Required();
        var zone = ZoneDns.Publishing(records);

        MailDnsRecords.Verify(records, zone.Answer, out var checks).ShouldBeTrue();

        checks.ShouldAllBe(static x => x.Status == MailDnsRecords.Verified);
    }

    [Fact]
    public void ADkimRecordForAnotherKeyIsAMismatchAndHoldsSending() {
        // ⚠ THE SILENT FAILURE, CAUGHT BEFORE A MESSAGE IS SENT. A well-formed DKIM record for a key
        // the signer does not hold is what a tenant who copied an old record has; every receiver would
        // fail every message.
        var records = Required();
        using var other = RSA.Create(MailDomains.DkimKeyBits);
        var zone = new ZoneDns([
            .. records.Select(x => x.Role == MailDnsRecords.Dkim
                ? (x.Name, x.Kind, "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()))
                : (x.Name, x.Kind, x.Value))
        ]);

        MailDnsRecords.Verify(records, zone.Answer, out var checks).ShouldBeFalse();

        checks.Single(static x => x.Record.Role == MailDnsRecords.Dkim).Status.ShouldBe(MailDnsRecords.Mismatch);
    }

    [Fact]
    public void TwoSpfRecordsAreAMismatchBecauseEveryReceiverTreatsThemAsAnError() {
        var records = Required();
        var zone = new ZoneDns([
            .. records.Select(static x => (x.Name, x.Kind, x.Value)),
            ("example.com", "TXT", "v=spf1 include:_spf.google.com ~all")
        ]);

        MailDnsRecords.Verify(records, zone.Answer, out var checks).ShouldBeFalse();

        checks.Single(static x => x.Record.Role == MailDnsRecords.Spf).Status.ShouldBe(MailDnsRecords.Mismatch);
    }

    [Fact]
    public void AnSpfRecordThatAlsoNamesAnotherSenderStillVerifies() {
        // ⚠ Exactly-one-record, not exactly-this-value. A tenant who also sends through somebody else
        // has to say so in the same record, and refusing that would make the gate unpassable for them.
        var records = Required();
        var zone = new ZoneDns([
            .. records.Where(static x => x.Role != MailDnsRecords.Spf).Select(static x => (x.Name, x.Kind, x.Value)),
            ("example.com", "TXT", "v=spf1 include:_spf.cybercloud.example include:_spf.google.com -all"),
            ("example.com", "TXT", "google-site-verification=abc")
        ]);

        MailDnsRecords.Verify(records, zone.Answer, out _).ShouldBeTrue();
    }

    [Fact]
    public void TheMxAndTheTlsRecordsDoNotHoldSending() {
        var records = Required();
        var zone = new ZoneDns([
            .. records.Where(static x => x.GatesSending).Select(static x => (x.Name, x.Kind, x.Value))
        ]);

        MailDnsRecords.Verify(records, zone.Answer, out var checks)
            .ShouldBeTrue("a domain that publishes SPF, DKIM and DMARC may send before it receives");

        checks.Where(static x => !x.Record.GatesSending).ShouldAllBe(static x => x.Status == MailDnsRecords.Missing);
    }

    [Fact]
    public void AZoneFileSplitsTheDkimKeyIntoStringsOfAtMost255Bytes() {
        // RFC 1035 § 3.3: one character-string carries at most 255 bytes. A 2048-bit key's record is
        // about 410, so a line that did not split would be refused by every zone loader.
        var dkim = MailDnsRecords.ZoneFile(Required()).Split('\n').Single(static x => x.StartsWith("cc._domainkey", StringComparison.Ordinal));
        var strings = dkim[(dkim.IndexOf('"', StringComparison.Ordinal) + 1)..^1].Split("\" \"");

        strings.Length.ShouldBe(2);
        strings.ShouldAllBe(static x => x.Length <= 255);
        string.Concat(strings).ShouldBe(Required().Single(static x => x.Role == MailDnsRecords.Dkim).Value);
    }

    [Fact]
    public async Task ANewDomainRendersSendingHeldAndDefersForwardedMail() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var outcome = await MailHarness.Reconciler()
            .ReconcileAsync(MailHarness.Context(connection, body.RootElement), TestContext.Current.CancellationToken);

        outcome.IsConverged.ShouldBeTrue("a domain whose records are not published still receives mail");

        var mainCf = MainCf(connection);

        mainCf.ShouldContain(
            "smtpd_relay_restrictions = " + MailDomains.RelayRestrictions("example.com", MailSending.Held) + "\n"
        );

        // ⚠ THE HALF A SUBMISSION TEST CANNOT SEE. A forward is expanded after the recipient was
        // accepted as local, so only the transport stops it from leaving a held domain.
        mainCf.ShouldContain("defer_transports = smtp\n");
    }

    [Fact]
    public async Task PublishingTheRecordsOpensTheGateOnTheNextPass() {
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await MailHarness.Reconciler().ReconcileAsync(context, TestContext.Current.CancellationToken);

        // The tenant publishes what they were told, for the key the vault actually holds.
        var pem = vault.Peek(MailDomains.SecretPath(context.Id), MailDomains.DkimPrivateKeyField)!;
        MailDnsRecords.TryRequired("example.com", pem, MailHarness.Platform, out var records).ShouldBeTrue();

        await MailHarness.Reconciler(ZoneDns.Publishing(records))
            .ReconcileAsync(context, TestContext.Current.CancellationToken);

        var mainCf = MainCf(connection);

        mainCf.ShouldContain("smtpd_relay_restrictions = permit_sasl_authenticated, reject_unauth_destination\n");
        mainCf.ShouldNotContain("defer_transports");
    }

    [Fact]
    public async Task ASuspendedTenantSendsNothingEvenWithPerfectRecords() {
        // ⚠ Suspension wins over verification. doc 17 § Deliverability's abuse desk has to be able to
        // stop a tenant whose records are impeccable — those are the tenants whose spam lands.
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await MailHarness.Reconciler().ReconcileAsync(context, TestContext.Current.CancellationToken);

        var pem = vault.Peek(MailDomains.SecretPath(context.Id), MailDomains.DkimPrivateKeyField)!;
        MailDnsRecords.TryRequired("example.com", pem, MailHarness.Platform, out var records).ShouldBeTrue();

        await MailHarness.Reconciler(
                ZoneDns.Publishing(records),
                MailHarness.Platform with { SuspendedTenants = [MailHarness.TenantA] }
            )
            .ReconcileAsync(context, TestContext.Current.CancellationToken);

        var mainCf = MainCf(connection);

        mainCf.ShouldContain("is suspended by the platform's abuse desk");
        mainCf.ShouldContain("defer_transports = smtp\n");
    }

    [Fact]
    public async Task AnUnreachableResolverHoldsSendingAndStillConverges() {
        // ⚠ Fail closed, never fail the pass: receiving mail does not wait on the DNS.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var outcome = await MailHarness.Reconciler(new ZoneDns([]) { Unreachable = true })
            .ReconcileAsync(MailHarness.Context(connection, body.RootElement), TestContext.Current.CancellationToken);

        outcome.IsConverged.ShouldBeTrue();
        MainCf(connection).ShouldContain("is held until its SPF, DKIM and DMARC records verify");
    }

    [Fact]
    public async Task AnUnansweredDnsNeverClosesAGateThatIsOpen() {
        // ⚠ THE #34 REVIEW'S LOW FINDING. A verified domain met one pass whose resolver timed out, and
        // the first cut rendered that Held — the same as records withdrawn — so the domain's outbound
        // mail waited until the tenant happened to call verify. An unanswered question is not an
        // answer: the open gate stays open, from a pass and from verify alike. An ANSWER that says the
        // record is gone still closes it on the spot.
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await MailHarness.Reconciler().ReconcileAsync(context, TestContext.Current.CancellationToken);

        var pem = vault.Peek(MailDomains.SecretPath(context.Id), MailDomains.DkimPrivateKeyField)!;
        MailDnsRecords.TryRequired("example.com", pem, MailHarness.Platform, out var records).ShouldBeTrue();

        await MailHarness.Reconciler(ZoneDns.Publishing(records)).ReconcileAsync(context, TestContext.Current.CancellationToken);
        MainCf(connection).ShouldContain(Gate(MailSending.Open));

        var silent = new ZoneDns([]) { Unreachable = true };

        (await MailHarness.Reconciler(silent).ReconcileAsync(context, TestContext.Current.CancellationToken))
            .IsConverged.ShouldBeTrue();
        MainCf(connection).ShouldContain(Gate(MailSending.Open), customMessage: "one unanswered pass closed a verified domain");

        var verified = await new MailVerifyHandler(MailHarness.Platform, silent).InvokeAsync(
            new(context.Id, context.ApiVersion, MailDomains.VerifyAction, default, body.RootElement, context.Namespace, connection, vault),
            TestContext.Current.CancellationToken
        );
        JsonNode.Parse(verified.GetValueOrThrow())!["sendingEnabled"]!.GetValue<bool>().ShouldBeTrue();
        MainCf(connection).ShouldContain(Gate(MailSending.Open));

        // An answer — the DKIM record is gone — closes it.
        var withdrawn = new ZoneDns([
            .. records.Where(static x => x.Role != MailDnsRecords.Dkim).Select(static x => (x.Name, x.Kind, x.Value))
        ]);

        await MailHarness.Reconciler(withdrawn).ReconcileAsync(context, TestContext.Current.CancellationToken);
        MainCf(connection).ShouldContain(Gate(MailSending.Held));

        // ⚠ And a domain that was never open is not opened by silence: fail closed where nothing was verified.
        await MailHarness.Reconciler(silent).ReconcileAsync(context, TestContext.Current.CancellationToken);
        MainCf(connection).ShouldContain(Gate(MailSending.Held));
    }

    [Fact]
    public async Task VerifyOpensTheGateByReapplyingTheConfigMapAndReportsEveryRecord() {
        // ⚠ How a converged domain learns the DNS changed: nothing reconciles it again by itself, so
        // verify decides the same way the reconciler does and re-applies the ConfigMap.
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await MailHarness.Reconciler().ReconcileAsync(context, TestContext.Current.CancellationToken);

        var pem = vault.Peek(MailDomains.SecretPath(context.Id), MailDomains.DkimPrivateKeyField)!;
        MailDnsRecords.TryRequired("example.com", pem, MailHarness.Platform, out var records).ShouldBeTrue();

        var result = await new MailVerifyHandler(MailHarness.Platform, ZoneDns.Publishing(records)).InvokeAsync(
            new(context.Id, context.ApiVersion, MailDomains.VerifyAction, default, body.RootElement, context.Namespace, connection, vault),
            TestContext.Current.CancellationToken
        );

        var answer = JsonNode.Parse(result.GetValueOrThrow())!;

        answer["sendingEnabled"]!.GetValue<bool>().ShouldBeTrue();
        answer["sending"]!.GetValue<string>().ShouldBe("open");

        foreach (var role in MailDnsRecords.Roles) {
            answer["records"]![role]!["status"]!.GetValue<string>().ShouldBe(MailDnsRecords.Verified);
        }

        MainCf(connection).ShouldContain("permit_sasl_authenticated, reject_unauth_destination");
    }

    [Fact]
    public async Task DnsRecordsIsRefusedInARegionWithNoMailHostsRatherThanInvented() {
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await MailHarness.Reconciler().ReconcileAsync(context, TestContext.Current.CancellationToken);

        var result = await new MailDnsRecordsHandler(new MailPlatformOptions()).InvokeAsync(
            new(context.Id, context.ApiVersion, MailDomains.DnsRecordsAction, default, body.RootElement, context.Namespace, connection, vault),
            TestContext.Current.CancellationToken
        );

        result.IsFailure.ShouldBeTrue();
        result.Error!.Message.ShouldContain(MailPlatformOptions.Section);
    }

    static string Gate(MailSending sending) =>
        "smtpd_relay_restrictions = " + MailDomains.RelayRestrictions("example.com", sending) + "\n";

    /// <summary>The <c>main.cf</c> of the last <c>ConfigMap</c> applied.</summary>
    static string MainCf(RecordingConnection connection) =>
        JsonNode.Parse(connection.Applied.Last(static x => x.Target.Kind.Kind == "ConfigMap").Body)!["data"]![
            MailDomains.PostfixMainCfKey]!.GetValue<string>();
}
