using System.Text.Json;
using CyberCloud.Core.Resources;
using Shouldly;

namespace CyberCloud.Kubernetes.Contracts.Tests;

/// <summary>
///     The rest of the ADR-013 builder: what it emits, what it refuses, and the seams it declares.
/// </summary>
public sealed class KubeCommandBuilderTests
{
    static readonly GroupVersionKind Deployments = new()
    {
        Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments",
    };

    static ResourceId Resource() => new(
        Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
        Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
        "prod",
        new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers"),
        "main",
        Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"));

    static IKubeCommandBuilder Builder(IChartRenderer? charts = null)
    {
        var id = Resource();
        return KubeCommand.For(new NullConnection(), charts)
            .WithTenantId(id.TenantId)
            .WithResourceId(id);
    }

    static IKubeCommandBuilder Complete(IChartRenderer? charts = null) =>
        Builder(charts)
            .WithKind(Deployments)
            .InNamespace("tenant-space")
            .ObjectJson("""{"metadata":{"name":"main"},"spec":{"replicas":2}}""");

    // ── What it emits ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBodyCarriesTheApiVersionAndKindFromTheGvkAndNotFromTheCaller()
    {
        // An apply patch whose apiVersion disagrees with the URL is rejected, and the URL is built
        // from the GVK — so the GVK wins rather than whatever the rendered body happened to say.
        var command = Builder()
            .WithKind(Deployments)
            .ObjectJson("""{"apiVersion":"v1","kind":"ConfigMap","metadata":{"name":"main"}}""")
            .Build();

        using var document = JsonDocument.Parse(command.Body);
        document.RootElement.GetProperty("apiVersion").GetString().ShouldBe("apps/v1");
        document.RootElement.GetProperty("kind").GetString().ShouldBe("Deployment");
    }

    [Fact]
    public void TheNamespaceAndNameLandInMetadata()
    {
        var command = Complete().Build();

        using var document = JsonDocument.Parse(command.Body);
        var metadata = document.RootElement.GetProperty("metadata");

        metadata.GetProperty("name").GetString().ShouldBe("main");
        metadata.GetProperty("namespace").GetString().ShouldBe("tenant-space");
        command.Target.Namespace.ShouldBe("tenant-space");
        command.Target.Name.ShouldBe("main");
    }

    [Fact]
    public void TheCallersOwnFieldsAreLeftAlone()
    {
        var command = Complete().Build();

        using var document = JsonDocument.Parse(command.Body);
        document.RootElement.GetProperty("spec").GetProperty("replicas").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void TheSubscriptionIsInferredFromTheResourceAndCanBeOverridden()
    {
        // docs/plan/09 § The command builder: "inferred from ResourceId; override for platform
        // objects".
        Complete().Build().SubscriptionId
            .ShouldBe(Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"));

        var platform = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var overridden = Complete().WithSubscriptionId(platform).Build();

        overridden.SubscriptionId.ShouldBe(platform);
        overridden.Labels[KubeLabels.SubscriptionId].ShouldBe(platform.ToString("D", null));
    }

    [Fact]
    public void ForceIsAlwaysFalseAndThereIsNoBuilderMethodToChangeIt()
    {
        // ⚠ force: true is the switch that turns ADR-013's conflict back into the silent revert it
        // exists to replace. It is unreachable from the builder on purpose.
        Complete().Build().Force.ShouldBeFalse();

        typeof(IKubeCommandBuilder).GetMethods()
            .ShouldNotContain(m => m.Name.Contains("Force", StringComparison.OrdinalIgnoreCase));
    }

    // ── The reconcile hash ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReconcileHashIsStableAcrossPropertyOrdering()
    {
        // ⚠ A no-op detector that reports a change because a field moved is worse than none: it
        // turns every reconcile into an apply. The hash is over a canonicalised rendering.
        var a = Builder().WithKind(Deployments)
            .ObjectJson("""{"metadata":{"name":"main"},"spec":{"replicas":2,"paused":false}}""")
            .Build();

        var b = Builder().WithKind(Deployments)
            .ObjectJson("""{"spec":{"paused":false,"replicas":2},"metadata":{"name":"main"}}""")
            .Build();

        a.ReconcileHash.ShouldBe(b.ReconcileHash);
    }

    [Fact]
    public void TheReconcileHashChangesWhenTheDesiredStateDoes()
    {
        var a = Complete().Build();
        var b = Builder().WithKind(Deployments)
            .ObjectJson("""{"metadata":{"name":"main"},"spec":{"replicas":3}}""")
            .Build();

        a.ReconcileHash.ShouldNotBe(b.ReconcileHash);
    }

    [Fact]
    public void TheHashIsOverTheDesiredBodyAndNotOverTheInjectedResult()
    {
        // ⚠ Hashing after injection would fold the hash annotation's own presence into the hash and
        // make every second apply look different.
        var command = Complete().Build();

        command.Annotations[KubeLabels.ReconcileHashAnnotation].ShouldBe(command.ReconcileHash);
        command.Body.ShouldContain(command.ReconcileHash);

        // Adding a label the caller invented does not change what the DESIRED body hashes to.
        var labelled = Complete().WithLabels(("cybercloud.io/extra", "x")).Build();
        labelled.ReconcileHash.ShouldBe(command.ReconcileHash);
    }

    // ── Owner references ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void WithOwnerEmitsAnOwnerReferenceThatCascades()
    {
        var parent = Resource() with { Name = "parent", Id = Guid.Parse("11111111-2222-3333-4444-555555555555") };

        var command = Complete()
            .WithOwner(parent, Deployments, "parent-deployment", "uid-1234")
            .Build();

        using var document = JsonDocument.Parse(command.Body);
        var owners = document.RootElement.GetProperty("metadata").GetProperty("ownerReferences");

        owners.GetArrayLength().ShouldBe(1);
        owners[0].GetProperty("uid").GetString().ShouldBe("uid-1234");
        owners[0].GetProperty("name").GetString().ShouldBe("parent-deployment");
        owners[0].GetProperty("blockOwnerDeletion").GetBoolean().ShouldBeTrue();

        command.Annotations["cybercloud.io/owner-resource-id"]
            .ShouldBe("11111111-2222-3333-4444-555555555555");
    }

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AMissingKindIsRefusedWithAMessageNamingThePluralProblem()
    {
        var built = Builder().ObjectJson("""{"metadata":{"name":"main"}}""").TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain("plural");
        built.Error.Message.ShouldContain("WithKind");
    }

    [Fact]
    public void AKindWithoutAPluralIsRefused()
    {
        var built = Builder()
            .WithKind(new GroupVersionKind { Group = "apps", Version = "v1", Kind = "Deployment" })
            .ObjectJson("""{"metadata":{"name":"main"}}""")
            .TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain("incomplete");
    }

    [Fact]
    public void AMissingObjectIsRefused()
    {
        var built = Builder().WithKind(Deployments).TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain("no object was set");
    }

    [Fact]
    public void MalformedJsonIsRefusedWithTheParserSMessage()
    {
        var built = Builder().WithKind(Deployments).ObjectJson("{ not json").TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain("not valid JSON");
    }

    [Fact]
    public void BuildThrowsWhereTryBuildReturns()
    {
        // docs/plan/00 § Coding standards: a domain outcome is a Result; a bug is an exception. Both
        // spellings exist so a reconciler can choose which this is for it.
        var builder = Builder().WithKind(Deployments);

        builder.TryBuild().IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void AnObjectWithNoNameFallsBackToTheResourceName()
    {
        // ResourceNaming is the DNS-1123 label rule, which is exactly what a Kubernetes object name
        // must satisfy — so the fallback is always legal.
        var command = Builder().WithKind(Deployments).ObjectJson("""{"spec":{}}""").Build();

        command.Target.Name.ShouldBe("main");
    }

    // ── The chart seam ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChartWithoutARendererFailsWithAMessageNamingTheChartsAssembly()
    {
        // ⚠ THE SEAM, NOT A STUB. docs/plan/03 § src puts Helm rendering in
        // CyberCloud.Kubernetes.Charts. Silently applying nothing would be the bad outcome; so would
        // an exception with no explanation.
        var built = Builder()
            .WithKind(Deployments)
            .Chart("managed/postgres", JsonDocument.Parse("{}").RootElement)
            .TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain("CyberCloud.Kubernetes.Charts");
        built.Error.Message.ShouldContain("managed/postgres");
        built.Error.Message.ShouldContain("HelmRelease");
        built.Error.Message.ShouldContain("ADR-001");
    }

    [Fact]
    public void ARegisteredRendererIsUsedAndItsOutputIsLabelledLikeAnyOtherObject()
    {
        // The seam works when filled — asserted so the shape is fixed before the renderer exists.
        var renderer = new StubRenderer("""{"metadata":{"name":"main"},"spec":{"replicas":7}}""");

        var command = Builder(renderer)
            .WithKind(Deployments)
            .InNamespace("tenant-space")
            .Chart("managed/postgres", JsonDocument.Parse("""{"size":"small"}""").RootElement)
            .Build();

        renderer.Chart.ShouldBe("managed/postgres");
        renderer.ReleaseNamespace.ShouldBe("tenant-space");
        renderer.ReleaseName.ShouldBe("main");

        command.Labels.Count.ShouldBe(7, "a rendered object gets the seven like any other.");
        command.Body.ShouldContain("\"replicas\":7");
    }

    [Fact]
    public void AMultiObjectChartIsRefusedWithAnExplanationRatherThanSilentlyApplyingTheFirst()
    {
        var renderer = new StubRenderer(
            """{"metadata":{"name":"a"}}""",
            """{"metadata":{"name":"b"}}""");

        var built = Builder(renderer)
            .WithKind(Deployments)
            .Chart("managed/postgres", JsonDocument.Parse("{}").RootElement)
            .TryBuild();

        built.IsFailure.ShouldBeTrue();
        built.Error!.Message.ShouldContain("2 objects");
        built.Error.Message.ShouldContain("CyberCloud.Kubernetes.Charts");
    }

    // ── The deferred connection kind ───────────────────────────────────────────────────────────

    [Fact]
    public void TheConnectionKindEnumIsTheDocumentsClosedSetIncludingTheDeferredOne()
    {
        // docs/plan/09 § Cluster connections' table has four rows. AgentInitiated is declared even
        // though it is not built, so the enum is the document's set rather than the subset that
        // happens to work — and so persisted state naming it round-trips when it lands.
        Enum.GetValues<ClusterConnectionKind>().ShouldBe(
        [
            ClusterConnectionKind.Unknown,
            ClusterConnectionKind.Kubeconfig,
            ClusterConnectionKind.ServiceAccountToken,
            ClusterConnectionKind.AgentInitiated,
            ClusterConnectionKind.InHouse,
        ]);
    }

    sealed class StubRenderer(params string[] documents) : IChartRenderer
    {
        public string? Chart { get; private set; }

        public string? ReleaseNamespace { get; private set; }

        public string? ReleaseName { get; private set; }

        public Result<IReadOnlyList<string>> Render(
            string chart,
            JsonElement values,
            string releaseNamespace,
            string releaseName)
        {
            Chart = chart;
            ReleaseNamespace = releaseNamespace;
            ReleaseName = releaseName;
            return Result<IReadOnlyList<string>>.Success(documents);
        }
    }
}
