using CyberCloud.Core.Time;
using Orleans.Multitenant;
using System.Globalization;
using System.Security.Cryptography;

namespace CyberCloud.Providers.KeyVault;

/// <summary>
///     Converges a vault: mints its root in the platform vault once, opens its grain with the
///     body's settings, and reads both back.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The root is minted here and read everywhere else.</b> <c>ISecretWriter</c> is on
///         <see cref="ReconcileContext" /> and nowhere else, so the reconciler is the one component
///         that can put a vault's root into OpenBao, and <c>cas=0</c> makes the second pass's mint a
///         no-op that reports <c>Minted: false</c> — two silos racing a create end with one root.
///         Clause 4's read-back resolves it through <see cref="ReconcileContext.Secrets" />, which is
///         the same seam the grain reads it through; a vault that converged over a root nobody can
///         resolve would refuse every call its tenant makes.
///     </para>
///     <para>
///         ⚠ <b>The teardown has two endings and <see cref="ReconcileContext.Parking" /> says which.</b>
///         A soft delete seals the grain — contents kept for the seven days, data plane refused — and
///         a restore's <see cref="ReconcileAsync" /> reopens it. A purge destroys every secret and
///         key. Before <c>Parking</c> existed the two passes were indistinguishable, and the only safe
///         reading would have been to keep everything, forever.
///     </para>
/// </remarks>
/// <param name="grains">Reaches the vault's grain, through <c>ForTenant</c>.</param>
/// <param name="clock">The platform clock.</param>
public sealed class KeyVaultReconciler(IGrainFactory grains, IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => KeyVaults.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        context.Log.Report("root", $"making sure vault '{context.Id.Name}' has a root in the platform vault", 20);

        var root = RandomNumberGenerator.GetBytes(KeyVaults.RootLength);

        try {
            var minted = await context.SecretWriter.MintAsync(
                KeyVaults.RootPath(context.Id.TenantId, context.Id.Id),
                new Dictionary<string, string>(StringComparer.Ordinal) {
                    [KeyVaults.RootField] = Convert.ToBase64String(root)
                },
                cancellationToken
            );

            if (minted.TryGetError(out var mintError)) {
                return ReconcileOutcome.FromFailure(mintError);
            }
        } finally {
            CryptographicOperations.ZeroMemory(root);
        }

        // ── Clause 4, first half: the root reads back, and is a root ─────────────────────────────
        var resolved = await context.Secrets.ResolveAsync(KeyVaults.RootRef(context.Id.TenantId, context.Id.Id), cancellationToken);

        if (resolved.TryGetError(out var resolveError)) {
            return ReconcileOutcome.FromFailure(resolveError);
        }

        if (!IsRoot(resolved.GetValueOrThrow())) {
            // ⚠ Not retryable. Mint-once means the value at this path will never change, so a pass
            // that retried would find the same unusable value forever — an operator has to look.
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"The root at '{KeyVaults.RootPath(context.Id.TenantId, context.Id.Id)}' is not a base64 "
                + $"{KeyVaults.RootLength.ToString(CultureInfo.InvariantCulture)}-byte key, and mint-once "
                + "will never replace it."
            );
        }

        context.Log.Report("opening", $"opening vault '{context.Id.Name}'", 60);

        var settings = KeyVaults.SettingsOf(context.Id.Id, context.Desired);
        var opened = await Vault(context.Id).OpenAsync(settings);

        if (opened.TryGetError(out var openError)) {
            return ReconcileOutcome.FromFailure(openError);
        }

        // ── Clause 4, second half: the grain reads back as what the body says ────────────────────
        var described = await Vault(context.Id).DescribeAsync();

        if (described.TryGetError(out var describeError)) {
            return ReconcileOutcome.FromFailure(describeError);
        }

        if (!Matches(described.GetValueOrThrow(), settings)) {
            return ReconcileOutcome.InProgress(
                $"vault '{context.Id.Name}' does not yet read back as open with the body's settings",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("ready", $"vault '{context.Id.Name}' is open", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Parking) {
            context.Log.Report("sealing", $"sealing vault '{context.Id.Name}' for its recovery window");

            var sealedVault = await Vault(context.Id).SealAsync();
            if (sealedVault.TryGetError(out var sealError)) {
                return ReconcileOutcome.FromFailure(sealError);
            }

            var parked = await Vault(context.Id).DescribeAsync();
            if (parked.TryGetError(out var parkedError)) {
                return ReconcileOutcome.FromFailure(parkedError);
            }

            // A vault that was never opened has nothing to seal and nothing serving, which is the goal.
            return parked.GetValueOrThrow() is { IsOpen: false } or { IsSealed: true }
                ? ReconcileOutcome.Converged
                : ReconcileOutcome.InProgress($"vault '{context.Id.Name}' does not yet read back as sealed", TimeSpan.FromSeconds(5));
        }

        context.Log.Report("destroying", $"destroying every secret and key in vault '{context.Id.Name}'");

        var destroyed = await Vault(context.Id).DestroyAsync();
        if (destroyed.TryGetError(out var destroyError)) {
            return ReconcileOutcome.FromFailure(destroyError);
        }

        var readBack = await Vault(context.Id).DescribeAsync();
        if (readBack.TryGetError(out var readError)) {
            return ReconcileOutcome.FromFailure(readError);
        }

        var vault = readBack.GetValueOrThrow();

        if (vault.IsOpen || vault.SecretCount + vault.KeyCount + vault.DeletedCount > 0) {
            return ReconcileOutcome.InProgress(
                $"vault '{context.Id.Name}' still holds secrets or keys after its destruction",
                TimeSpan.FromSeconds(5)
            );
        }

        context.Log.Report("deleted", $"vault '{context.Id.Name}' holds nothing", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) {
        var described = await Vault(context.Id).DescribeAsync();

        if (described.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the vault could not be read" };
        }

        var vault = described.GetValueOrThrow();

        return new() {
            Exists = vault.IsOpen && !vault.IsSealed,
            ObservedAt = clock.UtcNow,
            Summary = vault.IsDestroyed
                ? "the vault was purged"
                : vault.IsSealed
                    ? "the vault is sealed for its recovery window"
                    : vault.IsOpen
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"the vault is open with {vault.SecretCount} secret(s), {vault.KeyCount} key(s) and {vault.DeletedCount} deleted item(s)"
                        )
                        : "the vault is not open"
        };
    }

    /// <summary>Whether a vault reads back as open, unsealed, and with the settings a body asks for.</summary>
    /// <param name="vault">What the grain reported.</param>
    /// <param name="settings">What the body decides.</param>
    public static bool Matches(VaultDescriptor vault, VaultSettings settings) {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(settings);

        return vault is { IsOpen: true, IsSealed: false, IsDestroyed: false }
            && vault.PurgeProtection == settings.PurgeProtection
            && vault.RecoveryDays == settings.RecoveryDays;
    }

    static bool IsRoot(string value) {
        try {
            var bytes = Convert.FromBase64String(value);
            var ok = bytes.Length == KeyVaults.RootLength;
            CryptographicOperations.ZeroMemory(bytes);
            return ok;
        } catch (FormatException) {
            return false;
        }
    }

    IKeyVaultGrain Vault(ResourceId id) =>
        grains.ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IKeyVaultGrain>(GrainKeys.Resource(id.Id));
}
