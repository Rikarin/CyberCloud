namespace CyberCloud.Conformance;

/// <summary>
///     A signpost: the half of the conformance suite that needs real infrastructure now <b>runs</b>,
///     and it runs somewhere else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This class used to be five <c>Assert.Skip</c>s and a debt.</b> docs/plan/03
///         § Providers lists five things every provider must pass and docs/plan/24 § Phase 1 turns
///         three of them into exit criteria; two could not be honestly claimed against
///         <c>Orleans.TestingHost</c> with in-memory storage and a dictionary for an API server. All
///         five are now asserted for real, against a k3s container, a PostgreSQL container and a
///         Redis container, in <c>test/CyberCloud.Cluster.Conformance</c> — and per provider, in that
///         provider's own <c>.Cluster.Conformance</c> project, which is two class declarations.
///     </para>
///     <para>
///         ⚠ <b>Why a signpost survives at all, rather than the file being deleted.</b> This project
///         must keep running on a machine with no Docker — it is the per-PR gate for every provider
///         change, and putting <c>Testcontainers</c> in its <c>.csproj</c> would make the twelve-step
///         write path, the verb grammar, the four reconciler clauses and the cross-tenant 404 all
///         unrunnable without a daemon. So the cluster-backed assertions are in another assembly that
///         this one cannot reference, which means a Docker-free run of this suite has no way to
///         mention them. One visible skip is how it mentions them. An absence would let a reader of
///         "conformance: green" conclude that this suite is the whole of docs/plan/03 § Providers,
///         which it is not and never was.
///     </para>
/// </remarks>
/// <param name="case">The provider under test — named in the message so the output is useful.</param>
public abstract class ClusterBackedConformanceTests(ProviderConformanceCase @case) {
    /// <summary>The provider under test.</summary>
    protected ProviderConformanceCase Case { get; } = @case;

    [Fact]
    [Trait("Requires", "cluster")]
    public void TheClusterBackedHalfOfThisSuiteRunsInItsOwnProject() {
        Assert.Skip(
            $"SKIPPED HERE, AND NOT SKIPPED — {Case.DisplayName}'s five cluster-backed criteria run "
            + "in CyberCloud.Cluster.Conformance (and, for a provider, in its own "
            + "<Provider>.Cluster.Conformance project), against a real k3s API server, a real "
            + "PostgreSQL durable tier and a real Redis reminder table. They are: the lifecycle "
            + "against a real API server; drift corrected after a real kubectl delete; a field "
            + "conflict with another manager becoming a named DriftEvent; desired state surviving a "
            + "real serialization round trip; and killing the silo mid-create still converging — "
            + "docs/plan/24 § Phase 1's exit criterion 3. "
            + "This project deliberately references no Testcontainers package, because a package "
            + "reference here would make THIS suite — the per-PR gate for every provider change — "
            + "refuse to run without a Docker daemon. That is the whole reason the two halves are two "
            + "projects, and this message is the only way a Docker-free run of this half can say so."
        );
    }
}
