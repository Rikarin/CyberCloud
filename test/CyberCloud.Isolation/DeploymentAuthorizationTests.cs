using CyberCloud.Authorization.Contracts;
using CyberCloud.Providers.Sample.Contracts;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Isolation;

/// <summary>
///     A deployment's children, authorized by the real engine with the deployment's creator as the
///     subject — docs/plan/08 § Long-running operations, "Nested operations", and the argument on
///     <see cref="IResourceManager.WriteChildAsync" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here is doubled between the template and the tuple store.</b> The deployment's
///         <c>PUT</c> is checked by <c>ReBacResourceAuthorizer</c> at its group; its parent operation
///         runs on the silo and writes each child through the silo's own <c>ResourceManagerService</c>,
///         whose step 3 is <c>ReBacResourceAuthorizer</c> over the same <c>CyberCloudSchema</c>. So "the
///         child was refused" below is the engine answering <c>404</c> for a subject that holds nothing on
///         the child's group — not a switch a test flipped.
///     </para>
///     <para>
///         ⚠ <b>The shape of the probe.</b> A contributor on one group deploys a template whose second
///         resource names another group of the same subscription, where they hold nothing. A platform
///         that wrote children as itself would create both; one that wrote them as the caller creates the
///         first and is refused at the second, and the deployment fails naming it.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class DeploymentAuthorizationTests(IsolationCluster cluster) {
    static Guid Tenant => IsolationCluster.Victim;

    static Guid Subscription { get; } = Guid.Parse("99999999-0000-4000-8000-0000000000d1");

    const string Deployer = "dora";
    const string Home = "app";
    const string Elsewhere = "restricted";
    const string Open = "open";
    const string Revoked = "rita";
    const string Revocable = "revoke";

    static ResourceId Widget(string name, string group) =>
        new(Tenant, Subscription, group, SampleWidgets.Type, name, Guid.Empty);

    static CallerContext Dora => IsolationCluster.Caller(Tenant, Deployer);

    [Fact]
    public async Task AChildTheCallerMayNotWriteIsRefusedByTheEngineAndTheDeploymentFailsNamingIt() {
        await SeedAsync();

        var deployment = new ResourceId(Tenant, Subscription, Home, Deployments.Type, "cross-group", Guid.Empty);
        var accepted = await PutAsync(deployment);

        var ended = await DriveToEndAsync(accepted.OperationId);

        ended.State.ShouldBe(OperationState.Failed, Describe(ended));

        var refused = Widget("vault", Elsewhere);
        ended.Error.ShouldNotBeNull();
        ended.Error.Message.ShouldContain(refused.Path);
        ended.Error.Message.ShouldContain("ResourceNotFound", Case.Sensitive, "the engine's answer for a scope Dora cannot read.");

        // The first child was created, as Dora, and left in place — the rollback is recorded, not run.
        var front = await cluster.Manager.ReadAsync(
            new() { Path = Widget("front", Home).Path, ApiVersion = SampleWidgets.V2026, Caller = Dora },
            TestContext.Current.CancellationToken
        );

        front.IsSuccess.ShouldBeTrue(front.Error?.Message);
        front.GetValueOrThrow().CreatedBy.ShouldBe(Dora.ToString());
        front.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Succeeded);

        // The refused one does not exist in any form: no claim, so no name held and nothing to bill.
        (await cluster.For(Tenant).GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(refused)).ResolveAsync())
            .IsFailure.ShouldBeTrue("the refused child holds its name.");

        var record = await cluster.Manager.ReadAsync(
            new() { Path = deployment.Path, ApiVersion = Deployments.V2026, Caller = Dora },
            TestContext.Current.CancellationToken
        );

        record.IsSuccess.ShouldBeTrue(record.Error?.Message);
        record.GetValueOrThrow().ProvisioningState.ShouldBe(ProvisioningState.Failed);

        using var body = JsonDocument.Parse(record.GetValueOrThrow().Body);
        var properties = body.RootElement.GetProperty("properties");
        properties.GetProperty("rollback").GetString()!.ShouldContain(Widget("front", Home).Path);
        properties.GetProperty("steps")[1].GetString()!.ShouldStartWith("Failed " + refused.Path);

        // ── The control: the same template, its second resource aimed at a group Dora may write ─────
        //
        // ⚠ This is what makes the refusal above attributable to the right rather than to the template:
        // identical but for the group, it deploys. The rerun also leaves the first widget unchanged — a
        // no-op write, recorded as one — which is what makes re-running a failed deployment safe.
        //
        // The second control, "grant the missing right and rerun", follows it.
        var again = await PutAsync(deployment, Open);
        var rerun = await DriveToEndAsync(again.OperationId);

        rerun.State.ShouldBe(OperationState.Succeeded, Describe(rerun));
        rerun.Children.Length.ShouldBe(1, "the unchanged first widget was written again rather than left alone.");

        var steps = (await cluster.Manager.ReadAsync(
                new() { Path = deployment.Path, ApiVersion = Deployments.V2026, Caller = Dora },
                TestContext.Current.CancellationToken
            ))
            .GetValueOrThrow()
            .Body;

        using var after = JsonDocument.Parse(steps);
        var lines = after.RootElement.GetProperty("properties").GetProperty("steps").EnumerateArray()
            .Select(static x => x.GetString()!)
            .ToList();

        lines[0].ShouldBe($"Succeeded {Widget("front", Home).Path} — no change");
        lines[1].ShouldStartWith("Succeeded " + Widget("vault", Open).Path);

        // ── The second control: grant Dora the missing right and rerun the original template ──────
        //
        // ⚠ This is the control the test was first written with, and it failed until a child's step 3
        // became FullyConsistent. The refusal above cached a deny for Dora on 'restricted'; at
        // MinimizeLatency CheckGrain answers from any cached entry with no TTL (docs/plan/07
        // § Consistency), so the rerun met the cached 404 again after the grant. A child is written as
        // a recorded caller with no token behind it, and its check now reads the durable rows.
        await cluster.WriteTupleAsync(
            Tenant,
            Authorization.Contracts.ObjectRef.Of(ObjectTypes.ResourceGroup, GroupObject(Elsewhere)),
            Relations.Contributor,
            SubjectRef.Of(ObjectTypes.User, Deployer)
        );

        var granted = await DriveToEndAsync((await PutAsync(deployment)).OperationId);

        granted.State.ShouldBe(OperationState.Succeeded, Describe(granted));
        (await cluster.For(Tenant).GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(refused)).ResolveAsync())
            .IsSuccess.ShouldBeTrue("the grant did not reach the rerun's check of the child it was granted for.");
    }

    /// <summary>
    ///     A contributor revoked between a deployment's two children: the first is written as them, and
    ///     the second is refused by the engine at its own step 3 — the premise the argument on
    ///     <see cref="IResourceManager.WriteChildAsync" /> rests on.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This passed the wrong way until a child's check became <c>FullyConsistent</c>.</b> The
    ///     deployment's own <c>PUT</c> caches an allow for its creator at the group, both children are in
    ///     that group, and <c>MinimizeLatency</c> answers from that entry however old it is — the review of
    ///     #39 ran exactly this and saw the second child created after the revocation.
    /// </remarks>
    [Fact]
    public async Task ARightRevokedBetweenTwoChildrenIsHonouredAtTheSecond() {
        await SeedAsync();

        var tenant = cluster.For(Tenant);
        var rita = IsolationCluster.Caller(Tenant, Revoked);
        var ritaOnGroup = (
            Target: Authorization.Contracts.ObjectRef.Of(ObjectTypes.ResourceGroup, GroupObject(Revocable)),
            Subject: SubjectRef.Of(ObjectTypes.User, Revoked)
        );

        _ = await tenant.GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(Subscription, Revocable)).CreateAsync(Tenant, "eu-west-1");
        await cluster.WriteTupleAsync(Tenant, ritaOnGroup.Target, Relations.Contributor, ritaOnGroup.Subject);

        var template = new JsonObject {
            ["resources"] = new JsonArray(Resource("first", Revocable), Resource("second", Revocable, "first"))
        }.ToJsonString();

        var deployment = new ResourceId(Tenant, Subscription, Revocable, Deployments.Type, "revoked-midway", Guid.Empty);

        var written = await cluster.Manager.WriteAsync(
            new() {
                Path = deployment.Path,
                ApiVersion = Deployments.V2026,
                Verb = WriteVerb.Put,
                Body = new JsonObject { ["properties"] = new JsonObject { ["template"] = template } }.ToJsonString(),
                Caller = rita
            },
            TestContext.Current.CancellationToken
        );

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        var parentId = written.GetValueOrThrow().OperationId;

        // One pass: the first child is accepted as Rita, and nothing drives it yet.
        var first = (await tenant.GetGrain<IOperationGrain>(GrainKeys.Operation(parentId)).DriveAsync()).GetValueOrThrow();
        first.Children.Length.ShouldBe(1, Describe(first));

        await cluster.DeleteTupleAsync(Tenant, ritaOnGroup.Target, Relations.Contributor, ritaOnGroup.Subject);

        var ended = await DriveToEndAsync(parentId);

        ended.State.ShouldBe(OperationState.Failed, Describe(ended));
        ended.Children.Length.ShouldBe(1, "a child was written after its creator lost the right to write it.");

        var second = Widget("second", Revocable);
        ended.Error!.Message.ShouldContain(second.Path);
        ended.Error.Message.ShouldContain("ResourceNotFound", Case.Sensitive, "the engine's answer once Rita holds nothing on the group.");

        (await tenant.GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(second)).ResolveAsync())
            .IsFailure.ShouldBeTrue("the second child holds its name.");

        // The first was written before the revocation and is left, as a failed deployment leaves it.
        (await tenant.GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(Widget("first", Revocable))).ResolveAsync())
            .IsSuccess.ShouldBeTrue();
    }

    static string GroupObject(string group) =>
        Subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + group;

    static string Template(string vaultGroup) =>
        new JsonObject {
            ["resources"] = new JsonArray(
                Resource("front", null),
                Resource("vault", vaultGroup, "front")
            )
        }.ToJsonString();

    static JsonObject Resource(string name, string? group, params string[] dependsOn) {
        var resource = new JsonObject {
            ["type"] = SampleWidgets.Type.ToString(),
            ["apiVersion"] = SampleWidgets.V2026,
            ["name"] = name,
            ["location"] = "eu-central",
            ["properties"] = JsonNode.Parse(SampleWidgets.Body(IsolationCluster.ClusterId, "from a template"))!["properties"]!.DeepClone()
        };

        if (group is not null) {
            resource["resourceGroup"] = group;
        }

        if (dependsOn.Length > 0) {
            resource["dependsOn"] = new JsonArray([.. dependsOn.Select(static x => (JsonNode)x)]);
        }

        return resource;
    }

    async Task<WriteAccepted> PutAsync(ResourceId deployment, string vaultGroup = Elsewhere) {
        var written = await cluster.Manager.WriteAsync(
            new() {
                Path = deployment.Path,
                ApiVersion = Deployments.V2026,
                Verb = WriteVerb.Put,
                Body = new JsonObject { ["properties"] = new JsonObject { ["template"] = Template(vaultGroup) } }.ToJsonString(),
                Caller = Dora
            },
            TestContext.Current.CancellationToken
        );

        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
        return written.GetValueOrThrow();
    }

    async Task<OperationStatus> DriveToEndAsync(Guid parentId) {
        var parent = cluster.For(Tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(parentId));
        OperationStatus status = null!;

        for (var i = 0; i < 40; i++) {
            status = (await parent.DriveAsync()).GetValueOrThrow();

            if (status.IsTerminal) {
                return status;
            }

            foreach (var child in status.Children) {
                _ = await cluster.For(Tenant).GetGrain<IOperationGrain>(GrainKeys.Operation(child)).DriveAsync();
            }
        }

        return status;
    }

    /// <summary>
    ///     A subscription of this class's own with three groups, and Dora a contributor on two of them —
    ///     never on <c>restricted</c>.
    ///     Idempotent, because xUnit may construct the class more than once.
    /// </summary>
    async Task SeedAsync() {
        var tenant = cluster.For(Tenant);

        _ = await tenant.GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(Subscription)).CreateAsync("deployments");

        foreach (var group in (string[]) [Home, Elsewhere, Open]) {
            _ = await tenant.GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(Subscription, group)).CreateAsync(Tenant, "eu-west-1");
        }

        foreach (var group in (string[]) [Home, Open]) {
            await cluster.WriteTupleAsync(
                Tenant,
                Authorization.Contracts.ObjectRef.Of(ObjectTypes.ResourceGroup, GroupObject(group)),
                Relations.Contributor,
                SubjectRef.Of(ObjectTypes.User, Deployer)
            );
        }
    }

    static string Describe(OperationStatus status) =>
        $"{status.State}: {status.Error?.Message} — "
        + string.Join(" | ", status.Progress.TakeLast(6).Select(static x => x.ToString()));
}
