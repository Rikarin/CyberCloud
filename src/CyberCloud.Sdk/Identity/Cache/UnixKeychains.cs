using System.Diagnostics;
using System.Runtime.Versioning;

namespace CyberCloud.Sdk;

/// <summary>
///     The macOS Keychain, through the <c>security</c> command-line tool that ships with the OS.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The tool rather than <c>Security.framework</c>, and it is a trade rather than a
///         shortcut.</b> <c>SecItemAdd</c> and friends would avoid a process launch, but they cost
///         about a hundred lines of CoreFoundation marshalling that only ever run on one operating
///         system and that no test on any other machine can exercise. <c>/usr/bin/security</c> is
///         present on every macOS install, is the interface Apple documents for exactly this, and
///         puts the entry where a user can see and delete it in Keychain Access. The cost is one
///         <c>fork</c> per cache read, on a path that already tolerates a subprocess for
///         <see cref="CyberCloudCliCredential" />.
///     </para>
///     <para>
///         ⚠ <b>The secret is passed on stdin, never as an argument.</b> A refresh token in
///         <c>argv</c> is a refresh token in <c>ps</c> output for every user on the machine. The
///         <c>-w</c> form reads it from the terminal, and the <c>-X</c> form takes hex, which is what
///         is used here so no byte of the record needs quoting.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
sealed class MacOsKeychainTokenCache : ITokenCache {
    const string Tool = "/usr/bin/security";

    /// <inheritdoc />
    public bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(Tool);

    /// <inheritdoc />
    public async ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) {
        var result = await Subprocess
            .RunAsync(Tool, ["find-generic-password", "-s", TokenCache.ServiceName, "-a", key, "-w"], cancellationToken)
            .ConfigureAwait(false);

        // Exit code 44 is errSecItemNotFound. Absent is not an error.
        if (result.ExitCode != 0)
            return null;

        return TokenCache.Deserialise(Hex.Decode(result.StandardOutput.Trim()));
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        var hex = Hex.Encode(TokenCache.Serialise(record));

        // -U updates in place when the entry exists, so there is no read-modify-write race with
        // another process holding the same account.
        var result = await Subprocess
            .RunAsync(Tool, ["add-generic-password", "-U", "-s", TokenCache.ServiceName, "-a", key, "-X", hex], cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new AuthenticationFailedException("The token cache entry could not be written to the macOS Keychain.");
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        => await Subprocess
            .RunAsync(Tool, ["delete-generic-password", "-s", TokenCache.ServiceName, "-a", key], cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
///     The freedesktop secret service, through <c>secret-tool</c> (libsecret).
/// </summary>
/// <remarks>
///     ⚠ <b>Absent rather than fatal when <c>secret-tool</c> is not installed.</b> A container image
///     has no secret service and never will, and docs/plan/21 § `cyc` forbids the obvious fallback:
///     <i>"Never a plaintext file — that is how CI credentials leak into container images."</i> So the
///     cache reports itself unavailable, <see cref="TokenCache.CreatePersistent" /> substitutes
///     <see cref="TokenCache.None" />, and the credential re-authenticates every process. That is the
///     correct behaviour in a container: the workload there should be using
///     <see cref="WorkloadIdentityCredential" />, which caches nothing because it needs to cache
///     nothing.
/// </remarks>
[SupportedOSPlatform("linux")]
sealed class LibSecretTokenCache : ITokenCache {
    /// <inheritdoc />
    public bool IsAvailable { get; } = OperatingSystem.IsLinux() && Subprocess.Exists("secret-tool");

    /// <inheritdoc />
    public async ValueTask<TokenCacheRecord?> GetAsync(string key, CancellationToken cancellationToken = default) {
        var result = await Subprocess
            .RunAsync("secret-tool", ["lookup", "service", TokenCache.ServiceName, "account", key], cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode != 0 ? null : TokenCache.Deserialise(Hex.Decode(result.StandardOutput.Trim()));
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(string key, TokenCacheRecord record, CancellationToken cancellationToken = default) {
        var hex = Hex.Encode(TokenCache.Serialise(record));

        // ⚠ On stdin, not in argv — see MacOsKeychainTokenCache's remarks. `secret-tool store` reads
        // the secret from stdin by design for exactly this reason.
        var result = await Subprocess
            .RunAsync(
                "secret-tool",
                ["store", "--label", TokenCache.ServiceName, "service", TokenCache.ServiceName, "account", key],
                cancellationToken,
                standardInput: hex)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new AuthenticationFailedException("The token cache entry could not be written to the secret service.");
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        => await Subprocess
            .RunAsync("secret-tool", ["clear", "service", TokenCache.ServiceName, "account", key], cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Hex, because a keychain tool's argument and stdin are both text and this needs no quoting.</summary>
static class Hex {
    public static string Encode(byte[] bytes) => Convert.ToHexString(bytes);

    public static byte[] Decode(string text) {
        try {
            return Convert.FromHexString(text);
        } catch (FormatException) {
            // An entry written by something else under the same service name. Treated as absent, for
            // the same reason a record this SDK cannot parse is — see TokenCache.Deserialise.
            return [];
        }
    }
}

/// <summary>One child process, run to completion, with its output captured.</summary>
/// <remarks>
///     ⚠ Shared by the keychain caches and by <see cref="CyberCloudCliCredential" />, and it is the
///     one place in the SDK that starts a process. <c>UseShellExecute</c> is false everywhere, so
///     nothing ever goes through a shell and no argument is ever word-split.
/// </remarks>
static class Subprocess {
    public readonly record struct Result(int ExitCode, string StandardOutput, string StandardError);

    public static async ValueTask<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? standardInput = null,
        TimeSpan? timeout = null) {
        var info = new ProcessStartInfo(fileName) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };

        try {
            if (!process.Start())
                throw new CredentialUnavailableException($"'{fileName}' could not be started.");
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
            throw new CredentialUnavailableException($"'{fileName}' is not installed or is not on the path.", e);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));

        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);

        if (standardInput is not null) {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        try {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            Kill(process);

            throw new CredentialUnavailableException($"'{fileName}' did not exit within the time allowed.");
        }

        return new Result(process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    /// <summary>Whether an executable is on the path. Used to decide a cache is absent rather than broken.</summary>
    public static bool Exists(string fileName) {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
            if (File.Exists(Path.Combine(directory, fileName)))
                return true;
        }

        return false;
    }

    static void Kill(Process process) {
        try {
            process.Kill(entireProcessTree: true);
        } catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) {
            // The process exited between the timeout and the kill. Nothing to do, and rethrowing would
            // replace the timeout the caller needs to see with a race nobody can act on.
        }
    }
}
