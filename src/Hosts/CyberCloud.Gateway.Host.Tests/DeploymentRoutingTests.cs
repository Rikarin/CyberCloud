using CyberCloud.Gateway.Host.Tests.Infrastructure;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     Where the gateway sends a deployment: its <c>PUT</c> down the ordinary write path, its
///     <c>whatIf</c> to <c>IDeploymentManager</c>. docs/plan/08 § Long-running operations.
/// </summary>
/// <remarks>
///     ⚠ <b>Routing only.</b> What the what-if answers, and that it reads as the caller, is
///     <c>CyberCloud.ResourceManager.Tests.DeploymentTests</c>' against real grains; what a deployment's
///     children are refused with is <c>CyberCloud.Isolation</c>'s <c>DeploymentAuthorizationTests</c>
///     against the real engine. Here the question is which seam each request reaches, with what
///     address and what caller — so the managers are the recording ones and the assertions are about
///     what they were handed.
/// </remarks>
public sealed class DeploymentRoutingTests {
    static string DeploymentPath(Guid tenant, string name = "rollout") =>
        $"/tenants/{tenant:D}/subscriptions/{GatewayHarness.Subscription:D}/resourceGroups/prod"
        + $"/providers/{Deployments.ProviderNamespace}/{Deployments.TypePath}/{name}";

    const string Body = """{"properties":{"template":"{\"resources\":[]}"}}""";

    [Fact]
    public async Task WhatIfReachesTheDeploymentEntryPointWithTheRebuiltAddressAndTheTokensCaller() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            DeploymentPath(GatewayHarness.TenantA) + "/whatIf",
            gateway.Token(GatewayHarness.TenantA, "dora"),
            body: Body
        );

        response.Status.ShouldBe(StatusCodes.Status200OK, response.Body);
        response.Header("Azure-AsyncOperation").ShouldBeEmpty("a what-if is synchronous; there is nothing to poll.");

        var asked = gateway.Deployments.WhatIfs.ShouldHaveSingleItem();
        asked.Path.ShouldBe(DeploymentPath(GatewayHarness.TenantA));
        asked.Caller.TenantId.ShouldBe(GatewayHarness.TenantA);
        asked.Caller.SubjectId.ShouldBe("dora");
        asked.Body.ShouldBe(Body);

        // ⚠ And NOT the ordinary action path: ActionAsync refuses an action on an absent resource, and
        // this deployment does not exist.
        gateway.Manager.Actions.ShouldBeEmpty("the what-if reached IResourceManager.ActionAsync.");

        using var rendered = JsonDocument.Parse(response.Body);
        rendered.RootElement.GetProperty("status").GetString().ShouldBe("Succeeded");
        rendered.RootElement.GetProperty("changes")[0].GetProperty("changeType").GetString().ShouldBe(WhatIfChangeTypes.Create);
        rendered.RootElement.GetProperty("changes")[0].GetProperty("delta")[0].GetProperty("after").GetString().ShouldBe("hi");
    }

    [Fact]
    public async Task AWhatIfRefusalIsShapedLikeAnyOtherAndKeepsTheCanonical404() {
        var gateway = new GatewayHarness();
        gateway.Deployments.OnWhatIf = static request =>
            Result<DeploymentWhatIf>.Failure(ErrorCode.ResourceNotFound, $"'{request.Path}' does not exist.");

        var response = await gateway.SendAsync(
            "POST",
            DeploymentPath(GatewayHarness.TenantA) + "/whatIf",
            gateway.Token(GatewayHarness.TenantA),
            body: Body
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
    }

    [Fact]
    public async Task ADeploymentsPutIsAnOrdinaryWriteAndAnswers202WithAnOperationToPoll() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "PUT",
            DeploymentPath(GatewayHarness.TenantA),
            gateway.Token(GatewayHarness.TenantA),
            body: Body
        );

        response.Status.ShouldBe(StatusCodes.Status202Accepted, response.Body);
        response.Header("Azure-AsyncOperation").ShouldNotBeEmpty();
        gateway.Manager.Paths.ShouldContain(DeploymentPath(GatewayHarness.TenantA));
        gateway.Deployments.WhatIfs.ShouldBeEmpty();
        gateway.Manager.ChildWrites.ShouldBe(0, "the gateway wrote as a recorded caller.");
    }

    [Fact]
    public async Task AWhatIfNamingAnotherTenantNeverLeavesTheCallersOwn() {
        var gateway = new GatewayHarness();

        var response = await gateway.SendAsync(
            "POST",
            DeploymentPath(GatewayHarness.TenantB) + "/whatIf",
            gateway.Token(GatewayHarness.TenantA),
            body: Body
        );

        response.Status.ShouldBe(StatusCodes.Status404NotFound, response.Body);
        gateway.Deployments.WhatIfs.ShouldBeEmpty("a smuggled tenant reached the deployment entry point.");
    }
}
