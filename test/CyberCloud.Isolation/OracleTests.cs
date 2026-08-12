using CyberCloud.ResourceManager;
using System.Globalization;

namespace CyberCloud.Isolation;

/// <summary>
///     The ways a refusal can still answer a question it was not asked.
/// </summary>
/// <remarks>
///     ⚠ <b>Every test here is an attempt to get a <i>different</i> answer, not a forbidden one.</b>
///     A boundary that always says 404 is only as good as the paths that reach the 404: if a bad
///     api-version, an unknown action or an unparseable body is answered <i>before</i> the tenant
///     comparison, the shape of the error tells an attacker something about a tenant that is not
///     theirs. The write path's own ordering is what makes these pass, so they are the tests that
///     fail if somebody moves the tenant check down.
/// </remarks>
[Collection(IsolationSuite.Name)]
public sealed class OracleTests(IsolationCluster cluster) {
    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnUnknownApiVersionOnAnotherTenantsPathIsStill404(IsolationTarget target) {
        // If the registry were consulted before the tenant comparison, this would answer
        // InvalidApiVersion and helpfully list the versions — telling the attacker their path parsed,
        // named a real provider, and reached the registry.
        var theirs = Victim(target, "version-probe");

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = theirs.Path,
                ApiVersion = "2999-12-31",
                Verb = WriteVerb.Put,
                Body = target.Body(IsolationCluster.ClusterId),
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "the api-version was validated before the tenant was compared"
        );
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnInvalidBodyOnAnotherTenantsPathIsStill404(IsolationTarget target) {
        // Step 2 validates the body. If it ran before the tenant comparison, the JSON-Pointer target in
        // the error would describe the victim's schema to somebody who cannot see the victim.
        var theirs = Victim(target, "body-probe");

        var refused = await cluster.Manager.WriteAsync(
            new() {
                Path = theirs.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Put,
                Body = """{"thisIsNotAProperty":1}""",
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Target.ShouldBeNullOrEmpty("the error pointed at a field in the victim's schema");
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnUnknownActionOnAnotherTenantsResourceIsStill404(IsolationTarget target) {
        // ActionAsync looks the action up and, when it is unknown, answers with the list of declared
        // actions. That list must not be reachable through a path in somebody else's tenant.
        var theirs = Victim(target, "action-probe");

        var refused = await cluster.Manager.ActionAsync(
            new() {
                Path = theirs.Path,
                ApiVersion = target.ApiVersion,
                Verb = WriteVerb.Post,
                Action = "thisActionDoesNotExist",
                Caller = Attacker()
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
        refused.Error.Message.Contains(target.Action, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
            "the declared action list was disclosed through a cross-tenant path"
        );
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task AnEmptyCallerTenantReachesNothing(IsolationTarget target) {
        // A gateway bug, or a token with no tenant claim, must not produce a caller who matches a path
        // by accident. Guid.Empty is the value a default-constructed CallerContext carries.
        var theirs = Victim(target, "empty-caller");

        var refused = await cluster.Manager.ReadAsync(
            new() {
                Path = theirs.Path,
                ApiVersion = target.ApiVersion,
                Caller = new() { TenantId = Guid.Empty, SubjectType = "user", SubjectId = "nobody" }
            },
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ATenantGuidInAnAlternativeFormatDoesNotBypassTheComparison(IsolationTarget target) {
        // Guid.TryParse accepts B, N, D, P and X formats. If the path parser took any of them and the
        // caller comparison took another, the two could disagree about which tenant a request is for.
        var theirs = Victim(target, "format-probe");

        var braced = theirs.Path.Replace(
            IsolationCluster.Victim.ToString("D", CultureInfo.InvariantCulture),
            "{" + IsolationCluster.Victim.ToString("D", CultureInfo.InvariantCulture) + "}",
            StringComparison.Ordinal
        );

        var upper = theirs.Path.Replace(
            IsolationCluster.Victim.ToString("D", CultureInfo.InvariantCulture),
            IsolationCluster.Victim.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant(),
            StringComparison.Ordinal
        );

        foreach (var path in new[] { braced, upper }) {
            var refused = await cluster.Manager.ReadAsync(
                new() { Path = path, ApiVersion = target.ApiVersion, Caller = Attacker() },
                TestContext.Current.CancellationToken
            );

            refused.IsFailure.ShouldBeTrue($"'{path}' was reachable from another tenant");
            refused.Error!.Code.ShouldNotBe(ErrorCode.AuthorizationFailed);
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task ARefusedCrossTenantCreateDrawsNoQuotaFromEitherTenant(IsolationTarget target) {
        // ⚠ A SIDE CHANNEL WITH TEETH. If a cross-tenant create reserved before it refused, an attacker
        // could exhaust the victim's subscription allowance one refused request at a time — no data
        // read, no data written, and the victim cannot create anything.
        var quota = cluster.For(IsolationCluster.Victim)
            .GetGrain<IQuotaGrain>(GrainKeys.Subscription(IsolationCluster.VictimSubscription));

        var before = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();

        var theirs = Victim(target, "quota-probe");

        for (var i = 0; i < 5; i++) {
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
        }

        var after = (await quota.ListLeasesAsync()).GetValueOrThrow().Select(x => x.LeaseId).ToHashSet();
        after.ExceptWith(before);

        after.ShouldBeEmpty("a refused cross-tenant create reserved quota in the victim's subscription");
    }

    [Fact]
    public async Task ACallerWhoCanReadButNotDeleteStillGets403() {
        // ⚠ THE POSITIVE CONTROL, AND WITHOUT IT THE WHOLE SUITE IS VACUOUS. "404, never 403" is
        // trivially satisfied by a platform that never returns 403 — and that platform would be
        // broken in the other direction, telling a legitimate operator "no such resource" about a
        // resource they can see. docs/plan/07 § The enforcement seam calls the distinction "a real and
        // useful" one, so this asserts it is reachable.
        var target = IsolationCatalog.Targets[0];

        var id = await cluster.CreateAsync(
            target,
            "reader-control",
            IsolationCluster.Victim,
            IsolationCluster.VictimSubscription,
            IsolationCluster.VictimUser
        );

        // A second user in the victim's tenant with READER on the group: read yes, delete no.
        const string reader = "reginald";

        await cluster.WriteTupleAsync(
            IsolationCluster.Victim,
            Authorization.Contracts.ObjectRef.Of(
                Authorization.Contracts.ObjectTypes.ResourceGroup,
                IsolationCluster.VictimSubscription.ToString("N", CultureInfo.InvariantCulture)
                + "-"
                + IsolationCluster.Group
            ),
            Authorization.Contracts.Relations.Reader,
            Authorization.Contracts.SubjectRef.Of(Authorization.Contracts.ObjectTypes.User, reader)
        );

        var address = IsolationCluster
            .Address(target, "reader-control", IsolationCluster.Victim, IsolationCluster.VictimSubscription)
            .WithId(id);

        var caller = IsolationCluster.Caller(IsolationCluster.Victim, reader);

        var read = await cluster.Manager.ReadAsync(
            new() { Path = address.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(
            "a reader on the resource group cannot read a resource in it, so the ReBAC inheritance "
            + "this suite depends on is not working: " + read.Error?.Message
        );

        var deleted = await cluster.Manager.DeleteAsync(
            new() { Path = address.Path, ApiVersion = target.ApiVersion, Caller = caller },
            TestContext.Current.CancellationToken
        );

        deleted.IsFailure.ShouldBeTrue();
        deleted.Error!.Code.ShouldBe(
            ErrorCode.AuthorizationFailed,
            "403 is unreachable, which makes 'never 403' true for the wrong reason"
        );
    }

    [Fact]
    public void TheResourceGroupObjectTypeTheSeamChecksIsOneTheSchemaDefines() {
        // ⚠ THE DEFECT THIS SUITE WAS WRITTEN TO CATCH, KEPT AS A REGRESSION.
        //
        // ReBacResourceAuthorizer checks a create against its parent resource group, naming the object
        // type as a string; CyberCloudSchema defines the type, also as a string; and AuthorizationSchema
        // looks types up with StringComparer.Ordinal. The seam said "resourcegroup" and the schema says
        // "resourceGroup", so every check on a resource that did not exist yet answered SchemaInvalid,
        // which the seam correctly renders as 404 — and EVERY CREATE FAILED, with the reason only in a
        // log line.
        //
        // It survived because no test had ever driven a create through the real authorizer. The two
        // constants still live in two assemblies that cannot share one, so this asserts the strings
        // agree rather than trusting that they do.
        ReBacResourceAuthorizer.ResourceGroupObjectType.ShouldBe(Authorization.Contracts.ObjectTypes.ResourceGroup);
        ReBacResourceAuthorizer.ResourceObjectType.ShouldBe(Authorization.Contracts.ObjectTypes.Resource);

        Authorization.CyberCloudSchema.Instance.HasType(ReBacResourceAuthorizer.ResourceGroupObjectType)
            .ShouldBeTrue();

        Authorization.CyberCloudSchema.Instance.HasType(ReBacResourceAuthorizer.ResourceObjectType)
            .ShouldBeTrue();
    }

    /// <summary>The providers under attack.</summary>
    public static TheoryData<IsolationTarget> Targets => IsolationCatalog.All;

    static CallerContext Attacker() =>
        IsolationCluster.Caller(IsolationCluster.Attacker, IsolationCluster.AttackerUser);

    static ResourceId Victim(IsolationTarget target, string name) =>
        IsolationCluster.Address(target, name, IsolationCluster.Victim, IsolationCluster.VictimSubscription);
}
