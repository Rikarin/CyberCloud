// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.Mail;

/// <summary>
///     Managed mail — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         [17 § <c>CyberCloud.Mail</c>](../../../../docs/plan/17-communication-and-email.md) · M2 ·
///         3.5 EM, of which this is the back end and its DKIM identity. See
///         <see cref="MailDomains" /> for what a domain is, what it renders, and — at length — what
///         this row does <b>not</b> build and why each omission is a different kind.
///     </para>
///     <para>
///         ⚠ <b>THE ONLY TYPE IN THE CATALOGUE THAT DECLARES NO ACTION, AND THE REASON IS A RULE
///         RATHER THAN A GAP.</b> doc 17 § Resource model names three — <c>verify</c>,
///         <c>sendTest</c> and <c>exportMailbox</c>. <c>actions-without-handlers.txt</c> allows a
///         declared action with no handler <i>only</i> when the action's api-version is already
///         published, because a published api-version is immutable and removing a path from one is a
///         breaking change; <c>2026-08-01</c> of this type is published <b>by this change</b>, so
///         that door is shut and the file's own words apply: <i>"'No handler yet' on an unpublished
///         action is not a reason to add a line; it is a reason to write the handler or not declare
///         the action."</i> None of the three can be written yet — see <see cref="MailDomains" /> —
///         so none is declared. ⚠ <b>This costs nothing that matters</b>: the half of <c>verify</c>
///         a tenant actually needs, <i>the exact records to publish</i>, is
///         <see cref="MailDomains.TryRequiredRecords" />, a pure function reachable without an action
///         at all.
///     </para>
///     <para>
///         ⚠ <b>NO <c>SupportsSoftDelete</c>, AND ON THIS TYPE THAT IS AN ARGUMENT RATHER THAN A
///         DEFERRAL.</b> docs/plan/08 § Soft delete is built and a window is now a one-line
///         declaration, so the question is the provider's own: does the data deserve a recovery
///         window? For a mail domain the answer is that a <i>window is the wrong instrument</i>. A
///         soft-deleted resource keeps its name and its quota and answers 404 at its old address —
///         which for a mail domain means the MX still points at a pool that will no longer accept
///         for it, so mail bounces for the length of the window and then the volume is purged
///         anyway. What protects the data here is <see cref="MailDomains.RetainedClaims" />, which
///         is unconditional: the mail store outlives the resource with no window to expire and no
///         flag to turn it off. That is a stronger guarantee than seven days, not a weaker one.
///     </para>
///     <para>
///         ⚠ <b>What this provider owes against docs/plan/12 § The pattern, once.</b> Piece 5 —
///         credential provisioning into the tenant's vault — is built and is load-bearing here in a
///         way it is on no other type: <see cref="MailDomains.GenerateCredentials" /> mints a DKIM
///         keypair that <b>must never change</b>, so the mint-once rule is the difference between a
///         working domain and one whose mail silently fails DKIM at every receiver. Piece 6 — the
///         scrape object — is ours, because none of Postfix, Dovecot or Rspamd has an operator to
///         ask; Rspamd's controller serves the metrics and the <c>PodMonitor</c> is rendered here.
///     </para>
/// </remarks>
public sealed class MailProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => MailDomains.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(MailDomains.TypePath)
            .ApiVersion(MailDomains.V2026, MailDomains.Schema2026)
            .Reconciler<MailDomainReconciler>()
            // ⚠ THREE DERIVED METERS AND THE COUNT, AND THIS IS THE FIRST TYPE WHERE NONE OF THEM
            // MULTIPLIES BY ANYTHING. Every clustered service before this one derives its amounts as
            // a PRODUCT of a replica count and a per-replica quantity, and MeterDerivation exists
            // because a JSON Pointer cannot express a product. A mail back end is one pod by
            // construction — MailDomains.StatefulSetKind says why, and it is the mail store rather
            // than a scaling opinion — so the derivations here are the same shape with a factor of
            // one.
            //
            // ⚠ THAT DOES NOT MAKE A POINTER SUFFICIENT, WHICH IS THE POINT WORTH RECORDING. Every
            // amount is still a Kubernetes QUANTITY STRING and is still usually ABSENT, because
            // sizing.preset names it indirectly. Either alone is enough to defeat
            // `Meter(meter, pointer, fallback)`. So the seam earns its keep on a type that exercises
            // neither of the two properties it was built for.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with — ResourceManagerService.CommittedBy — so a derivation that read
            // a clock or configuration would make a delete return a different number than the create
            // committed, and quota would drift upward on every create/delete cycle.
            .Meter(QuotaMeter.Vcpu, VcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MemoryDrawn)
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            // ⚠ `publicIps` IS NOT DECLARED, AND ON THIS TYPE THAT IS NOT THE GAP THE OTHER
            // PROVIDERS REPORT. Theirs is a real draw that cannot be expressed, because
            // QuotaGrain.TryReserveAsync refuses a reservation of 0 and an optional external
            // listener derives zero on the default path. This type has no external listener at any
            // setting — MailDomains.ServiceJson is ClusterIP unconditionally — so it consumes no
            // public IP and there is no amount to derive. ⚠ `/properties/dedicatedIp` is NOT a
            // counter-example: it is a REQUEST for an outbound address out of a platform-owned pool,
            // subject to a volume threshold and to approval, and the pool is not built. A meter
            // reserving one IP the moment a tenant ticked a box would charge for an address nothing
            // allocated.
            .Permissions("read", "write", "delete")
            .Display(
                "Mail domain",
                "Mail domains",
                // ⚠ NOT `mail`, WHICH IS THE ONE WORD THIS TYPE COULD NOT HAVE. CliEmitter.GroupOf
                // derives a group from the provider namespace's last segment lower-cased, so this
                // type's own group key is `mail` and a short name equal to it gives `cyc mail mail`
                // two meanings. The token dictionary is per PARENT command, so a short name equal to
                // some OTHER group's key would parse cleanly; this one would not.
                shortName: "domain",
                summary: "A managed mail domain on Dovecot, Postfix and Rspamd, with a per-tenant "
                + "mail store, DKIM signing, and the SPF, DKIM, DMARC and MX records the domain "
                + "must publish before the platform will send for it."
            )
            .Chart(MailDomains.ChartName)
            .SupportsTags()
            .RequiresCluster(MailDomains.ClusterIdPointer);
    }

    // ── What a mail domain draws ───────────────────────────────────────────────────────────────

    /// <summary>vCPU: the back end's CPU request, from the explicit override or the preset.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="MailDomains.Presets" /> does not carry —
    ///     which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and which
    ///     is exactly the drift worth failing on when somebody adds a preset to the enum and forgets
    ///     the table.
    ///     <para>
    ///         ⚠ <b>The refusal is load-bearing here.</b>
    ///         <see cref="MailDomains.StatefulSetJson" /> writes no <c>resources</c> block when the
    ///         preset does not resolve, so the branch this meter refuses is the branch that would
    ///         otherwise provision a Burstable pod at quantities nobody chose, against no quota.
    ///     </para>
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "sizing.cpu, in cores, taking sizing.preset when the override is empty",
            ["/properties/sizing/preset", "/properties/sizing/cpu"],
            body => KubeQuantity.TryParse(MailDomains.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(cores)
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: the back end's memory request, in gibibytes.</summary>
    /// <remarks>
    ///     ⚠ <b>ClamAV's signature database is not added in, and it is roughly a gibibyte.</b>
    ///     <c>/properties/filtering/antivirus</c> puts it in the same pod, so it is taken out of the
    ///     container's memory limit — which this meter has already reserved. Adding it separately
    ///     would charge the same gibibyte twice, and the one place a customer would notice is the
    ///     bill. ⚠ What is genuinely owed is the other direction: the schema's own description warns
    ///     that antivirus costs about 1 GiB resident, and nothing stops a tenant enabling it on
    ///     <c>c1.nano</c>, where the pod will be OOM-killed in a loop. That is an admission-time
    ///     check on a combination of two fields, which this platform has no seam for.
    /// </remarks>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "sizing.memory, in GiB, taking sizing.preset when the override is empty",
            ["/properties/sizing/preset", "/properties/sizing/memory"],
            body => KubeQuantity.TryGibibytes(MailDomains.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: the mail volume, in gibibytes.</summary>
    /// <remarks>
    ///     ⚠ <b><c>storage.mailboxQuota</c> is deliberately not summed and is not a second meter.</b>
    ///     It is a per-mailbox ceiling Dovecot enforces <i>inside</i> the volume this meter has
    ///     already reserved, so a domain with ten 1 GiB mailboxes on a 20 GiB volume draws twenty
    ///     gibibytes and not ten. Charging the quota as well would bill the same bytes twice, and
    ///     charging it <i>instead</i> would under-reserve every domain whose mailboxes are not full.
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "storage.size, in GiB",
            ["/properties/storage/size"],
            body => KubeQuantity.TryGibibytes(MailDomains.StorageSize(body), out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("storage", "storage.size")
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a mail domain draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
