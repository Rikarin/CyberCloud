using System.Text.Json;

namespace CyberCloud.Providers.KeyVault.Tests;

/// <summary>
///     The reconciler against the real grain: the root is minted once and read back, a park keeps the
///     contents, and a purge destroys them — told apart by <see cref="ReconcileContext.Parking" />.
/// </summary>
public sealed class KeyVaultReconcilerTests(VaultSilo silo) : IClassFixture<VaultSilo> {
    [Fact]
    public async Task ACreateMintsOneRootAndASecondPassKeepsIt() {
        var id = Address();
        var reconciler = new KeyVaultReconciler(silo.Grains, VaultSilo.Clock);

        (await reconciler.ReconcileAsync(Context(id, KeyVaults.Body()), TestContext.Current.CancellationToken)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        var root = VaultSilo.PlatformVault.Peek(KeyVaults.RootPath(id.TenantId, id.Id), KeyVaults.RootField);
        root.ShouldNotBeNull("the reconciler converged without a root in the platform vault");
        Convert.FromBase64String(root).Length.ShouldBe(32);

        (await reconciler.ReconcileAsync(Context(id, KeyVaults.Body(true)), TestContext.Current.CancellationToken)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        VaultSilo.PlatformVault.Peek(KeyVaults.RootPath(id.TenantId, id.Id), KeyVaults.RootField)
            .ShouldBe(root, "a second pass replaced the root, which would make every sealed item unopenable");

        (await silo.Vault(id.Id).DescribeAsync()).GetValueOrThrow().PurgeProtection.ShouldBeTrue();
    }

    [Fact]
    public async Task AParkSealsAndKeepsARestoreReopensAndAPurgeDestroys() {
        var id = Address();
        var reconciler = new KeyVaultReconciler(silo.Grains, VaultSilo.Clock);
        (await reconciler.ReconcileAsync(Context(id, KeyVaults.Body()), TestContext.Current.CancellationToken)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var vault = silo.Vault(id.Id);
        await vault.OkAsync(KeyVaults.SetSecretAction, new { secretName = "kept", value = "v" });

        // ── A soft delete's pass: Parking ────────────────────────────────────────────────────────
        (await reconciler.DeleteAsync(Context(id, KeyVaults.Body()) with { Parking = true }, TestContext.Current.CancellationToken)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var parked = (await vault.DescribeAsync()).GetValueOrThrow();
        parked.IsSealed.ShouldBeTrue();
        parked.SecretCount.ShouldBe(1, "a soft delete destroyed what its seven days exist to hand back");

        // ── A restore's pass is an ordinary reconcile ────────────────────────────────────────────
        (await reconciler.ReconcileAsync(Context(id, KeyVaults.Body()), TestContext.Current.CancellationToken)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        (await vault.OkAsync(KeyVaults.GetSecretAction, new { secretName = "kept" })).GetProperty("value").GetString().ShouldBe("v");

        // ── A purge's pass: not Parking ──────────────────────────────────────────────────────────
        (await reconciler.DeleteAsync(Context(id, KeyVaults.Body()), TestContext.Current.CancellationToken)).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var purged = (await vault.DescribeAsync()).GetValueOrThrow();
        purged.IsDestroyed.ShouldBeTrue();
        (purged.SecretCount + purged.KeyCount + purged.DeletedCount).ShouldBe(0);
    }

    [Fact]
    public async Task ARootThatIsNotAKeyFailsThePassForGood() {
        var id = Address();
        (await VaultSilo.PlatformVault.MintAsync(
            KeyVaults.RootPath(id.TenantId, id.Id),
            new Dictionary<string, string> { [KeyVaults.RootField] = "bm90LWEta2V5" },
            TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        var outcome = await new KeyVaultReconciler(silo.Grains, VaultSilo.Clock).ReconcileAsync(Context(id, KeyVaults.Body()), TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Retryable.ShouldBeFalse("mint-once means the value will never change, so a retry finds it again");
    }

    static ResourceId Address() =>
        new(
            VaultSilo.Tenant,
            Guid.Parse("7a7a7a7a-0000-4000-8000-0000000000aa"),
            "prod",
            KeyVaults.Type,
            "vault-" + Guid.NewGuid().ToString("N")[..8],
            Guid.NewGuid()
        );

    static ReconcileContext Context(ResourceId id, string body) {
        using var document = JsonDocument.Parse(body);

        return new(id, KeyVaults.V2026, document.RootElement.Clone(), null, "", null, VaultSilo.PlatformVault, new NullLog()) {
            SecretWriter = VaultSilo.PlatformVault
        };
    }

    sealed class NullLog : IReconcileLog {
        public void Report(string phase, string detail) { }

        public void Report(string phase, string detail, int percent) { }
    }
}
