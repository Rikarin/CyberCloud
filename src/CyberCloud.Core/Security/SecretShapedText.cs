using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace CyberCloud.Core.Security;

/// <summary>
///     Finds credential-shaped runs of text and replaces them with a marker naming the rule that
///     fired.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/18 § Platform security, row Secrets: <i>"Never in grain state, never in env vars
///         in a manifest, never in a log — analyzer + admission policy + a log-scanning canary that
///         alerts on a key-shaped string in the log pipeline."</i> CC1005 is the analyzer and it
///         covers the first clause only: it is a name rule over <c>[Id(n)]</c> members, so it sees a
///         field called <c>AdminPassword</c> and cannot see
///         <c>logger.LogInformation("upstream said {Body}", body)</c>. This type is what the third
///         clause is made of; <c>CyberCloud.ServiceDefaults.Logging.SecretScrubbingSink</c> is where
///         it attaches.
///     </para>
///     <para>
///         ⚠ <b>Every rule below matches a shape whose issuer is known, or a structure whose grammar
///         puts a credential in a named position. There is deliberately no entropy rule.</b> The
///         obvious extra — "a long high-entropy run is a secret" — fires on trace ids, image digests,
///         resource ids, base64 payloads and Orleans grain keys, all of which this platform logs on
///         purpose. A redactor with false positives destroys the logs it exists to protect, and the
///         damage is silent: nobody notices that the correlation id in every line is now
///         <c>[redacted]</c> until an incident. The cost of the choice is stated rather than hidden —
///         an opaque credential with no recognisable prefix passes through, and closing that gap is
///         the tenant-facing key format, not a wider regex.
///     </para>
///     <para>
///         ⚠ <b>Every pattern is <see cref="RegexOptions.NonBacktracking" />, and on this code path
///         that is a correctness property rather than a performance one.</b> These expressions run
///         over attacker-influenced text — a request body quoted into an operator detail, a header,
///         an exception message — inside the logging pipeline of every process in the platform. A
///         catastrophic backtrack there stalls the thread that was trying to write a log line, which
///         is the thread serving a request. The non-backtracking engine is linear in the input by
///         construction, so no timeout is needed and none is set. It refuses lookarounds,
///         backreferences and atomic groups at construction time, so a later edit that reaches for
///         one fails loudly at type initialisation rather than quietly reintroducing the hazard.
///     </para>
/// </remarks>
public static class SecretShapedText {
    /// <summary>
    ///     What replaces a match, before the rule name. Public so a test, a log query or an alert
    ///     rule can look for redactions without hard-coding the whole marker.
    /// </summary>
    public const string RedactionPrefix = "[redacted:";

    /// <summary>The name of the group a rule may use to redact part of a match instead of all of it.</summary>
    const string SecretGroup = "secret";

    /// <summary>
    ///     One rule: a name that reaches the operator, and the shape it recognises.
    /// </summary>
    /// <remarks>
    ///     The name is the alert's dimension. "A secret reached a log" is not actionable; "an
    ///     <c>AwsAccessKey</c> reached a log" names the credential to rotate and the integration to
    ///     go and read.
    /// </remarks>
    sealed record Rule(string Name, Regex Pattern);

    // ⚠ ORDER MATTERS, AND IT RUNS SPECIFIC BEFORE STRUCTURAL. `Bearer eyJhbGci…` matches both
    // JsonWebToken and BearerToken; running the issuer-shaped rule first means the alert names the
    // credential rather than the header it arrived in. Each rule sees the output of the ones before
    // it, so a later rule cannot re-match text that is already a marker — the marker contains no
    // '=', no '://' and no run long enough to look like a token.
    static readonly Rule[] Rules =
    [
        // -----BEGIN OPENSSH PRIVATE KEY-----, RSA, EC, PGP … . Greedy to the end of the string on
        // purpose: a PEM body split across lines has no reliable terminator inside one log event,
        // and half a private key in a log is still a private key in a log.
        new("PrivateKey", Pattern("-----BEGIN[ A-Z]*PRIVATE KEY[ A-Z]*-----[\\s\\S]*")),

        // A JWT. This is the single most common real leak on a platform with token exchange
        // (docs/plan/11 § Managed identity): an access token quoted into a diagnostic. The third
        // segment may be empty — an unsigned JWT is still a bearer credential to something.
        new("JsonWebToken", Pattern("eyJ[A-Za-z0-9_=-]{8,}\\.[A-Za-z0-9_=-]{8,}\\.[A-Za-z0-9_=-]*")),

        // OpenBao and Vault service (hvs.) and batch (hvb.) tokens, and the legacy s.XXXX form the
        // root token still uses. docs/plan/18 § Shape: the platform holds a broad token per
        // namespace, so this one is ours and not a tenant's.
        new("VaultToken", Pattern("\\bhv[sb]\\.[A-Za-z0-9_-]{20,}")),
        new("VaultLegacyToken", Pattern("\\bs\\.[A-Za-z0-9]{24}\\b")),

        new("AwsAccessKey", Pattern("\\b(?:AKIA|ASIA|ABIA|ACCA|AGPA|AIDA|AIPA|ANPA|ANVA|APKA|AROA|ASCA)[0-9A-Z]{16}\\b")),
        new("GitHubToken", Pattern("\\bgh[pousr]_[A-Za-z0-9]{36,}\\b")),
        new("GitHubPersonalAccessToken", Pattern("\\bgithub_pat_[A-Za-z0-9_]{22,}\\b")),
        new("SlackToken", Pattern("\\bxox[abeprs]-[A-Za-z0-9-]{10,}")),

        // ⚠ THE ONE MOST LIKELY TO FIRE IN THIS TREE. ConfiguredShardConnections composes the
        // durable tier's connection string through NpgsqlConnectionStringBuilder
        // (docs/plan/05 § Storage provider wiring), and a connection string is the thing a storage
        // diagnostic reaches for first. The key is kept and only the value is replaced, because
        // "which setting was it" is the whole diagnostic value of the line.
        new("ConnectionStringPassword", Pattern("(?:password|pwd)\\s*=\\s*(?<secret>[^;,\\s\"']{3,})", ignoreCase: true)),

        // scheme://user:password@host — how a Redis, Postgres or AMQP endpoint is usually written
        // down, and how one usually reaches a log.
        new("UriCredentials", Pattern("[a-zA-Z][a-zA-Z0-9+.-]*://[^\\s/:@]+:(?<secret>[^\\s/@]+)@")),

        // An opaque bearer credential — the case the JsonWebToken rule cannot see, such as an
        // OpenBao wrapping token or a third-party engine's licence key on an Authorization header.
        new("BearerToken", Pattern("\\bbearer\\s+(?<secret>[A-Za-z0-9._~+/=-]{16,})", ignoreCase: true))
    ];

    static Regex Pattern(string pattern, bool ignoreCase = false)
        => new(
            pattern,
            RegexOptions.NonBacktracking | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None)
        );

    /// <summary>Every rule name, for a test or a dashboard that enumerates the dimension.</summary>
    public static IReadOnlyList<string> RuleNames { get; } = Rules.Select(x => x.Name).ToArray();

    /// <summary>
    ///     Replaces every credential-shaped run in <paramref name="text" /> with a marker.
    /// </summary>
    /// <param name="text">The text to scan. Null and empty are answered <see langword="false" />.</param>
    /// <param name="redacted">
    ///     The text with every match replaced, set only when this returns <see langword="true" />.
    /// </param>
    /// <param name="rules">
    ///     The names of the rules that fired, in the order they fired. Empty when this returns
    ///     <see langword="false" />.
    /// </param>
    /// <returns><see langword="true" /> when anything was replaced.</returns>
    /// <remarks>
    ///     ⚠ The clean path allocates nothing and returns the caller's own string untouched, because
    ///     it is every log line in the platform.
    /// </remarks>
    public static bool TryRedact(
        string? text,
        [NotNullWhen(true)] out string? redacted,
        out IReadOnlyList<string> rules
    ) {
        redacted = null;
        rules = [];

        if (string.IsNullOrEmpty(text)) {
            return false;
        }

        List<string>? fired = null;
        var current = text;

        foreach (var rule in Rules) {
            // The cheap gate, and the only work done for the overwhelming majority of log lines.
            if (!rule.Pattern.IsMatch(current)) {
                continue;
            }

            var replaced = false;
            var next = rule.Pattern.Replace(
                current,
                match => {
                    if (AlreadyRedacted(match)) {
                        return match.Value;
                    }

                    replaced = true;
                    return Replace(match, rule.Name);
                }
            );

            // ⚠ A MATCH IS NOT A FINDING. `Password=[redacted:ConnectionStringPassword]` matches
            // the connection-string rule a second time — the marker is a legal value as far as that
            // grammar is concerned. Without this the text would stay stable but every pass would
            // report the rule again, which on the counter behind the alert means a redaction that
            // happened once is alerted on for as long as the line is re-handled. Found by
            // `ASecondPassOverRedactedTextChangesNothing`, which failed the first time it was run.
            if (!replaced) {
                continue;
            }

            fired ??= [];
            fired.Add(rule.Name);
            current = next;
        }

        if (fired is null) {
            return false;
        }

        redacted = current;
        rules = fired;
        return true;
    }

    /// <summary>Whether what this match would replace is a marker this method already wrote.</summary>
    static bool AlreadyRedacted(Match match) {
        var secret = match.Groups[SecretGroup];
        var value = secret.Success ? secret.Value : match.Value;
        return value.StartsWith(RedactionPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The replacement for one match — the whole match, or just the <c>secret</c> group when the
    ///     rule declared one.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The fallback when the group did not capture is to redact the whole match, and it is a
    ///     fallback rather than an assertion on purpose.</b> A regex engine that declined to hand
    ///     back a group must not be able to turn this method into a pass-through: the safe answer to
    ///     "I cannot tell which part was the secret" is "all of it".
    /// </remarks>
    static string Replace(Match match, string rule) {
        var marker = RedactionPrefix + rule + "]";
        var secret = match.Groups[SecretGroup];

        if (!secret.Success) {
            return marker;
        }

        var start = secret.Index - match.Index;
        return string.Concat(
            match.Value.AsSpan(0, start),
            marker,
            match.Value.AsSpan(start + secret.Length)
        );
    }
}
