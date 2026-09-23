using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.KeyVault;

/// <summary>
///     One vault's data plane: its secrets and keys, sealed under the vault's root.
///     docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The root is resolved per call and held for that call.</b> Every operation that seals
///         or unseals asks the silo's <see cref="ISecretResolver" /> for
///         <see cref="KeyVaults.RootRef" />, uses the bytes, and zeroes them before it returns.
///         Nothing caches it: <c>OpenBaoSecretResolver</c>'s remarks give three reasons a resolved
///         value is never held, and a grain that kept its vault's root in a field for the length of an
///         activation would be holding the one value that opens everything here. The cost is one
///         vault read per sealing call.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An expired window is enforced when the vault is next called, not on the day.
///         </b> A deleted item past its <c>ScheduledPurgeDate</c> is dropped by the next data-plane
///         call, whichever it is, and is never visible after its date — but its ciphertext stays in
///         the durable tier until something calls. A reminder per vault is what would close it, and
///         docs/plan/18 § <c>CyberCloud.KeyVault/vaults</c> records it as owed rather than spending a
///         reminder per vault on it now.
///     </para>
///     <para>
///         ⚠ <b>The audit line names the vault, the action, the item and the trace, and never the
///         caller.</b> docs/plan/18 § The resource model asks for "the caller, the correlation id and
///         the secret name (never the value)", and <c>ActionContext</c> carries no caller — the
///         resource manager authorizes the call and does not hand the identity on. That is owed at
///         the same place; what is logged is everything the grain can see.
///     </para>
/// </remarks>
/// <param name="state">The vault's durable state.</param>
/// <param name="secrets">Where the vault's root is resolved from.</param>
/// <param name="clock">The platform clock.</param>
/// <param name="logger">Where the audit line goes.</param>
public sealed class KeyVaultGrain(
    [PersistentState("vault", StorageTiers.Durable)]
    IPersistentState<KeyVaultState> state,
    ISecretResolver secrets,
    IClock clock,
    ILogger<KeyVaultGrain> logger
) : Grain, IKeyVaultGrain {
    const string SecretKind = "secrets";
    const string KeyKind = "keys";

    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        var tenant = this.GetTenantId()
            ?? throw new InvalidOperationException(
                $"{nameof(KeyVaultGrain)} is a tenant-scoped grain but was activated with no tenant "
                + "qualification. Reach it with IGrainFactory.ForTenant(tenantId).GetGrain<IKeyVaultGrain>(…) "
                + "— ADR-002."
            );

        tenantId = Guid.Parse(tenant, CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }

    // ── Lifecycle — the reconciler's half ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<VaultDescriptor>> OpenAsync(VaultSettings settings) {
        ArgumentNullException.ThrowIfNull(settings);

        if (state.State.IsDestroyed) {
            return Result<VaultDescriptor>.Failure(
                ErrorCode.Conflict,
                "This vault was purged and a purged vault never reopens. A vault created again under "
                + "the same name is a different resource with a different GUID and a different grain."
            );
        }

        if (state.State.IsOpen && state.State.VaultId != settings.VaultId) {
            return Result<VaultDescriptor>.Failure(
                ErrorCode.Conflict,
                $"This grain holds vault {state.State.VaultId:D} and was asked to open {settings.VaultId:D}."
            );
        }

        // ⚠ One edge and no inverse, the resource manager's rule for the same flag
        // (ResourceManagerService.PurgeProtectionRefusalAsync), enforced here too because this is
        // where a purge of an ITEM is decided and the manager never sees one.
        if (state.State.PurgeProtection && !settings.PurgeProtection) {
            return Result<VaultDescriptor>.Failure(
                ErrorCode.Conflict,
                "This vault has purge protection on and it cannot be turned off — docs/plan/08 § Soft "
                + "delete: a flag whose holder can clear it and then purge is not a protection."
            );
        }

        if (settings.RecoveryDays < 1) {
            return Result<VaultDescriptor>.Failure(ErrorCode.InternalError, "A recovery window is at least a day.");
        }

        var changed = !state.State.IsOpen
            || state.State.IsSealed
            || state.State.PurgeProtection != settings.PurgeProtection
            || state.State.RecoveryDays != settings.RecoveryDays;

        if (changed) {
            if (!state.State.IsOpen) {
                state.State.OpenedAt = clock.UtcNow;
            }

            state.State.VaultId = settings.VaultId;
            state.State.IsOpen = true;
            state.State.IsSealed = false;
            state.State.PurgeProtection = settings.PurgeProtection;
            state.State.RecoveryDays = settings.RecoveryDays;
            await state.WriteStateAsync();
        }

        return Result<VaultDescriptor>.Success(Snapshot());
    }

    /// <inheritdoc />
    public async Task<Result<VaultDescriptor>> SealAsync() {
        if (state.State.IsOpen && !state.State.IsSealed) {
            state.State.IsSealed = true;
            await state.WriteStateAsync();
        }

        return Result<VaultDescriptor>.Success(Snapshot());
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Destroys the ciphertext and not the root.</b> <c>ISecretWriter</c> mints once and has
    ///     no delete, so the vault's root stays in OpenBao after a purge; a purged vault's contents
    ///     are unrecoverable because they are gone from the durable tier, and are not yet
    ///     cryptographically shredded. docs/plan/18 records the root's deletion as owed.
    /// </remarks>
    public async Task<Result<VaultDescriptor>> DestroyAsync() {
        if (!state.State.IsDestroyed) {
            state.State.Secrets.Clear();
            state.State.Keys.Clear();
            state.State.IsOpen = false;
            state.State.IsSealed = false;
            state.State.IsDestroyed = true;
            await state.WriteStateAsync();
        }

        return Result<VaultDescriptor>.Success(Snapshot());
    }

    /// <inheritdoc />
    public Task<Result<VaultDescriptor>> DescribeAsync() => Task.FromResult(Result<VaultDescriptor>.Success(Snapshot()));

    VaultDescriptor Snapshot() =>
        new() {
            IsOpen = state.State.IsOpen,
            IsSealed = state.State.IsSealed,
            IsDestroyed = state.State.IsDestroyed,
            PurgeProtection = state.State.PurgeProtection,
            RecoveryDays = state.State.RecoveryDays,
            SecretCount = state.State.Secrets.Values.Count(static x => x.DeletedOn is null),
            KeyCount = state.State.Keys.Values.Count(static x => x.DeletedOn is null),
            DeletedCount = state.State.Secrets.Values.Concat(state.State.Keys.Values).Count(static x => x.DeletedOn is not null),
            OpenedAt = state.State.OpenedAt
        };

    // ── The data plane ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(VaultCall request) {
        ArgumentNullException.ThrowIfNull(request);

        if (!state.State.IsOpen || state.State.IsSealed || state.State.IsDestroyed) {
            // ⚠ Reached only for a vault whose create has not converged: a sealed or purged vault's
            // address already answers the canonical 404 at the resource manager's step 1, so no
            // action is dispatched to it at all.
            return Refuse(ErrorCode.Conflict, "The vault is not open yet. Retry once its create has converged.");
        }

        await DropExpiredAsync();

        JsonElement body;
        try {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
            body = document.RootElement.Clone();
        } catch (JsonException) {
            // ⚠ The body is not reproduced: it may carry a value.
            return Refuse(ErrorCode.InvalidRequestBody, "The body is not JSON.");
        }

        var result = request.Action.ToUpperInvariant() switch {
            "SETSECRET" => await SetSecretAsync(body),
            "GETSECRET" => await GetSecretAsync(body),
            "UPDATESECRET" => await UpdateAsync(SecretKind, body),
            "LISTSECRETS" => List(state.State.Secrets),
            "LISTSECRETVERSIONS" => ListVersions(SecretKind, body),
            "DELETESECRET" => await DeleteAsync(SecretKind, body),
            "LISTDELETEDSECRETS" => ListDeleted(state.State.Secrets),
            "RECOVERDELETEDSECRET" => await RecoverAsync(SecretKind, body),
            "PURGEDELETEDSECRET" => await PurgeAsync(SecretKind, body),
            "CREATEKEY" => await CreateKeyAsync(body),
            "IMPORTKEY" => await ImportKeyAsync(body),
            "GETKEY" => GetKey(body),
            "UPDATEKEY" => await UpdateAsync(KeyKind, body),
            "LISTKEYS" => List(state.State.Keys),
            "LISTKEYVERSIONS" => ListVersions(KeyKind, body),
            "DELETEKEY" => await DeleteAsync(KeyKind, body),
            "LISTDELETEDKEYS" => ListDeleted(state.State.Keys),
            "RECOVERDELETEDKEY" => await RecoverAsync(KeyKind, body),
            "PURGEDELETEDKEY" => await PurgeAsync(KeyKind, body),
            "ENCRYPT" => EncryptOrWrap(body, "encrypt"),
            "WRAPKEY" => EncryptOrWrap(body, "wrapKey"),
            "DECRYPT" => await DecryptOrUnwrapAsync(body, "decrypt"),
            "UNWRAPKEY" => await DecryptOrUnwrapAsync(body, "unwrapKey"),
            "SIGN" => await SignAsync(body),
            "VERIFY" => Verify(body),
            _ => Refuse(ErrorCode.InvalidRequestBody, $"'{request.Action}' is not an action a vault serves.")
        };

        // ⚠ THE AUDIT LINE. Names and versions are addresses and are logged; a value, a plaintext, a
        // signature input and a key are never on it. See the class remarks for the caller it cannot
        // name.
        logger.LogInformation(
            "Key vault {VaultId} {Action}: {Outcome}. item={Item} trace={TraceId}",
            state.State.VaultId,
            request.Action,
            result.IsSuccess ? "served" : "refused " + result.Error!.Code,
            ItemNameOf(body),
            request.TraceId.Length > 0 ? request.TraceId : "none"
        );

        return result;
    }

    // ── Secrets ────────────────────────────────────────────────────────────────────────────────

    async Task<Result<string>> SetSecretAsync(JsonElement body) {
        var name = Text(body, "secretName");
        var value = Text(body, "value");

        if (Invalid(name) is { } nameError) {
            return nameError;
        }

        if (Times(body) is { IsFailure: true } times) {
            return Result<string>.Failure(times.Error!);
        }

        var key = name.ToLowerInvariant();

        if (state.State.Secrets.TryGetValue(key, out var existing) && existing.DeletedOn is not null) {
            return Refuse(
                ErrorCode.Conflict,
                $"Secret '{name}' is deleted and recoverable until {KeyVaults.Timestamp(existing.ScheduledPurgeDate!.Value)}. "
                + "Recover it, or purge it, before setting it again."
            );
        }

        var root = await RootAsync();
        if (root.TryGetError(out var rootError)) {
            return Result<string>.Failure(rootError);
        }

        var version = NewVersion();
        var plaintext = Encoding.UTF8.GetBytes(value);
        var rootBytes = root.GetValueOrThrow();
        var now = clock.UtcNow;

        try {
            var item = existing ?? new VaultItem { Name = name };

            item.Versions.Add(
                new() {
                    Version = version,
                    Sealed = VaultCrypto.Seal(rootBytes, plaintext, VaultCrypto.AssociatedData(state.State.VaultId, SecretKind, key, version)),
                    ContentType = Text(body, "contentType"),
                    Enabled = Flag(body, "enabled") ?? true,
                    NotBefore = Stamp(body, "notBefore"),
                    ExpiresOn = Stamp(body, "expiresOn"),
                    Created = now,
                    Updated = now
                }
            );

            state.State.Secrets[key] = item;
            await state.WriteStateAsync();

            return Result<string>.Success(SecretJson(item, item.Versions[^1], null).ToJsonString());
        } finally {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(rootBytes);
        }
    }

    async Task<Result<string>> GetSecretAsync(JsonElement body) {
        var found = FindVersion(SecretKind, body);
        if (found.TryGetError(out var findError)) {
            return Result<string>.Failure(findError);
        }

        var (key, item, version) = found.GetValueOrThrow();

        if (Unusable(SecretKind, item, version, true) is { } refusal) {
            return refusal;
        }

        var root = await RootAsync();
        if (root.TryGetError(out var rootError)) {
            return Result<string>.Failure(rootError);
        }

        var rootBytes = root.GetValueOrThrow();

        try {
            var opened = VaultCrypto.Open(
                rootBytes,
                version.Sealed,
                VaultCrypto.AssociatedData(state.State.VaultId, SecretKind, key, version.Version)
            );

            if (opened.TryGetError(out var openError)) {
                logger.LogError("Key vault {VaultId}: {Detail}", state.State.VaultId, openError.Message);
                return Refuse(ErrorCode.InternalError, "The secret could not be opened under the vault's root.");
            }

            var plaintext = opened.GetValueOrThrow();

            try {
                return Result<string>.Success(SecretJson(item, version, Encoding.UTF8.GetString(plaintext)).ToJsonString());
            } finally {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        } finally {
            CryptographicOperations.ZeroMemory(rootBytes);
        }
    }

    static JsonObject SecretJson(VaultItem item, ItemVersion version, string? value) {
        var json = new JsonObject { ["name"] = item.Name, ["version"] = version.Version };

        if (version.ContentType.Length > 0) {
            json["contentType"] = version.ContentType;
        }

        if (value is not null) {
            json["value"] = value;
        }

        AddAttributes(json, version);
        return json;
    }

    // ── Keys ───────────────────────────────────────────────────────────────────────────────────

    async Task<Result<string>> CreateKeyAsync(JsonElement body) {
        var kty = Text(body, "kty");
        var size = Whole(body, "keySize");
        var curve = Text(body, "curve");

        if (kty == "RSA") {
            if (curve.Length > 0) {
                return Refuse(ErrorCode.InvalidRequestBody, "An RSA key takes a keySize and no curve.", "/curve");
            }

            size ??= 2048;

            if (!KeyVaults.RsaKeySizes.Contains(size.Value)) {
                return Refuse(
                    ErrorCode.InvalidRequestBody,
                    $"An RSA key is {string.Join(", ", KeyVaults.RsaKeySizes)} bits.",
                    "/keySize"
                );
            }

            return await StoreKeyAsync(body, VaultCrypto.GenerateRsa(size.Value), false);
        }

        if (kty == "EC") {
            if (size is not null) {
                return Refuse(ErrorCode.InvalidRequestBody, "An EC key takes a curve and no keySize.", "/keySize");
            }

            return await StoreKeyAsync(body, VaultCrypto.GenerateEc(curve.Length == 0 ? "P-256" : curve), false);
        }

        return Refuse(ErrorCode.InvalidRequestBody, "A key is RSA or EC.", "/kty");
    }

    async Task<Result<string>> ImportKeyAsync(JsonElement body) {
        byte[] pkcs8;

        try {
            pkcs8 = Convert.FromBase64String(Text(body, "pkcs8"));
        } catch (FormatException) {
            return Refuse(ErrorCode.InvalidRequestBody, "pkcs8 is not standard base64.", "/pkcs8");
        }

        try {
            var imported = VaultCrypto.Import(pkcs8);
            return imported.TryGetError(out var importError)
                ? Result<string>.Failure(importError.Code, importError.Message, "/pkcs8")
                : await StoreKeyAsync(body, imported.GetValueOrThrow(), true);
        } finally {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    async Task<Result<string>> StoreKeyAsync(JsonElement body, KeyMaterial material, bool imported) {
        try {
            var name = Text(body, "keyName");

            if (Invalid(name) is { } nameError) {
                return nameError;
            }

            var operations = Operations(body, material.Public.Kty);
            if (operations.TryGetError(out var operationsError)) {
                return Result<string>.Failure(operationsError);
            }

            if (Times(body) is { IsFailure: true } times) {
                return Result<string>.Failure(times.Error!);
            }

            var key = name.ToLowerInvariant();

            if (state.State.Keys.TryGetValue(key, out var existing) && existing.DeletedOn is not null) {
                return Refuse(
                    ErrorCode.Conflict,
                    $"Key '{name}' is deleted and recoverable until {KeyVaults.Timestamp(existing.ScheduledPurgeDate!.Value)}. "
                    + "Recover it, or purge it, before creating it again."
                );
            }

            var root = await RootAsync();
            if (root.TryGetError(out var rootError)) {
                return Result<string>.Failure(rootError);
            }

            var rootBytes = root.GetValueOrThrow();
            var version = NewVersion();
            var now = clock.UtcNow;

            try {
                var item = existing ?? new VaultItem { Name = name };
                var half = material.Public;

                item.Versions.Add(
                    new() {
                        Version = version,
                        Sealed = VaultCrypto.Seal(rootBytes, material.Pkcs8, VaultCrypto.AssociatedData(state.State.VaultId, KeyKind, key, version)),
                        Enabled = Flag(body, "enabled") ?? true,
                        NotBefore = Stamp(body, "notBefore"),
                        ExpiresOn = Stamp(body, "expiresOn"),
                        Created = now,
                        Updated = now,
                        Kty = half.Kty,
                        KeySize = half.KeySize,
                        Curve = half.Curve,
                        KeyOps = [.. operations.GetValueOrThrow()],
                        N = half.N,
                        E = half.E,
                        X = half.X,
                        Y = half.Y,
                        Imported = imported
                    }
                );

                state.State.Keys[key] = item;
                await state.WriteStateAsync();

                return Result<string>.Success(KeyJson(item, item.Versions[^1]).ToJsonString());
            } finally {
                CryptographicOperations.ZeroMemory(rootBytes);
            }
        } finally {
            CryptographicOperations.ZeroMemory(material.Pkcs8);
        }
    }

    Result<string> GetKey(JsonElement body) {
        var found = FindVersion(KeyKind, body);
        return found.TryGetError(out var findError)
            ? Result<string>.Failure(findError)
            : Result<string>.Success(KeyJson(found.GetValueOrThrow().Item, found.GetValueOrThrow().Version).ToJsonString());
    }

    static JsonObject KeyJson(VaultItem item, ItemVersion version) {
        var json = new JsonObject {
            ["name"] = item.Name,
            ["version"] = version.Version,
            ["kty"] = version.Kty,
            ["keyOps"] = new JsonArray([.. version.KeyOps.Select(static x => (JsonNode)x)]),
            ["imported"] = version.Imported
        };

        if (version.Kty == "RSA") {
            json["keySize"] = version.KeySize;
            json["n"] = version.N;
            json["e"] = version.E;
        } else {
            json["crv"] = version.Curve;
            json["x"] = version.X;
            json["y"] = version.Y;
        }

        AddAttributes(json, version);
        return json;
    }

    // ── Cryptographic operations ───────────────────────────────────────────────────────────────

    Result<string> EncryptOrWrap(JsonElement body, string operation) {
        var found = UsableKey(body, operation, true);
        if (found.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var (_, item, version) = found.GetValueOrThrow();

        if (version.Kty != "RSA") {
            return Refuse(ErrorCode.InvalidRequestBody, $"{operation} needs an RSA key; '{item.Name}' is EC.", "/keyName");
        }

        if (!VaultCrypto.TryDecode(Text(body, "value"), out var input)) {
            return Refuse(ErrorCode.InvalidRequestBody, "value is not base64url.", "/value");
        }

        var algorithm = Text(body, "alg");
        var output = VaultCrypto.Encrypt(version.Public, algorithm, input);

        return output.TryGetError(out var encryptError)
            ? Result<string>.Failure(encryptError.Code, encryptError.Message, "/value")
            : Result<string>.Success(CryptoJson(item, version, algorithm, VaultCrypto.Encode(output.GetValueOrThrow())).ToJsonString());
    }

    async Task<Result<string>> DecryptOrUnwrapAsync(JsonElement body, string operation) {
        var found = UsableKey(body, operation, false);
        if (found.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var (key, item, version) = found.GetValueOrThrow();

        if (version.Kty != "RSA") {
            return Refuse(ErrorCode.InvalidRequestBody, $"{operation} needs an RSA key; '{item.Name}' is EC.", "/keyName");
        }

        if (!VaultCrypto.TryDecode(Text(body, "value"), out var input)) {
            return Refuse(ErrorCode.InvalidRequestBody, "value is not base64url.", "/value");
        }

        var algorithm = Text(body, "alg");

        return await WithPrivateAsync(
            key,
            version,
            pkcs8 => {
                var output = VaultCrypto.Decrypt(pkcs8, algorithm, input);

                if (output.TryGetError(out var decryptError)) {
                    return Result<string>.Failure(decryptError.Code, decryptError.Message, "/value");
                }

                var plaintext = output.GetValueOrThrow();

                try {
                    return Result<string>.Success(CryptoJson(item, version, algorithm, VaultCrypto.Encode(plaintext)).ToJsonString());
                } finally {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        );
    }

    async Task<Result<string>> SignAsync(JsonElement body) {
        var found = UsableKey(body, "sign", true);
        if (found.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var (key, item, version) = found.GetValueOrThrow();
        var algorithm = Text(body, "alg");

        if (VaultCrypto.SignatureMismatch(version.Public, algorithm) is { } mismatch) {
            return Refuse(ErrorCode.InvalidRequestBody, mismatch, "/alg");
        }

        if (!VaultCrypto.TryDecode(Text(body, "digest"), out var digest) || digest.Length != VaultCrypto.DigestLength(algorithm)) {
            return Refuse(
                ErrorCode.InvalidRequestBody,
                $"digest is not a base64url {VaultCrypto.DigestLength(algorithm)}-byte digest, which {algorithm} signs.",
                "/digest"
            );
        }

        return await WithPrivateAsync(
            key,
            version,
            pkcs8 => Result<string>.Success(
                CryptoJson(item, version, algorithm, VaultCrypto.Encode(VaultCrypto.Sign(pkcs8, version.Public, algorithm, digest)))
                    .ToJsonString()
            )
        );
    }

    Result<string> Verify(JsonElement body) {
        var found = UsableKey(body, "verify", false);
        if (found.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var (_, item, version) = found.GetValueOrThrow();
        var algorithm = Text(body, "alg");

        if (VaultCrypto.SignatureMismatch(version.Public, algorithm) is { } mismatch) {
            return Refuse(ErrorCode.InvalidRequestBody, mismatch, "/alg");
        }

        if (!VaultCrypto.TryDecode(Text(body, "digest"), out var digest) || digest.Length != VaultCrypto.DigestLength(algorithm)) {
            return Refuse(ErrorCode.InvalidRequestBody, $"digest is not a base64url {algorithm} digest.", "/digest");
        }

        if (!VaultCrypto.TryDecode(Text(body, "signature"), out var signature)) {
            return Refuse(ErrorCode.InvalidRequestBody, "signature is not base64url.", "/signature");
        }

        var json = new JsonObject {
            ["name"] = item.Name,
            ["version"] = version.Version,
            ["alg"] = algorithm,
            ["value"] = VaultCrypto.Verify(version.Public, algorithm, digest, signature)
        };

        return Result<string>.Success(json.ToJsonString());
    }

    static JsonObject CryptoJson(VaultItem item, ItemVersion version, string algorithm, string value) =>
        new() { ["name"] = item.Name, ["version"] = version.Version, ["alg"] = algorithm, ["value"] = value };

    /// <summary>
    ///     Finds a key version and refuses it when the operation is not one it permits or the
    ///     version is not usable now.
    /// </summary>
    /// <param name="body">The request.</param>
    /// <param name="operation">The <c>key_ops</c> name the call needs.</param>
    /// <param name="timeBound">
    ///     Whether the validity window applies. ⚠ True for encrypt, wrap and sign, false for decrypt,
    ///     unwrap and verify — Azure's rule, and the reason for it: an expired key must stop
    ///     producing new ciphertext and signatures, and must still open what it already produced,
    ///     or its expiry destroys the data it protected.
    /// </param>
    Result<(string Key, VaultItem Item, ItemVersion Version)> UsableKey(JsonElement body, string operation, bool timeBound) {
        var found = FindVersion(KeyKind, body);
        if (found.TryGetError(out var error)) {
            return found;
        }

        var (_, item, version) = found.GetValueOrThrow();

        if (!version.KeyOps.Contains(operation, StringComparer.Ordinal)) {
            return Result<(string, VaultItem, ItemVersion)>.Failure(
                ErrorCode.Conflict,
                $"Key '{item.Name}' version {version.Version} does not permit {operation}; it permits "
                + $"[{string.Join(", ", version.KeyOps)}]."
            );
        }

        return Unusable(KeyKind, item, version, timeBound) is { } refusal
            ? Result<(string, VaultItem, ItemVersion)>.Failure(refusal.Error!)
            : found;
    }

    /// <summary>Unseals a key version's private half, runs one operation, and zeroes both.</summary>
    async Task<Result<string>> WithPrivateAsync(string key, ItemVersion version, Func<byte[], Result<string>> operation) {
        var root = await RootAsync();
        if (root.TryGetError(out var rootError)) {
            return Result<string>.Failure(rootError);
        }

        var rootBytes = root.GetValueOrThrow();

        try {
            var opened = VaultCrypto.Open(
                rootBytes,
                version.Sealed,
                VaultCrypto.AssociatedData(state.State.VaultId, KeyKind, key, version.Version)
            );

            if (opened.TryGetError(out var openError)) {
                logger.LogError("Key vault {VaultId}: {Detail}", state.State.VaultId, openError.Message);
                return Refuse(ErrorCode.InternalError, "The key could not be opened under the vault's root.");
            }

            var pkcs8 = opened.GetValueOrThrow();

            try {
                return operation(pkcs8);
            } finally {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        } finally {
            CryptographicOperations.ZeroMemory(rootBytes);
        }
    }

    Result<ImmutableArray<string>> Operations(JsonElement body, string kty) {
        var allowed = VaultCrypto.DefaultOperations(kty);

        if (!body.TryGetProperty("keyOps", out var ops) || ops.ValueKind != JsonValueKind.Array) {
            return Result<ImmutableArray<string>>.Success(allowed);
        }

        var named = ops.EnumerateArray().Select(static x => x.GetString() ?? "").Distinct(StringComparer.Ordinal).ToList();

        var refused = named.Where(x => !allowed.Contains(x, StringComparer.Ordinal)).ToList();
        if (refused.Count > 0) {
            return Result<ImmutableArray<string>>.Failure(
                ErrorCode.InvalidRequestBody,
                $"A {kty} key cannot permit [{string.Join(", ", refused)}]; it can permit [{string.Join(", ", allowed)}].",
                "/keyOps"
            );
        }

        return named.Count == 0
            ? Result<ImmutableArray<string>>.Failure(ErrorCode.InvalidRequestBody, "A key permits at least one operation.", "/keyOps")
            : Result<ImmutableArray<string>>.Success([.. named]);
    }

    // ── What secrets and keys share ────────────────────────────────────────────────────────────

    Dictionary<string, VaultItem> Items(string kind) => kind == SecretKind ? state.State.Secrets : state.State.Keys;

    static string Noun(string kind) => kind == SecretKind ? "Secret" : "Key";

    static string NameProperty(string kind) => kind == SecretKind ? "secretName" : "keyName";

    Result<(string Key, VaultItem Item, ItemVersion Version)> FindVersion(string kind, JsonElement body) {
        var name = Text(body, NameProperty(kind));
        var wanted = Text(body, "version");
        var key = name.ToLowerInvariant();

        // ⚠ A deleted item answers exactly as an absent one does, as Azure's data plane does: the
        // deleted listing is where it is found, and a read that saw it would make the recovery
        // window a second copy of the live one.
        if (!Items(kind).TryGetValue(key, out var item) || item.DeletedOn is not null) {
            return Result<(string, VaultItem, ItemVersion)>.Failure(
                ErrorCode.ResourceNotFound,
                $"{Noun(kind)} '{name}' does not exist in this vault."
            );
        }

        var version = wanted.Length == 0
            ? item.Versions[^1]
            : item.Versions.Find(x => string.Equals(x.Version, wanted, StringComparison.Ordinal));

        return version is null
            ? Result<(string, VaultItem, ItemVersion)>.Failure(
                ErrorCode.ResourceNotFound,
                $"{Noun(kind)} '{name}' has no version {wanted}."
            )
            : Result<(string, VaultItem, ItemVersion)>.Success((key, item, version));
    }

    /// <summary>The refusal for a disabled version, or one outside its validity window, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b><c>409</c>, not Azure's <c>403</c>.</b> Here <c>403</c> means the ReBAC check refused
    ///     the caller, and a tenant reading "forbidden" for a secret that is merely expired would go
    ///     looking at role assignments. The item exists and its state refuses the call, which is
    ///     what <see cref="ErrorCode.Conflict" /> says everywhere else in the platform.
    /// </remarks>
    Result<string>? Unusable(string kind, VaultItem item, ItemVersion version, bool timeBound) {
        if (!version.Enabled) {
            return Refuse(ErrorCode.Conflict, $"{Noun(kind)} '{item.Name}' version {version.Version} is disabled.");
        }

        if (!timeBound) {
            return null;
        }

        var now = clock.UtcNow;

        if (version.NotBefore is { } notBefore && now < notBefore) {
            return Refuse(
                ErrorCode.Conflict,
                $"{Noun(kind)} '{item.Name}' version {version.Version} is not valid before {KeyVaults.Timestamp(notBefore)}."
            );
        }

        return version.ExpiresOn is { } expiresOn && now >= expiresOn
            ? Refuse(
                ErrorCode.Conflict,
                $"{Noun(kind)} '{item.Name}' version {version.Version} expired at {KeyVaults.Timestamp(expiresOn)}."
            )
            : (Result<string>?)null;
    }

    async Task<Result<string>> UpdateAsync(string kind, JsonElement body) {
        var found = FindVersion(kind, body);
        if (found.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        if (Times(body) is { IsFailure: true } times) {
            return Result<string>.Failure(times.Error!);
        }

        var (_, item, version) = found.GetValueOrThrow();

        if (kind == KeyKind && body.TryGetProperty("keyOps", out _)) {
            var operations = Operations(body, version.Kty);
            if (operations.TryGetError(out var operationsError)) {
                return Result<string>.Failure(operationsError);
            }

            version.KeyOps = [.. operations.GetValueOrThrow()];
        }

        if (kind == SecretKind && body.TryGetProperty("contentType", out _)) {
            version.ContentType = Text(body, "contentType");
        }

        if (Flag(body, "enabled") is { } enabled) {
            version.Enabled = enabled;
        }

        if (body.TryGetProperty("notBefore", out _)) {
            version.NotBefore = Stamp(body, "notBefore");
        }

        if (body.TryGetProperty("expiresOn", out _)) {
            version.ExpiresOn = Stamp(body, "expiresOn");
        }

        version.Updated = clock.UtcNow;
        await state.WriteStateAsync();

        return Result<string>.Success((kind == SecretKind ? SecretJson(item, version, null) : KeyJson(item, version)).ToJsonString());
    }

    static Result<string> List(Dictionary<string, VaultItem> items) {
        var lines = items.Values
            .Where(static x => x.DeletedOn is null)
            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static x => Line(x, x.Versions[^1]))
            .ToList();

        return Listing(lines);
    }

    Result<string> ListVersions(string kind, JsonElement body) {
        var found = FindVersion(kind, body);
        if (found.TryGetError(out var error)) {
            return Result<string>.Failure(error);
        }

        var item = found.GetValueOrThrow().Item;
        return Listing([.. Enumerable.Reverse(item.Versions).Select(x => Line(item, x))]);
    }

    static Result<string> ListDeleted(Dictionary<string, VaultItem> items) {
        var lines = items.Values
            .Where(static x => x.DeletedOn is not null)
            .OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static x => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{x.Name} deleted {KeyVaults.Timestamp(x.DeletedOn!.Value)} purges {KeyVaults.Timestamp(x.ScheduledPurgeDate!.Value)}"
                )
            )
            .ToList();

        return Listing(lines);
    }

    static string Line(VaultItem item, ItemVersion version) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{item.Name} {version.Version} {(version.Enabled ? "enabled" : "disabled")} created {KeyVaults.Timestamp(version.Created)} expires {(version.ExpiresOn is { } at ? KeyVaults.Timestamp(at) : "never")}"
        );

    static Result<string> Listing(List<string> lines) =>
        Result<string>.Success(
            new JsonObject { ["count"] = lines.Count, ["items"] = new JsonArray([.. lines.Select(static x => (JsonNode)x)]) }
                .ToJsonString()
        );

    async Task<Result<string>> DeleteAsync(string kind, JsonElement body) {
        var name = Text(body, NameProperty(kind));

        if (!Items(kind).TryGetValue(name.ToLowerInvariant(), out var item) || item.DeletedOn is not null) {
            return Refuse(ErrorCode.ResourceNotFound, $"{Noun(kind)} '{name}' does not exist in this vault.");
        }

        var now = clock.UtcNow;
        item.DeletedOn = now;
        item.ScheduledPurgeDate = now.AddDays(state.State.RecoveryDays);
        await state.WriteStateAsync();

        return Result<string>.Success(DeletedJson(item).ToJsonString());
    }

    static JsonObject DeletedJson(VaultItem item) =>
        new() {
            ["name"] = item.Name,
            ["deletedOn"] = KeyVaults.Timestamp(item.DeletedOn!.Value),
            ["scheduledPurgeDate"] = KeyVaults.Timestamp(item.ScheduledPurgeDate!.Value)
        };

    async Task<Result<string>> RecoverAsync(string kind, JsonElement body) {
        var name = Text(body, NameProperty(kind));

        if (!Items(kind).TryGetValue(name.ToLowerInvariant(), out var item) || item.DeletedOn is null) {
            return Refuse(ErrorCode.ResourceNotFound, $"{Noun(kind)} '{name}' is not a deleted item in this vault.");
        }

        item.DeletedOn = null;
        item.ScheduledPurgeDate = null;
        await state.WriteStateAsync();

        var current = item.Versions[^1];
        return Result<string>.Success((kind == SecretKind ? SecretJson(item, current, null) : KeyJson(item, current)).ToJsonString());
    }

    async Task<Result<string>> PurgeAsync(string kind, JsonElement body) {
        var name = Text(body, NameProperty(kind));
        var key = name.ToLowerInvariant();

        if (!Items(kind).TryGetValue(key, out var item) || item.DeletedOn is null) {
            return Refuse(
                ErrorCode.ResourceNotFound,
                $"{Noun(kind)} '{name}' is not a deleted item in this vault. Only a deleted item can be purged."
            );
        }

        // ⚠ THE PROTECTION ITSELF. docs/plan/18 § The resource model: "a deleted signing key is a
        // business-ending event and 'are you sure' is not sufficient". The item goes on its date, by
        // DropExpiredAsync, and not a moment before — whoever asks, with whatever role.
        if (state.State.PurgeProtection) {
            return Refuse(
                ErrorCode.Conflict,
                $"This vault has purge protection on, so {Noun(kind).ToLowerInvariant()} '{item.Name}' "
                + $"cannot be purged before {KeyVaults.Timestamp(item.ScheduledPurgeDate!.Value)}. It is "
                + "purged then without being asked."
            );
        }

        Items(kind).Remove(key);
        await state.WriteStateAsync();

        return Result<string>.Success(new JsonObject { ["name"] = item.Name, ["purged"] = true }.ToJsonString());
    }

    /// <summary>Drops every deleted item whose window has ended. See the class remarks.</summary>
    async Task DropExpiredAsync() {
        var now = clock.UtcNow;
        var dropped = 0;

        foreach (var items in new[] { state.State.Secrets, state.State.Keys }) {
            foreach (var key in items.Where(x => x.Value.ScheduledPurgeDate is { } at && at <= now).Select(static x => x.Key).ToList()) {
                items.Remove(key);
                dropped++;
            }
        }

        if (dropped > 0) {
            await state.WriteStateAsync();
        }
    }

    // ── The root ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Resolves the vault's root. ⚠ The caller zeroes it.</summary>
    async Task<Result<byte[]>> RootAsync() {
        var resolved = await secrets.ResolveAsync(KeyVaults.RootRef(tenantId, state.State.VaultId));

        if (resolved.TryGetError(out var error)) {
            // The resolver's own refusal is already split into a tenant half and an operator half;
            // what reaches the caller is its tenant half.
            return Result<byte[]>.Failure(error);
        }

        byte[] root;
        try {
            root = Convert.FromBase64String(resolved.GetValueOrThrow());
        } catch (FormatException) {
            return Result<byte[]>.Failure(ErrorCode.InternalError, "The vault's root is not base64 in the platform vault.");
        }

        if (root.Length != KeyVaults.RootLength) {
            CryptographicOperations.ZeroMemory(root);
            return Result<byte[]>.Failure(ErrorCode.InternalError, "The vault's root in the platform vault is not 32 bytes.");
        }

        return Result<byte[]>.Success(root);
    }

    // ── Reading a validated body ───────────────────────────────────────────────────────────────

    // ⚠ The resource manager validated the body against the action's schema before the handler
    // ran, so a type mismatch cannot arrive through the gateway. These readers are still total —
    // they answer empty, null or absent rather than throwing — because the grain is also callable
    // directly, and an exception here would be a 500 with the body in the stack.

    static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    static bool? Flag(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value.ValueKind switch {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    static int? Whole(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var whole)
            ? whole
            : null;

    static DateTimeOffset? Stamp(JsonElement body, string name) =>
        DateTimeOffset.TryParse(Text(body, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    static Result? Times(JsonElement body) {
        var notBefore = Stamp(body, "notBefore");
        var expiresOn = Stamp(body, "expiresOn");

        return notBefore is not null && expiresOn is not null && notBefore >= expiresOn
            ? Result.Failure(ErrorCode.InvalidRequestBody, "notBefore is not before expiresOn.", "/expiresOn")
            : null;
    }

    static Result<string>? Invalid(string name) =>
        name.Length is > 0 and <= 127 && name.All(static x => char.IsAsciiLetterOrDigit(x) || x == '-')
            ? (Result<string>?)null
            : Refuse(ErrorCode.InvalidRequestBody, $"'{name}' is not a name: 1–127 letters, digits and dashes.");

    static string ItemNameOf(JsonElement body) {
        var secret = Text(body, "secretName");
        return secret.Length > 0 ? secret : Text(body, "keyName");
    }

    static string NewVersion() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    static void AddAttributes(JsonObject json, ItemVersion version) {
        json["enabled"] = version.Enabled;

        if (version.NotBefore is { } notBefore) {
            json["notBefore"] = KeyVaults.Timestamp(notBefore);
        }

        if (version.ExpiresOn is { } expiresOn) {
            json["expiresOn"] = KeyVaults.Timestamp(expiresOn);
        }

        json["created"] = KeyVaults.Timestamp(version.Created);
        json["updated"] = KeyVaults.Timestamp(version.Updated);
    }

    static Result<string> Refuse(ErrorCode code, string message, string target = "") =>
        Result<string>.Failure(code, message, target.Length == 0 ? null : target);
}
