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
///         ⚠ <b>Never a plaintext file.</b> docs/plan/21 § `cyc`, on the token cache row: <i>"OS
///         keychain (DPAPI / Keychain / libsecret). ⚠ <b>Never a plaintext file</b> — that is how CI
///         credentials leak into container images."</i> Every implementation of this interface that
///         ships here is a keychain or a process-lifetime dictionary; there is no file-backed one to
///         reach for in a hurry.
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
    ///     ⚠ Falls back to <see cref="None" /> rather than to a file when no keychain is reachable —
    ///     a headless container with no <c>secret-tool</c> gets no persistence and re-authenticates,
    ///     which is the behaviour docs/plan/21 § `cyc`'s "never a plaintext file" rule demands. The
    ///     fallback is silent to the caller and visible through <see cref="ITokenCache.IsAvailable" />.
    /// </remarks>
    public static ITokenCache CreatePersistent() {
        if (OperatingSystem.IsMacOS())
            return Fallback(new MacOsKeychainTokenCache());

        if (OperatingSystem.IsWindows())
            return Fallback(new WindowsCredentialManagerTokenCache());

        if (OperatingSystem.IsLinux())
            return Fallback(new LibSecretTokenCache());

        return None;

        static ITokenCache Fallback(ITokenCache cache) => cache.IsAvailable ? cache : None;
    }

    /// <summary>A cache that lives as long as the process. What the tests use, and what a server-side host wants.</summary>
    public static ITokenCache CreateInMemory() => new InMemoryTokenCache();

    /// <summary>
    ///     The key an entry is stored under: authority, client id and the tenant, so that two
    ///     authorities or two client registrations never read each other's refresh tokens.
    /// </summary>
    /// <param name="authority">The identity host.</param>
    /// <param name="clientId">The OAuth client id.</param>
    /// <param name="tenantId">The tenant, or <see langword="null" />.</param>
    public static string KeyFor(Uri authority, string clientId, string? tenantId)
        => string.Create(CultureInfo.InvariantCulture, $"{authority.Host}|{clientId}|{tenantId ?? "-"}");

    internal static byte[] Serialise(TokenCacheRecord record)
        => JsonSerializer.SerializeToUtf8Bytes(record, SdkJsonContext.Default.TokenCacheRecord);

    internal static TokenCacheRecord? Deserialise(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty)
            return null;

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

    public ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<TokenCacheRecord?>(null);

    public ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

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
        lock (gate)
            return ValueTask.FromResult(entries.GetValueOrDefault(key));
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        lock (gate)
            entries[key] = record;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) {
        lock (gate)
            entries.Remove(key);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
///     The Windows Credential Manager, through <c>advapi32</c>'s <c>CredReadW</c> /
///     <c>CredWriteW</c> / <c>CredDeleteW</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>P/Invoke rather than <c>System.Security.Cryptography.ProtectedData</c>, and the reason is
///     the dependency register.</b> DPAPI's managed wrapper is a NuGet package that docs/plan/02 does
///     not list, and docs/plan/02's own rule is that a package not in the register needs an ADR.
///     Credential Manager is in the OS, reachable with <c>[LibraryImport]</c> — which the AOT analyser
///     is happy with — and is the store a Windows user can actually inspect and revoke, which a
///     DPAPI blob in a file is not.
/// </remarks>
[SupportedOSPlatform("windows")]
sealed partial class WindowsCredentialManagerTokenCache : ITokenCache {
    const int CredentialTypeGeneric = 1;
    const int CredentialPersistLocalMachine = 2;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) {
        if (!CredRead(TargetName(key), CredentialTypeGeneric, 0, out var handle))
            return ValueTask.FromResult<TokenCacheRecord?>(null);

        try {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            return ValueTask.FromResult(TokenCache.Deserialise(bytes));
        } finally {
            CredFree(handle);
        }
    }

    public ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        var bytes = TokenCache.Serialise(record);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        var target = Marshal.StringToCoTaskMemUni(TargetName(key));

        try {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new Credential {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
            };

            if (!CredWrite(ref credential, 0))
                throw new AuthenticationFailedException("The token cache entry could not be written to Credential Manager.");
        } finally {
            // ⚠ Zeroed before it is freed. A refresh token left in released unmanaged memory is a
            // refresh token in whatever allocates that page next, and this is the one place in the SDK
            // where the runtime is not doing that for us.
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(target);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) {
        CredDelete(TargetName(key), CredentialTypeGeneric, 0);

        return ValueTask.CompletedTask;
    }

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

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int reservedFlag, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);
}
