using CyberCloud.Providers.Mail.Dns;
using System.Collections.Immutable;

namespace CyberCloud.Providers.Mail;

/// <summary>What deciding the sending gate found: the gate, the per-record checks, and why.</summary>
/// <param name="Sending">The gate.</param>
/// <param name="Checks">One check per required record, or empty when nothing could be required.</param>
/// <param name="Reason">A sentence for the reconcile log and the refusal.</param>
public readonly record struct MailDeliverabilityDecision(
    MailSending Sending,
    ImmutableArray<MailDnsCheck> Checks,
    string Reason
);

/// <summary>
///     Decides doc 17 § Deliverability's gate for one domain: resolve the required records, compare
///     them, and let the abuse desk's suspension win over both.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>ONE DECISION, TWO CALLERS, AND THEY MUST NOT DISAGREE.</b> The reconciler decides on
///         every pass and renders the result into <c>main.cf</c>; <c>verify</c> decides on demand
///         and re-applies the same <c>ConfigMap</c> when the answer moved. A caller that decided
///         differently would flap the gate between two passes over an unchanged zone — so both call
///         this, and nothing else decides.
///     </para>
///     <para>
///         ⚠ <b>Fail closed, and never fail the pass.</b> An unconfigured region, a key that does not
///         parse, a resolver that times out: each is <see cref="MailSending.Held" /> with the reason,
///         and the domain still converges — receiving mail does not wait on the DNS.
///     </para>
/// </remarks>
public static class MailDeliverability {
    /// <summary>Resolves, compares and decides.</summary>
    /// <param name="tenantId">The domain's tenant, for the suspension seam.</param>
    /// <param name="domain">The mail domain.</param>
    /// <param name="dkimPrivateKeyPem">The key the vault holds.</param>
    /// <param name="platform">The platform's hosts and suspensions.</param>
    /// <param name="dns">The resolver.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <remarks>
    ///     ⚠ <b>The seven questions are asked at once.</b> Clause 3 of docs/plan/08 § The reconcile
    ///     loop gives a pass thirty seconds, and seven sequential questions at a three-second timeout
    ///     against two unreachable nameservers would spend most of it.
    /// </remarks>
    public static async Task<MailDeliverabilityDecision> DecideAsync(
        Guid tenantId,
        string domain,
        string dkimPrivateKeyPem,
        MailPlatformOptions platform,
        IMailDnsResolver dns,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(dns);

        if (platform.Suspends(tenantId)) {
            return new(MailSending.Suspended, [], "the platform's abuse desk has suspended this tenant's outbound mail");
        }

        if (!platform.IsConfigured) {
            return new(
                MailSending.Held,
                [],
                "the platform's mail hosts are not configured under " + MailPlatformOptions.Section
                + ", so there are no records to verify"
            );
        }

        if (!MailDnsRecords.TryRequired(domain, dkimPrivateKeyPem, platform, out var records)) {
            return new(MailSending.Held, [], "the DKIM key in the vault does not parse, so no DKIM record can verify");
        }

        var answers = await Task.WhenAll(records.Select(x => dns.QueryAsync(x.Name, x.Kind, cancellationToken)));
        var byRecord = records.Zip(answers).ToDictionary(static x => x.First, static x => x.Second);
        var verified = MailDnsRecords.Verify(records, x => byRecord[x], out var checks);

        return new(
            verified ? MailSending.Open : MailSending.Held,
            checks,
            verified
                ? "SPF, DKIM and DMARC verify"
                : "not verified: " + string.Join(
                    ", ",
                    checks.Where(static x => x.Record.GatesSending && x.Status != MailDnsRecords.Verified)
                        .Select(static x => x.Record.Role + " " + x.Status)
                )
        );
    }
}
