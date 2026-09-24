using System.Text.Json;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     An action on a child resource carries its parent's GUID, read from the tenant's index — and an
///     action on a top-level resource carries none.
/// </summary>
/// <remarks>
///     ⚠ <b>The GUID is compared with the index's own binding, not with "some GUID".</b>
///     <c>CyberCloud.Monitor/workspaces/components</c> derives the ClickHouse database it reads from
///     this value, so a parent resolved at the wrong address — the child's own, a sibling's — would be
///     a view over the wrong tenancy that still answered.
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ActionParentTests(ResourceManagerCluster cluster) {
    static Guid Subscription => ResourceManagerCluster.IsolatedSubscription;

    [Fact]
    public async Task AnActionOnAChildCarriesItsParentsGuidFromTheIndex() {
        ResourceManagerCluster.ResetDoubles();

        var parent = new ResourceId(
            ResourceManagerCluster.Tenant,
            Subscription,
            "prod",
            ConformingReconciler.TypeName,
            "echo-widget",
            Guid.Empty
        );

        var created = await Put(parent.Path, TestingProvider.Body());
        await ConvergeAsync(created);

        var child = new ResourceId(
            ResourceManagerCluster.Tenant,
            Subscription,
            "prod",
            TestingProvider.ChildTypeName,
            "echo-gadget",
            Guid.Empty,
            parent.Name
        );

        await ConvergeAsync(await Put(child.Path, TestingProvider.ChildBody()));

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = child.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = ParentEchoHandler.ActionName,
                Body = "{}",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);

        var bound = (await cluster.Index(parent).ResolveAsync()).GetValueOrThrow();
        using var response = JsonDocument.Parse(action.GetValueOrThrow().ActionResponse);

        response.RootElement.GetProperty("parentId")
            .GetString()
            .ShouldBe(
                bound.ToString("D", System.Globalization.CultureInfo.InvariantCulture),
                "the handler was not handed the GUID the tenant's index binds the parent's address to"
            );

        response.RootElement.GetProperty("parentPath").GetString().ShouldBe(parent.Path);
    }

    [Fact]
    public async Task AnActionOnATopLevelResourceCarriesNoParent() {
        ResourceManagerCluster.ResetDoubles();
        RestartHandler.Reset();

        var address = ResourceManagerCluster.Address("echo-top-level");
        await ConvergeAsync(await Put(address.Path, TestingProvider.Body()));

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "restart",
                Body = "{}",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        RestartHandler.Invocations.ShouldBe(1);
        RestartHandler.LastParent.ShouldBeNull("a top-level resource's parent is its group, which is not a resource");
    }

    async Task<WriteAccepted> Put(string path, string body) {
        var written = await cluster.Manager.WriteAsync(
            new() {
                Path = path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = body,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        return written.GetValueOrThrow();
    }

    async Task ConvergeAsync(WriteAccepted accepted) {
        var operation = cluster.Operation(ResourceManagerCluster.Tenant, accepted.OperationId);

        for (var i = 0; i < 5; i++) {
            if ((await operation.DriveAsync()).GetValueOrThrow().IsTerminal) {
                return;
            }
        }
    }
}
