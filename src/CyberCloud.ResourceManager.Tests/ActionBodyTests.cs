using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     An action's declared request shape, enforced by the write path.
/// </summary>
/// <remarks>
///     ⚠ <b>The point of this file is that the emitted document and the running API agree about an
///     action.</b> Before <c>ActionRegistration.Request</c> existed, the generated OpenAPI said
///     <c>schema: {}</c> for every action and the manager checked nothing — an action was the one
///     part of the API surface with no contract at all. Declaring the shape without checking it would
///     be worse: a published constraint the API does not apply.
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class ActionBodyTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task ABodyThatSatisfiesTheDeclaredShapeIsAccepted() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("action-good");
        await Create(address);

        var action = await Invoke(address, """{"size":4,"tier":"standard"}""");

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
    }

    [Fact]
    public async Task AValueOutsideTheDeclaredEnumerationIsRefused() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("action-enum");
        await Create(address);

        var action = await Invoke(address, """{"size":4,"tier":"enormous"}""");

        action.IsFailure.ShouldBeTrue();
        action.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        action.Error.Target.ShouldBe("/tier");
        action.Error.Message.ShouldContain("standard");
    }

    [Fact]
    public async Task AValueOutsideTheDeclaredBoundsIsRefused() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("action-bounds");
        await Create(address);

        var action = await Invoke(address, """{"size":99}""");

        action.IsFailure.ShouldBeTrue();
        action.Error!.Target.ShouldBe("/size");
    }

    [Fact]
    public async Task AMissingRequiredParameterIsRefusedBecauseAPostIsNotAMerge() {
        // ⚠ There is nothing to merge a POST into, so every required parameter must be present —
        // exactly as on a PUT and unlike a PATCH.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("action-required");
        await Create(address);

        var action = await Invoke(address, """{"tier":"basic"}""");

        action.IsFailure.ShouldBeTrue();
        action.Error!.Target.ShouldBe("/size");
    }

    [Fact]
    public async Task AnUndeclaredParameterIsRefusedRatherThanDropped() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("action-unknown");
        await Create(address);

        var action = await Invoke(address, """{"size":2,"sizes":2}""");

        action.IsFailure.ShouldBeTrue();
        action.Error!.Target.ShouldBe("/sizes");
    }

    [Fact]
    public async Task AnActionThatDeclaresNoRequestStillTakesWhateverItIsGiven() {
        // Both branches are real. `restart` declares nothing, and refusing an undeclared body here
        // would break every action that already works.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("action-open");
        await Create(address);

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "restart",
                Body = """{"anything":"at all"}""",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
    }

    [Fact]
    public async Task TheBodyIsCheckedBeforeTheCallerIsAuthorized() {
        // Step 2 before step 3, as docs/plan/08 § The write path, end to end numbers them. It also
        // means a malformed body is refused for a resource that does not exist — which is why this
        // asserts InvalidRequestBody rather than ResourceNotFound.
        ResourceManagerCluster.ResetDoubles();

        var action = await Invoke(ResourceManagerCluster.Address("action-absent"), """{"size":99}""");

        action.IsFailure.ShouldBeTrue();
        action.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
    }

    Task<Result<WriteAccepted>> Invoke(ResourceId address, string body) =>
        cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "resize",
                Body = body,
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

    Task<Result<WriteAccepted>> Create(ResourceId address) =>
        cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );
}
