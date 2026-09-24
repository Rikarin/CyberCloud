using CyberCloud.Core.Contracts;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.KeyVault.Contracts;

/// <summary>
///     <c>CyberCloud.KeyVault/vaults</c> — the shape of a vault, of its data plane, and of the
///     permissions that data plane checks. docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE DATA PLANE IS ACTIONS ON THE VAULT, AND SECRETS AND KEYS ARE NOT RESOURCE
///             TYPES.
///         </b> docs/plan/18 § The resource model draws <c>vaults/{name}/secrets/{name}</c> and
///         <c>…/keys/{name}</c> as children. A child resource's body is desired state, and the
///         resource manager keeps desired state durably in <c>ResourceGrain</c> and hands it to
///         anyone holding <c>read</c> — so a secret's value in a child body would be a secret in
///         grain state, which docs/plan/05 § What is deliberately not in either tier forbids and <c>CC1005</c> exists
///         to catch. Azure draws the same line: <c>Microsoft.KeyVault/vaults/secrets</c> is a
///         control-plane type whose value is write-only, and the values live behind a separate
///         data-plane endpoint. Here that endpoint is <c>POST {vault}/{action}</c> through the
///         gateway, and every action is checked against its own permission — see
///         <see cref="ReadSecretsPermission" />.
///     </para>
///     <para>
///         ⚠ <b>Base64url everywhere bytes cross the wire, as JWK and Azure's data plane spell them</b>
///         — a digest, a signature, a ciphertext, a wrapped key, the public <c>n</c>/<c>e</c>/<c>x</c>/<c>y</c>.
///         The one exception is <c>importKey</c>'s <c>pkcs8</c>, which is standard base64 because it
///         is what <c>openssl pkcs8 -topk8 -outform DER | base64</c> produces.
///     </para>
/// </remarks>
public static class KeyVaults {
    /// <summary>The provider namespace.</summary>
    public const string ProviderNamespace = "CyberCloud.KeyVault";

    /// <summary>The resource type path under the namespace.</summary>
    public const string TypePath = "vaults";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The type, as the registry names it.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>
    ///     The recovery window, in days, for a deleted vault and for a deleted secret or key inside
    ///     one. docs/plan/18 § The resource model: "a 7-day recovery window".
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One number for both, and not Azure's configurable 7–90.</b> A vault's own window is
    ///     the resource manager's, which holds one <c>SoftDeleteDays</c> per <i>type</i>; a property
    ///     that set the items' window and not the vault's would say two different things about the
    ///     same word. docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c> records the per-vault window as owed.
    /// </remarks>
    public const int RecoveryDays = 7;

    /// <summary>The pointer to the vault's purge protection, which the resource manager reads too.</summary>
    public const string PurgeProtectionPointer = "/properties/enablePurgeProtection";

    /// <summary>The pointer to the vault's description, its one freely mutable property.</summary>
    public const string DescriptionPointer = "/properties/description";

    /// <summary>The field of the vault's root in the platform vault — see <see cref="RootRef" />.</summary>
    public const string RootField = "root";

    /// <summary>How many random bytes a vault's root is: an AES-256 key.</summary>
    public const int RootLength = 32;

    // ── The data-plane permissions — docs/plan/07 § Azure RBAC, expressed in it ─────────────────
    //
    // ⚠ SPELLED HERE AND IN CyberCloud.Authorization.Contracts' Permissions, AND THE TWO CANNOT
    // SEE EACH OTHER. A provider does not reference the authorization assemblies (docs/plan/07
    // § The enforcement seam: providers never call the engine), and a permission the schema does not
    // declare can only evaluate false — which the authorizer turns into the canonical 404 for every
    // caller, the owner included. That is how `purge` went unnoticed until issue #70's isolation run.
    // KeyVaultDeclarationTests.TheSixPermissionsAreTheSchemasAndNoControlPlaneRoleHoldsThem pins these
    // six against CyberCloudSchema from the test project, which
    // may reference both.

    /// <summary>Read a secret's value, its versions and the vault's secret names.</summary>
    /// <remarks>
    ///     Held by the <c>keyVaultSecretsUser</c> role and by <c>keyVaultSecretsOfficer</c> above it.
    ///     ⚠ <b>Not by <c>owner</c>, <c>contributor</c> or <c>reader</c></b>: a control-plane role
    ///     manages the vault and reads none of its contents, as Azure's Owner holds no data action.
    /// </remarks>
    public const string ReadSecretsPermission = "readSecrets";

    /// <summary>Set, update, delete and recover secrets. Held by <c>keyVaultSecretsOfficer</c>.</summary>
    public const string WriteSecretsPermission = "writeSecrets";

    /// <summary>Purge a deleted secret. Held by <c>keyVaultSecretsOfficer</c>, and removed by a deny row.</summary>
    public const string PurgeSecretsPermission = "purgeSecrets";

    /// <summary>
    ///     Use a key — encrypt, decrypt, wrap, unwrap, sign, verify — and read its public half and
    ///     the vault's key names. Held by <c>keyVaultCryptoUser</c> and by <c>keyVaultCryptoOfficer</c>.
    /// </summary>
    public const string UseKeysPermission = "useKeys";

    /// <summary>Create, import, update, delete and recover keys. Held by <c>keyVaultCryptoOfficer</c>.</summary>
    public const string WriteKeysPermission = "writeKeys";

    /// <summary>Purge a deleted key. Held by <c>keyVaultCryptoOfficer</c>, and removed by a deny row.</summary>
    public const string PurgeKeysPermission = "purgeKeys";

    /// <summary>Every permission the data plane checks, for the test that pins them against the schema.</summary>
    public static ImmutableArray<string> DataPlanePermissions { get; } = [
        ReadSecretsPermission, WriteSecretsPermission, PurgeSecretsPermission, UseKeysPermission,
        WriteKeysPermission, PurgeKeysPermission
    ];

    // ── The actions ────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates a secret, or a new version of one.</summary>
    public const string SetSecretAction = "setSecret";

    /// <summary>Reads a secret version's value.</summary>
    public const string GetSecretAction = "getSecret";

    /// <summary>Changes a secret version's attributes — content type, enabled, validity.</summary>
    public const string UpdateSecretAction = "updateSecret";

    /// <summary>Lists the vault's live secrets, one line each.</summary>
    public const string ListSecretsAction = "listSecrets";

    /// <summary>Lists one secret's versions, newest first.</summary>
    public const string ListSecretVersionsAction = "listSecretVersions";

    /// <summary>Soft-deletes a secret, every version of it.</summary>
    public const string DeleteSecretAction = "deleteSecret";

    /// <summary>Lists the vault's soft-deleted secrets and when each will be purged.</summary>
    public const string ListDeletedSecretsAction = "listDeletedSecrets";

    /// <summary>Brings a soft-deleted secret back, every version of it.</summary>
    public const string RecoverDeletedSecretAction = "recoverDeletedSecret";

    /// <summary>Destroys a soft-deleted secret before its window ends. Refused under purge protection.</summary>
    public const string PurgeDeletedSecretAction = "purgeDeletedSecret";

    /// <summary>Generates a key, or a new version of one.</summary>
    public const string CreateKeyAction = "createKey";

    /// <summary>Imports a private key as a key, or as a new version of one.</summary>
    public const string ImportKeyAction = "importKey";

    /// <summary>Reads a key version's public half and attributes.</summary>
    public const string GetKeyAction = "getKey";

    /// <summary>Changes a key version's attributes — operations, enabled, validity.</summary>
    public const string UpdateKeyAction = "updateKey";

    /// <summary>Lists the vault's live keys, one line each.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a credential export</b>, which is what <c>listKeys</c> means on eleven other
    ///     types in the catalogue: it lists the key <i>names</i> this vault holds, as
    ///     <c>az keyvault key list</c> does, and returns no material. It checks
    ///     <see cref="UseKeysPermission" />, not a <c>listKeys</c> permission.
    /// </remarks>
    public const string ListKeysAction = "listKeys";

    /// <summary>Lists one key's versions, newest first.</summary>
    public const string ListKeyVersionsAction = "listKeyVersions";

    /// <summary>Soft-deletes a key, every version of it.</summary>
    public const string DeleteKeyAction = "deleteKey";

    /// <summary>Lists the vault's soft-deleted keys and when each will be purged.</summary>
    public const string ListDeletedKeysAction = "listDeletedKeys";

    /// <summary>Brings a soft-deleted key back, every version of it.</summary>
    public const string RecoverDeletedKeyAction = "recoverDeletedKey";

    /// <summary>Destroys a soft-deleted key before its window ends. Refused under purge protection.</summary>
    public const string PurgeDeletedKeyAction = "purgeDeletedKey";

    /// <summary>Encrypts a small plaintext with an RSA key.</summary>
    public const string EncryptAction = "encrypt";

    /// <summary>Decrypts what <see cref="EncryptAction" /> produced.</summary>
    public const string DecryptAction = "decrypt";

    /// <summary>Wraps a symmetric key with an RSA key — the envelope half of encryption at rest.</summary>
    public const string WrapKeyAction = "wrapKey";

    /// <summary>Unwraps what <see cref="WrapKeyAction" /> produced.</summary>
    public const string UnwrapKeyAction = "unwrapKey";

    /// <summary>Signs a digest.</summary>
    public const string SignAction = "sign";

    /// <summary>Verifies a signature over a digest.</summary>
    public const string VerifyAction = "verify";

    // ── Vocabularies ───────────────────────────────────────────────────────────────────────────

    /// <summary>A secret or key name: Azure's rule, 1–127 letters, digits and dashes.</summary>
    public const string NamePattern = "^[0-9A-Za-z-]{1,127}$";

    /// <summary>A version: 32 lower-case hex digits, as Azure spells one.</summary>
    public const string VersionPattern = "^[0-9a-f]{32}$";

    /// <summary>The largest secret value accepted — Azure's 25 KB, enforced in UTF-8 bytes.</summary>
    /// <remarks>
    ///     ⚠ The schema's <c>maxLength</c> counts characters, which is what JSON Schema can say. The
    ///     grain counts the UTF-8 bytes it seals, so a value of 25,600 characters that are three bytes
    ///     each isn't 75 KB of durable state.
    /// </remarks>
    public const int MaxSecretLength = 25_600;

    /// <summary>How many versions, of secrets and keys together and deleted ones included, one vault holds.</summary>
    /// <remarks>
    ///     ⚠ <b>A bound on one durable row, not a product limit copied from Azure</b>, which has none.
    ///     A vault is one grain, and every write serializes its whole state. Without a bound, one
    ///     Secrets Officer could grow a single row without limit, and quota counts vaults only.
    ///     Deleting and purging an item frees its versions.
    /// </remarks>
    public const int MaxVersionsPerVault = 1_000;

    /// <summary>How many sealed bytes one vault holds across every version of every item.</summary>
    /// <remarks>
    ///     The bytes half of <see cref="MaxVersionsPerVault" />'s bound: 4 MiB is about 160 secrets at
    ///     the largest size, and far more than any count of keys reaches first.
    /// </remarks>
    public const int MaxSealedBytesPerVault = 4 * 1024 * 1024;

    /// <summary>The key types a vault generates and imports.</summary>
    public static ImmutableArray<string> KeyTypes { get; } = ["RSA", "EC"];

    /// <summary>The curves an EC key may use — the three JOSE names.</summary>
    public static ImmutableArray<string> Curves { get; } = ["P-256", "P-384", "P-521"];

    /// <summary>The RSA modulus sizes a vault generates. An imported key must be one of them too.</summary>
    public static ImmutableArray<int> RsaKeySizes { get; } = [2048, 3072, 4096];

    /// <summary>The operations a key may permit, as JWK <c>key_ops</c> spells them.</summary>
    public static ImmutableArray<string> KeyOperations { get; } = [
        "encrypt", "decrypt", "sign", "verify", "wrapKey", "unwrapKey"
    ];

    /// <summary>The RSA encryption algorithms, for encrypt, decrypt, wrap and unwrap.</summary>
    /// <remarks>
    ///     ⚠ <b>No <c>RSA1_5</c></b>, which Azure still accepts: PKCS#1 v1.5 encryption is the
    ///     padding-oracle construction, and a new service has no client relying on it.
    /// </remarks>
    public static ImmutableArray<string> EncryptionAlgorithms { get; } = ["RSA-OAEP", "RSA-OAEP-256"];

    /// <summary>The signature algorithms, by their JWA names.</summary>
    public static ImmutableArray<string> SignatureAlgorithms { get; } = [
        "RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"
    ];

    // ── The vault's root ───────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Where the vault's root lives in the platform vault: one AES-256 key, minted once by the
    ///     reconciler through <c>ISecretWriter</c> and resolved by the grain through
    ///     <see cref="ISecretResolver" />.
    /// </summary>
    /// <param name="tenantId">The vault's tenant.</param>
    /// <param name="vaultId">The vault's resource GUID.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is how "at rest under the platform vault seam's root" is met without a
    ///         second OpenBao client.</b> <c>CyberCloud.Vault</c> is the platform's one reader and
    ///         writer of OpenBao; every secret version and every key's private half this vault holds is
    ///         sealed with AES-256-GCM under the value at this path, and the grain's durable state
    ///         carries only the ciphertext. A copy of the durable tier without OpenBao opens nothing.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             UNDER <see cref="RootPrefix" />, AND NEVER UNDER <c>tenants/{tenantId}/</c>, BECAUSE
    ///             A TENANT CAN SPELL A PATH UNDER THAT PREFIX AND HAVE THE PLATFORM RESOLVE IT.
    ///         </b> <c>VirtualMachines.ParseCloudInitRef</c> accepts any handle under
    ///         <c>VirtualMachines.TenantVaultPrefix</c>. The VM reconciler resolves it with the
    ///         platform's one broad token and writes the value into a Secret the guest mounts. That
    ///         prefix holds only what the tenant is already handed, such as a cache's
    ///         <c>listKeys</c> password. A vault's root is not handed to anyone. A Contributor who
    ///         knew the vault's GUID could have put it in a guest's cloud-init with no data-plane
    ///         role at all, and then every secret and private key the vault seals would open
    ///         without the platform vault.
    ///         Neither family may reference the other, so <c>KeyVaultRootIsOutsideTheTenantPrefixTests</c>
    ///         holds the two to it from <c>CyberCloud.Gateway.Host.Tests</c>, which can see both.
    ///     </para>
    ///     <para>
    ///         The tenant is still in the path, so one tenant's roots stay under one prefix a policy
    ///         can scope.
    ///     </para>
    /// </remarks>
    public static SecretRef RootRef(Guid tenantId, Guid vaultId) =>
        new() { Path = RootPath(tenantId, vaultId), Field = RootField };

    /// <summary>
    ///     The platform-only prefix every vault's root is under: <c>platform/CyberCloud.KeyVault/vaults/</c>.
    /// </summary>
    /// <remarks>
    ///     Outside every tenant's <c>tenants/{tenantId}/</c>, which is the whole point — see
    ///     <see cref="RootRef" />.
    /// </remarks>
    public const string RootPrefix = "platform/" + ProviderNamespace + "/" + TypePath + "/";

    /// <summary>
    ///     The path half of <see cref="RootRef" />:
    ///     <c>platform/CyberCloud.KeyVault/vaults/{tenantId}/{vaultId}</c>.
    /// </summary>
    /// <param name="tenantId">The vault's tenant.</param>
    /// <param name="vaultId">The vault's resource GUID.</param>
    public static string RootPath(Guid tenantId, Guid vaultId) =>
        string.Create(CultureInfo.InvariantCulture, $"{RootPrefix}{tenantId:D}/{vaultId:D}");

    // ── The resource body ──────────────────────────────────────────────────────────────────────

    /// <summary>The body schema at <see cref="V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the vault is billed in and served from."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The vault's own settings."),
                new(
                    PurgeProtectionPointer,
                    SchemaKind.Boolean,
                    Description: "Whether a deleted vault, secret or key may be purged before its "
                    + "seven-day recovery window ends. Once true it stays true: a write that sets it "
                    + "false is refused, and so is every purge until the window is out."
                ) { DefaultJson = "false" },
                new(
                    DescriptionPointer,
                    SchemaKind.Text,
                    Description: "What the vault is for, shown in the portal beside its name."
                ) { MaxLength = 256 }
            ]
        );

    /// <summary>A vault body.</summary>
    /// <param name="purgeProtection">Whether purge protection is on.</param>
    /// <param name="description">The description, or <see langword="null" /> for none.</param>
    /// <param name="location">The region.</param>
    public static string Body(bool purgeProtection = false, string? description = null, string location = "eu-central") {
        var properties = new JsonObject { ["enablePurgeProtection"] = purgeProtection };

        if (description is not null) {
            properties["description"] = description;
        }

        return new JsonObject { ["location"] = location, ["properties"] = properties }.ToJsonString();
    }

    /// <summary>Whether a desired body turns purge protection on. Absent and non-boolean read as off.</summary>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ Off for anything that is not literally <c>true</c>, which is the resource manager's own
    ///     reading of the same pointer (<c>ResourceManagerService.IsPurgeProtected</c>) — two readers
    ///     disagreeing about one flag would let the vault purge what the manager thinks is protected.
    /// </remarks>
    public static bool PurgeProtectionOf(JsonElement desired) =>
        desired.ValueKind == JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty("enablePurgeProtection", out var flag)
        && flag.ValueKind == JsonValueKind.True;

    /// <summary>The settings a desired body opens a vault with.</summary>
    /// <param name="vaultId">The vault's resource GUID.</param>
    /// <param name="desired">The desired body.</param>
    public static VaultSettings SettingsOf(Guid vaultId, JsonElement desired) =>
        new() { VaultId = vaultId, PurgeProtection = PurgeProtectionOf(desired), RecoveryDays = RecoveryDays };

    // ── Request shapes ─────────────────────────────────────────────────────────────────────────

    static SchemaProperty SecretName { get; } =
        new("/secretName", SchemaKind.Text, true, Description: "The secret's name: 1–127 letters, digits and dashes.") {
            Pattern = NamePattern, ExampleJson = "\"db-password\""
        };

    static SchemaProperty KeyName { get; } =
        new("/keyName", SchemaKind.Text, true, Description: "The key's name: 1–127 letters, digits and dashes.") {
            Pattern = NamePattern, ExampleJson = "\"signing\""
        };

    static SchemaProperty Version { get; } =
        new("/version", SchemaKind.Text, Description: "A version, as 32 hex digits. Omit it for the newest.") {
            Pattern = VersionPattern
        };

    static SchemaProperty Enabled { get; } =
        new("/enabled", SchemaKind.Boolean, Description: "Whether the version may be used. A disabled version is refused, not hidden.") {
            DefaultJson = "true"
        };

    static SchemaProperty NotBefore { get; } =
        new("/notBefore", SchemaKind.Text, Description: "The version is refused before this time.") {
            Format = SchemaFormat.DateTime
        };

    static SchemaProperty ExpiresOn { get; } =
        new("/expiresOn", SchemaKind.Text, Description: "The version is refused from this time on.") {
            Format = SchemaFormat.DateTime
        };

    static SchemaProperty ContentType { get; } =
        new("/contentType", SchemaKind.Text, Description: "What the value is, for the consumer — for example text/plain. Not interpreted.") {
            MaxLength = 255
        };

    static SchemaProperty KeyOps { get; } =
        new(
            "/keyOps",
            SchemaKind.Array,
            Description: "The operations the key permits. Omit it for every operation its type supports; an EC key signs and verifies only."
        ) { ElementKind = SchemaKind.Text, AllowedValues = [.. KeyOperations] };

    /// <summary>What <c>setSecret</c> takes.</summary>
    public static ResourceSchema SetSecretRequest { get; } =
        ResourceSchema.Of(
            [
                SecretName,
                new("/value", SchemaKind.Text, true, Secret: true, Description: "The secret's value. Sealed under the vault's root before it is stored.") {
                    MaxLength = MaxSecretLength
                },
                ContentType,
                Enabled,
                NotBefore,
                ExpiresOn
            ]
        );

    /// <summary>What <c>getSecret</c> takes: a name and, optionally, a version.</summary>
    public static ResourceSchema SecretVersionRequest { get; } = ResourceSchema.Of([SecretName, Version]);

    /// <summary>What <c>updateSecret</c> takes. An absent attribute is left as it is.</summary>
    public static ResourceSchema UpdateSecretRequest { get; } =
        ResourceSchema.Of([SecretName, Version, ContentType, Enabled with { DefaultJson = "" }, NotBefore, ExpiresOn]);

    /// <summary>What an action naming one secret takes.</summary>
    public static ResourceSchema SecretNameRequest { get; } = ResourceSchema.Of([SecretName]);

    /// <summary>What <c>createKey</c> takes.</summary>
    public static ResourceSchema CreateKeyRequest { get; } =
        ResourceSchema.Of(
            [
                KeyName,
                new("/kty", SchemaKind.Text, true, Description: "RSA or EC.") {
                    AllowedValues = [.. KeyTypes], ExampleJson = "\"RSA\""
                },
                new("/keySize", SchemaKind.WholeNumber, Description: "An RSA key's modulus in bits: 2048, 3072 or 4096. 2048 when omitted; refused on an EC key.") {
                    Minimum = 2048, Maximum = 4096
                },
                new("/curve", SchemaKind.Text, Description: "An EC key's curve. P-256 when omitted; refused on an RSA key.") {
                    AllowedValues = [.. Curves]
                },
                KeyOps,
                Enabled,
                NotBefore,
                ExpiresOn
            ]
        );

    /// <summary>What <c>importKey</c> takes: an unencrypted PKCS#8 private key, in standard base64.</summary>
    public static ResourceSchema ImportKeyRequest { get; } =
        ResourceSchema.Of(
            [
                KeyName,
                new(
                    "/pkcs8",
                    SchemaKind.Text,
                    true,
                    Secret: true,
                    Description: "The private key as unencrypted PKCS#8 DER, in standard base64. RSA of 2048, 3072 or 4096 bits, or EC on P-256, P-384 or P-521. Sealed on arrival and never returned."
                ) { MaxLength = 16_384 },
                KeyOps,
                Enabled,
                NotBefore,
                ExpiresOn
            ]
        );

    /// <summary>What <c>getKey</c> takes.</summary>
    public static ResourceSchema KeyVersionRequest { get; } = ResourceSchema.Of([KeyName, Version]);

    /// <summary>What <c>updateKey</c> takes. An absent attribute is left as it is.</summary>
    public static ResourceSchema UpdateKeyRequest { get; } =
        ResourceSchema.Of([KeyName, Version, KeyOps, Enabled with { DefaultJson = "" }, NotBefore, ExpiresOn]);

    /// <summary>What an action naming one key takes.</summary>
    public static ResourceSchema KeyNameRequest { get; } = ResourceSchema.Of([KeyName]);

    static SchemaProperty EncryptionAlgorithm { get; } =
        new("/alg", SchemaKind.Text, true, Description: "RSA-OAEP (SHA-1) or RSA-OAEP-256 (SHA-256).") {
            AllowedValues = [.. EncryptionAlgorithms], ExampleJson = "\"RSA-OAEP-256\""
        };

    /// <summary>What encrypt and wrapKey take: plaintext, so marked secret.</summary>
    /// <remarks>
    ///     ⚠ <b>Secret because a wrapKey's input is a data key</b>, and separate from
    ///     <see cref="CiphertextRequest" /> for that reason. One schema for both directions would mark
    ///     both <c>/value</c>s or neither. Unmarked, the plaintext a caller wraps reaches OpenAPI without
    ///     <c>writeOnly</c> and the portal's form without masking, while the same bytes coming back out
    ///     of decrypt are marked.
    /// </remarks>
    public static ResourceSchema PlaintextRequest { get; } =
        ResourceSchema.Of(
            [
                KeyName,
                Version,
                EncryptionAlgorithm,
                new("/value", SchemaKind.Text, true, Secret: true, Description: "The plaintext to encrypt or the key to wrap, base64url without padding.") {
                    MaxLength = 4_096
                }
            ]
        );

    /// <summary>What decrypt and unwrapKey take: ciphertext, which isn't secret.</summary>
    public static ResourceSchema CiphertextRequest { get; } =
        ResourceSchema.Of(
            [
                KeyName,
                Version,
                EncryptionAlgorithm,
                new("/value", SchemaKind.Text, true, Description: "The ciphertext or wrapped key, base64url without padding.") {
                    MaxLength = 4_096
                }
            ]
        );

    static SchemaProperty SignatureAlgorithm { get; } =
        new("/alg", SchemaKind.Text, true, Description: "A JWA signature algorithm. RS* and PS* need an RSA key, ES256/ES384/ES512 an EC key on P-256/P-384/P-521.") {
            AllowedValues = [.. SignatureAlgorithms], ExampleJson = "\"ES256\""
        };

    static SchemaProperty Digest { get; } =
        new("/digest", SchemaKind.Text, true, Description: "The digest to sign, base64url. Its length must be the algorithm's hash length.") {
            MaxLength = 128
        };

    /// <summary>What <c>sign</c> takes.</summary>
    public static ResourceSchema SignRequest { get; } = ResourceSchema.Of([KeyName, Version, SignatureAlgorithm, Digest]);

    /// <summary>What <c>verify</c> takes.</summary>
    public static ResourceSchema VerifyRequest { get; } =
        ResourceSchema.Of(
            [
                KeyName,
                Version,
                SignatureAlgorithm,
                Digest,
                new("/signature", SchemaKind.Text, true, Description: "The signature, base64url. An EC signature is r‖s, as JWS spells it.") {
                    MaxLength = 1_024
                }
            ]
        );

    // ── Response shapes ────────────────────────────────────────────────────────────────────────

    static SchemaProperty ItemName { get; } = new("/name", SchemaKind.Text, true, Description: "The secret's or key's name.");

    static SchemaProperty ItemVersion { get; } = new("/version", SchemaKind.Text, true, Description: "The version this response is about.");

    static SchemaProperty Stamp(string pointer, bool required, string description) =>
        new(pointer, SchemaKind.Text, required, Description: description) { Format = SchemaFormat.DateTime };

    static ImmutableArray<SchemaProperty> Attributes { get; } = [
        new("/enabled", SchemaKind.Boolean, true, Description: "Whether the version may be used."),
        Stamp("/notBefore", false, "Refused before this time. Absent when unset."),
        Stamp("/expiresOn", false, "Refused from this time on. Absent when unset."),
        Stamp("/created", true, "When the version was created."),
        Stamp("/updated", true, "When the version's attributes last changed.")
    ];

    /// <summary>A secret version's metadata — never its value.</summary>
    public static ResourceSchema SecretResponse { get; } =
        ResourceSchema.Of([ItemName, ItemVersion, new("/contentType", SchemaKind.Text, Description: "What the value is. Absent when unset."), .. Attributes]);

    /// <summary>A secret version's metadata and its value — <c>getSecret</c>'s answer.</summary>
    public static ResourceSchema SecretValueResponse { get; } =
        ResourceSchema.Of(
            [
                ItemName,
                ItemVersion,
                new("/contentType", SchemaKind.Text, Description: "What the value is. Absent when unset."),
                new("/value", SchemaKind.Text, true, Secret: true, Description: "The secret's value."),
                .. Attributes
            ]
        );

    /// <summary>A key version's public half and attributes. The private half is never in any response.</summary>
    public static ResourceSchema KeyResponse { get; } =
        ResourceSchema.Of(
            [
                ItemName,
                ItemVersion,
                new("/kty", SchemaKind.Text, true, Description: "RSA or EC."),
                new("/keyOps", SchemaKind.Array, true, Description: "The operations the key permits.") {
                    ElementKind = SchemaKind.Text
                },
                new("/imported", SchemaKind.Boolean, true, Description: "Whether the key was imported rather than generated here."),
                new("/keySize", SchemaKind.WholeNumber, Description: "An RSA key's modulus in bits."),
                new("/n", SchemaKind.Text, Description: "An RSA key's modulus, base64url."),
                new("/e", SchemaKind.Text, Description: "An RSA key's public exponent, base64url."),
                new("/crv", SchemaKind.Text, Description: "An EC key's curve."),
                new("/x", SchemaKind.Text, Description: "An EC key's x coordinate, base64url."),
                new("/y", SchemaKind.Text, Description: "An EC key's y coordinate, base64url."),
                .. Attributes
            ]
        );

    /// <summary>What a listing answers: a count and one line per item.</summary>
    public static ResourceSchema ListResponse { get; } =
        ResourceSchema.Of(
            [
                new("/count", SchemaKind.WholeNumber, true, Description: "How many lines follow."),
                new(
                    "/items",
                    SchemaKind.Array,
                    true,
                    Description: "One line per item, ordered by name — or per version, newest first: "
                    + "'{name} {version} {enabled|disabled} created {created} expires {expiresOn|never}'. "
                    + "A deleted item's line is '{name} deleted {deletedOn} purges {scheduledPurgeDate}'."
                ) { ElementKind = SchemaKind.Text }
            ]
        );

    /// <summary>What a delete answers: when the item went and when it will be purged.</summary>
    public static ResourceSchema DeletedResponse { get; } =
        ResourceSchema.Of(
            [
                ItemName,
                Stamp("/deletedOn", true, "When the item was deleted."),
                Stamp("/scheduledPurgeDate", true, "When the item is purged unless it is recovered first.")
            ]
        );

    /// <summary>What a purge answers.</summary>
    public static ResourceSchema PurgedResponse { get; } =
        ResourceSchema.Of([ItemName, new("/purged", SchemaKind.Boolean, true, Description: "True: the item and every version of it are gone.")]);

    static SchemaProperty CryptoName { get; } = new("/name", SchemaKind.Text, true, Description: "The key that did the work.");

    static SchemaProperty CryptoVersion { get; } = new("/version", SchemaKind.Text, true, Description: "The key version that did the work.");

    static SchemaProperty CryptoAlgorithm { get; } = new("/alg", SchemaKind.Text, true, Description: "The algorithm used.");

    /// <summary>What encrypt, wrapKey and sign answer.</summary>
    public static ResourceSchema CryptoResponse { get; } =
        ResourceSchema.Of([CryptoName, CryptoVersion, CryptoAlgorithm, new("/value", SchemaKind.Text, true, Description: "The result, base64url.")]);

    /// <summary>What decrypt and unwrapKey answer: plaintext, so marked secret.</summary>
    public static ResourceSchema PlaintextResponse { get; } =
        ResourceSchema.Of(
            [
                CryptoName,
                CryptoVersion,
                CryptoAlgorithm,
                new("/value", SchemaKind.Text, true, Secret: true, Description: "The plaintext, base64url.")
            ]
        );

    /// <summary>What verify answers.</summary>
    public static ResourceSchema VerifyResponse { get; } =
        ResourceSchema.Of([CryptoName, CryptoVersion, CryptoAlgorithm, new("/value", SchemaKind.Boolean, true, Description: "Whether the signature is valid.")]);

    /// <summary>A timestamp as every response spells it: RFC 3339, UTC.</summary>
    /// <param name="at">The instant.</param>
    public static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);
}
