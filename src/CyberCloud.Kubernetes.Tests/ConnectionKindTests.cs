using CyberCloud.Kubernetes.Connections;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     The connection kinds of docs/plan/09 § Cluster connections, including the one that is
///     deliberately not built.
/// </summary>
public sealed class ConnectionKindTests
{
    static readonly KubeApiClientFactory Factory = new(new TestClock());

    static ClusterConnectionDescriptor Descriptor(ClusterConnectionKind kind) => new()
    {
        ClusterId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d"),
        OwningTenantId = Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
        Kind = kind,
        CredentialRef = "vault://clusters/x",
    };

    [Fact]
    public async Task AgentInitiatedIsRefusedWithAMessagePointingAtTheDocument()
    {
        // ⚠ NOT BUILT, ON PURPOSE, AND SAID SO. docs/plan/09 § Cluster connections budgets
        // AgentInitiated at 1.5 EM in M2 and warns that it "is not optional and is easy to defer
        // into a crisis" — the brief's "connection string to kubernetes" implies inbound
        // reachability, which for a tenant's on-prem cluster is usually false.
        //
        // A stub that pretended to connect would be exactly how you discover that at the first
        // on-prem customer, so the refusal is explicit and names what is missing.
        var outcome = await Factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.AgentInitiated),
            TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();

        var message = outcome.Error!.Message;
        message.ShouldContain("not implemented");
        message.ShouldContain("docs/plan/09");
        message.ShouldContain("M2");
        message.ShouldContain("NAT");
        message.ShouldContain(
            "cluster resource id",
            customMessage: "the message must name the authorization work, which is the expensive "
                + "half — a compromised agent must not be able to act as another tenant.");
    }

    [Fact]
    public async Task AKindlessDescriptorIsRefused()
    {
        var outcome = await Factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.Unknown),
            TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    [Fact]
    public async Task AKubeconfigConnectionNeedsAResolverAndSaysWhereItComesFrom()
    {
        // The vault seam. CyberCloud.KeyVault (docs/plan/18) does not exist, so the refusal names
        // the missing assembly rather than failing with "not found".
        var outcome = await Factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.Kubeconfig),
            TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Message.ShouldContain("CyberCloud.KeyVault");
        outcome.Error.Message.ShouldContain("Vault");
    }

    [Fact]
    public async Task AResolvedKubeconfigProducesAClient()
    {
        // The seam works when filled — the same shape the k3s fixture uses in production form.
        var factory = new KubeApiClientFactory(new TestClock())
        {
            ResolveKubeconfig = (_, _) => Task.FromResult(Result<string>.Success(
                """
                apiVersion: v1
                kind: Config
                clusters:
                - cluster: { server: https://cluster.example:6443 }
                  name: c
                contexts:
                - context: { cluster: c, user: u }
                  name: ctx
                current-context: ctx
                users:
                - name: u
                  user: { token: abc }
                """)),
        };

        var outcome = await factory.ConnectAsync(
            Descriptor(ClusterConnectionKind.Kubeconfig),
            TestContext.Current.CancellationToken);

        outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Message);
        outcome.GetValueOrThrow().Dispose();
    }

    [Fact]
    public void TheKindsAreTheDocumentsFourPlusTheDefault()
    {
        Enum.GetValues<ClusterConnectionKind>().Length.ShouldBe(
            5,
            "docs/plan/09 § Cluster connections has four rows, plus Unknown for default(enum).");
    }
}
