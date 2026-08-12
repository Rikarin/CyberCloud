using CyberCloud.Vault.Tests.Infrastructure;

namespace CyberCloud.Vault.Tests;

/// <summary>
///     Minting a credential into OpenBao: once, and only once, whatever the caller does.
/// </summary>
/// <remarks>
///     <para>
///         <b>docs/plan/12 § The pattern, once, piece 5 is this class's subject.</b> That document's
///         table used to assign credential provisioning to <c>ISecretResolver</c>, which reads and
///         cannot provision anything; the row is corrected and <see cref="ISecretWriter" /> is the
///         seam. Until it existed nothing in the tree wrote to a vault, which is why
///         <c>CyberCloud.Storage/accounts</c> rendered a reference to a <c>Secret</c> nobody could
///         produce.
///     </para>
///     <para>
///         ⚠ <b>AGAINST A REAL OPENBAO, BECAUSE MINT-ONCE IS <i>ITS</i> BEHAVIOUR AND NOT OURS.</b>
///         The whole semantic rests on <c>kv-v2</c>'s <c>options.cas</c>: <c>cas: 0</c> writes only
///         when the path has never held anything. What a stub would check is that this class sends
///         the field — which is a restatement of the code, not a test of the property. Two things
///         here came out differently from what the API documentation implied: a failed check-and-set
///         is a <c>400</c> rather than a <c>409</c>, and the reason arrives only in the body.
///     </para>
///     <para>
///         ⚠ <b>The property under test is what a reconciler needs, not what a vault API offers.</b>
///         A reconcile pass runs over and over on a converged resource, and each pass offers a fresh
///         candidate key pair. If the second write won, the tenant's working credential would be
///         replaced on every reminder — a rotation nobody asked for, with no grace period, against a
///         data plane still holding the old one.
///     </para>
/// </remarks>
[Collection(OpenBaoSuite.Name)]
public sealed class MintOnceTests(OpenBaoFixture vault) {
    const string Path = "tenants/7c31/storage/accounts/first";

    [Fact]
    public async Task AMintWritesTheFieldsAndReportsThatItWrote() {
        var path = Unique();

        var minted = await vault.Writer(await Minter()).MintAsync(
            path,
            Pair("AKIAFIRST", "first-secret"),
            TestContext.Current.CancellationToken
        );

        minted.IsSuccess.ShouldBeTrue(minted.Error?.Message);
        minted.GetValueOrThrow().Minted.ShouldBeTrue();

        var read = await vault.Resolver(await Reader()).ResolveAsync(
            new() { Path = path, Field = "secretAccessKey" },
            TestContext.Current.CancellationToken
        );

        read.IsSuccess.ShouldBeTrue(read.Error?.Message);
        read.GetValueOrThrow().ShouldBe("first-secret");
    }

    [Fact]
    public async Task ASecondMintLeavesTheFirstCredentialAloneAndSaysSo() {
        // ⚠ FAILURE CLASS (d), AT THE SEAM. The reconciler drives this on every pass with a NEW
        // candidate pair, so a writer that overwrote would rotate a tenant's key out from under them
        // on a reminder. Success carrying Minted: false is the right answer rather than a conflict —
        // the caller's goal is that a credential exists, and the second pass achieves it.
        var path = Unique();
        var writer = vault.Writer(await Minter());

        (await writer.MintAsync(path, Pair("AKIAFIRST", "first-secret"), TestContext.Current.CancellationToken))
            .GetValueOrThrow()
            .Minted
            .ShouldBeTrue();

        var second = await writer.MintAsync(
            path,
            Pair("AKIASECOND", "second-secret"),
            TestContext.Current.CancellationToken
        );

        second.IsSuccess.ShouldBeTrue(
            "a path that already holds a credential is the ORDINARY outcome of every reconcile pass "
            + "after the first, and reporting it as a failure would fail every one of them"
        );

        second.GetValueOrThrow().Minted.ShouldBeFalse();

        var read = await vault.Resolver(await Reader()).ResolveAsync(
            new() { Path = path, Field = "secretAccessKey" },
            TestContext.Current.CancellationToken
        );

        read.GetValueOrThrow().ShouldBe(
            "first-secret",
            "the second mint overwrote the credential the tenant is already using"
        );
    }

    [Fact]
    public async Task AMintWithNoFieldsIsRefusedRatherThanOccupyingThePath() {
        // ⚠ An empty document is worse than no document: the path exists, every reader reports a
        // missing field, and cas=0 never fires again — so the credential could never be minted.
        var minted = await vault.Writer(await Minter()).MintAsync(
            Unique(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken
        );

        minted.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task AnEmptyValueIsRefusedBecauseTheResolverWouldRefuseItComingBack() {
        // OpenBaoSecretResolver refuses an empty string on the way out — its last gate before a value
        // reaches a manifest. Writing one produces a secret nothing can ever read.
        var minted = await vault.Writer(await Minter()).MintAsync(
            Unique(),
            Pair("AKIA", ""),
            TestContext.Current.CancellationToken
        );

        minted.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task APolicyThatCannotWriteIsRefusedAndTheRefusalSaysNothingAboutTheVault() {
        // The tenant-facing half of a 403. VaultFailures.PermissionDenied keeps the address, the
        // mount and the role in the operator's log and out of the caller's message.
        var reader = await Reader();

        var minted = await vault.Writer(reader).MintAsync(
            Unique(),
            Pair("AKIA", "nope"),
            TestContext.Current.CancellationToken
        );

        minted.IsFailure.ShouldBeTrue("a read-only token minted a credential");
        minted.Error!.Message.ShouldNotContain(vault.Address, Case.Sensitive);
        minted.Error.Message.ShouldNotContain("cc-silo", Case.Sensitive);
    }

    [Fact]
    public async Task AMintDoesNotDisturbANeighbouringPath() {
        // Mint-once is per path, and a writer that keyed on anything coarser would make the second
        // account in a tenant unmintable.
        var writer = vault.Writer(await Minter());
        var first = Unique();
        var second = Unique();

        await writer.MintAsync(first, Pair("AKIAONE", "one"), TestContext.Current.CancellationToken);

        var minted = await writer.MintAsync(
            second,
            Pair("AKIATWO", "two"),
            TestContext.Current.CancellationToken
        );

        minted.GetValueOrThrow().Minted.ShouldBeTrue();

        var resolver = vault.Resolver(await Reader());

        (await resolver.ResolveAsync(
            new() { Path = first, Field = "secretAccessKey" },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow().ShouldBe("one");

        (await resolver.ResolveAsync(
            new() { Path = second, Field = "secretAccessKey" },
            TestContext.Current.CancellationToken
        )).GetValueOrThrow().ShouldBe("two");
    }

    static Dictionary<string, string> Pair(string keyId, string secret) =>
        new(StringComparer.Ordinal) {
            ["accessKeyId"] = keyId, ["secretAccessKey"] = secret
        };

    /// <summary>A path no other test in this class has used.</summary>
    /// <remarks>
    ///     ⚠ Unique per call, because mint-once makes every path single-use for the container's life
    ///     and the fixture is shared across the suite. A fixed path would make the second test to run
    ///     see the first one's credential.
    /// </remarks>
    static string Unique() => Path + "-" + Guid.NewGuid().ToString("N");

    async Task<string> Minter() {
        await vault.WritePolicyAsync(
            "minter",
            $"path \"{OpenBaoFixture.KvMount}/data/tenants/7c31/*\" "
            + "{ capabilities = [\"create\", \"update\", \"read\"] }"
        );

        return (await vault.IssueTokenAsync(["minter"])).Token;
    }

    async Task<string> Reader() {
        await vault.WritePolicyAsync(
            "mint-reader",
            $"path \"{OpenBaoFixture.KvMount}/data/tenants/7c31/*\" {{ capabilities = [\"read\"] }}"
        );

        return (await vault.IssueTokenAsync(["mint-reader"])).Token;
    }
}
