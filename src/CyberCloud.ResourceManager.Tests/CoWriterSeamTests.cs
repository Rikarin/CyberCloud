using CyberCloud.Kubernetes.Contracts;
using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <c>ReconcileContext.CoWriter</c> — the seam a child reconciler co-writes onto a parent's
///     object through (issue #89) — follows the pass's cluster connection.
/// </summary>
/// <remarks>
///     The seam is derived rather than wired, which is not the shape the context's other seams take,
///     so what is worth pinning is the derivation: a pass with a cluster gets a working co-writer over
///     that connection with nothing else configured, and a pass without one gets a refusal that names
///     the fix.
/// </remarks>
public sealed class CoWriterSeamTests {
    const string Namespace = "22222222222222222222222222222222-rg";

    [Fact]
    public void APassWithAClusterGetsACoWriterOverThatClusterForFree() {
        var context = Context(new RefusingConnection());

        context.CoWriter.ShouldBeOfType<KubeCoWriter>();
    }

    [Fact]
    public async Task APassWithoutAClusterGetsARefusalNamingRequiresCluster() {
        var context = Context(null);

        context.CoWriter.ShouldBeOfType<NoClusterCoWriter>();

        var refused = await context.CoWriter.ApplyFragmentAsync(
            context.Id,
            new() { Kind = new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" }, Namespace = Namespace, Name = "x" },
            """{ "data": { "k": "v" } }""",
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("RequiresCluster");
    }

    [Fact]
    public async Task TheCoWriterReadsThroughThePassesOwnConnection() {
        // ⚠ The one thing the seam must not do is reach a different cluster than the pass was handed.
        var connection = new RefusingConnection();
        var context = Context(connection);

        await context.CoWriter.ApplyFragmentAsync(
            context.Id,
            new() { Kind = new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" }, Namespace = Namespace, Name = "x" },
            """{ "data": { "k": "v" } }""",
            TestContext.Current.CancellationToken
        );

        connection.Reads.ShouldBe(1, "the co-writer read the owner's object through the pass's connection");
    }

    [Fact]
    public async Task AContextGivenAClusterAfterConstructionCoWritesThroughThatCluster() {
        // ⚠ "Derived from Cluster" has to mean the cluster the context HAS, not the one it was
        // constructed with. A context built without a connection and handed one by `with` would
        // otherwise keep the refusal it was born with, and the remark would be true only at
        // construction. No driver builds a context that way today; this is what keeps it from
        // mattering when one does.
        var connection = new RefusingConnection();
        var context = Context(null) with { Cluster = connection };

        context.CoWriter.ShouldBeOfType<KubeCoWriter>();

        await context.CoWriter.ApplyFragmentAsync(
            context.Id,
            new() { Kind = new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" }, Namespace = Namespace, Name = "x" },
            """{ "data": { "k": "v" } }""",
            TestContext.Current.CancellationToken
        );

        connection.Reads.ShouldBe(1, "the co-writer follows the cluster the context has now");

        (Context(connection) with { Cluster = null }).CoWriter.ShouldBeOfType<NoClusterCoWriter>("and the other direction");
    }

    [Fact]
    public void ACallerMayStillSupplyItsOwn() {
        var theirs = new NoClusterCoWriter();

        var context = Context(new RefusingConnection()) with { CoWriter = theirs };

        context.CoWriter.ShouldBeSameAs(theirs);
    }

    static ReconcileContext Context(IKubeClusterConnection? cluster) =>
        new(
            ResourceId.ParsePath(
                "/tenants/11111111-1111-1111-1111-111111111111"
                + "/subscriptions/22222222-2222-2222-2222-222222222222"
                + "/resourceGroups/rg/providers/CyberCloud.Testing/vaults/vault"
            )
                .GetValueOrThrow()
                .WithId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            TestingProvider.V2026,
            JsonDocument.Parse("{}").RootElement,
            null,
            Namespace,
            cluster,
            new UnavailableSecretResolver(),
            new RecordingReconcileLog()
        );

    /// <summary>A connection that counts reads and holds no objects.</summary>
    sealed class RefusingConnection : IKubeClusterConnection {
        public int Reads { get; private set; }

        public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

        public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("nothing is applied against an object that is not there");

        public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
            Reads++;
            return Task.FromResult(Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here"));
        }

        public Task<Result> DeleteAsync(
            KubeCommand command,
            CascadePolicy policy = CascadePolicy.Background,
            CancellationToken cancellationToken = default
        ) =>
            throw new NotSupportedException("nothing is deleted here");
    }
}
