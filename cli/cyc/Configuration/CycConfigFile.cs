namespace CyberCloud.Cli.Configuration;

/// <summary>
///     <c>~/.cyc/config</c> — an INI file of named profiles, as docs/plan/21 § Decisions specifies.
/// </summary>
/// <remarks>
///     <para>
///         The format is the one <c>git config</c> and <c>aws</c> already taught everyone:
///     </para>
///     <code>
///     default = work
///
///     [work]
///     subscription = 6f1a…
///     tenant       = contoso
///     output       = table
///
///     [lab]
///     endpoint     = https://api.lab.internal/
///     </code>
///     <para>
///         ⚠ <b>No token, no secret, no credential of any kind is ever written here, and nothing in
///         this type can write one.</b> docs/plan/21 § Decisions: <i>"Never a plaintext file — that is
///         how CI credentials leak into container images."</i> The refresh token lives in the OS
///         keychain and the SDK owns it (<c>TokenCache.CreatePersistent</c>); the CLI never sees it.
///         <see cref="Set" /> refuses a key that looks like a credential, so the rule survives someone
///         adding <c>cyc config set token …</c> in a hurry.
///     </para>
///     <para>
///         ⚠ <b>INI rather than JSON.</b> The file is edited by hand more often than by
///         <c>cyc config</c>, and a trailing comma in a JSON config is a CLI that stops working with
///         a parser error nobody asked for.
///     </para>
/// </remarks>
sealed class CycConfigFile {
    /// <summary>The profile used when nothing names one.</summary>
    public const string DefaultProfileName = "default";

    readonly Dictionary<string, Dictionary<string, string>> profiles;

    CycConfigFile(string? path, string defaultProfile, Dictionary<string, Dictionary<string, string>> profiles) {
        Path = path;
        DefaultProfile = defaultProfile;
        this.profiles = profiles;
    }

    /// <summary>Where the file is, or <c>null</c> when there is none.</summary>
    public string? Path { get; }

    /// <summary>The profile named by the file's own <c>default =</c> line, or <c>default</c>.</summary>
    public string DefaultProfile { get; }

    /// <summary>Every profile in the file, in the order they were declared.</summary>
    public IReadOnlyCollection<string> Profiles => profiles.Keys;

    /// <summary>An empty configuration — what a machine with no <c>~/.cyc/config</c> has.</summary>
    public static CycConfigFile Empty { get; } =
        new(path: null, DefaultProfileName, new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The directory the CLI keeps its state in — <c>~/.cyc</c>.</summary>
    /// <remarks>⚠ <c>~/.cyc</c>, never <c>~/.cc</c>. See cyc.csproj.</remarks>
    public static string DirectoryPath
        => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
            ".cyc");

    /// <summary>The configuration file's path.</summary>
    public static string FilePath => System.IO.Path.Combine(DirectoryPath, "config");

    /// <summary>Reads the file, or returns <see cref="Empty" /> when it does not exist.</summary>
    /// <param name="path">The file, defaulting to <see cref="FilePath" />.</param>
    /// <exception cref="CycUsageException">The file exists and cannot be read.</exception>
    public static CycConfigFile Read(string? path = null) {
        path ??= FilePath;

        if (!File.Exists(path))
            return Empty;

        try {
            return Parse(File.ReadAllText(path), path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            throw new CycUsageException($"'{path}' could not be read: {e.Message}", e);
        }
    }

    /// <summary>Parses the file's text.</summary>
    /// <param name="text">The contents.</param>
    /// <param name="path">Where it came from, for messages.</param>
    public static CycConfigFile Parse(string text, string? path = null) {
        ArgumentNullException.ThrowIfNull(text);

        var profiles = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var current = DefaultProfileName;
        var defaultProfile = DefaultProfileName;

        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();

            // `#` and `;` both start a comment: git config uses one and every INI writer in the
            // world uses the other, and refusing either is a surprise nobody needs.
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '[' && line[^1] == ']') {
                current = line[1..^1].Trim();
                profiles.TryAdd(current, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // A `default =` line before any [section] names the profile to use, which is the one
            // setting that is about the file rather than about a profile.
            if (string.Equals(current, DefaultProfileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(key, "default", StringComparison.OrdinalIgnoreCase)
                && !profiles.ContainsKey(DefaultProfileName)) {
                defaultProfile = value;

                continue;
            }

            profiles.TryAdd(current, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            profiles[current][key] = value;
        }

        return new CycConfigFile(path, defaultProfile, profiles);
    }

    /// <summary>One setting's value in one profile, or <c>null</c>.</summary>
    /// <param name="profile">The profile name.</param>
    /// <param name="key">The setting's key.</param>
    public string? Value(string profile, string key)
        => profiles.TryGetValue(profile, out var settings) && settings.TryGetValue(key, out var value)
            ? value
            : null;

    /// <summary>Every setting in one profile.</summary>
    /// <param name="profile">The profile name.</param>
    public IReadOnlyDictionary<string, string> Settings(string profile)
        => profiles.TryGetValue(profile, out var settings)
            ? settings
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Sets one value, returning the new file. The instance is not mutated: a command that
    ///     writes settings renders the result once, so a half-written file is not reachable.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <param name="key">The setting's key.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="CycUsageException">
    ///     The key names something credential-shaped. ⚠ This file is plaintext on disk; the SDK's
    ///     keychain-backed cache is where token material lives, and there is no CLI path that puts
    ///     any of it here.
    /// </exception>
    public CycConfigFile Set(string profile, string key, string value) {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (LooksLikeCredential(key))
            throw new CycUsageException(
                $"'{key}' is credential-shaped and ~/.cyc/config is a plaintext file. docs/plan/21 "
                + "§ Decisions: \"Never a plaintext file — that is how CI credentials leak into "
                + "container images.\" Sign in with 'cyc login', which stores the refresh token in the "
                + "OS keychain, or pass a service principal through the CYC_CLIENT_* environment "
                + "variables.");

        var copy = new Dictionary<string, Dictionary<string, string>>(profiles, StringComparer.OrdinalIgnoreCase);

        copy[profile] = copy.TryGetValue(profile, out var existing)
            ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        copy[profile][key] = value;

        return new CycConfigFile(Path, DefaultProfile, copy);
    }

    /// <summary>Sets the profile used when nothing names one.</summary>
    /// <param name="profile">The profile name.</param>
    public CycConfigFile WithDefaultProfile(string profile) {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        return new CycConfigFile(Path, profile, profiles);
    }

    /// <summary>Renders the file.</summary>
    public string Render() {
        var text = new StringBuilder();

        text.Append("# cyc configuration — docs/plan/21 § Decisions.").Append('\n');
        text.Append("# ⚠ No credential is ever written here. 'cyc login' puts the refresh token in the OS keychain.").Append('\n');
        text.Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"default = {DefaultProfile}").Append('\n');

        foreach (var profile in profiles.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            text.Append('\n').Append('[').Append(profile.Key).Append(']').Append('\n');

            foreach (var setting in profile.Value.OrderBy(x => x.Key, StringComparer.Ordinal))
                text.Append(CultureInfo.InvariantCulture, $"{setting.Key} = {setting.Value}").Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Writes the file, creating <c>~/.cyc</c> if it is missing.</summary>
    /// <param name="path">Where to write, defaulting to where it was read from.</param>
    public void Write(string? path = null) {
        path ??= Path ?? FilePath;

        var directory = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, Render());
    }

    /// <summary>
    ///     Whether a setting's name suggests it carries token material.
    /// </summary>
    /// <remarks>
    ///     The same shape <c>CC1005</c> refuses in grain state (docs/plan/00 § Non-negotiables), for
    ///     the same reason: the words people reach for when they are about to store a secret are a
    ///     short and stable list.
    /// </remarks>
    internal static bool LooksLikeCredential(string key)
        => key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("key", StringComparison.OrdinalIgnoreCase);
}
