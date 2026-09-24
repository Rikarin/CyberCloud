using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Mail.Conformance;

namespace CyberCloud.Providers.Mail.ClusterConformance;

/// <summary>The cluster-backed suite, run against the managed-mail provider.</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             FIVE OF THE SIX OBJECTS ARE CORE API, so a green run here is genuine evidence that
///             the objects exist — and no evidence at all that the harness' CRD derivation works.
///         </b> Two Secrets, a ConfigMap, a Service and a StatefulSet are real in a bare k3s; only the
///         <c>PodMonitor</c> needs a definition derived from the case's own <c>Objects</c>.
///     </para>
///     <para>
///         ⚠ <b>THIS SUITE IS ABOUT THE DOCUMENTS; <c>MailDeliveryOnK3sTests</c> IS ABOUT THE MAIL.</b>
///         Until issue #34's second pass the StatefulSet here named three images nothing built, and a
///         green run of this class was the only evidence the type had — over a pod that could never
///         start. The images are real now, and whether the pod they make accepts, signs, delivers
///         and serves mail is asserted by that class, on its own cluster, through the same write path.
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class MailDomainLifecycleConformance(ClusterConformanceFixture<MailDomainCase> fixture)
    : ClusterConformanceTests<MailDomainCase>(fixture), IClassFixture<ClusterConformanceFixture<MailDomainCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-mail provider.</summary>
public sealed class MailDomainSiloKillConformance : SiloKillConformanceTests<MailDomainCase>;

// ⚠ NO ClusterConformanceTests<MailMailboxCase>, AND THE PEERING SAYS WHY. That class asserts that
// cybercloud.io/resource-id is the resource's own on every object and provokes a conflict on a field
// "we own" — the opposite reading for a second writer, whose one object is its domain's.
// charts/managed/kube-ovn-vpc-peering/conformance.yaml § owed, `the-shared-cluster-suite-presumes-ownership`,
// has the fix that would make it one line here. Until then the mailbox's co-write against a real API
// server is MailDeliveryOnK3sTests', which writes three mailboxes into one Secret and then logs in as
// them; the silo kill below presumes nothing about ownership and runs as the shared class.

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the mailbox child type.</summary>
public sealed class MailMailboxSiloKillConformance : SiloKillConformanceTests<MailMailboxCase>;
