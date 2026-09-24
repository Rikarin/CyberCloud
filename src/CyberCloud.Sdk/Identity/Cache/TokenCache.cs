using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     Where refresh tokens live between processes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The keychain first, and a file only where there is no keychain.</b> docs/plan/21
///         § Decisions' token-cache row: the OS keychain (Credential Manager / Keychain / libsecret),
///         and CI signs in with no cache at all, because a file is how CI credentials leak into
///         container images. Until #43 the rule was "never a file", and on a headless box with no
///         <c>secret-tool</c> that meant no cache — so the device flow, the one sign-in built for such
///         a box, persisted nothing there. <see cref="FileTokenCache" /> is now the fallback, owner-only
///         on write and refused on read when it is not, and its remarks say what it does and does not
///         protect against.
///     </para>
///     <para>
///         ⚠ <b>The SDK reads and writes it; the CLI does neither.</b> docs/plan/21 § The .NET SDK's
///         first line is <c>new CyberCloudCliCredential()</c>, so the SDK is on the read side by
///         construction. Splitting the two halves across two codebases would make the record format an
///         undocumented contract, and an undocumented contract between two programs that ship
///         separately drifts on the first release where only one of them changes.
///     </para>
/// </remarks>
public interface ITokenCache {
    /// <summary>Whether this cache can be used here. A keychain on a machine with no keychain is not an error, it is absent.</summary>
    bool IsAvailable { get; }

    /// <summary>Reads an entry.</summary>
    /// <param name="key">The entry's key. See <see cref="TokenCache.KeyFor" />.</param>
    /// <param name="cancellationToken">The token.</param>
    ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes an entry, replacing any existing one.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="record">The record.</param>
    /// <param name="cancellationToken">The token.</param>
    ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default);

    /// <summary>Removes an entry. Not an error when it is not there.</summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="cancellationToken">The token.</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Chooses and builds token caches.</summary>
public static class TokenCache {
    /// <summary>The keychain service name every implementation stores under.</summary>
    public const string ServiceName = "io.cybercloud.sdk";

    /// <summary>A cache that keeps nothing. The right choice for CI, where there is nothing to keep.</summary>
    public static ITokenCache None { get; } = new NullTokenCache();

    /// <summary>
    ///     The OS keychain: Keychain on macOS, Credential Manager on Windows, libsecret on Linux.
    /// </summary>
    /// <remarks>
    ///     ⚠ Falls back to <see cref="FileTokenCache" /> in <see cref="FileTokenCache.DefaultDirectory" />
    ///     when no keychain is reachable — a headless box with no <c>secret-tool</c> — and no longer to
    ///     <see cref="None" />, which is what made <c>cyc login --device-code</c> a sign-in that the
    ///     next command could not find (#43). <see cref="IsFileBacked" /> tells a caller which one it
    ///     got, so <c>cyc login</c> can say where the token went.
    /// </remarks>
    public static ITokenCache CreatePersistent() {
        if (OperatingSystem.IsMacOS()) {
            return Fallback(new MacOsKeychainTokenCache());
        }

        if (OperatingSystem.IsWindows()) {
            return Fallback(new WindowsCredentialManagerTokenCache());
        }

        if (OperatingSystem.IsLinux()) {
            return Fallback(new LibSecretTokenCache());
        }

        return new FileTokenCache(FileTokenCache.DefaultDirectory);

        static ITokenCache Fallback(ITokenCache cache) =>
            cache.IsAvailable ? cache : new FileTokenCache(FileTokenCache.DefaultDirectory);
    }

    /// <summary>Whether <paramref name="cache" /> keeps entries in a file rather than a keychain.</summary>
    /// <param name="cache">A cache <see cref="CreatePersistent" /> returned, or any other.</param>
    public static bool IsFileBacked(ITokenCache cache) => cache is FileTokenCache;

    /// <summary>A cache that lives as long as the process. What the tests use, and what a server-side host wants.</summary>
    public static ITokenCache CreateInMemory() => new InMemoryTokenCache();

    /// <summary>
    ///     The key an entry is stored under: authority, client id and the tenant, so that two
    ///     authorities or two client registrations never read each other's refresh tokens.
    /// </summary>
    /// <param name="authority">The identity host.</param>
    /// <param name="clientId">The OAuth client id.</param>
    /// <param name="tenantId">The tenant, or <see langword="null" />.</param>
    public static string KeyFor(Uri authority, string clientId, string? tenantId) =>
        string.Create(CultureInfo.InvariantCulture, $"{authority.Host}|{clientId}|{tenantId ?? "-"}");

    internal static byte[] Serialise(TokenCacheRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record, SdkJsonContext.Default.TokenCacheRecord);

    internal static TokenCacheRecord? Deserialise(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize(bytes, SdkJsonContext.Default.TokenCacheRecord);
        } catch (JsonException) {
            // A record written by an older or newer SDK that this one cannot read is treated as
            // absent. Throwing would make a stale keychain entry an unrecoverable sign-in failure
            // that no error message could explain to the user.
            return null;
        }
    }
}

sealed class NullTokenCache : ITokenCache {
    public bool IsAvailable => false;

    public ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<TokenCacheRecord?>(null);

    public ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>A cache in a dictionary. Process lifetime, no persistence, no keychain prompt.</summary>
public sealed class InMemoryTokenCache : ITokenCache {
    readonly Dictionary<string, TokenCacheRecord> entries = new(StringComparer.Ordinal);
    readonly Lock gate = new();

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) {
        lock (gate) {
            return ValueTask.FromResult(entries.GetValueOrDefault(key));
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        lock (gate) {
            entries[key] = record;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) {
        lock (gate) {
            entries.Remove(key);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
///     The Windows Credential Manager, through <c>advapi32</c>'s <c>CredReadW</c> /
///     <c>CredWriteW</c> / <c>CredDeleteW</c>.
/// </summary>
/// <remarks>
///     ⚠
///     <b>
///         P/Invoke rather than <c>System.Security.Cryptography.ProtectedData</c>, and the reason is
///         the dependency register.
///     </b> DPAPI's managed wrapper is a NuGet package that docs/plan/02 does
///     not list, and docs/plan/02's own rule is that a package not in the register needs an ADR.
///     Credential Manager is in the OS, reachable with <c>[LibraryImport]</c> — which the AOT analyser
///     is happy with — and is the store a Windows user can actually inspect and revoke, which a
///     DPAPI blob in a file is not.
///     <para>
///         ⚠ <b>A record is written in chunks, because one credential holds 2,560 bytes and a real
///         record does not fit.</b> The access token, the refresh token OpenIddict encrypts and the
///         metadata beside them came to 2,951 bytes against the real identity host, and
///         <c>CredWriteW</c> refused the write — so <c>cyc login</c> on Windows failed after the
///         person had approved it, and no scripted-server test could see it, because a scripted
///         token is thirty bytes. #43 found it (<c>Identity.Host.Tests</c>'
///         <c>DeviceFlowThroughTheSdkTests</c> measures the record); the first chunk's first byte
///         is the count and the rest follow under <c>{target}#1</c>, <c>#2</c> and so on.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
sealed partial class WindowsCredentialManagerTokenCache : ITokenCache {
    const int CredentialTypeGeneric = 1;
    const int CredentialPersistLocalMachine = 2;

    /// <summary><c>CRED_MAX_CREDENTIAL_BLOB_SIZE</c> — 5 × 512 bytes. <c>CredWriteW</c> refuses anything larger.</summary>
    const int MaxBlob = 2_560;

    /// <summary>The most chunks a record may take — far past any record this SDK writes, and a byte.</summary>
    const int MaxChunks = 16;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) {
        if (Read(TargetName(key, 0)) is not { Length: > 0 } first) {
            return ValueTask.FromResult<TokenCacheRecord?>(null);
        }

        // ⚠ A record written before chunking starts with its JSON's '{'; a chunked one starts with
        // its chunk count, which is never that byte. Read either.
        if (first[0] == (byte)'{') {
            return ValueTask.FromResult(TokenCache.Deserialise(first));
        }

        var chunks = first[0];
        var payload = new List<byte>(first.Length * chunks);
        payload.AddRange(first.AsSpan(1));

        for (var index = 1; index < chunks; index++) {
            if (Read(TargetName(key, index)) is not { } chunk) {
                // A chunk missing is a record half-written or half-removed: absent, not an error.
                return ValueTask.FromResult<TokenCacheRecord?>(null);
            }

            payload.AddRange(chunk);
        }

        return ValueTask.FromResult(TokenCache.Deserialise(payload.ToArray()));
    }

    public ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        var bytes = TokenCache.Serialise(record);

        try {
            // ⚠ Chunked: see the type's remarks. The first chunk carries the count in its first byte.
            var chunks = (bytes.Length + MaxBlob) / MaxBlob;

            if (chunks > MaxChunks) {
                throw new AuthenticationFailedException(
                    "The token cache entry is too large for Credential Manager even in chunks."
                );
            }

            for (var index = 0; index < chunks; index++) {
                var blob = index == 0
                    ? [(byte)chunks, .. bytes.AsSpan(0, Math.Min(bytes.Length, MaxBlob - 1))]
                    : bytes.AsSpan(index * MaxBlob - 1, Math.Min(MaxBlob, bytes.Length - (index * MaxBlob - 1))).ToArray();

                try {
                    Write(TargetName(key, index), blob);
                } finally {
                    CryptographicOperations.ZeroMemory(blob);
                }
            }

            // A shorter record than the last one leaves chunks behind; clear them.
            for (var index = chunks; index < MaxChunks && CredDelete(TargetName(key, index), CredentialTypeGeneric, 0); index++) { }
        } finally {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) {
        CredDelete(TargetName(key, 0), CredentialTypeGeneric, 0);

        for (var index = 1; index < MaxChunks && CredDelete(TargetName(key, index), CredentialTypeGeneric, 0); index++) { }

        return ValueTask.CompletedTask;
    }

    static byte[]? Read(string target) {
        if (!CredRead(target, CredentialTypeGeneric, 0, out var handle)) {
            return null;
        }

        try {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            return bytes;
        } finally {
            CredFree(handle);
        }
    }

    static void Write(string targetName, byte[] bytes) {
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        var target = Marshal.StringToCoTaskMemUni(targetName);

        try {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new Credential {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine
            };

            if (!CredWrite(ref credential, 0)) {
                throw new AuthenticationFailedException(
                    "The token cache entry could not be written to Credential Manager."
                );
            }
        } finally {
            // ⚠ Zeroed before it is freed. A refresh token left in released unmanaged memory is a
            // refresh token in whatever allocates that page next, and this is the one place in the SDK
            // where the runtime is not doing that for us.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(target);
        }
    }

    static string TargetName(string key, int chunk) =>
        chunk == 0 ? TargetName(key) : TargetName(key) + "#" + chunk.ToString(CultureInfo.InvariantCulture);

    static string TargetName(string key) => $"{TokenCache.ServiceName}:{key}";

    [StructLayout(LayoutKind.Sequential)]
    struct Credential {
        public int Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int reservedFlag, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, int flags);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);
}
