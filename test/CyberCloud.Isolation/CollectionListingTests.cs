namespace CyberCloud.Isolation;

/// <summary>
///     The collection <c>GET</c>, against the <b>real</b> ReBAC engine. A listing must not return a
///     name its caller could not have read one at a time.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the suite the endpoint had to be safe in before it could ship.</b> Every
///         other verb answers about one resource the caller named, so an oracle there is one bit per
///         probe; a listing answers about resources the caller did <i>not</i> name, so an oracle here
///         hands over a whole group's namespace in one request. docs/plan/07 § The enforcement seam:
///         <i>"a competitor can discover a customer's resource names by probing"</i> — a listing
///         without a per-member filter removes the probing.
///     </para>
///     <para>
///         ⚠ <b>It runs against <c>ReBacResourceAuthorizer</c> over <c>CyberCloudSchema</c> and not
///         against a double.</b> <c>CyberCloud.ResourceManager.Tests.CollectionListingTests</c> pins
///         the filter's <i>shape</i> — one check per member, no trace of a dropped one, paging that
///         advances past it — against <c>SwitchableAuthorizer</c>. What it cannot pin is the
///         <i>verdict</i>, because the double answers whatever its author believed. That gap is how
///         <c>ReBacResourceAuthorizer</c>'s <c>resourcegroup</c> casing bug survived: every test of
///         the write path substituted a double, so nothing had driven a create through the real
///         engine.
///     </para>
///     <para>
///         ⚠ <b>Every attack runs against resources that genuinely exist and are genuinely
///         readable by their owner.</b> A listing over an empty group answers empty by accident, and
///         a filter that returned nothing at all would pass such a test.
///     </para>
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class CollectionListingTests(IsolationCluster cluster) {
    /// <summary>
    ///     ⚠ <b>A collection path in another tenant returns nothing and says nothing — the same
    ///     absence a resource in another tenant gets.</b>
    /// </summary>
    /// <remarks>
    ///     The refusal comes from the tenant comparison in <c>ListAsync</c>'s step 1, which runs
    ///     before the registry is consulted and before the group grain is addressed. Both matter: the
    ///     grain is reached through <c>ForTenant</c>, so a missing comparison would land in the
    ///     <i>attacker's</i> tenant and answer empty rather than leaking — which is safe and is also
    ///     an oracle in the other direction, because "empty" for a group the attacker has and
    ///     "empty" for one they do not would still be one answer for two facts.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ListingAnotherTenantsCollectionIs404(IsolationTarget target) {
        await VictimResourceAsync(target, "list-cross-tenant");

        var theirs = CollectionOf(target, IsolationCluster.Victim, IsolationCluster.VictimSubscription);

        var refused = await cluster.Manager.ListAsync(
            new() { Path = theirs.Path, ApiVersion = target.ApiVersion, Caller = Attacker() },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue($"'{theirs.Path}' was listable from another tenant");
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        refused.Error.Code.ShouldNotBe(
            ErrorCode.AuthorizationFailed,
            "403 across a tenant boundary confirms the collection exists — docs/plan/07 § The "
            + "enforcement seam calls that an enumeration oracle"
        );
    }

    /// <summary>
    ///     ⚠ <b>The name of a resource the caller cannot read is not in the page, and the whole page
    ///     is empty rather than partly full.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the assertion the endpoint exists to satisfy and the one a doubled authorizer
    ///         cannot make. The attacker is a real subject in their own tenant with real rights in
    ///         their own group; the victim's group is listed <i>through the attacker's own tenant's
    ///         address</i> in the sibling case below, and here through the victim's, so that both the
    ///         tenant check and the per-member check are exercised rather than only whichever runs
    ///         first.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It asserts on the resource <i>names</i> and not only on the count.</b> A filter
    ///         that returned the right number of the wrong resources would pass a count assertion,
    ///         and the names are what a listing leaks.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ACallerWhoHoldsNothingOnAGroupSeesNoneOfItsResources(IsolationTarget target) {
        await VictimResourceAsync(target, "list-invisible-one");
        await VictimResourceAsync(target, "list-invisible-two");

        // The victim's own listing is the control: these resources are real and are readable, so an
        // empty answer below is the filter working and not the group being empty.
        var owners = await cluster.Manager.ListAsync(
            new() {
                Path = CollectionOf(target, IsolationCluster.Victim, IsolationCluster.VictimSubscription).Path,
                ApiVersion = target.ApiVersion,
                Caller = Victim()
            },
            TestContext.Current.CancellationToken
        );

        owners.IsSuccess.ShouldBeTrue(owners.Error?.Message);
        owners.GetValueOrThrow().Resources.ShouldNotBeEmpty(
            "the control failed: the victim cannot list their own resources, so the case below "
            + "would pass for the wrong reason"
        );

        // ⚠ THE ATTACK ITSELF, run from inside the victim's tenant so that the tenant comparison is
        // not what refuses it. A caller in the right tenant with no role on the group is exactly the
        // caller the per-member Check exists for — and is the caller a filter built on the RESOURCE
        // GROUP alone would let through the moment they held any role anywhere in it.
        var intruder = IsolationCluster.Caller(IsolationCluster.Victim, "nobody-at-all");

        var listed = await cluster.Manager.ListAsync(
            new() {
                Path = CollectionOf(target, IsolationCluster.Victim, IsolationCluster.VictimSubscription).Path,
                ApiVersion = target.ApiVersion,
                Caller = intruder
            },
            TestContext.Current.CancellationToken
        );

        listed.IsSuccess.ShouldBeTrue(
            "a caller who may see nothing gets an empty page and not a refusal — a refusal would "
            + "distinguish a group they may not read from one that does not exist"
        );

        listed.GetValueOrThrow().Resources.ShouldBeEmpty(
            "a listing returned resources to a caller who holds no relation on any of them, which "
            + "is the whole group's namespace in one request"
        );
    }

    /// <summary>
    ///     ⚠ <b>Group-scoped rights are what a listing grants on, and they stop at the group.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The attacker is made <c>owner</c> of the resource group in <i>their own</i>
    ///         subscription, which is a real grant they legitimately hold. It must give them nothing
    ///         in the victim's group of the same name — <c>ReBacResourceAuthorizer.GroupObjectId</c>
    ///         qualifies a group by its subscription for exactly this reason, and a listing is the
    ///         first endpoint where getting that wrong returns rows rather than one resource.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both directions are asserted.</b> That the attacker sees nothing of the victim's
    ///         is the security half; that they see their <i>own</i> is what stops the case passing
    ///         because the filter refuses everybody.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task GroupRightsInOneSubscriptionListNothingInAnother(IsolationTarget target) {
        await VictimResourceAsync(target, "list-scoped-victim");

        await cluster.GrantGroupOwnerAsync(
            IsolationCluster.Attacker,
            IsolationCluster.AttackerSubscription,
            IsolationCluster.AttackerUser
        );

        await cluster.CreateAsync(
            target,
            "list-scoped-mine",
            IsolationCluster.Attacker,
            IsolationCluster.AttackerSubscription,
            IsolationCluster.AttackerUser
        );

        var mine = await cluster.Manager.ListAsync(
            new() {
                Path = CollectionOf(target, IsolationCluster.Attacker, IsolationCluster.AttackerSubscription).Path,
                ApiVersion = target.ApiVersion,
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        mine.IsSuccess.ShouldBeTrue(mine.Error?.Message);

        mine.GetValueOrThrow()
            .Resources
            .Select(x => x.Name)
            .ShouldContain(
                "list-scoped-mine",
                "the control failed: an owner of a group cannot list the resources in it"
            );

        mine.GetValueOrThrow()
            .Resources
            .Select(x => x.Name)
            .ShouldNotContain(
                "list-scoped-victim",
                "a listing crossed a subscription boundary — the two groups share a name and "
                + "GroupObjectId is what keeps them different authorization objects"
            );
    }

    /// <summary>
    ///     ⚠ <b>A listing that finds nothing and a listing whose every member is invisible are the
    ///     same answer, byte for byte.</b>
    /// </summary>
    /// <remarks>
    ///     The status code alone does not close this oracle: two empty pages that differed in any
    ///     member — a count, a token, a header — would be a way to ask "does this group hold anything
    ///     I cannot see". <c>ResourceListPage</c> carries no count for this reason, and this is the
    ///     assertion that keeps one from being added.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnEmptyCollectionAndAFullyFilteredOneAreIndistinguishable(IsolationTarget target) {
        await VictimResourceAsync(target, "list-indistinguishable");

        var intruder = IsolationCluster.Caller(IsolationCluster.Victim, "nobody-at-all");

        var filtered = await cluster.Manager.ListAsync(
            new() {
                Path = CollectionOf(target, IsolationCluster.Victim, IsolationCluster.VictimSubscription).Path,
                ApiVersion = target.ApiVersion,
                Caller = intruder
            },
            TestContext.Current.CancellationToken
        );

        // ⚠ THE SAME REQUEST, RESUMED PAST THE LAST MEMBER — a walk whose candidate set is empty
        // before the filter ever runs. That is the honest way to build "genuinely nothing here" in a
        // suite with one resource group: the alternative, a second group, would differ from the first
        // in ways the answer could legitimately depend on.
        var empty = await cluster.Manager.ListAsync(
            new() {
                Path = CollectionOf(target, IsolationCluster.Victim, IsolationCluster.VictimSubscription).Path,
                ApiVersion = target.ApiVersion,
                Caller = intruder,
                Continuation = "￿"
            },
            TestContext.Current.CancellationToken
        );

        filtered.IsSuccess.ShouldBeTrue(filtered.Error?.Message);
        empty.IsSuccess.ShouldBeTrue(empty.Error?.Message);

        filtered.GetValueOrThrow().Resources.ShouldBeEmpty();
        empty.GetValueOrThrow().Resources.ShouldBeEmpty();
        filtered.GetValueOrThrow().Continuation.ShouldBe(empty.GetValueOrThrow().Continuation);
    }

    /// <summary>The providers under attack.</summary>
    public static TheoryData<IsolationTarget> Targets => IsolationCatalog.All;

    static CallerContext Attacker() =>
        IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser);

    static CallerContext Victim() =>
        IsolationCluster.Caller(IsolationCluster.Victim, IsolationCluster.VictimUser);

    /// <summary>The collection a target's resources live in, in one tenant's subscription.</summary>
    static ResourceCollectionId CollectionOf(IsolationTarget target, Guid tenant, Guid subscription) =>
        ResourceCollectionId.Of(IsolationCluster.Address(target, "probe", tenant, subscription));

    /// <summary>Creates a real, converged, readable resource in the victim's tenant.</summary>
    Task<Guid> VictimResourceAsync(IsolationTarget target, string name) =>
        cluster.CreateAsync(
            target,
            name,
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );
}
