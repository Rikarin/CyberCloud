namespace CyberCloud.Providers.KeyVault;

/// <summary>
///     Key vaults: secrets and keys a tenant's workloads read and use through the gateway, sealed
///     under a root the platform vault holds. docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             NOT A SECOND VAULT CLIENT, AND NOT AN OpenBao NAMESPACE PER TENANT — WHICH IS WHAT
///             docs/plan/18 § Shape DREW, AND WHY THIS IS NOT IT.
///         </b> <c>CyberCloud.Vault</c> is the platform's seam to OpenBao: one token, one namespace,
///         <c>kv-v2</c> reads and mint-once writes, and nothing else. docs/plan/18 § Shape asks for a
///         namespace per tenant, a <c>transit</c> engine, and JWT auth mapping managed identities to
///         OpenBao roles; none of the three is built, and building them would be a second OpenBao
///         client beside the one the platform already trusts. What this family does instead needs
///         exactly the seam that exists: the reconciler mints one AES-256 root per vault through
///         <c>ISecretWriter</c>, the grain resolves it through <c>ISecretResolver</c>, and every
///         secret value and every private key is sealed under it in the grain's durable state.
///         Authorization is ReBAC and only ReBAC — docs/plan/18 § The resource model's "one
///         authorization system" — so OpenBao never learns who a caller is.
///     </para>
///     <para>
///         ⚠ <b>A soft-delete window, with purge protection, at both levels.</b> The vault's own is
///         the resource manager's (<c>SupportsSoftDelete</c>, the manager enforcing that protection
///         only ever turns on); a secret's or key's is the grain's, over the same flag and the same
///         seven days. The reconciler tells a park from a purge by
///         <see cref="ReconcileContext.Parking" />.
///     </para>
///     <para>
///         ⚠ <b>Clusterless.</b> No <c>RequiresCluster</c>, no chart, no namespace: a vault is a grain
///         and a path in the platform vault, like <c>CyberCloud.Communication/services</c> is a grain
///         in the silo.
///     </para>
/// </remarks>
public sealed class KeyVaultProvider : IResourceProvider {
    /// <summary>The <c>cyc</c> alias for a vault.</summary>
    /// <remarks>
    ///     <c>vault</c>, which <c>CyberCloud.RecoveryServices/vaults</c> left free on purpose — its own
    ///     short name is <c>backupvault</c> for exactly this type.
    /// </remarks>
    public const string ShortName = "vault";

    /// <inheritdoc />
    public string ProviderNamespace => KeyVaults.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        var type = builder
            .ResourceType(KeyVaults.TypePath)
            .ApiVersion(KeyVaults.V2026, KeyVaults.Schema2026)
            .Reconciler<KeyVaultReconciler>()
            // What a vault costs is its items and its calls, neither of which a body reserves. The
            // count is what quota can say today; docs/plan/18 records the per-operation meter as owed.
            .Meters(QuotaMeter.Resources)
            // ⚠ The CONTROL plane's three. Owner, contributor and reader manage the vault resource and
            // read none of its contents — every action below checks a data-plane permission instead.
            .Permissions("read", "write", "delete");

        // ── Secrets ─────────────────────────────────────────────────────────────────────────────
        Data(type, KeyVaults.SetSecretAction, KeyVaults.WriteSecretsPermission, KeyVaults.SetSecretRequest, KeyVaults.SecretResponse);
        // ⚠ secret: true — the resource manager checks it FullyConsistent (docs/plan/07 § Consistency),
        // so a revoked Secrets User is refused on the next call rather than after a cache expiry.
        Data(type, KeyVaults.GetSecretAction, KeyVaults.ReadSecretsPermission, KeyVaults.SecretVersionRequest, KeyVaults.SecretValueResponse, true);
        Data(type, KeyVaults.UpdateSecretAction, KeyVaults.WriteSecretsPermission, KeyVaults.UpdateSecretRequest, KeyVaults.SecretResponse);
        Data(type, KeyVaults.ListSecretsAction, KeyVaults.ReadSecretsPermission, null, KeyVaults.ListResponse);
        Data(type, KeyVaults.ListSecretVersionsAction, KeyVaults.ReadSecretsPermission, KeyVaults.SecretNameRequest, KeyVaults.ListResponse);
        Data(type, KeyVaults.DeleteSecretAction, KeyVaults.WriteSecretsPermission, KeyVaults.SecretNameRequest, KeyVaults.DeletedResponse);
        Data(type, KeyVaults.ListDeletedSecretsAction, KeyVaults.ReadSecretsPermission, null, KeyVaults.ListResponse);
        Data(type, KeyVaults.RecoverDeletedSecretAction, KeyVaults.WriteSecretsPermission, KeyVaults.SecretNameRequest, KeyVaults.SecretResponse);
        Data(type, KeyVaults.PurgeDeletedSecretAction, KeyVaults.PurgeSecretsPermission, KeyVaults.SecretNameRequest, KeyVaults.PurgedResponse);

        // ── Keys ────────────────────────────────────────────────────────────────────────────────
        Data(type, KeyVaults.CreateKeyAction, KeyVaults.WriteKeysPermission, KeyVaults.CreateKeyRequest, KeyVaults.KeyResponse);
        Data(type, KeyVaults.ImportKeyAction, KeyVaults.WriteKeysPermission, KeyVaults.ImportKeyRequest, KeyVaults.KeyResponse);
        Data(type, KeyVaults.GetKeyAction, KeyVaults.UseKeysPermission, KeyVaults.KeyVersionRequest, KeyVaults.KeyResponse);
        Data(type, KeyVaults.UpdateKeyAction, KeyVaults.WriteKeysPermission, KeyVaults.UpdateKeyRequest, KeyVaults.KeyResponse);
        Data(type, KeyVaults.ListKeysAction, KeyVaults.UseKeysPermission, null, KeyVaults.ListResponse);
        Data(type, KeyVaults.ListKeyVersionsAction, KeyVaults.UseKeysPermission, KeyVaults.KeyNameRequest, KeyVaults.ListResponse);
        Data(type, KeyVaults.DeleteKeyAction, KeyVaults.WriteKeysPermission, KeyVaults.KeyNameRequest, KeyVaults.DeletedResponse);
        Data(type, KeyVaults.ListDeletedKeysAction, KeyVaults.UseKeysPermission, null, KeyVaults.ListResponse);
        Data(type, KeyVaults.RecoverDeletedKeyAction, KeyVaults.WriteKeysPermission, KeyVaults.KeyNameRequest, KeyVaults.KeyResponse);
        Data(type, KeyVaults.PurgeDeletedKeyAction, KeyVaults.PurgeKeysPermission, KeyVaults.KeyNameRequest, KeyVaults.PurgedResponse);

        // ── Using a key ─────────────────────────────────────────────────────────────────────────
        Data(type, KeyVaults.EncryptAction, KeyVaults.UseKeysPermission, KeyVaults.CryptoRequest, KeyVaults.CryptoResponse);
        Data(type, KeyVaults.DecryptAction, KeyVaults.UseKeysPermission, KeyVaults.CryptoRequest, KeyVaults.PlaintextResponse, true);
        Data(type, KeyVaults.WrapKeyAction, KeyVaults.UseKeysPermission, KeyVaults.CryptoRequest, KeyVaults.CryptoResponse);
        Data(type, KeyVaults.UnwrapKeyAction, KeyVaults.UseKeysPermission, KeyVaults.CryptoRequest, KeyVaults.PlaintextResponse, true);
        Data(type, KeyVaults.SignAction, KeyVaults.UseKeysPermission, KeyVaults.SignRequest, KeyVaults.CryptoResponse);
        Data(type, KeyVaults.VerifyAction, KeyVaults.UseKeysPermission, KeyVaults.VerifyRequest, KeyVaults.VerifyResponse);

        type
            .Display(
                "Key vault",
                "Key vaults",
                ShortName,
                "Secrets and RSA/EC keys for your workloads, sealed under a platform-held root, with a "
                + "seven-day recovery window and optional purge protection."
            )
            .SupportsTags()
            .SupportsSoftDelete(
                KeyVaults.RecoveryDays,
                purgeProtectionPointer: KeyVaults.PurgeProtectionPointer
            );
    }

    /// <summary>Declares one data-plane action, synchronous, served by <see cref="KeyVaultActionHandler" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Synchronous, always.</b> A long-running action's result travels on an operation record
    ///     any <c>read</c> holder can poll (<c>ResourceManagerService.ActionAsync</c> says so at
    ///     length), which is exactly where a secret's value and a plaintext must never be.
    /// </remarks>
    static void Data(
        IResourceTypeBuilder type,
        string action,
        string permission,
        ResourceSchema? request,
        ResourceSchema response,
        bool secret = false
    ) =>
        type.Action(
            action,
            ActionKind.Post,
            permission,
            secret,
            request,
            response,
            handler: typeof(KeyVaultActionHandler)
        );
}
