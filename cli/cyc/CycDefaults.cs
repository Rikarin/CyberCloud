using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CyberCloud.Cli;

/// <summary>
///     The real implementations of the three things <see cref="CycHost" /> abstracts: the SDK client,
///     the signed-in credential, and the browser.
/// </summary>
static class CycDefaults {
    /// <summary>
    ///     Builds an SDK client for one invocation.
    /// </summary>
    /// <param name="request">Where to send, which api-version, and for which tenant.</param>
    /// <param name="credential">The credential the pipeline asks for tokens.</param>
    public static CyberCloudClient Client(CycClientRequest request, TokenCredential credential) {
        ArgumentNullException.ThrowIfNull(request);

        return new CyberCloudClient(request.Endpoint, credential, new CyberCloudClientOptions(ServiceVersionFor(request.ApiVersion)));
    }

    /// <summary>
    ///     Maps an api-version on the wire onto the SDK's <c>ServiceVersion</c>.
    /// </summary>
    /// <param name="apiVersion">The wire form — <c>2026-08-01</c>.</param>
    /// <exception cref="CycUsageException">
    ///     The verb tree carries an api-version this SDK build cannot speak.
    /// </exception>
    /// <remarks>
    ///     ⚠ <b>Hand-maintained, and it should not have to be.</b> <c>CyberCloudClientOptions</c>
    ///     turns its enum into a wire string and offers no way back, so every consumer that takes an
    ///     api-version from configuration writes this switch. Reported as an SDK gap: the mapping
    ///     belongs next to the enum, where the generator that adds a member can add the arm.
    /// </remarks>
    public static CyberCloudClientOptions.ServiceVersion ServiceVersionFor(string apiVersion)
        => apiVersion switch {
            "2026-08-01" => CyberCloudClientOptions.ServiceVersion.V2026_08_01,
            _ => throw new CycUsageException(
                $"This build of cyc carries a verb tree for api-version '{apiVersion}' and its SDK cannot "
                + "speak it. The two ship together, so this is a build problem rather than something to "
                + "work around — rebuild cyc against a matching CyberCloud.Sdk."),
        };

    /// <summary>
    ///     The credential for a developer who has already run <c>cyc login</c>, or for CI.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The browser callback refuses.</b> The last link redeems the refresh token the SDK put
    ///     in the OS keychain, which is the path that carries every command after a sign-in; if there
    ///     is nothing cached it would otherwise open a browser, and a browser opening on a build agent
    ///     turns a missing environment variable into a job that hangs until somebody kills it. Refusing
    ///     with <see cref="CredentialUnavailableException" /> makes the chain report what it tried and
    ///     tell the user to run <c>cyc login</c>.
    /// </remarks>
    public static TokenCredential SignedInCredential()
        => new DefaultCyberCloudCredential(new DefaultCyberCloudCredentialOptions {
            ExcludeCliCredential = true,
            IncludeInteractiveCredential = true,
            OpenBrowser = (_, _) => throw new CredentialUnavailableException(
                "No sign-in is cached for this authority. Run 'cyc login', or set CYC_TENANT_ID, "
                + "CYC_CLIENT_ID and CYC_CLIENT_SECRET for a service principal."),
        });

    /// <summary>
    ///     Opens a URL in whatever the platform considers the user's browser.
    /// </summary>
    /// <param name="uri">The URL.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <remarks>
    ///     ⚠ Failure is not an error here. A headless box has no browser and the device-code flow
    ///     prints a URL the user opens somewhere else — so a launcher that threw would turn the
    ///     supported case into a failed sign-in.
    /// </remarks>
    public static Task OpenBrowser(Uri uri, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(uri);

        try {
            var start = new ProcessStartInfo { UseShellExecute = true, FileName = uri.ToString() };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                start = new ProcessStartInfo("xdg-open", [uri.ToString()]) { UseShellExecute = false };

            using var process = Process.Start(start);
        } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) {
            // Nothing to open it with. The caller has already printed the URL.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Whether this looks like a machine with no browser — an SSH session, or a Linux box with no
    ///     display. <c>cyc login</c> chooses the device-code flow when it is true.
    /// </summary>
    /// <param name="environment">The process environment.</param>
    public static bool LooksHeadless(IReadOnlyDictionary<string, string> environment) {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.ContainsKey("SSH_CONNECTION") || environment.ContainsKey("SSH_TTY"))
            return true;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            && !environment.ContainsKey("DISPLAY")
            && !environment.ContainsKey("WAYLAND_DISPLAY");
    }

    /// <summary>The process environment as a dictionary.</summary>
    public static IReadOnlyDictionary<string, string> Environment() {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables()) {
            if (entry.Key is string key && entry.Value is string value)
                values[key] = value;
        }

        return values;
    }
}
