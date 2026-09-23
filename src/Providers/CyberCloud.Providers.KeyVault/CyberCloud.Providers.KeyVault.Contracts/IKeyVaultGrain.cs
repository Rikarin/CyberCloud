namespace CyberCloud.Providers.KeyVault.Contracts;

/// <summary>
///     What the reconciler hands a vault's grain when it opens it: the settings the desired body
///     decides.
/// </summary>
/// <remarks>
///     ⚠ <b>Two settings, both from the body, and neither is a secret.</b> The root the vault
///     encrypts under is not here and never crosses a grain call: the grain resolves it itself,
///     through the silo's <see cref="ISecretResolver" />, at the moment it needs it —
///     <see cref="KeyVaults.RootRef" /> says where it lives.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.KeyVault.VaultSettings")]
public sealed record VaultSettings {
    /// <summary>The vault's resource GUID, which every sealed item's associated data binds to.</summary>
    [Id(0)]
    public Guid VaultId { get; init; }

    /// <summary>Whether a deleted secret or key may be purged before its recovery window ends.</summary>
    /// <remarks>⚠ Once true, the grain refuses to set it false again, whatever the caller.</remarks>
    [Id(1)]
    public bool PurgeProtection { get; init; }

    /// <summary>How many days a deleted secret or key stays recoverable.</summary>
    [Id(2)]
    public int RecoveryDays { get; init; } = KeyVaults.RecoveryDays;
}

/// <summary>What a vault's grain reports about itself — never about a value it holds.</summary>
[GenerateSerializer]
[Alias("CyberCloud.KeyVault.VaultDescriptor")]
public sealed record VaultDescriptor {
    /// <summary>Whether the reconciler has opened the vault and it is serving its data plane.</summary>
    [Id(0)]
    public bool IsOpen { get; init; }

    /// <summary>
    ///     Whether the vault is parked for a soft delete: its contents are kept and its data plane
    ///     refuses every call.
    /// </summary>
    [Id(1)]
    public bool IsSealed { get; init; }

    /// <summary>Whether the vault's contents were destroyed by a purge. A destroyed vault never reopens.</summary>
    [Id(2)]
    public bool IsDestroyed { get; init; }

    /// <summary>The purge protection the vault enforces.</summary>
    [Id(3)]
    public bool PurgeProtection { get; init; }

    /// <summary>The recovery window, in days, the vault gives a deleted secret or key.</summary>
    [Id(4)]
    public int RecoveryDays { get; init; }

    /// <summary>How many secrets are live — not deleted — counting names rather than versions.</summary>
    [Id(5)]
    public int SecretCount { get; init; }

    /// <summary>How many keys are live, counting names rather than versions.</summary>
    [Id(6)]
    public int KeyCount { get; init; }

    /// <summary>How many secrets and keys are soft-deleted and still inside their window.</summary>
    [Id(7)]
    public int DeletedCount { get; init; }

    /// <summary>When the vault was first opened.</summary>
    [Id(8)]
    public DateTimeOffset OpenedAt { get; init; }
}

/// <summary>One data-plane call: an action's name and its already-validated body.</summary>
/// <remarks>
///     ⚠
///     <b>
///         The body travels as the JSON the gateway validated, rather than as one typed record per
///         action, and that is a choice about the process boundary.
///     </b> The handler runs in the gateway and the grain on a silo, so every type on this
///     interface needs an alias both processes agree on; a string and a string is the smallest
///     wire contract that can carry twenty-five actions, and the request shapes that matter are
///     <see cref="KeyVaults" />' schemas, which the resource manager checks before this is built.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.KeyVault.VaultCall")]
public sealed record VaultCall {
    /// <summary>The action, for example <c>setSecret</c>. Matched case-insensitively, as the gateway matches it.</summary>
    [Id(0)]
    public string Action { get; init; } = string.Empty;

    /// <summary>The request body. <c>{}</c> for an action that takes none.</summary>
    /// <remarks>
    ///     ⚠ May carry a secret's value, a plaintext to encrypt or a PKCS#8 private key. Never
    ///     stored as sent, and kept out of <see cref="ToString" />, which is what a log template or
    ///     an exception message would print.
    /// </remarks>
    [Id(1)]
    public string Body { get; init; } = "{}";

    /// <summary>The trace the call belongs to, for the audit line. Empty when there is none.</summary>
    [Id(2)]
    public string TraceId { get; init; } = string.Empty;

    /// <summary>Names the action, the body's length and the trace, and never the body itself.</summary>
    /// <remarks>
    ///     ⚠ <b>Overridden because a record prints every property.</b> The compiler's
    ///     <c>ToString</c> put <see cref="Body" /> into any log line, assertion message or Orleans
    ///     diagnostic that formatted a call. So "never logged" was a promise nothing kept until the
    ///     #30 review. <c>KeyVaultDeclarationTests.AVaultCallNeverPrintsItsBody</c> holds it.
    /// </remarks>
    /// <returns>For example <c>VaultCall { Action = setSecret, Body = 41 chars, TraceId = none }</c>.</returns>
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"VaultCall {{ Action = {Action}, Body = {Body.Length} chars, TraceId = {(TraceId.Length > 0 ? TraceId : "none")} }}"
        );
}

/// <summary>
///     One vault's secrets and keys — the data plane of <c>CyberCloud.KeyVault/vaults</c>.
///     docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c>.
/// </summary>
/// <remarks>
///     <para>
///         Keyed by <see cref="GrainKeys.Resource" /> of the vault's GUID and reached through
///         <c>ForTenant</c>, exactly like <c>IFeedGrain</c>. The reconciler opens, seals and destroys
///         it; the action handler reaches it through <see cref="InvokeAsync" /> and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Nothing on this interface returns key material.</b> A key's private half is sealed
///         under the vault's root the moment it exists and is only ever unsealed inside the grain,
///         for the length of one operation. A secret's value leaves only through
///         <c>getSecret</c>, which the resource manager authorizes <c>FullyConsistent</c>.
///     </para>
/// </remarks>
[Alias("CyberCloud.KeyVault.IKeyVaultGrain")]
public interface IKeyVaultGrain : IGrainWithStringKey {
    /// <summary>
    ///     Opens the vault with the body's settings, or reopens one a soft delete sealed. Idempotent.
    /// </summary>
    /// <param name="settings">What the desired body decides.</param>
    /// <returns>
    ///     The vault after the call, or <see cref="ErrorCode.Conflict" /> when the vault was destroyed
    ///     or the call would turn purge protection off.
    /// </returns>
    Task<Result<VaultDescriptor>> OpenAsync(VaultSettings settings);

    /// <summary>Parks the vault for a soft delete: contents kept, data plane refused. Idempotent.</summary>
    /// <returns>The vault after the call.</returns>
    Task<Result<VaultDescriptor>> SealAsync();

    /// <summary>Destroys every secret and key the vault holds, for a purge. Idempotent and final.</summary>
    /// <returns>The vault after the call.</returns>
    Task<Result<VaultDescriptor>> DestroyAsync();

    /// <summary>Reports the vault's state without touching it.</summary>
    /// <returns>The vault.</returns>
    Task<Result<VaultDescriptor>> DescribeAsync();

    /// <summary>Runs one data-plane action.</summary>
    /// <param name="request">The action and its body.</param>
    /// <returns>
    ///     The response body, shaped as the action's response schema in <see cref="KeyVaults" />
    ///     declares, or the reason the call was refused.
    /// </returns>
    Task<Result<string>> InvokeAsync(VaultCall request);
}
