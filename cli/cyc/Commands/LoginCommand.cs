using CyberCloud.Cli.Execution;
using CyberCloud.Cli.Output;
using CyberCloud.Cli.VerbTree;
using System.Buffers.Text;
using System.CommandLine;
using System.Security.Cryptography.X509Certificates;

namespace CyberCloud.Cli.Commands;

/// <summary>
///     <c>cyc login</c> — docs/plan/21 § Grammar:
///     <c>cyc login [--device-code | --service-principal --tenant T --client-id C --certificate P]</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This command is user experience and nothing else.</b> It parses flags, picks one of
///         the SDK's credentials, prints the user code and the verification URL, opens a browser and
///         shows that something is still happening while the SDK polls. The protocol — the device
///         authorization request, RFC 8628's back-off, PKCE, the loopback listener, the
///         <c>state</c> check, the refresh-token exchange with reuse detection and the keychain the
///         result is written to — is <c>CyberCloud.Sdk</c>'s, all of it. There is no HTTP call in this
///         file.
///     </para>
///     <para>
///         ⚠ <b>No secret is accepted on the command line.</b> docs/plan/21 § Grammar's service
///         principal row names <c>--certificate</c> and not <c>--client-secret</c>, and that is the
///         right shape: an argument is in the shell history, in <c>ps</c> output and in every CI log
///         that echoes its command. A client secret has to arrive in <c>CYC_CLIENT_SECRET</c>.
///     </para>
///     <para>
///         ⚠ <b>Nothing is written to <c>~/.cyc</c> by signing in.</b> The refresh token goes to the
///         OS keychain through <c>CyberCloudCredentialOptions.TokenCache</c>, whose default is
///         <c>TokenCache.CreatePersistent</c> — or, on a machine with no keychain, to the SDK's
///         owner-only file in the per-user state directory (<c>FileTokenCache</c>, #43), and the
///         command says which. <c>NoCredentialLeakTests.NoTokenCacheIsWrittenByTheCli</c> signs in
///         against a scripted identity server and asserts the CLI's own state directory is untouched.
///     </para>
///     <para>
///         ⚠ <b>The tenant it reports is the token's, not the flag's.</b> A person signing in picks
///         the organisation on the sign-in page: the device authorization request carries no tenant
///         (RFC 8628 has no parameter for one, and the identity host reads none there), and a cached
///         sign-in is refreshed for whatever tenant it was for. So <c>--tenant</c> is a hint the
///         browser flow sends and the device flow can't, and the report reads <c>tid</c> off the
///         access token it got. The review of #43 found <c>cyc login --device-code --tenant X</c>
///         printing <c>tenant: X</c> for a token in another tenant.
///     </para>
/// </remarks>
static class LoginCommand {
    /// <summary>Builds the command.</summary>
    /// <param name="host">The host.</param>
    /// <param name="globals">The global options.</param>
    /// <param name="tree">The verb tree, for the api-version the session is bound to.</param>
    public static Command Build(CycHost host, GlobalOptions globals, VerbTreeDocument tree) {
        ArgumentNullException.ThrowIfNull(host);

        var deviceCode = new Option<bool>("--device-code") {
            Description = "Sign in with a code typed into a browser on another machine. The default over SSH."
        };

        var servicePrincipal = new Option<bool>("--service-principal") {
            Description = "Sign in as an application rather than a person."
        };

        var tenant = new Option<string>("--tenant") { Description = "The tenant to sign in to. Also CYC_TENANT." };
        var clientId = new Option<string>("--client-id") { Description = "The application's client id." };

        var certificate = new Option<string>("--certificate") {
            Description = "A PKCS#12 file holding the service principal's certificate and private key."
        };

        var certificatePassword = new Option<bool>("--certificate-password-from-env") {
            Description = "Read the certificate's password from CYC_CLIENT_CERTIFICATE_PASSWORD."
        };

        var command = new Command("login", "Sign in. docs/plan/11 § Protocol.") {
            deviceCode,
            servicePrincipal,
            tenant,
            clientId,
            certificate,
            certificatePassword
        };

        command.SetAction(async (parse, cancellationToken) => {
                var invocation = CycRunner.Bind(host, globals, tree, parse);
                var tenantId = parse.GetValue(tenant) ?? invocation.Settings.Get("tenant");

                var credentialCache = parse.GetValue(servicePrincipal) ? null : invocation.Host.CreateCredentialOptions().TokenCache;
                var credential = parse.GetValue(servicePrincipal)
                    ? ServicePrincipal(
                        invocation,
                        parse.GetValue(clientId),
                        tenantId,
                        parse.GetValue(certificate),
                        parse.GetValue(certificatePassword)
                    )
                    : Interactive(invocation, parse.GetValue(deviceCode));

                if (credential is DeviceCodeCredential && !string.IsNullOrEmpty(parse.GetValue(tenant))) {
                    invocation.Console.Note(
                        "--tenant is not sent with a device sign-in: choose the organisation on the sign-in page."
                    );
                }

                try {
                    var token = await AuthenticateAsync(
                        invocation,
                        credential,
                        tenantId,
                        cancellationToken
                    ).ConfigureAwait(false);

                    invocation.Console.Note("Signed in.");

                    if (credentialCache is FileTokenCache file) {
                        // ⚠ Said out loud: docs/plan/21 keeps a refresh token in the keychain, and a
                        // person on a box without one should know it is in a file instead, and where.
                        invocation.Console.Note(
                            $"No keychain on this machine: the sign-in is kept in {file.Directory}, readable by you alone."
                        );
                    }

                    // ⚠ The token's tenant — the type's remarks. A service principal's token names
                    // one too, and when a token can't be read the flag is reported only for the one
                    // grant that sends it to the token endpoint.
                    var signedInto = TenantOf(token.Token) ?? (parse.GetValue(servicePrincipal) ? tenantId : null);

                    invocation.Render(
                        Payload.Object(
                            [
                                new KeyValuePair<string, Payload>(
                                    "tenant",
                                    signedInto is null ? Payload.Null : Payload.Text(signedInto)
                                ),
                                new KeyValuePair<string, Payload>(
                                    "authority",
                                    Payload.Text(Authority(invocation).ToString())
                                ),
                                // ⚠ The expiry, never the token. `cyc account get-access-token` is the one command
                                // that prints token material, because CyberCloudCliCredential parses its output.
                                new KeyValuePair<string, Payload>(
                                    "expiresOn",
                                    Payload.Text(token.ExpiresOn.ToString("O", CultureInfo.InvariantCulture))
                                )
                            ]
                        )
                    );

                    return (int)ExitCode.Ok;
                } finally {
                    (credential as IDisposable)?.Dispose();
                }
            }
        );

        return command;
    }

    /// <summary>
    ///     Gets the token, showing that the poll is still going while the SDK waits for the user.
    /// </summary>
    /// <remarks>
    ///     ⚠ The ticker writes to stderr and only when stderr is a terminal. A dot every few seconds
    ///     is reassurance at a prompt and noise in a CI log, and the log is where a stuck sign-in gets
    ///     read.
    /// </remarks>
    static async Task<AccessToken> AuthenticateAsync(
        CycInvocation invocation,
        TokenCredential credential,
        string? tenantId,
        CancellationToken cancellationToken
    ) {
        var context = new TokenRequestContext([CyberCloudScopes.Default], tenantId);
        using var ticker = new CancellationTokenSource();

        var progress = invocation.Console.IsErrorRedirected
            ? Task.CompletedTask
            : TickAsync(invocation, ticker.Token);

        try {
            return await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        } finally {
            await ticker.CancelAsync().ConfigureAwait(false);
            await progress.ConfigureAwait(false);

            if (!invocation.Console.IsErrorRedirected) {
                invocation.Console.Note(string.Empty);
            }
        }
    }

    static async Task TickAsync(CycInvocation invocation, CancellationToken cancellationToken) {
        try {
            while (!cancellationToken.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(2), invocation.Host.Time, cancellationToken)
                    .ConfigureAwait(false);

                invocation.Console.Tick('.');
            }
        } catch (OperationCanceledException) {
            // The sign-in finished, which is what cancels this.
        }
    }

    /// <summary>
    ///     Chooses between the two interactive grants — docs/plan/11 § Protocol's
    ///     <i>
    ///         "the only
    ///         interactive flow. No implicit, no hybrid"
    ///     </i> and its device-authorization row,
    ///     <i>"<c>cyc login</c> on a headless box"</i>.
    /// </summary>
    static TokenCredential Interactive(CycInvocation invocation, bool deviceCode) {
        var options = invocation.Host.CreateCredentialOptions();
        options.AuthorityHost = Authority(invocation);

        if (deviceCode || CycDefaults.LooksHeadless(invocation.Host.Environment)) {
            return new DeviceCodeCredential(
                CyberCloudCliCredential.CliClientId,
                (info, token) => PromptAsync(invocation, info, token),
                options
            );
        }

        return new InteractiveBrowserCredential(
            CyberCloudCliCredential.CliClientId,
            async (uri, token) => {
                invocation.Console.Note($"Opening {uri.GetLeftPart(UriPartial.Path)} to sign in.");
                await invocation.Host.OpenBrowser(uri, token).ConfigureAwait(false);
            },
            options
        );
    }

    /// <summary>
    ///     Shows the user their device code — <see cref="DeviceCodePromptCallback" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ On stderr, like everything else that is not the answer, so that
    ///     <c>cyc login --output json</c> still writes one JSON document to stdout. The code itself is
    ///     not a secret: RFC 8628 makes it a one-time value the user types into a page that then
    ///     authenticates them. The <c>device_code</c>, which <i>is</i> a secret, never leaves the SDK.
    /// </remarks>
    static async Task PromptAsync(CycInvocation invocation, DeviceCodeInfo info, CancellationToken cancellationToken) {
        var target = info.VerificationUriComplete ?? info.VerificationUri;

        invocation.Console.Note(string.Empty);
        invocation.Console.Note($"  To sign in, open {info.VerificationUri} and enter the code {info.UserCode}");
        invocation.Console.Note($"  The code expires at {info.ExpiresOn.ToLocalTime():t}.");
        invocation.Console.Note(string.Empty);

        await invocation.Host.OpenBrowser(target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The service-principal grants — docs/plan/11 § Credentials.
    /// </summary>
    /// <exception cref="CycUsageException">
    ///     Neither a certificate nor <c>CYC_CLIENT_SECRET</c> was supplied, or a required flag is
    ///     missing. ⚠ The message never repeats a secret back, not even to say it was wrong.
    /// </exception>
    static TokenCredential ServicePrincipal(
        CycInvocation invocation,
        string? clientId,
        string? tenantId,
        string? certificatePath,
        bool passwordFromEnvironment
    ) {
        clientId ??= invocation.Settings.Get("client-id");
        tenantId ??= invocation.Settings.Get("tenant");

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId)) {
            throw new CycUsageException(
                "--service-principal needs --client-id and --tenant (or CYC_CLIENT_ID and CYC_TENANT_ID)."
            );
        }

        var options = invocation.Host.CreateCredentialOptions();
        options.AuthorityHost = Authority(invocation);
        options.TenantId = tenantId;

        // ⚠ Nothing cached. A CI runner using client credentials has nothing worth keeping between
        // processes and everything to lose by writing it somewhere.
        options.TokenCache = TokenCache.None;

        if (!string.IsNullOrEmpty(certificatePath)) {
            var password = passwordFromEnvironment
                ? invocation.Host.Environment.GetValueOrDefault("CYC_CLIENT_CERTIFICATE_PASSWORD")
                : null;

            X509Certificate2 certificate;

            try {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, password);
            } catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException) {
                throw new CycUsageException($"'{certificatePath}' could not be read as a PKCS#12 file: {e.Message}", e);
            }

            return new CertificateCredential(tenantId, clientId, certificate, options);
        }

        if (invocation.Host.Environment.GetValueOrDefault("CYC_CLIENT_SECRET") is { Length: > 0 } secret) {
            return new ClientSecretCredential(tenantId, clientId, secret, options);
        }

        throw new CycUsageException(
            "--service-principal needs a credential: pass --certificate <file.pfx>, or put the client "
            + "secret in CYC_CLIENT_SECRET. ⚠ cyc does not accept a secret as an argument — an argument "
            + "is in the shell history, in ps output and in every CI log that echoes its command line."
        );
    }

    /// <summary>
    ///     The <c>tid</c> claim of an access token, or <see langword="null" /> for a token that isn't a
    ///     JWT or names no tenant.
    /// </summary>
    /// <remarks>
    ///     ⚠ Read, not validated. The token came from the authority over TLS a moment ago and this is
    ///     a report to the person who asked for it; nothing is authorized on what it says. The
    ///     gateway validates the same token on every call.
    /// </remarks>
    internal static string? TenantOf(string accessToken) {
        var parts = accessToken.Split('.');

        if (parts.Length != 3) {
            return null;
        }

        try {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));

            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("tid", out var tid)
                && tid.ValueKind == JsonValueKind.String
                && tid.GetString() is { Length: > 0 } value
                    ? value
                    : null;
        } catch (Exception e) when (e is FormatException or JsonException) {
            return null;
        }
    }

    /// <summary>The identity host — <c>CYC_AUTHORITY_HOST</c>, the profile's <c>authority</c>, or the SDK's default.</summary>
    internal static Uri Authority(CycInvocation invocation) =>
        invocation.Settings.Get("authority") is { Length: > 0 } value
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : CyberCloudAuthorityHosts.Default;
}
