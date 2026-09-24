using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace CyberCloud.Sdk;

/// <summary>
///     The token cache for a machine with no keychain: one file per entry, in the per-user state
///     directory the operating system names, readable and writable by the owner and nobody else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The fallback, never the first choice, and why it exists at all.</b> docs/plan/21
///         § Decisions put refresh tokens in the OS keychain and nowhere else, and on a desktop that
///         still holds — <see cref="TokenCache.CreatePersistent" /> takes the keychain whenever there
///         is one. But the device flow exists for the machine that has none: a build agent, an SSH
///         session on a server with no <c>secret-tool</c>. There the old answer was no cache at all,
///         so <c>cyc login --device-code</c> signed in, printed "Signed in." and the next command
///         asked again — the one flow built for headless boxes did nothing on one. #43 put this file
///         behind the keychain, the shape <c>gh</c> and the Azure CLI both settled on for the same
///         machine.
///     </para>
///     <para>
///         ⚠ <b>Owner-only is enforced on write <i>and</i> checked on read.</b> On Unix the directory
///         is created <c>0700</c> and each file <c>0600</c> from the first byte — created with that
///         mode rather than chmodded after, so there is no instant where another user could open it
///         — and a file whose mode has grown group or other bits is treated as absent, as
///         <c>ssh</c> refuses a private key it can read too widely: somebody else may already hold
///         it, and redeeming it would be using a token that is no longer only ours. On Windows the
///         file gets a protected DACL granting the current user alone, with inheritance cut, so an
///         administrator's broad ACL on the profile does not flow into it.
///     </para>
///     <para>
///         ⚠ <b>The file name is a digest of the key, not the key.</b> The key is
///         <c>{authority host}|{client id}|{tenant}</c> (<see cref="TokenCache.KeyFor" />), and a
///         directory listing that spelled out which tenants a person is signed into would be a small
///         leak with no purpose.
///     </para>
///     <para>
///         ⚠ <b>What this does not protect against, stated.</b> A process running as the same user
///         reads it — as it could read the keychain by asking <c>cyc account get-access-token</c>,
///         docs/plan/21 § The trust boundary — and so does root. And an image built with
///         <c>cyc login</c> in a <c>RUN</c> step carries the file in a layer, which is the leak
///         docs/plan/21 warned of: CI signs in with <c>--service-principal</c>, whose cache is
///         <see cref="TokenCache.None" />, and that is the rule the docs keep.
///     </para>
/// </remarks>
/// <param name="directory">Where the entries live. <see cref="DefaultDirectory" /> unless a test says otherwise.</param>
public sealed class FileTokenCache(string directory) : ITokenCache {
    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode Wider = ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>The directory the entries live in.</summary>
    public string Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>
    ///     The per-user location on this operating system: <c>%LOCALAPPDATA%\CyberCloud\tokens</c> on
    ///     Windows, <c>~/Library/Application Support/CyberCloud/tokens</c> on macOS, and
    ///     <c>$XDG_STATE_HOME/cybercloud/tokens</c> — <c>~/.local/state</c> when unset — elsewhere.
    /// </summary>
    /// <remarks>
    ///     State, not configuration, on Linux: the XDG base-directory specification puts "data that
    ///     should persist between restarts but is not important or portable enough" there, which is
    ///     a refresh token exactly, and it keeps the file out of a dotfiles repository that tracks
    ///     <c>~/.config</c>.
    /// </remarks>
    public static string DefaultDirectory {
        get {
            if (OperatingSystem.IsWindows()) {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CyberCloud",
                    "tokens"
                );
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (OperatingSystem.IsMacOS()) {
                return Path.Combine(home, "Library", "Application Support", "CyberCloud", "tokens");
            }

            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } configured
                && Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(home, ".local", "state");

            return Path.Combine(state, "cybercloud", "tokens");
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <summary>The file an entry lives in.</summary>
    /// <param name="key">The entry's key.</param>
    public string PathFor(string key) {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

        return Path.Combine(Directory, digest + ".json");
    }

    /// <inheritdoc />
    public async ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) {
        var path = PathFor(key);

        if (!File.Exists(path) || !IsOwnerOnly(path)) {
            return null;
        }

        try {
            return TokenCache.Deserialise(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(record);

        EnsureDirectory();

        var path = PathFor(key);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var bytes = TokenCache.Serialise(record);

        try {
            // ⚠ Created owner-only, not narrowed afterwards — see the type's remarks — then moved over
            // the entry, so a reader never sees half a record.
            await using (var stream = CreateOwnerOnly(staging)) {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(staging, path, true);
        } finally {
            CryptographicOperations.ZeroMemory(bytes);

            if (File.Exists(staging)) {
                File.Delete(staging);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) {
        var path = PathFor(key);

        if (File.Exists(path)) {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Whether <paramref name="path" /> is readable by its owner and by nobody else.</summary>
    /// <param name="path">A file this cache wrote.</param>
    public static bool IsOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) {
            return IsOwnerOnlyOnWindows(path);
        }

        return (File.GetUnixFileMode(path) & Wider) == 0;
    }

    void EnsureDirectory() {
        if (OperatingSystem.IsWindows()) {
            System.IO.Directory.CreateDirectory(Directory);

            return;
        }

        System.IO.Directory.CreateDirectory(Directory, OwnerOnlyDirectory);
        // ⚠ CreateDirectory leaves an existing directory's mode alone, so one made by something
        // else — or by an older build — is narrowed here.
        File.SetUnixFileMode(Directory, OwnerOnlyDirectory);
    }

    static FileStream CreateOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) {
            return CreateOwnerOnlyOnWindows(path);
        }

        return new(
            path,
            new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = OwnerOnlyFile
            }
        );
    }

    [SupportedOSPlatform("windows")]
    static FileStream CreateOwnerOnlyOnWindows(string path) {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");

        var security = new FileSecurity();
        security.SetOwner(owner);
        // ⚠ Protected, and no inherited rules kept: the profile directory's ACL grants SYSTEM and
        // Administrators, and a token file that inherited them would be owner-plus.
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));

        return new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.ReadData | FileSystemRights.Delete,
            FileShare.None,
            4096,
            FileOptions.None,
            security
        );
    }

    [SupportedOSPlatform("windows")]
    static bool IsOwnerOnlyOnWindows(string path) {
        var owner = WindowsIdentity.GetCurrent().User;
        var rules = new FileInfo(path).GetAccessControl()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(static x => x.AccessControlType == AccessControlType.Allow)
            .ToList();

        return owner is not null && rules.Count > 0 && rules.All(x => owner.Equals(x.IdentityReference));
    }
}
