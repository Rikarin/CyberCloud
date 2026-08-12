using CyberCloud.Vault.Tests.Infrastructure;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     The four ways a resolve fails, against a real OpenBao, and the one thing none of them is.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE WHOLE SUITE IS ONE ASSERTION SAID FIVE WAYS: A FAILED RESOLVE IS NEVER AN EMPTY
///         SECRET.</b> <c>UnavailableSecretResolver</c> makes that argument for the unwired default —
///         <i>"an empty password reaching a rendered manifest is a database with no password, applied
///         to a real cluster, reported as a successful provision"</i> — and it applies with more
///         force to the wired one, which has four ways to end up with nothing where the unwired one
///         has a single refusal.
///     </para>
///     <para>
///         ⚠ <b>Against a real OpenBao because every one of these is <i>its</i> behaviour.</b> That a
///         missing path answers <c>404</c> with an empty error list, that a pinned version which
///         never existed answers the same, that a policy denial answers <c>403</c> with
///         <c>["permission denied"]</c>, that an existing path with a missing key answers <c>200</c>
///         — none of it is ours, and a stub would encode the belief rather than check it. Two of
///         these turned out differently from what the API documentation implied.
///     </para>
/// </remarks>
[Collection(OpenBaoSuite.Name)]
public sealed class ResolveFailureTests(OpenBaoFixture vault) {
    const string Path = "tenants/9f2b/postgres/main";
    const string Password = "correct-horse-battery-staple";

    [Fact]
    public async Task AResolveThatWorksReturnsTheValueAndNothingElse() {
        await Seed();

        var token = await Reader();
        var resolved = await vault.Resolver(token).ResolveAsync(
            new() { Path = Path, Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsSuccess.ShouldBeTrue(resolved.Error?.Message);
        resolved.GetValueOrThrow().ShouldBe(Password);
    }

    [Fact]
    public async Task APathThatIsNotThereIsNotAnEmptySecret() {
        await Seed();

        var resolved = await vault.Resolver(await Reader()).ResolveAsync(
            new() { Path = "tenants/9f2b/postgres/never-written", Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        // ⚠ ResourceNotFound and not InternalError: the vault is healthy and the platform is
        // permitted; the secret is simply not there, and an operator's fix is to write it.
        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task APinnedVersionThatWasNeverWrittenIsAlsoNotAnEmptySecret() {
        await Seed();

        // ⚠ VERIFIED AGAINST A RUNNING OPENBAO RATHER THAN INFERRED, AND THE ANSWER IS THE
        // UNCOMFORTABLE ONE: a ?version= that does not exist comes back as a bare 404 with an EMPTY
        // error list, exactly like a path that was never written. So the client cannot tell "the
        // secret is gone" from "the version this resource pinned was destroyed by a rotation", and
        // VaultFailures.NotFound says so in the operator detail instead of guessing.
        var resolved = await vault.Resolver(await Reader()).ResolveAsync(
            new() { Path = Path, Field = "adminPassword", Version = "99" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task APermissionDenialIsNotAMissingSecret() {
        await Seed();

        // A real token carrying a real policy that covers a different path. The read is refused by
        // OpenBao, not by anything here.
        var (token, _) = await vault.IssueTokenAsync(["elsewhere"]);

        var resolved = await vault.Resolver(token).ResolveAsync(
            new() { Path = Path, Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(
            ErrorCode.AuthorizationFailed,
            "a policy that does not cover the path is a different incident from a path that is not "
            + "there, and reporting it as ResourceNotFound sends an operator to write a secret that "
            + "already exists"
        );
    }

    [Fact]
    public async Task AnUncoveredPathIsAPermissionDenialAndNotAMissingSecret() {
        await Seed();

        // ⚠ THE FINDING THAT CORRECTED THIS SUITE, AND IT IS A CONSTRAINT ON THE WHOLE FOUR-WAY
        // TAXONOMY RATHER THAN A CURIOSITY.
        //
        // OpenBao evaluates policy BEFORE existence. A path the platform's token does not cover
        // answers 403 whether or not a secret is stored there — so "the secret is missing" is only
        // an OBSERVABLE answer for paths inside the policy. Outside it, ResourceNotFound is
        // unreachable and everything collapses into AuthorizationFailed.
        //
        // Which makes docs/plan/18's "the platform holds a broad token per namespace" load-bearing
        // for something the document does not connect it to: a least-privilege token scoped to each
        // exact path would be tighter and would destroy the platform's ability to tell an operator
        // "nothing was ever written there". A prefix-scoped policy per namespace keeps both.
        var (scoped, _) = await vault.IssueTokenAsync(["elsewhere"]);

        var resolved = await vault.Resolver(scoped).ResolveAsync(
            new() { Path = "tenants/9f2b/postgres/never-written", Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(
            ErrorCode.AuthorizationFailed,
            "the path does not exist AND is not covered, and OpenBao answers the second — so this "
            + "client cannot report the first, and must not guess at it"
        );
    }

    [Fact]
    public async Task AVaultThatIsNotThereIsNotAMissingSecretEither() {
        // ⚠ A port nothing is listening on, which is what a sealed vault, a crashed pod or a
        // NetworkPolicy looks like from here.
        var options = vault.Options();
        options.Address = "http://127.0.0.1:1";
        options.RequestTimeout = TimeSpan.FromSeconds(5);

        var resolved = await vault.Resolver("irrelevant", options).ResolveAsync(
            new() { Path = Path, Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(ErrorCode.InternalError);
        resolved.Error.Message.ShouldNotContain("127.0.0.1");
    }

    [Fact]
    public async Task APathThatExistsWithoutTheFieldIsTheQUIETESTFailureAndIsStillAFailure() {
        await Seed();

        // ⚠ THE ONE THAT DOES NOT LOOK LIKE A FAILURE. OpenBao answers 200 and a well-formed
        // data.data object; the field is simply absent from it. The obvious implementation reads a
        // missing key into a null string, coalesces it to "", and hands back a SUCCESSFUL result
        // holding an empty password — which is precisely the outcome every other row here is about.
        var resolved = await vault.Resolver(await Reader()).ResolveAsync(
            new() { Path = Path, Field = "admin_password" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue(
            "a kv-v2 read of an existing path with a missing key is a 200, and treating that as a "
            + "value is how an empty password reaches a rendered manifest"
        );

        resolved.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task AFieldThatReallyHoldsAnEmptyStringIsRefusedToo() {
        await Seed();

        // ⚠ The last gate, and the only one where OpenBao is behaving perfectly. Somebody wrote ""
        // to that field — a provisioning job that failed halfway, a template that rendered nothing —
        // and every layer between here and the manifest would carry it faithfully.
        var resolved = await vault.Resolver(await Reader()).ResolveAsync(
            new() { Path = Path, Field = "emptyOnPurpose" },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AnEmptyHandleIsRefusedWithoutAskingTheVault() {
        // ⚠ No seeding and a token that is not a token: if this reached OpenBao it would come back
        // as a permission denial rather than as the handle fault it is. SecretRef.IsEmpty's own
        // remarks make the argument — "an address that resolves to nothing, and a caller that passed
        // one meant to pass a real one".
        var resolved = await vault.Resolver("not-a-token").ResolveAsync(
            new() { Path = Path },
            TestContext.Current.CancellationToken
        );

        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(ErrorCode.InternalError);
    }

    [Fact]
    public async Task ARevokedTokenIsRetriedOnceAndOnlyOnce() {
        await Seed();

        // ⚠ THE CASE THE EXPIRY SKEW CANNOT COVER. An operator responding to a suspected compromise
        // revokes the platform's token; OpenBao then refuses a token whose lease says it is good for
        // another month. Without the retry, every provision on this silo fails until the lease runs
        // out — which for OpenBao's default TTL means "until somebody restarts the silo".
        var (token, accessor) = await vault.IssueTokenAsync(["reader"]);
        await vault.RevokeAsync(accessor);

        var source = new FixedTokenSource(token);
        var resolver = new OpenBaoSecretResolver(
            new() { Timeout = TimeSpan.FromSeconds(10) },
            source,
            vault.Options()
        );

        var resolved = await resolver.ResolveAsync(
            new() { Path = Path, Field = "adminPassword" },
            TestContext.Current.CancellationToken
        );

        // The stand-in source hands out the same dead token twice, so the second attempt is refused
        // as well — which is the correct end state for a token that is genuinely revoked rather than
        // merely stale.
        resolved.IsFailure.ShouldBeTrue();
        resolved.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        source.Invalidated.ShouldBe(1, "a refused read must throw the token away exactly once");
        source.Asked.ShouldBe(
            2,
            "once for the first attempt and once for the retry — a loop here would re-login on every "
            + "genuine policy denial, and each login is a TokenReview call against the cluster's API "
            + "server"
        );
    }

    /// <summary>Writes the secret and the two policies every row above reads through.</summary>
    /// <remarks>
    ///     ⚠ Idempotent, and called per test rather than once for the collection, because a shared
    ///     fixture that is mutated by one test and read by another is the kind of ordering
    ///     dependence that only fails under a parallel run.
    /// </remarks>
    async Task Seed() {
        await vault.WriteSecretAsync(
            Path,
            new Dictionary<string, string> {
                ["adminPassword"] = Password,
                ["username"] = "cc_admin",
                ["emptyOnPurpose"] = string.Empty,
            }
        );

        // ⚠ A PREFIX AND NOT THE EXACT PATH, AND FINDING OUT WHY CORRECTED THIS SUITE'S FIRST
        // VERSION. Written as `path "secret/data/tenants/9f2b/postgres/main"`, the "a path that is
        // not there" row came back AuthorizationFailed rather than ResourceNotFound — because
        // OpenBao evaluates policy BEFORE existence, so an uncovered path is 403 whether or not
        // anything is stored at it. See AnUncoveredPathIsAPermissionDenialAndNotAMissingSecret for
        // what that means for the taxonomy.
        await vault.WritePolicyAsync(
            "reader",
            $"path \"{OpenBaoFixture.KvMount}/data/tenants/9f2b/*\" {{ capabilities = [\"read\"] }}"
        );

        await vault.WritePolicyAsync(
            "elsewhere",
            $"path \"{OpenBaoFixture.KvMount}/data/tenants/other/*\" {{ capabilities = [\"read\"] }}"
        );
    }

    async Task<string> Reader() => (await vault.IssueTokenAsync(["reader"])).Token;
}
