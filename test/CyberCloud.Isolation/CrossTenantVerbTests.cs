namespace CyberCloud.Isolation;

/// <summary>
///     Every provider, every verb, wrong tenant. <b>404, never 403.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § The enforcement seam: <i>"404, never 403, on a resource the caller cannot
///         read. A 403 confirms the resource exists, which is an enumeration oracle: a competitor can
///         discover a customer's resource names by probing."</i>
///     </para>
///     <para>
///         ⚠ <b>Every attack in this class runs against a resource that genuinely exists.</b> A sweep
///         over names that are absent proves nothing: absent names answer 404 by accident. The
///         victim's resource is created first, driven to <c>Succeeded</c>, and given the ReBAC parent
///         tuple that makes it readable <i>to its owner</i> — so if any of these answers differs from
///         the answer for a name that was never used, that difference is the oracle.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class CrossTenantVerbTests(IsolationCluster cluster) {
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task PuttingOverAnotherTenantsResourceIs404(IsolationTarget target) {
        var victim = await VictimResourceAsync(target, "put-victim");

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = victim.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        ShouldBeInvisible(refused, victim);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task PatchingAnotherTenantsResourceIs404(IsolationTarget target) {
        var victim = await VictimResourceAsync(target, "patch-victim");

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = victim.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Patch,
                Body = """{"location":"somewhere-else"}""",
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        ShouldBeInvisible(refused, victim);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ReadingAnotherTenantsResourceIs404(IsolationTarget target) {
        var victim = await VictimResourceAsync(target, "get-victim");

        var refused = await cluster.Manager.ReadAsync(
            new() { Path = victim.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Code.ShouldNotBe(ErrorCode.AuthorizationFailed);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task DeletingAnotherTenantsResourceIs404AndItSurvives(IsolationTarget target) {
        var victim = await VictimResourceAsync(target, "delete-victim");

        var refused = await cluster.Manager.DeleteAsync(
            new() { Path = victim.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        ShouldBeInvisible(refused, victim);

        // ⚠ AND IT IS STILL THERE. A 404 that had already released the index or begun a teardown would
        // be a denial-of-service dressed as a refusal — and the delete path releases the index BEFORE
        // it does anything else, so this is the assertion that keeps that ordering safe.
        var entry = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(victim))
            .GetAsync();

        entry.GetValueOrThrow().State.ShouldBe(IndexEntryState.Confirmed);

        var still = await cluster.Manager.ReadAsync(
            new() { Path = victim.Path, ApiVersion = target.ApiVersion, Caller = Victim() },
            TestContext.Current.CancellationToken
        );

        still.IsSuccess.ShouldBeTrue(still.Error?.Message);
        still.GetValueOrThrow().ProvisioningState.ShouldNotBe(ProvisioningState.Deleting);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task PostingAnActionOnAnotherTenantsResourceIs404(IsolationTarget target) {
        var victim = await VictimResourceAsync(target, "action-victim");

        var refused = await cluster.Manager.ActionAsync(
            new() {
                Path = victim.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Post,
                Action = target.Action,
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        ShouldBeInvisible(refused, victim);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task CreatingUnderAnotherTenantsPathIs404AndReachesNoProvider(IsolationTarget target) {
        // The attacker invents a name in the victim's tenant. Nothing about the answer, and nothing in
        // the cluster, may differ from a request that was simply wrong.
        cluster.World.Reset();

        var theirs = IsolationCluster.Address(
            target,
            "invented",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = theirs.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        cluster.World.Applied.ShouldBeEmpty("a cross-tenant write reached a provider");

        var entry = await cluster.For(IsolationCluster.Victim)
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(theirs))
            .GetAsync();

        entry.GetValueOrThrow().State.ShouldBe(
            IndexEntryState.Free,
            "a refused cross-tenant create claimed a name in the victim's index — which is a "
            + "denial-of-service on their namespace even though it leaked nothing"
        );
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task TheAnswerForAnInvisibleResourceIsIdenticalToTheAnswerForAnAbsentOne(IsolationTarget target) {
        // ⚠ THE ORACLE THE STATUS CODE ALONE DOES NOT CLOSE. Two 404s whose MESSAGES differ are still
        // a way to ask "does this name exist". The messages must differ only by the path the caller
        // themselves supplied.
        var victim = await VictimResourceAsync(target, "identical-real");

        var absent = IsolationCluster.Address(
            target,
            "identical-absent",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription
        );

        var onReal = await cluster.Manager.ReadAsync(
            new() { Path = victim.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        var onAbsent = await cluster.Manager.ReadAsync(
            new() { Path = absent.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        onReal.Error!.Code.ShouldBe(onAbsent.Error!.Code);
        onReal.Error.Target.ShouldBe(onAbsent.Error.Target);

        // The one legitimate difference: each message quotes the path it was given.
        onReal.Error.Message.Replace(victim.Path, "PATH", StringComparison.Ordinal)
            .ShouldBe(onAbsent.Error.Message.Replace(absent.Path, "PATH", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task NoCrossTenantRefusalEverCarriesTheDetailOfWhyItWasRefused(IsolationTarget target) {
        // A 404 that names the permission the caller was missing has told them the resource exists and
        // what to ask for. docs/plan/08 § Errors also bans exception detail outright.
        var victim = await VictimResourceAsync(target, "quiet-refusal");

        var refusals = new List<Error>();

        var read = await cluster.Manager.ReadAsync(
            new() { Path = victim.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        refusals.Add(read.Error!);

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = victim.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        refusals.Add(deleted.Error!);

        foreach (var error in refusals) {
            error.Message.Contains("permission", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(error.Message);
            error.Message.Contains("read", StringComparison.OrdinalIgnoreCase).ShouldBeFalse(error.Message);
            error.Message.Contains(IsolationCluster.VictimUser, StringComparison.OrdinalIgnoreCase)
                .ShouldBeFalse(error.Message);
            error.Message.Contains("   at ", StringComparison.Ordinal).ShouldBeFalse(error.Message);
            error.Details.ShouldBeEmpty("a refusal with details is a refusal that explained itself");
        }
    }

    /// <summary>The providers under attack.</summary>
    public static TheoryData<IsolationTarget> Targets => IsolationCatalog.All;

    static CallerContext Attacker() =>
        IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser);

    static CallerContext Victim() =>
        IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser);

    /// <summary>Creates a real resource in the victim's tenant and returns its resolved address.</summary>
    async Task<ResourceId> VictimResourceAsync(IsolationTarget target, string name) {
        var id = await cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        return IsolationCluster
            .Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);
    }

    static void ShouldBeInvisible<T>(Result<T> refused, ResourceId victim)
        where T : notnull {
        refused.IsFailure.ShouldBeTrue($"'{victim.Path}' was reachable from another tenant");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        refused.Error.Code.ShouldNotBe(
            ErrorCode.AuthorizationFailed,
            "403 across a tenant boundary confirms the resource exists — docs/plan/07 § The "
            + "enforcement seam calls that an enumeration oracle"
        );
    }
}
