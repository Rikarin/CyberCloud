using CyberCloud.ResourceManager.Conformance;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The DKIM identity: that it is minted once, that what is rendered is what the vault holds, and
///     that the record a tenant publishes is the public half of the key the signer uses.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS FILE IS THE REASON THIS TYPE HAS A HAND-WRITTEN SUITE AT ALL.</b> The shared
///         conformance suite drives one create, so it cannot see a second pass — and a second pass is
///         the only place the failure this file is about can appear.
///     </para>
///     <para>
///         ⚠ <b>THE FAILURE IS SILENT IN A WAY ALMOST NOTHING ELSE IN THIS PLATFORM IS.</b> A
///         reconciler that regenerated the key each pass would still converge: the <c>Secret</c>
///         settles within seconds, every object reads back, the resource reports <c>Succeeded</c>,
///         and no gate anywhere goes red. What breaks is outside the cluster — the public key the
///         tenant published in DNS now matches nothing, so every message the domain sends fails DKIM
///         at every receiver, and the platform's own status page says the domain is healthy. There is
///         no observation inside this system that would catch it, which is why it is asserted here
///         rather than left to the loop.
///     </para>
/// </remarks>
public sealed class MailDkimTests {
    [Fact]
    public async Task ASecondPassRendersTheSameDkimKeyAsTheFirst() {
        // ⚠ THE ASSERTION THIS TYPE MOST NEEDS. MailDomains.GenerateCredentials returns a NEW keypair
        // on every call — it has to, or a domain's signing key would be derivable from its address —
        // so the only thing standing between this platform and a key that changes per pass is that
        // the reconciler renders what it RESOLVED rather than what it generated.
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var reconciler = new MailDomainReconciler(new FixedClock());
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);

        var first = SecretBody(connection);

        await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);

        var second = SecretBody(connection);

        second.ShouldBe(
            first,
            "the second pass rendered a different DKIM key. The Secret converges either way, so "
            + "nothing in this platform would report it — and every message the domain sends would "
            + "fail DKIM at every receiver against the public key already in its DNS."
        );

        // ⚠ And the vault was written ONCE. Without this, a reconciler that overwrote the document
        // and then read it back would pass the assertion above — both passes would render the
        // SECOND key, agreeing with each other and with nothing the tenant published.
        vault.Writes.ShouldBe(1, "the vault was written more than once; cas=0 was not honoured");
    }

    [Fact]
    public async Task WhatIsRenderedIsWhatTheVaultHeldBeforeThePassRan() {
        // ⚠ THE STRONGER HALF, AND IT FAILS ON A MISTAKE THE TEST ABOVE CANNOT SEE. A reconciler
        // that generated a key, minted it, and rendered THE GENERATED ONE would be correct on the
        // first pass and correct on every later pass too — because cas=0 makes the mint a no-op and
        // its own generated value happens to be discarded. Seeding the vault FIRST, with a key this
        // test knows, is what distinguishes "rendered the resolved value" from "rendered the
        // generated value that happened to be the resolved one".
        var vault = new InMemorySecretVault();
        var address = MailHarness.Address("seeded-example", MailHarness.TenantA, MailHarness.SubscriptionA);

        using var known = RSA.Create(MailDomains.DkimKeyBits);
        var knownPem = known.ExportPkcs8PrivateKeyPem();

        await vault.MintAsync(
            MailDomains.SecretPath(address),
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [MailDomains.DkimPrivateKeyField] = knownPem,
                [MailDomains.MasterPasswordField] = "seeded-master-password"
            },
            TestContext.Current.CancellationToken
        );

        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        await new MailDomainReconciler(new FixedClock()).ReconcileAsync(
            MailHarness.Context(connection, body.RootElement, vault, address),
            TestContext.Current.CancellationToken
        );

        // ⚠ Parsed rather than substring-matched. A PEM carries newlines and the applied body is
        // JSON, so the rendered key is `\n`-escaped — a raw substring check could never match, and
        // would fail identically whether the reconciler was right or wrong.
        SecretField(connection, MailDomains.DkimPrivateKeyField).ShouldBe(
            knownPem,
            "the rendered Secret does not carry the key that was already in the vault, so the "
            + "reconciler rendered what it generated rather than what it resolved"
        );
    }

    [Fact]
    public void ThePublishedRecordIsThePublicHalfOfTheSigningKey() {
        // ⚠ THE OTHER END OF THE SAME CLAIM. The two tests above prove the signer keeps one key; this
        // proves the record a tenant is told to publish belongs to THAT key. A platform could hold
        // the key perfectly still and hand out a p= tag derived from something else, and the symptom
        // would be identical: DKIM failures at every receiver, nothing red anywhere.
        using var key = RSA.Create(MailDomains.DkimKeyBits);
        var pem = key.ExportPkcs8PrivateKeyPem();

        MailDomains.TryRequiredRecords("example.com", pem, "mx.cybercloud.io", out var records)
            .ShouldBeTrue();

        var dkim = records.Single(static x => x.Name.StartsWith(MailDomains.DkimSelector, StringComparison.Ordinal));

        dkim.Value.ShouldContain(
            "p=" + Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Case.Sensitive,
            "the published p= tag is not the public half of this private key"
        );
    }

    [Fact]
    public void TheFourRecordsAreTheFourDeliverabilityRequires() {
        // docs/plan/17 § Deliverability names SPF, DKIM, DMARC and reverse DNS, and § Resource model
        // adds the MX. ⚠ PTR is NOT here and cannot be: it is a record on the OUTBOUND IP's reverse
        // zone, which belongs to whoever owns the address block, not to the tenant's domain. A
        // tenant cannot publish it and this platform must — charts/managed/mail/conformance.yaml
        // § owed, outbound-pools-and-warm-up.
        using var key = RSA.Create(MailDomains.DkimKeyBits);

        MailDomains.TryRequiredRecords(
            "example.com",
            key.ExportPkcs8PrivateKeyPem(),
            "mx.cybercloud.io",
            out var records
        )
            .ShouldBeTrue();

        records.Length.ShouldBe(4);
        records.Count(static x => x.Kind == "MX").ShouldBe(1);
        records.Count(static x => x.Kind == "TXT").ShouldBe(3);

        // ⚠ `-all` and not `~all`. A soft fail asks the receiver to accept a forgery and mark it,
        // which hands an attacker delivery as this domain — and makes the "will not send until the
        // records verify" gate protect nothing.
        records.Single(static x => x.Value.StartsWith("v=spf1", StringComparison.Ordinal))
            .Value.ShouldEndWith("-all");
    }

    [Fact]
    public void AnEmptyOrCorruptKeyIsRefusedRatherThanPublishedAsAnEmptyTag() {
        // ⚠ THE FAILURE THIS GUARDS IS A RECORD THAT LOOKS VALID. `v=DKIM1; k=rsa; p=` is
        // well-formed and means "this key is revoked" to a receiver — so deriving a record from a
        // key that did not parse would publish a REVOCATION rather than an error.
        MailDomains.TryDkimPublicKey(string.Empty, out var empty).ShouldBeFalse();
        empty.ShouldBeEmpty();

        MailDomains.TryDkimPublicKey("-----BEGIN PRIVATE KEY-----\nnonsense\n-----END PRIVATE KEY-----", out _)
            .ShouldBeFalse();

        MailDomains.TryRequiredRecords("example.com", string.Empty, "mx.cybercloud.io", out var records)
            .ShouldBeFalse();

        records.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoTenantsWithTheSameDomainNameGetDifferentKeys() {
        // ⚠ THE CROSS-TENANT SHAPE, AND A RECONCILER IS A SINGLETON SO THIS IS NOT HYPOTHETICAL. One
        // instance serves every tenant in the process; a field caching a resolved key would hand
        // tenant B tenant A's signing identity, which is the worst instance of the clause-2 failure
        // class in the catalogue — B could sign as A.
        var vault = new InMemorySecretVault();
        var reconciler = new MailDomainReconciler(new FixedClock());
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var a = new RecordingConnection();
        var b = new RecordingConnection();

        await reconciler.ReconcileAsync(
            MailHarness.Context(
                a,
                body.RootElement,
                vault,
                MailHarness.Address("example-com", MailHarness.TenantA, MailHarness.SubscriptionA)
            ),
            TestContext.Current.CancellationToken
        );

        await reconciler.ReconcileAsync(
            MailHarness.Context(
                b,
                body.RootElement,
                vault,
                MailHarness.Address(
                    "example-com",
                    MailHarness.TenantB,
                    MailHarness.SubscriptionA,
                    Guid.Parse("33333333-3333-4333-8333-444444444444")
                )
            ),
            TestContext.Current.CancellationToken
        );

        SecretBody(a).ShouldNotBe(
            SecretBody(b),
            "two tenants were handed the same DKIM key, so either could sign mail as the other"
        );
    }

    [Fact]
    public async Task NoObjectButTheSecretCarriesTheKeyOrThePassword() {
        // ⚠ A ConfigMap is readable by anyone holding `get configmaps`, which is a strictly weaker
        // right than `get secrets`. The rendered Postfix and Rspamd configuration REFERENCES the key
        // by path; it must never contain it.
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await new MailDomainReconciler(new FixedClock())
            .ReconcileAsync(context, TestContext.Current.CancellationToken);

        foreach (var field in MailDomains.CredentialFields) {
            var value = vault.Peek(MailDomains.SecretPath(context.Id), field);

            value.ShouldNotBeNull($"'{field}' was not minted at all");

            foreach (var command in connection.Applied.Where(static x => x.Target.Kind.Kind != "Secret")) {
                command.Body.ShouldNotContain(
                    value,
                    Case.Sensitive,
                    $"'{command.Target.Name}' carries the value of '{field}' rather than a reference "
                    + "to the Secret that holds it"
                );
            }
        }
    }

    /// <summary>One field of the <c>Secret</c> the pass applied, decoded out of the document.</summary>
    static string SecretField(RecordingConnection connection, string field) {
        var document = JsonNode.Parse(SecretBody(connection))!.AsObject();

        return document["stringData"]?[field]?.GetValue<string>()
            ?? throw new InvalidOperationException($"the applied Secret carries no '{field}'");
    }

    /// <summary>The body of the one <c>Secret</c> the pass applied.</summary>
    static string SecretBody(RecordingConnection connection) {
        var applied = connection.Applied.LastOrDefault(static x => x.Target.Kind.Kind == "Secret");

        applied.ShouldNotBeNull("no Secret was applied at all");

        return applied.Body;
    }
}
