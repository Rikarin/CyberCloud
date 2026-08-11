using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     The token a developer already has, from <c>cyc login</c> — docs/plan/21 § The .NET SDK's first
///     line, <c>new CyberCloudCliCredential()</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>It reads the CLI's cache directly, and asks the CLI only as a fallback.</b> Both
///         halves are here rather than split across two programs — see <see cref="ITokenCache" />'s
///         remarks on why. The cache read costs a keychain lookup; the fallback costs a process
///         launch and exists for the case where the CLI is a different build of a different vintage
///         and its record is one this SDK cannot read.
///     </para>
///     <para>
///         ⚠ <b>Nothing this type throws contains the token.</b> The subprocess's stdout is parsed and
///         discarded; a parse failure names the command, never its output.
///     </para>
/// </remarks>
public sealed class CyberCloudCliCredential : TokenCredential {
    /// <summary>The executable, and the arguments docs/plan/21 § Grammar's <c>cyc account</c> group implies.</summary>
    internal const string Executable = "cyc";

    readonly CyberCloudCredentialOptions options;
    readonly string cacheKey;

    /// <summary>Creates the credential.</summary>
    /// <param name="options">The options.</param>
    public CyberCloudCliCredential(CyberCloudCredentialOptions? options = null) {
        this.options = options ?? new CyberCloudCredentialOptions();
        cacheKey = TokenCache.KeyFor(this.options.AuthorityHost, CliClientId, this.options.TenantId);
    }

    /// <summary>
    ///     The client id <c>cyc</c> registers as. ⚠ Placeholder — no document assigns one, and the
    ///     identity module owns the application registrations (docs/plan/11 § The object model,
    ///     <c>IApplicationGrain</c>). Reported.
    /// </summary>
    public const string CliClientId = "cyc";

    /// <summary>Runs the CLI. Substituted by the tests.</summary>
    internal Func<IReadOnlyList<string>, CancellationToken, ValueTask<Subprocess.Result>> Run { get; set; } =
        (arguments, cancellationToken) => Subprocess.RunAsync(Executable, arguments, cancellationToken);

    /// <inheritdoc />
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        var cached = await options.TokenCache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        if (cached is { AccessToken.Length: > 0 } record && record.ExpiresOn - AccessTokenCache.RefreshWindow > DateTimeOffset.UtcNow)
            return new AccessToken(record.AccessToken!, record.ExpiresOn);

        var arguments = new List<string> { "account", "get-access-token", "--output", "json" };

        if ((context.TenantId ?? options.TenantId) is { } tenant) {
            arguments.Add("--tenant");
            arguments.Add(tenant);
        }

        foreach (var scope in context.Scopes ?? []) {
            arguments.Add("--scope");
            arguments.Add(scope);
        }

        var result = await Run(arguments, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
            throw new CredentialUnavailableException(
                // docs/plan/21 § Decisions: exit code 3 is auth. Anything else from the CLI is a
                // different problem and saying "run cyc login" would be wrong advice.
                result.ExitCode == 3
                    ? $"'{Executable}' is not signed in. Run 'cyc login'."
                    : $"'{Executable} account get-access-token' failed with exit code {result.ExitCode}.");

        CliTokenPayload payload;

        try {
            payload = JsonSerializer.Deserialize(result.StandardOutput, SdkJsonContext.Default.CliTokenPayload)
                ?? throw new CredentialUnavailableException($"'{Executable} account get-access-token' printed nothing.");
        } catch (JsonException e) {
            // ⚠ The output is not in the message. It contains an access token.
            throw new CredentialUnavailableException(
                $"'{Executable} account get-access-token --output json' did not print the expected JSON.", e);
        }

        return new AccessToken(payload.AccessToken, payload.ExpiresOn);
    }
}

/// <summary>
///     A service principal described by environment variables — the shape CI uses.
/// </summary>
/// <remarks>
///     <c>CYC_TENANT_ID</c> and <c>CYC_CLIENT_ID</c>, plus either <c>CYC_CLIENT_SECRET</c> or
///     <c>CYC_CLIENT_CERTIFICATE_PATH</c> (with an optional <c>CYC_CLIENT_CERTIFICATE_PASSWORD</c>).
///     The <c>CYC_</c> prefix is docs/plan/21 § Decisions' — <i>"every setting also an env var
///     (<c>CYC_SUBSCRIPTION</c>, …) for CI"</i>.
/// </remarks>
public sealed class EnvironmentCredential : TokenCredential, IDisposable {
    readonly TokenCredential inner;

    /// <summary>Creates the credential from the current environment.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="CredentialUnavailableException">The environment does not describe a service principal.</exception>
    public EnvironmentCredential(CyberCloudCredentialOptions? options = null) {
        var tenantId = Environment.GetEnvironmentVariable("CYC_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("CYC_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("CYC_CLIENT_SECRET");
        var certificatePath = Environment.GetEnvironmentVariable("CYC_CLIENT_CERTIFICATE_PATH");

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId))
            throw new CredentialUnavailableException(
                "CYC_TENANT_ID and CYC_CLIENT_ID are not both set, so no service principal is described by this environment.");

        if (!string.IsNullOrEmpty(secret)) {
            inner = new ClientSecretCredential(tenantId, clientId, secret, options);

            return;
        }

        if (!string.IsNullOrEmpty(certificatePath)) {
            var password = Environment.GetEnvironmentVariable("CYC_CLIENT_CERTIFICATE_PASSWORD");
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password);

            inner = new CertificateCredential(tenantId, clientId, certificate, options);

            return;
        }

        throw new CredentialUnavailableException(
            "CYC_CLIENT_SECRET and CYC_CLIENT_CERTIFICATE_PATH are both unset, so the service principal named by "
            + "CYC_CLIENT_ID has no credential to present.");
    }

    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default)
        => inner.GetTokenAsync(context, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => (inner as IDisposable)?.Dispose();
}

/// <summary>
///     Tries each credential in turn, stopping at the first that produces a token.
/// </summary>
/// <remarks>
///     ⚠ <b>Only <see cref="CredentialUnavailableException" /> moves to the next link.</b> Anything
///     else stops the chain and is reported. A wrong client secret that fell through to an
///     interactive browser prompt would be a chain that hides its own misconfiguration — the
///     developer sees a sign-in window and never learns that CI is broken.
/// </remarks>
public class ChainedTokenCredential : TokenCredential, IDisposable {
    readonly TokenCredential[] sources;

    /// <summary>Creates the chain.</summary>
    /// <param name="sources">The credentials, in order.</param>
    public ChainedTokenCredential(params TokenCredential[] sources) {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Length == 0)
            throw new ArgumentException("A credential chain needs at least one credential.", nameof(sources));

        this.sources = sources;
    }

    /// <inheritdoc />
    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default) {
        List<Exception>? unavailable = null;

        foreach (var source in sources) {
            try {
                return await source.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            } catch (CredentialUnavailableException e) {
                unavailable ??= [];
                unavailable.Add(e);
            }
        }

        // The aggregate is the message: "no credential found" without saying which ones were tried and
        // why each declined is the single most common unanswerable support question about a chain.
        throw new CredentialUnavailableException(
            "No credential in the chain could be used:"
            + string.Concat(unavailable!.Select(x => $"{Environment.NewLine}  • {x.Message}")),
            new AggregateException(unavailable!));
    }

    /// <inheritdoc />
    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes every credential in the chain that needs it.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    protected virtual void Dispose(bool disposing) {
        if (!disposing)
            return;

        foreach (var source in sources)
            (source as IDisposable)?.Dispose();
    }
}

/// <summary>
///     The credential to reach for when you do not want to choose one — the shape
///     <c>DefaultAzureCredential</c> made familiar, over this platform's mechanisms.
/// </summary>
/// <remarks>
///     <para>
///         The order is deliberate and runs from most explicit to most interactive:
///     </para>
///     <list type="number">
///         <item><see cref="EnvironmentCredential" /> — CI said exactly what to use.</item>
///         <item><see cref="WorkloadIdentityCredential" /> — the pod is bound to a managed identity.</item>
///         <item><see cref="CyberCloudCliCredential" /> — a developer is signed in at a terminal.</item>
///         <item>An interactive sign-in, <b>only if asked for</b>.</item>
///     </list>
///     <para>
///         ⚠ <b>Interactive is off by default</b>, unlike Azure's equivalent. A chain that opens a
///         browser on a build agent converts a missing environment variable into a job that hangs
///         until it is killed, and the failure a developer needs at that point is "no credential
///         found", immediately and in the log.
///     </para>
/// </remarks>
public sealed class DefaultCyberCloudCredential : ChainedTokenCredential {
    /// <summary>Creates the chain.</summary>
    /// <param name="options">The options.</param>
    public DefaultCyberCloudCredential(DefaultCyberCloudCredentialOptions? options = null)
        : base(Build(options ?? new DefaultCyberCloudCredentialOptions())) { }

    static TokenCredential[] Build(DefaultCyberCloudCredentialOptions options) {
        var chain = new List<TokenCredential>();

        // ⚠ Each link is constructed inside a try: these credentials report "not applicable" from
        // their constructor, because that is where the environment is read. A chain that let one
        // constructor throw would be a chain that cannot be built on the machine it is meant to
        // fall back on.
        Add(() => new EnvironmentCredential(options), !options.ExcludeEnvironmentCredential);
        Add(() => new WorkloadIdentityCredential(options), !options.ExcludeWorkloadIdentityCredential);
        Add(() => new CyberCloudCliCredential(options), !options.ExcludeCliCredential);

        if (options.IncludeInteractiveCredential) {
            var openBrowser = options.OpenBrowser
                ?? throw new ArgumentException(
                    $"{nameof(DefaultCyberCloudCredentialOptions.IncludeInteractiveCredential)} is set but "
                    + $"{nameof(DefaultCyberCloudCredentialOptions.OpenBrowser)} is not. The SDK does not decide on its "
                    + "own that opening a browser is appropriate here.",
                    nameof(options));

            var clientId = options.ClientId ?? CyberCloudCliCredential.CliClientId;

            Add(() => new InteractiveBrowserCredential(clientId, openBrowser, options), true);
        }

        if (chain.Count == 0)
            throw new CredentialUnavailableException("Every credential was excluded, so the chain is empty.");

        return [.. chain];

        void Add(Func<TokenCredential> create, bool included) {
            if (!included)
                return;

            try {
                chain.Add(create());
            } catch (CredentialUnavailableException) {
                // Not applicable here. The next link gets its turn, and if none of them apply the
                // chain's own message lists what was tried.
            }
        }
    }
}
