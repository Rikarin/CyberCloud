using CyberCloud.Identity.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using System.Security.Cryptography;

namespace CyberCloud.Identity.Host.Tokens;

/// <summary>
///     The signing and encryption keys a development run keeps on disk, so a restart does not end
///     every session. docs/plan/11 § Protocol — and, deliberately, not its rotation story.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Development only, and the refusal is at start-up, by name.</b> docs/plan/11
///         § Protocol wants "a rotating key set (30-day rotation, both keys published for 60)", and
///         that is <c>CyberCloud.Vault</c>'s seam (docs/plan/18): a key every replica can read and a
///         rotation job can write. A PEM file in a directory is neither — a second replica has a
///         different key, and a backup of the directory is a backup of the signing key — so a host
///         configured with a directory outside Development throws here, from options configuration,
///         and the exception names the vault. Keyed on the environment rather than on a setting for
///         the reason <c>IdentityHostOpenIddict</c> gives for plain HTTP: a value somebody can set
///         is a value somebody will set. <c>DevelopmentKeyFileTests.RefusesToLoadOutsideDevelopment</c>.
///     </para>
///     <para>
///         ⚠ <b>Both keys persist, not only the signing one.</b> Authorization codes and refresh
///         tokens are encrypted with the encryption key (access tokens are not —
///         <c>IdentityHostOpenIddict</c>), so an ephemeral encryption key beside a persistent
///         signing key would still refuse every portal's refresh cookie after a restart, with an
///         <c>invalid_grant</c> that looks like a session bug. The data-protection key ring, which
///         protects the session and passkey cookies, persists to the same directory for the same
///         reason.
///     </para>
///     <para>
///         Unset means ephemeral keys, as before — every existing test builds the host that way and
///         is untouched. <see cref="AccessTokenPolicy.SigningKeyRotation" /> and
///         <see cref="AccessTokenPolicy.SigningKeyOverlap" /> stay owed to the vault wiring.
///     </para>
/// </remarks>
public sealed class DevelopmentKeyFile {
    /// <summary>The signing key's file name — an ES256 P-256 private key, PKCS#8 PEM.</summary>
    public const string SigningKeyFileName = "signing.key";

    /// <summary>The encryption key's file name — 32 random bytes, base64.</summary>
    public const string EncryptionKeyFileName = "encryption.key";

    /// <summary>The data-protection key ring's directory, under the configured one.</summary>
    public const string DataProtectionDirectoryName = "dataprotection";

    readonly IdentityHostOptions options;
    readonly IHostEnvironment environment;

    /// <summary>Reads the directory and the environment; refuses the combination that is not allowed.</summary>
    /// <param name="options">Where <see cref="IdentityHostOptions.DevelopmentKeyDirectory" /> comes from.</param>
    /// <param name="environment">Which environment this is.</param>
    /// <exception cref="InvalidOperationException">
    ///     A directory is configured and the environment is not Development. The message names
    ///     <c>CyberCloud.Vault</c>.
    /// </exception>
    public DevelopmentKeyFile(IOptions<IdentityHostOptions> options, IHostEnvironment environment) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        this.options = options.Value;
        this.environment = environment;

        if (IsConfigured && !environment.IsDevelopment()) {
            throw new InvalidOperationException(
                $"{IdentityHostOptions.SectionName}:{nameof(IdentityHostOptions.DevelopmentKeyDirectory)} is set and the "
                + $"environment is '{environment.EnvironmentName}'. A key file on disk is a development convenience "
                + "and not a key store: outside Development the signing key set comes from CyberCloud.Vault "
                + "(docs/plan/18) through the seam docs/plan/11 § Protocol names, and that wiring is owed. Unset "
                + "the directory to run with ephemeral keys, or run as Development."
            );
        }
    }

    /// <summary>Whether a directory is configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.DevelopmentKeyDirectory);

    /// <summary>The configured directory, made absolute against the content root.</summary>
    /// <exception cref="InvalidOperationException">Nothing is configured.</exception>
    public string Directory =>
        IsConfigured
            ? Path.GetFullPath(options.DevelopmentKeyDirectory, environment.ContentRootPath)
            : throw new InvalidOperationException("No development key directory is configured.");

    /// <summary>Where the data-protection key ring goes.</summary>
    public string DataProtectionDirectory => Path.Combine(Directory, DataProtectionDirectoryName);

    /// <summary>
    ///     Loads the signing key, creating the file when it does not exist yet.
    /// </summary>
    /// <returns>An ES256 key. The same key on every call as long as the file is there.</returns>
    /// <remarks>
    ///     ⚠ The <c>kid</c> is derived from the public key, so the JWKS the gateway caches names the
    ///     same key before and after a restart — and a different one if the file was replaced, which
    ///     is what makes a stale gateway cache refuse a token instead of accepting one it cannot
    ///     verify. <c>DevelopmentKeyFileTests.ASecondStartLoadsTheSameKeys</c>.
    /// </remarks>
    public ECDsaSecurityKey LoadOrCreateSigningKey() {
        var path = Path.Combine(Directory, SigningKeyFileName);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(path)) {
            key.ImportFromPem(File.ReadAllText(path));
        } else {
            EnsureDirectory();
            WritePrivately(path, key.ExportPkcs8PrivateKeyPem());
        }

        return new ECDsaSecurityKey(key) { KeyId = KeyIdOf(key) };
    }

    /// <summary>
    ///     Loads the encryption key, creating the file when it does not exist yet.
    /// </summary>
    /// <returns>A 256-bit symmetric key for OpenIddict's <c>A256KW</c> wrapping.</returns>
    public SymmetricSecurityKey LoadOrCreateEncryptionKey() {
        var path = Path.Combine(Directory, EncryptionKeyFileName);

        byte[] bytes;

        if (File.Exists(path)) {
            bytes = Convert.FromBase64String(File.ReadAllText(path).Trim());
        } else {
            EnsureDirectory();
            bytes = RandomNumberGenerator.GetBytes(32);
            WritePrivately(path, Convert.ToBase64String(bytes));
        }

        return new SymmetricSecurityKey(bytes);
    }

    void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>
    ///     Writes a key file that only the current user can read.
    /// </summary>
    /// <remarks>
    ///     ⚠ Owner-only on POSIX through <see cref="File.SetUnixFileMode(string, UnixFileMode)" />, which Windows does
    ///     not have; there the directory lives under the developer's own checkout and inherits its
    ///     ACL. Neither is a substitute for the vault, and the class remarks say why.
    /// </remarks>
    static void WritePrivately(string path, string content) {
        File.WriteAllText(path, content);

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>A stable <c>kid</c>: the base64url SHA-256 of the public key's SubjectPublicKeyInfo.</summary>
    static string KeyIdOf(ECDsa key) =>
        Base64UrlEncoder.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
}

/// <summary>
///     Puts the keys on OpenIddict's options: the file's when a directory is configured, ephemeral
///     ones otherwise.
/// </summary>
/// <remarks>
///     ⚠ An <see cref="IConfigureOptions{TOptions}" /> rather than two <c>AddEphemeral*</c> calls in
///     the server builder, because which keys to add depends on <see cref="IdentityHostOptions" />
///     and the environment, and the builder's lambda sees neither. The algorithm is the contract's
///     (<see cref="AccessTokenPolicy.SigningAlgorithm" />) on both paths, so the published key set
///     and the gateway's pinned algorithm cannot disagree whichever path ran.
/// </remarks>
public sealed class IdentityHostKeys(DevelopmentKeyFile file) : IConfigureOptions<OpenIddictServerOptions> {
    /// <inheritdoc />
    public void Configure(OpenIddictServerOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        if (file.IsConfigured) {
            options.SigningCredentials.Add(new SigningCredentials(file.LoadOrCreateSigningKey(), AccessTokenPolicy.SigningAlgorithm));
            options.EncryptionCredentials.Add(
                new EncryptingCredentials(
                    file.LoadOrCreateEncryptionKey(),
                    SecurityAlgorithms.Aes256KW,
                    SecurityAlgorithms.Aes256CbcHmacSha512
                )
            );

            return;
        }

        // ⚠ Ephemeral, as before this type existed — the same two keys AddEphemeralSigningKey and
        // AddEphemeralEncryptionKey would have minted, with the contract's algorithm. Every process
        // restart invalidates every issued token, which is survivable in a test and is the reason
        // the AppHost configures a directory.
        options.SigningCredentials.Add(
            new SigningCredentials(new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)), AccessTokenPolicy.SigningAlgorithm)
        );

        options.EncryptionCredentials.Add(
            new EncryptingCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)),
                SecurityAlgorithms.Aes256KW,
                SecurityAlgorithms.Aes256CbcHmacSha512
            )
        );
    }
}
