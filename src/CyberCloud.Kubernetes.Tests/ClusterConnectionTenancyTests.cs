using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Connections;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The one place tenancy is enforced by code rather than by key — docs/plan/06 § Grain keys.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Grain keys:
///         <i>
///             "<c>IClusterConnectionGrain</c> is a null-tenant grain and
///             that is a deliberate exception … The grain therefore carries the owning tenant as state
///             and checks it on every call, and <c>PlatformCrossTenantAuthorizer</c> explicitly allows
///             the platform → connection edge and logs it.
///             <b>
///                 This is the single place tenancy is
///                 enforced by code rather than by key
///             </b>
///             , and it is called out here so nobody has to
///             discover it."
///         </i>
///     </para>
///     <para>
///         ⚠ Every call below is routed through <see cref="IKubeReacherGrain" /> because a test
///         calling the grain directly is a <i>client</i>, and a client has no
///         <c>IGrainCallContext.SourceId</c> — so a direct call could never exercise the tenant path
///         at all. This is the same trap <c>CrossTenantReachabilityTests</c> § route 7 documents in
///         the tenancy suite.
///     </para>
/// </remarks>
[Collection(KubeClusterSuite.Name)]
public sealed class ClusterConnectionTenancyTests(KubeTestCluster cluster) {
    static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    static int next;

    // ── The refusal ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantBIsRefusedOnAConnectionOwnedByTenantA() {
        // ⚠ THE FAILURE CLASS. The grain key is `cluster/{id:N}` with no tenant in it, so tenant B
        // can address tenant A's connection grain directly and Orleans' separation allows the edge.
        // Only this grain's own check stands between them.
        var clusterId = await AttachedToAsync(TenantA);

        (await cluster.Reacher(TenantA).ReachHealthAsync(clusterId))
            .ShouldBe("Unknown", "the owner reaches its own cluster.");

        (await cluster.Reacher(TenantB).ReachHealthAsync(clusterId))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");
    }

    [Fact]
    public async Task TheRefusalIsNotFoundAndNotAuthorizationFailed() {
        // ⚠ docs/plan/00 § Non-negotiables requires 404 rather than 403 for a caller who may not
        // read a resource, because 403 discloses existence. Since the key carries no tenant, tenant
        // B can enumerate cluster ids; a 403 would turn that into a platform-wide oracle for which
        // cluster ids exist.
        var clusterId = await AttachedToAsync(TenantA);

        var refused = await cluster.Reacher(TenantB).ReachHealthAsync(clusterId);

        refused.ShouldBe($"<{ErrorCode.ResourceNotFound}>");
        refused.ShouldNotContain(ErrorCode.AuthorizationFailed.Value);
    }

    [Fact]
    public async Task AnUnknownClusterAndAForbiddenOneAreIndistinguishable() {
        // The other half of the same property: the two answers must be identical, or the 404 leaks
        // by comparison.
        var owned = await AttachedToAsync(TenantA);
        var neverAttached = NewClusterId();

        var forbidden = await cluster.Reacher(TenantB).ReachHealthAsync(owned);
        var absent = await cluster.Reacher(TenantB).ReachHealthAsync(neverAttached);

        forbidden.ShouldBe(absent);
    }

    [Fact]
    public async Task TenantBIsRefusedOnEveryVerbAndNotJustReads() {
        var clusterId = await AttachedToAsync(TenantA);
        var reacher = cluster.Reacher(TenantB);

        (await reacher.ReachHealthAsync(clusterId)).ShouldBe($"<{ErrorCode.ResourceNotFound}>");
        (await reacher.ReachWatchAsync(clusterId)).ShouldBe($"<{ErrorCode.ResourceNotFound}>");
        (await reacher.ReachApplyAsync(clusterId, CommandFor(TenantB)))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");
        (await reacher.ReachAttachAsync(Descriptor(clusterId, TenantB)))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");
    }

    [Fact]
    public async Task TenantBCannotStealAClusterByReAttachingItToItself() {
        // ⚠ The move that would defeat "check the owner on every call" in one call. If a re-attach
        // could rewrite OwningTenantId without a check, every other check would be decorative.
        var clusterId = await AttachedToAsync(TenantA);

        (await cluster.Reacher(TenantB).ReachAttachAsync(Descriptor(clusterId, TenantB)))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");

        // …and A still owns it.
        (await cluster.Reacher(TenantA).ReachHealthAsync(clusterId)).ShouldBe("Unknown");
    }

    [Fact]
    public async Task TheOwnerItselfCannotHandTheClusterToAnotherTenant() {
        // A transfer is a platform-operator action, not a tenant one — otherwise tenant A could
        // push a cluster (and its running workloads) onto tenant B's bill.
        var clusterId = await AttachedToAsync(TenantA);

        (await cluster.Reacher(TenantA).ReachAttachAsync(Descriptor(clusterId, TenantB)))
            .ShouldBe($"<{ErrorCode.AuthorizationFailed}>");
    }

    // ── The platform edge ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePlatformEdgeIsRefusedWithoutAnOperatorRelation() {
        // The seam denies by default — IClusterOperatorAuthority's remarks say why.
        var clusterId = await AttachedToAsync(TenantA);
        SwitchableClusterOperatorAuthority.Operator = null;

        (await cluster.Reacher(ClusterConnectionGrain.PlatformTenantId).ReachHealthAsync(clusterId))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");
    }

    [Fact]
    public async Task ThePlatformEdgeIsAllowedAndLoggedWithTheOperatorsUserId() {
        // ⚠ docs/plan/06 § Platform administration, row 1: allowed when the caller holds an active
        // platform:root#operator relation, and logged "Always, with the operator's user id".
        // docs/plan/06 § Grain keys names this grain's edge specifically.
        var clusterId = await AttachedToAsync(TenantA);
        SwitchableClusterOperatorAuthority.Operator = "user:ops-7f3c";

        try {
            var reached = await cluster.Reacher(ClusterConnectionGrain.PlatformTenantId)
                .ReachHealthAsync(clusterId);

            reached.ShouldBe("Unknown", "the platform operator reaches the connection.");

            var lines = LogCapture.Lines.Select(x => x.Message).ToList();

            lines.ShouldContain(
                x => x.Contains("user:ops-7f3c", StringComparison.Ordinal)
                    && x.Contains(
                        clusterId.ToString("D", CultureInfo.InvariantCulture),
                        StringComparison.OrdinalIgnoreCase
                    ),
                "the edge must be logged with the operator's user id and the cluster. Lines: "
                + string.Join(" | ", lines.TakeLast(10))
            );
        } finally {
            SwitchableClusterOperatorAuthority.Operator = null;
        }
    }

    [Fact]
    public async Task ADeniedEdgeRaisesASecurityEvent() {
        // docs/plan/06 § Platform administration, row 3: "UnauthorizedAccessException + a security
        // event". The grain returns a Result rather than throwing (docs/plan/00 § Coding standards),
        // so the observable half is the log line — which must name both sides.
        var clusterId = await AttachedToAsync(TenantA);

        await cluster.Reacher(TenantB).ReachHealthAsync(clusterId);

        var errors = LogCapture.Lines
            .Where(x => x.Level >= LogLevel.Error)
            .Select(x => x.Message)
            .ToList();

        errors.ShouldContain(
            x => x.Contains("DENIED", StringComparison.Ordinal)
                && x.Contains(TenantB.ToString("D", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                && x.Contains(TenantA.ToString("D", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase),
            "the denial must name the caller and the owner. Lines: "
            + string.Join(" | ", errors.TakeLast(5))
        );
    }

    // ── The command's own labels ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACommandLabelledForAnotherTenantIsRefusedEvenFromTheOwner() {
        // ⚠ The second, independent check. The ambient check answers "who is calling"; this answers
        // "whose object is this". They come apart when a platform operator, legitimately reaching
        // in, applies a command carrying the wrong tenant's labels — and cybercloud.io/tenant-id is
        // what billing and orphan detection join on, so an object in the wrong cluster with the
        // right labels is worse than a refused write.
        var clusterId = await AttachedToAsync(TenantA);

        (await cluster.Reacher(TenantA).ReachApplyAsync(clusterId, CommandFor(TenantB)))
            .ShouldBe($"<{ErrorCode.AuthorizationFailed}>");

        (await cluster.Reacher(TenantA).ReachApplyAsync(clusterId, CommandFor(TenantA)))
            .ShouldNotBe($"<{ErrorCode.AuthorizationFailed}>");
    }

    // ── Fail-closed ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnattachedConnectionRefusesEverything() {
        var clusterId = NewClusterId();

        (await cluster.Reacher(TenantA).ReachHealthAsync(clusterId))
            .ShouldBe($"<{ErrorCode.ResourceNotFound}>");
    }

    [Fact]
    public async Task TheDescriptorAndTheKeyMustAgree() {
        // A descriptor naming a different cluster would put one cluster's credentials behind
        // another's key.
        var clusterId = NewClusterId();
        var wrong = Descriptor(NewClusterId(), TenantA);

        var outcome = await cluster.Connection(clusterId).AttachAsync(wrong);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public void TheCallerIsUnknownAndThereforeRefusedWhenNoFilterHasRun() {
        // ⚠ Fail-closed, asserted directly. If ClusterConnectionTenantFilter were not registered,
        // CallerTenant.Current would be Unknown on every call — and Unknown must be a refusal, not a
        // pass. A security control that fails open when its plumbing is missing is worse than none,
        // because it looks present.
        CallerTenant.Unknown.Kind.ShouldBe(CallerKind.Unknown);
        default(CallerTenant).Kind.ShouldBe(CallerKind.Unknown);
    }

    [Fact]
    public async Task AClientCallIsRefusedBecauseNothingElseChecksIt() {
        // ⚠ Orleans' tenant separation returns early for a non-grain caller, so a cluster client is
        // the one caller nothing else checks. Every legitimate use of this grain is from a
        // reconciler, which is a grain.
        var clusterId = await AttachedToAsync(TenantA);

        var outcome = await cluster.Connection(clusterId).GetHealthAsync();

        outcome.IsFailure.ShouldBeTrue("a client is not a tenant and must not be treated as the owner.");
        outcome.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    // ── The structural guarantee ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryGrainMethodChecksTheOwningTenant() {
        // ⚠ THE GUARD AGAINST THE NEXT METHOD. docs/plan/06 § Grain keys says the check happens "on
        // every call"; a new method that forgot it would be a silent hole, and no per-method test
        // would exist to catch it because nobody writes a test for the check they forgot.
        //
        // So the interface is enumerated and every method is invoked as a tenant that does not own
        // the cluster. Each must refuse. AttachAsync is included: the first attach establishes the
        // owner (and is guarded by cardinality instead), but a re-attach must be checked, so the
        // cluster below is already attached.
        var clusterId = await AttachedToAsync(TenantA);

        typeof(IClusterConnectionGrain)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Length
            .ShouldBe(9, "the interface has nine methods; the probe covers whatever is there.");

        var unchecked_ = await cluster.Reacher(TenantB)
            .ProbeUncheckedMethodsAsync(
                clusterId,
                Descriptor(clusterId, TenantB),
                CommandFor(TenantB)
            );

        unchecked_.ShouldBeEmpty(
            "these methods did not check the owning tenant: "
            + string.Join(", ", unchecked_)
            + ". docs/plan/06 § Grain keys makes that check the reason this grain type exists."
        );
    }

    static Guid NewClusterId() =>
        Guid.Parse(FormattableString.Invariant($"cccccccc-0000-0000-0000-{Interlocked.Increment(ref next):D12}"));

    static ClusterConnectionDescriptor Descriptor(Guid clusterId, Guid owner) =>
        new() {
            ClusterId = clusterId,
            OwningTenantId = owner,
            Kind = ClusterConnectionKind.Kubeconfig,
            CredentialRef = "vault://clusters/test",
            Endpoint = "https://cluster.example:6443",
            DisplayName = "test"
        };

    static KubeCommand CommandFor(Guid tenantId) {
        var id = new ResourceId(
            tenantId,
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            "main",
            Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d")
        );

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(tenantId)
            .WithResourceId(id)
            .WithKind(new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" })
            .InNamespace("ns")
            .ObjectJson("""{"metadata":{"name":"main"}}""")
            .Build();
    }

    async Task<Guid> AttachedToAsync(Guid owner) {
        var clusterId = NewClusterId();
        (await cluster.Connection(clusterId).AttachAsync(Descriptor(clusterId, owner)))
            .IsSuccess.ShouldBeTrue();

        return clusterId;
    }
}

/// <summary>A connection that is only ever passed to <c>KubeCommand.For</c>.</summary>
sealed class UnusedConnection : IKubeClusterConnection {
    public Guid ClusterId => Guid.Empty;

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) =>
        throw new NotSupportedException();
}
