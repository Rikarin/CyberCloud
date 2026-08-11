using CyberCloud.ResourceManager.Tests.Infrastructure;

namespace CyberCloud.ResourceManager.Tests;

/// <summary>
///     <b>404, never 403.</b> docs/plan/07 § The enforcement seam.
/// </summary>
/// <remarks>
///     <i>
///         "404, never 403, on a resource the caller cannot read. A 403 confirms the resource exists,
///         which is an enumeration oracle: a competitor can discover a customer's resource names by
///         probing. 403 is returned only when the caller can <b>read</b> the object but not perform
///         the <b>action</b> — which is a real and useful distinction, and it means the response code
///         itself is authorization output."
///     </i>
/// </remarks>
[Collection(ResourceManagerSuite.Name)]
public sealed class EnforcementSeamTests(ResourceManagerCluster cluster) {
    [Fact]
    public async Task ACallerWhoCannotReadGets404OnAResourceThatExists() {
        // ⚠ THE ENUMERATION ORACLE, CLOSED. The resource is real; the caller cannot read it; the
        // answer is indistinguishable from "there is no such resource".
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("seam-invisible");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        SwitchableAuthorizer.GrantOnly();

        var read = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        read.IsFailure.ShouldBeTrue();
        read.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        read.Error.Code.ShouldNotBe(ErrorCode.AuthorizationFailed);
    }

    [Fact]
    public async Task ACallerWhoCannotReadGetsTheSameAnswerForAResourceThatDoesNotExist() {
        // ⚠ The two answers must be IDENTICAL — the same code and the same message. Two different
        // messages would be the oracle the status code just closed.
        ResourceManagerCluster.ResetDoubles();
        var real = ResourceManagerCluster.Address("seam-real");
        var fake = ResourceManagerCluster.Address("seam-fake");

        await Create(real);
        SwitchableAuthorizer.GrantOnly();

        var onReal = await cluster.Manager.ReadAsync(
            new() { Path = real.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        var onFake = await cluster.Manager.ReadAsync(
            new() { Path = fake.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        onReal.Error!.Code.ShouldBe(onFake.Error!.Code);
        onReal.Error.Code.ShouldBe(ErrorCode.ResourceNotFound);

        // The messages differ only by the path the caller themselves supplied — nothing in either
        // says whether the resource is there.
        onReal.Error.Message.ShouldBe($"'{real.Path}' does not exist.");
        onFake.Error.Message.ShouldBe($"'{fake.Path}' does not exist.");
    }

    [Fact]
    public async Task ACallerWhoCanReadButNotWriteGets403() {
        // ⚠ THE OTHER HALF, AND IT IS A REAL DISTINCTION. The caller already knows the resource exists,
        // so 403 leaks nothing and tells them something useful: ask for the write permission.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("seam-readonly-caller");

        var created = await Create(address);
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        SwitchableAuthorizer.GrantOnly("read");

        var write = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(size: 4),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        write.IsFailure.ShouldBeTrue();
        write.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        write.Error.Message.ShouldContain("can read");
        write.Error.Message.ShouldContain("write");
    }

    [Fact]
    public async Task ACallerWhoCanNeitherReadNorWriteGets404OnAWrite() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("seam-neither");

        await Create(address);
        SwitchableAuthorizer.GrantOnly("somethingelse");

        var write = await cluster.Manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Put,
                Body = TestingProvider.Body(size: 4),
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        write.IsFailure.ShouldBeTrue();
        write.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task ACallerWhoCanReadButNotDeleteGets403AndTheResourceSurvives() {
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("seam-nodelete");

        var created = await Create(address);
        SwitchableAuthorizer.GrantOnly("read");

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = TestingProvider.V2026, Caller = ResourceManagerCluster.Caller() },
            TestContext.Current.CancellationToken
        );

        deleted.IsFailure.ShouldBeTrue();
        deleted.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        SwitchableAuthorizer.Reset();
        var entry = await cluster.Index(address).GetAsync();
        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);
        entry.GetValueOrThrow().BoundTo.ShouldBe(created.GetValueOrThrow().Resource.Id);
    }

    [Fact]
    public async Task ASecretReturningActionAsksForAFullyConsistentCheck() {
        // docs/plan/07 § Consistency, row 3: "Bypass all caches, read durable … Deletion, key export,
        // billing changes, anything where a stale allow is a real incident." listKeys is a key export.
        ResourceManagerCluster.ResetDoubles();
        var address = ResourceManagerCluster.Address("seam-secret");

        await Create(address);

        var action = await cluster.Manager.ActionAsync(
            new() {
                Path = address.Path,
                ApiVersion = TestingProvider.V2026,
                Verb = WriteVerb.Post,
                Action = "listKeys",
                Caller = ResourceManagerCluster.Caller()
            },
            TestContext.Current.CancellationToken
        );

        action.IsSuccess.ShouldBeTrue(action.Error?.Message);
        SwitchableAuthorizer.Asked.ShouldContain("listKeys");
    }

    [Fact]
    public void TheRealAuthorizerSaysNothingDifferentForAbsentAndInvisible() {
        // The shape of the rule in the production implementation, asserted without standing up a ReBAC
        // schema: NotFound is built from the path alone and from nothing about whether the resource is
        // there.
        var absent = ResourceManagerCluster.Address("absent");
        var present = ResourceManagerCluster.Address("present");

        // ReBacResourceAuthorizer's group object id is what a create is checked against — a bare name
        // would merge the "prod" group of every subscription into one authorization object.
        ReBacResourceAuthorizer.GroupObjectId(absent)
            .ShouldBe(ReBacResourceAuthorizer.GroupObjectId(present));

        ReBacResourceAuthorizer.GroupObjectId(absent)
            .ShouldStartWith(ResourceManagerCluster.Subscription.ToString("N"));

        ReBacResourceAuthorizer.GroupObjectId(absent).ShouldEndWith("-prod");
    }

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
