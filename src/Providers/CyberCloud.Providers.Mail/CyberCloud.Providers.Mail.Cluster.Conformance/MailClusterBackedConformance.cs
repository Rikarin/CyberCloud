using CyberCloud.Cluster.Conformance;
using CyberCloud.Cluster.Conformance.Infrastructure;
using CyberCloud.Providers.Mail.Conformance;

namespace CyberCloud.Providers.Mail.ClusterConformance;

/// <summary>The cluster-backed suite, run against the managed-mail provider.</summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             FOUR OF THE FIVE OBJECTS ARE CORE API, so a green run here is genuine evidence that
///             the objects exist — and no evidence at all that the harness' CRD derivation works.
///         </b> A
///         Secret, a ConfigMap, a Service and a StatefulSet are real in a bare k3s; only the
///         <c>PodMonitor</c> needs a definition derived from the case's own <c>Objects</c>.
///     </para>
///     <para>
///         ⚠ <b>AND THE STATEFULSET IS THE ONE OBJECT HERE WHOSE GREEN IS MOST MISLEADING.</b> The
///         API server accepts and stores a <c>StatefulSet</c> whose containers reference images that
///         do not exist; the pod then sits in <c>ImagePullBackOff</c> forever. Every assertion in
///         this suite is about the applied documents, so all of them pass over a mail domain that
///         cannot start — and <c>docker.io/cybercloud/dovecot</c>,
///         <c>docker.io/cybercloud/postfix</c> and <c>docker.io/cybercloud/rspamd</c>
///         <b>are not built by anything in this repository</b>. That is
///         <c>charts/managed/mail/conformance.yaml § owed</c>, <c>the-images-do-not-exist</c>, and it
///         is the reason a green run of this project must not be read as "managed mail works".
///     </para>
/// </remarks>
/// <param name="fixture">The harness.</param>
public sealed class MailDomainLifecycleConformance(ClusterConformanceFixture<MailDomainCase> fixture)
    : ClusterConformanceTests<MailDomainCase>(fixture), IClassFixture<ClusterConformanceFixture<MailDomainCase>>;

/// <summary>docs/plan/24 § Phase 1's exit criterion 3, against the managed-mail provider.</summary>
public sealed class MailDomainSiloKillConformance : SiloKillConformanceTests<MailDomainCase>;
