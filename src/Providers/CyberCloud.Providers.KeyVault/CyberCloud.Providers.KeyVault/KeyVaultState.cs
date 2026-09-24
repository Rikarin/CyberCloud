namespace CyberCloud.Providers.KeyVault;

/// <summary>
///     A vault's durable state: its settings, and every secret and key it holds — sealed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Nothing here is a secret in the sense <c>CC1005</c> guards, and the reason is the
///         sealing rather than the naming.</b> A secret's value and a key's private half are stored
///         only as <see cref="ItemVersion.Sealed" /> — AES-256-GCM under the vault's root, which lives
///         in the platform vault and is never in this state or on any grain call. A backup of the
///         durable tier carries ciphertext that opens nothing without OpenBao; that is docs/plan/05
///         § What is deliberately not in either tier kept by construction rather than by review.
///         <c>KeyVaultGrainTests.TheDurableStateHoldsNoValueAndNoKeyMaterial</c> reads the stored
///         state back and searches it for the plaintext.
///     </para>
///     <para>
///         ⚠ <b>No member name ends in <c>Key</c>, <c>Secret</c>, <c>Token</c> or <c>Password</c></b>,
///         so <c>CC1005</c> stays switched on here and has nothing to say. <c>PublicJwk</c>-shaped
///         members are spelled <c>N</c>, <c>E</c>, <c>X</c> and <c>Y</c> for the same reason.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.KeyVault.KeyVaultState")]
public sealed class KeyVaultState {
    /// <summary>The vault's resource GUID, which every item's associated data binds to.</summary>
    [Id(0)]
    public Guid VaultId { get; set; }

    /// <summary>Whether the reconciler has opened the vault.</summary>
    [Id(1)]
    public bool IsOpen { get; set; }

    /// <summary>Whether a soft delete has parked the vault.</summary>
    [Id(2)]
    public bool IsSealed { get; set; }

    /// <summary>Whether a purge has destroyed the vault's contents. Final.</summary>
    [Id(3)]
    public bool IsDestroyed { get; set; }

    /// <summary>Whether a deleted item may be purged inside its window.</summary>
    [Id(4)]
    public bool PurgeProtection { get; set; }

    /// <summary>The recovery window, in days.</summary>
    [Id(5)]
    public int RecoveryDays { get; set; } = KeyVaults.RecoveryDays;

    /// <summary>When the vault was first opened.</summary>
    [Id(6)]
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>The secrets, keyed by lower-cased name.</summary>
    /// <remarks>
    ///     ⚠ Lower-cased keys under the default comparer rather than an ignore-case comparer, because
    ///     the serializer round-trips a dictionary's contents and not its comparer; a case-insensitive
    ///     dictionary would come back case-sensitive after the first activation.
    /// </remarks>
    [Id(7)]
    public Dictionary<string, VaultItem> Secrets { get; set; } = [];

    /// <summary>The keys, keyed by lower-cased name.</summary>
    [Id(8)]
    public Dictionary<string, VaultItem> Keys { get; set; } = [];
}

/// <summary>One secret or key: its versions, and whether it is soft-deleted.</summary>
[GenerateSerializer]
[Alias("CyberCloud.KeyVault.VaultItem")]
public sealed class VaultItem {
    /// <summary>The name as it was first spelled.</summary>
    [Id(0)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Every version, oldest first. The last is the current one.</summary>
    [Id(1)]
    public List<ItemVersion> Versions { get; set; } = [];

    /// <summary>When the item was deleted, or <see langword="null" /> while it is live.</summary>
    [Id(2)]
    public DateTimeOffset? DeletedOn { get; set; }

    /// <summary>When a deleted item is purged unless it is recovered first.</summary>
    [Id(3)]
    public DateTimeOffset? ScheduledPurgeDate { get; set; }
}

/// <summary>One version of a secret or key.</summary>
[GenerateSerializer]
[Alias("CyberCloud.KeyVault.ItemVersion")]
public sealed class ItemVersion {
    /// <summary>32 hex digits.</summary>
    [Id(0)]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    ///     The secret's value or the key's PKCS#8 private half, sealed under the vault's root:
    ///     <c>nonce ‖ ciphertext ‖ tag</c>. See <see cref="VaultCrypto" />.
    /// </summary>
    [Id(1)]
    public byte[] Sealed { get; set; } = [];

    /// <summary>A secret's content type. Empty when unset, and for a key.</summary>
    [Id(2)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Whether the version may be used.</summary>
    [Id(3)]
    public bool Enabled { get; set; } = true;

    /// <summary>Refused before this time.</summary>
    [Id(4)]
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>Refused from this time on.</summary>
    [Id(5)]
    public DateTimeOffset? ExpiresOn { get; set; }

    /// <summary>When the version was created.</summary>
    [Id(6)]
    public DateTimeOffset Created { get; set; }

    /// <summary>When the version's attributes last changed.</summary>
    [Id(7)]
    public DateTimeOffset Updated { get; set; }

    /// <summary>A key's type, <c>RSA</c> or <c>EC</c>. Empty for a secret.</summary>
    [Id(8)]
    public string Kty { get; set; } = string.Empty;

    /// <summary>An RSA key's modulus in bits.</summary>
    [Id(9)]
    public int KeySize { get; set; }

    /// <summary>An EC key's curve.</summary>
    [Id(10)]
    public string Curve { get; set; } = string.Empty;

    /// <summary>The operations a key permits.</summary>
    [Id(11)]
    public List<string> KeyOps { get; set; } = [];

    /// <summary>An RSA modulus, base64url. Public.</summary>
    [Id(12)]
    public string N { get; set; } = string.Empty;

    /// <summary>An RSA public exponent, base64url. Public.</summary>
    [Id(13)]
    public string E { get; set; } = string.Empty;

    /// <summary>An EC x coordinate, base64url. Public.</summary>
    [Id(14)]
    public string X { get; set; } = string.Empty;

    /// <summary>An EC y coordinate, base64url. Public.</summary>
    [Id(15)]
    public string Y { get; set; } = string.Empty;

    /// <summary>Whether the key was imported rather than generated here.</summary>
    [Id(16)]
    public bool Imported { get; set; }

    /// <summary>The public half, for the operations that need no unsealing.</summary>
    public PublicHalf Public => new(Kty, KeySize, Curve, N, E, X, Y);
}
