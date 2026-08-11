using CyberCloud.Cli.Configuration;
using CyberCloud.Cli.VerbTree;
using CyberCloud.Sdk;

namespace CyberCloud.Cli.Tests;

/// <summary>
///     Builds a <see cref="CycHost" /> that touches nothing outside the test.
/// </summary>
/// <remarks>
///     ⚠ <b>Four things are substituted and none of them is a back door.</b> The console is two
///     <see cref="StringWriter" />s; the client is built over
///     <c>CyberCloudClientOptions.Transport</c>, which is the SDK's own documented seam and what a
///     private deployment uses; the state directory is a temporary folder, so no test can write into a
///     developer's <c>~/.cyc</c>; and the browser callback records instead of launching. Everything
///     else — the pipeline, the retry policy, the operation poller, the JSON — is the real code.
/// </remarks>
sealed class TestHost : IDisposable {
    readonly string stateDirectory;

    TestHost(CycHost host, StringWriter output, StringWriter error, string stateDirectory, List<Uri> browsed) {
        Host = host;
        Output = output;
        Error = error;
        this.stateDirectory = stateDirectory;
        Browsed = browsed;
    }

    /// <summary>The host under test.</summary>
    public CycHost Host { get; }

    /// <summary>Everything written to stdout.</summary>
    public StringWriter Output { get; }

    /// <summary>Everything written to stderr.</summary>
    public StringWriter Error { get; }

    /// <summary>What stdout holds.</summary>
    public string Stdout => Output.ToString();

    /// <summary>What stderr holds.</summary>
    public string Stderr => Error.ToString();

    /// <summary>Every URL the browser callback was handed.</summary>
    public IReadOnlyList<Uri> Browsed { get; }

    /// <summary>The temporary <c>~/.cyc</c>.</summary>
    public string StateDirectory => stateDirectory;

    /// <summary>The catalog of trees the CLI assembly carries.</summary>
    public static VerbTreeCatalog Catalog() => VerbTreeCatalog.FromAssembly(typeof(CycApplication).Assembly);

    /// <summary>Builds the host.</summary>
    /// <param name="transport">The scripted HTTP transport, or <c>null</c> for a client nothing calls.</param>
    /// <param name="environment">Environment variables the command should see.</param>
    /// <param name="config">The contents of <c>~/.cyc/config</c>, or <c>null</c> for none.</param>
    /// <param name="credential">The credential, or <c>null</c> for one that hands out a fixed token.</param>
    /// <param name="time">The clock.</param>
    /// <param name="credentialOptions">
    ///     The options `cyc login` hands the SDK's credentials. ⚠ Supply one with
    ///     <c>TokenCache.CreateInMemory()</c> in any test that signs in, or the run writes into the
    ///     developer's real keychain.
    /// </param>
    public static TestHost Create(
        HttpMessageHandler? transport = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? config = null,
        TokenCredential? credential = null,
        TimeProvider? time = null,
        Func<CyberCloudCredentialOptions>? credentialOptions = null) {
        var output = new StringWriter();
        var error = new StringWriter();
        var state = Path.Combine(Path.GetTempPath(), "cyc-tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(state);

        if (config is not null)
            File.WriteAllText(Path.Combine(state, "config"), config);

        var browsed = new List<Uri>();
        var token = credential ?? new StaticCredential(FixedToken);

        var host = new CycHost(
            CycConsole.For(output, error),
            environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            CycConfigFile.Read(Path.Combine(state, "config")),
            Catalog(),
            request => new CyberCloudClient(
                request.Endpoint,
                token,
                new CyberCloudClientOptions(CyberCloudClientOptions.ServiceVersion.V2026_08_01) {
                    Transport = transport ?? new RefusingTransport(),
                    // ⚠ Zero, so a three-poll operation costs three round trips and no wall-clock
                    // time. The SDK's own suite uses the same setting for the same reason.
                    PollingInterval = TimeSpan.Zero,
                }),
            (uri, _) => {
                browsed.Add(uri);

                return Task.CompletedTask;
            }) {
            CreateCredential = () => token,
            CreateCredentialOptions = credentialOptions ?? (() => new CyberCloudCredentialOptions { TokenCache = TokenCache.CreateInMemory() }),
            StateDirectory = state,
            Time = time ?? TimeProvider.System,
        };

        var built = new TestHost(host, output, error, state, browsed);

        return built;
    }

    /// <summary>The access token every test's credential hands out — a sentinel no output may contain.</summary>
    public const string FixedToken = "sentinel-access-token-6f1a9c";

    /// <summary>Runs a command line.</summary>
    /// <param name="arguments">The arguments, without the executable name.</param>
    public Task<int> RunAsync(params string[] arguments) => CycApplication.RunAsync(Host, arguments);

    /// <summary>Runs a command line with a cancellation token — how a timeout is provoked.</summary>
    /// <param name="cancellationToken">The token.</param>
    /// <param name="arguments">The arguments.</param>
    public Task<int> RunAsync(CancellationToken cancellationToken, params string[] arguments)
        => CycApplication.RunAsync(Host, arguments, cancellationToken);

    /// <summary>Parses stdout as JSON, failing the test when it is not a document.</summary>
    public JsonDocument StdoutAsJson() {
        var text = Stdout;

        try {
            return JsonDocument.Parse(text);
        } catch (JsonException e) {
            throw new ShouldAssertException($"stdout was not valid JSON:{Environment.NewLine}{text}", e);
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        Output.Dispose();
        Error.Dispose();

        try {
            Directory.Delete(stateDirectory, recursive: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // A leftover temporary directory is not worth failing a passing test over.
        }
    }
}

/// <summary>
///     A clock a test moves by hand.
/// </summary>
/// <remarks>
///     ⚠ Written here rather than taken from <c>Microsoft.Extensions.TimeProvider.Testing</c>: that
///     package is not in Directory.Packages.props, and adding a pin for six lines would touch a file
///     every project in the repository shares. Only <see cref="GetUtcNow" /> is overridden, which is
///     all the update check reads.
/// </remarks>
sealed class TestClock(DateTimeOffset now) : TimeProvider {
    DateTimeOffset current = now;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => current;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => current += by;
}

/// <summary>A credential that hands out one token and never talks to anything.</summary>
sealed class StaticCredential(string token) : TokenCredential {
    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(10)));
}

/// <summary>A credential that is never usable — what a machine with no sign-in has.</summary>
sealed class UnavailableCredential(string message) : TokenCredential {
    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken = default)
        => throw new CredentialUnavailableException(message);
}

/// <summary>A transport that fails the test if anything reaches it.</summary>
sealed class RefusingTransport : HttpMessageHandler {
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new ShouldAssertException($"This test scripted no transport, and {request.Method} {request.RequestUri} was sent.");
}
