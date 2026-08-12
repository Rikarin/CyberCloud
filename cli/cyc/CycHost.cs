using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.VerbTree;

namespace CyberCloud.Cli;

/// <summary>
///     Everything a command needs that is not the command line: the two streams, the environment, the
///     configuration file, the verb trees, and the two things that touch the outside world — the SDK
///     client and the browser.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the seam the test suite drives, and it is the same seam a private deployment
///     uses.</b> <see cref="CreateClient" /> hands back a <see cref="CyberCloudClient" />; the tests
///     hand back one whose <c>CyberCloudClientOptions.Transport</c> is a scripted
///     <see cref="HttpMessageHandler" />, which is exactly how <c>CyberCloud.Sdk.Tests</c> works.
///     Nothing in <c>cyc</c>'s own test suite opens a socket, reads a keychain or launches a browser.
/// </remarks>
sealed class CycHost {
    /// <summary>Creates the host.</summary>
    /// <param name="console">The two streams.</param>
    /// <param name="environment">The process environment, already read.</param>
    /// <param name="config">The parsed <c>~/.cyc/config</c>.</param>
    /// <param name="catalog">The verb trees this build carries.</param>
    /// <param name="createClient">Builds the SDK client for one invocation.</param>
    /// <param name="openBrowser">Opens a URL, for the interactive sign-in.</param>
    public CycHost(
        CycConsole console,
        IReadOnlyDictionary<string, string> environment,
        CycConfigFile config,
        VerbTreeCatalog catalog,
        Func<CycClientRequest, CyberCloudClient> createClient,
        Func<Uri, CancellationToken, Task> openBrowser) {
        Console = console;
        Environment = environment;
        Config = config;
        Catalog = catalog;
        CreateClient = createClient;
        OpenBrowser = openBrowser;
    }

    /// <summary>The two streams.</summary>
    public CycConsole Console { get; }

    /// <summary>The process environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>The configuration file, as read at start-up.</summary>
    public CycConfigFile Config { get; }

    /// <summary>The verb trees.</summary>
    public VerbTreeCatalog Catalog { get; }

    /// <summary>Builds an SDK client.</summary>
    public Func<CycClientRequest, CyberCloudClient> CreateClient { get; }

    /// <summary>Opens a URL in the user's browser.</summary>
    public Func<Uri, CancellationToken, Task> OpenBrowser { get; }

    /// <summary>
    ///     Builds the credential the CLI presents when it is not signing in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Every one of these types is the SDK's.</b> docs/plan/21 § The .NET SDK owns the
    ///         grants, discovery, JWKS, PKCE, refresh with reuse detection and the keychain-backed
    ///         token cache; <c>cyc</c> chooses which credential to hand the pipeline and renders what
    ///         comes back, and that is the whole of its involvement.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="CyberCloudCliCredential" /> is deliberately not in the chain, and the
    ///         reason is that <c>cyc</c> is what it shells out to.</b> That credential exists for the
    ///         SDK's <i>callers</i> — it runs <c>cyc account get-access-token</c> — so a <c>cyc</c>
    ///         that used it on itself would fork itself once per expired token.
    ///     </para>
    /// </remarks>
    public Func<TokenCredential> CreateCredential { get; init; } = CycDefaults.SignedInCredential;

    /// <summary>
    ///     Builds the options <c>cyc login</c> hands the SDK's credentials.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The default is the SDK's own default, which means the OS keychain.</b> The tests
    ///     replace it with <c>TokenCache.CreateInMemory()</c> and a scripted identity transport —
    ///     without that seam a test run would sign a developer's real keychain in and out, and
    ///     <c>cyc login</c> would be the one command with no test at all.
    /// </remarks>
    public Func<CyberCloudCredentialOptions> CreateCredentialOptions { get; init; } = () => new CyberCloudCredentialOptions();

    /// <summary>The clock, so a test can decide that a day has passed without waiting one.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    ///     Runs an installed extension as a child process.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A seam for the same reason <see cref="CreateClient" /> is one.</b> The suite drives
    ///     the whole dispatch path — the name resolution, the integrity check, the argument split and
    ///     the environment — without starting a process, so it can assert what <i>would</i> have been
    ///     launched and, in particular, that no access token is in it. One test replaces this with the
    ///     real <see cref="Extensions.ExtensionLauncher.StartAsync" /> and runs a script end to end,
    ///     because a seam nothing ever exercises for real is a seam that stops matching reality.
    /// </remarks>
    public Func<Extensions.ExtensionLaunch, CancellationToken, Task<int>> LaunchExtension { get; init; } =
        Extensions.ExtensionLauncher.StartAsync;

    /// <summary>
    ///     This <c>cyc</c>'s own path, handed to an extension so it can ask <i>this</i> build for a
    ///     token. <c>null</c> when the runtime cannot say.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="System.Environment.ProcessPath" /> rather than the word <c>cyc</c>: an
    ///     extension that re-resolved the name through <c>PATH</c> would hand the token question to
    ///     whichever binary happened to answer. See <see cref="Extensions.ExtensionLauncher" />.
    /// </remarks>
    public string? ExecutablePath { get; init; } = System.Environment.ProcessPath;

    /// <summary>
    ///     Where <c>~/.cyc</c> is, so a test does not write into the developer's own.
    /// </summary>
    public string StateDirectory { get; init; } = CycConfigFile.DirectoryPath;
}

/// <summary>What a command needs a client for.</summary>
/// <param name="Endpoint">The service endpoint.</param>
/// <param name="ApiVersion">The api-version on the wire — <c>2026-08-01</c>.</param>
/// <param name="TenantId">The tenant to get a token for, or <c>null</c> for the credential's own.</param>
sealed record CycClientRequest(Uri Endpoint, string ApiVersion, string? TenantId);
