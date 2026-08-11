namespace CyberCloud.Cli.Configuration;

/// <summary>
///     The settings a command reads, resolved from the three places docs/plan/21 § Decisions allows:
///     the flag, the environment, and the current profile in <c>~/.cyc/config</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The order is flag, then environment, then profile, then nothing</b> — most explicit
///         first. CI sets <c>CYC_SUBSCRIPTION</c> and expects it to win over whatever profile the
///         image happened to bake in; a developer typing <c>--subscription</c> expects it to win over
///         both.
///     </para>
///     <para>
///         ⚠ <b>Every setting has an environment variable and the name is mechanical:</b>
///         <c>CYC_</c> + the setting's key upper-cased with <c>-</c> to <c>_</c>. docs/plan/21
///         § Decisions asks for "every setting also an env var (<c>CYC_SUBSCRIPTION</c>, …) for CI",
///         and a mechanical rule is the only version of that promise nobody has to maintain a table
///         for.
///     </para>
/// </remarks>
sealed class CycSettings {
    /// <summary>The environment prefix. ⚠ <c>CYC_</c>, not <c>CC_</c> — see cyc.csproj for why.</summary>
    public const string EnvironmentPrefix = "CYC_";

    readonly CycConfigFile file;
    readonly IReadOnlyDictionary<string, string> environment;
    readonly string profile;

    CycSettings(CycConfigFile file, IReadOnlyDictionary<string, string> environment, string profile) {
        this.file = file;
        this.environment = environment;
        this.profile = profile;
    }

    /// <summary>The profile these settings resolve through.</summary>
    public string Profile => profile;

    /// <summary>The file behind them, for the commands that write it.</summary>
    public CycConfigFile File => file;

    /// <summary>Resolves the settings for one invocation.</summary>
    /// <param name="file">The parsed <c>~/.cyc/config</c>.</param>
    /// <param name="environment">The process environment, already read.</param>
    /// <param name="profileFlag">The <c>--profile</c> value, or <c>null</c>.</param>
    public static CycSettings Resolve(CycConfigFile file, IReadOnlyDictionary<string, string> environment, string? profileFlag) {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(environment);

        var name = profileFlag
            ?? environment.GetValueOrDefault(EnvironmentPrefix + "PROFILE")
            ?? file.DefaultProfile;

        return new CycSettings(file, environment, name);
    }

    /// <summary>
    ///     One setting's value, or <c>null</c>.
    /// </summary>
    /// <param name="key">
    ///     The setting's key as it appears in <c>~/.cyc/config</c> — <c>subscription</c>,
    ///     <c>endpoint</c>. The environment variable is derived from it.
    /// </param>
    /// <param name="flagValue">What the flag said, or <c>null</c> when it was not given.</param>
    public string? Get(string key, string? flagValue = null) {
        if (!string.IsNullOrEmpty(flagValue))
            return flagValue;

        if (environment.TryGetValue(VariableFor(key), out var fromEnvironment) && fromEnvironment.Length > 0)
            return fromEnvironment;

        return file.Value(profile, key);
    }

    /// <summary>The environment variable that supplies a setting — <c>subscription</c> becomes <c>CYC_SUBSCRIPTION</c>.</summary>
    /// <param name="key">The setting's key.</param>
    public static string VariableFor(string key) {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return EnvironmentPrefix + key.Replace('-', '_').ToUpperInvariant();
    }

    /// <summary>
    ///     The service endpoint. Defaults to <see cref="CyberCloudClient.DefaultEndpoint" />, which is
    ///     what a private or regional deployment overrides with <c>CYC_ENDPOINT</c>.
    /// </summary>
    /// <exception cref="CycUsageException">The configured value is not an absolute URL.</exception>
    public Uri Endpoint {
        get {
            if (Get("endpoint") is not { Length: > 0 } value)
                return CyberCloudClient.DefaultEndpoint;

            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                ? uri
                : throw new CycUsageException(
                    $"'{value}' is not an absolute URL, so it cannot be the endpoint. Set it with "
                    + $"'cyc config set endpoint https://…' or {VariableFor("endpoint")}.");
        }
    }
}
