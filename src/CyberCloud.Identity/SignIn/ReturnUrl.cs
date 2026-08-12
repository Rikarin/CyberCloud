namespace CyberCloud.Identity.SignIn;

/// <summary>
///     Decides where a user may be sent once they have signed in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A sign-in page that redirects to a caller-supplied URL is the classic phishing
///         primitive</b>, and it is worth spelling out why it is worse here than on an ordinary site.
///         The attacker sends <c>https://id.cybercloud.io/signin?returnUrl=https://evil.example</c>.
///         The victim reads the origin, sees the real identity host, and types a real password into a
///         real sign-in page — everything they were taught to check passes. They are then handed to
///         the attacker's page, already in the frame of mind that they are signing in, and the second
///         prompt gets the second factor.
///     </para>
///     <para>
///         <b>The rule is an allow-list of one shape, not a block-list of tricks.</b> A same-origin
///         relative path, and nothing else. Every block-list version of this control has been beaten
///         by a spelling somebody did not think of — <c>//evil.example</c>, <c>/\evil.example</c>,
///         <c>https:/\evil.example</c>, a tab or newline inside the scheme, a percent-encoded slash
///         that a browser decodes after the check. Requiring the value to <i>be</i> one known-good
///         shape means an unanticipated spelling fails closed, because it is not that shape.
///     </para>
///     <para>
///         ⚠ <b><see cref="Sanitize" /> never throws and never propagates a rejected value.</b> It
///         returns <see cref="Default" /> instead, so a caller cannot accidentally use the input by
///         forgetting to check a boolean. The alternative — a <c>TryParse</c> whose <c>false</c>
///         branch somebody leaves empty — fails open, which for this control means the vulnerability.
///     </para>
/// </remarks>
public static class ReturnUrl {
    /// <summary>Where a user goes when the request named nowhere usable.</summary>
    public const string Default = "/";

    /// <summary>
    ///     The longest value considered. Past this the answer is <see cref="Default" />.
    /// </summary>
    /// <remarks>
    ///     Not a security property on its own — it bounds the work a hostile caller can ask for on an
    ///     unauthenticated endpoint, and it keeps the value inside what a browser and a log line will
    ///     actually carry.
    /// </remarks>
    public const int MaximumLength = 1024;

    /// <summary>
    ///     Returns <paramref name="candidate" /> when it is a safe same-origin path, and
    ///     <see cref="Default" /> when it is anything else.
    /// </summary>
    /// <param name="candidate">
    ///     The value from the query string, unmodified. Pass it exactly as it arrived — decoding or
    ///     trimming it first moves the check away from the bytes the browser will act on, which is
    ///     the gap every encoding bypass lives in.
    /// </param>
    /// <returns>
    ///     A value that always begins with a single <c>/</c> and names this origin. Safe to put in a
    ///     <c>Location</c> header or an <c>href</c> without further checking.
    /// </returns>
    public static string Sanitize(string? candidate) => IsSafe(candidate) ? candidate! : Default;

    /// <summary>
    ///     Reports whether <paramref name="candidate" /> is a same-origin path this host may redirect
    ///     to.
    /// </summary>
    /// <param name="candidate">The value from the query string, unmodified.</param>
    /// <returns>
    ///     <see langword="true" /> only for a path starting with exactly one <c>/</c>, carrying no
    ///     authority component, no control characters, and no backslash.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Prefer <see cref="Sanitize" />.</b> This is public because a test asserts on the
    ///     decision directly, and because an endpoint that wants to answer <c>400</c> rather than
    ///     silently redirect home needs to know. A caller that uses it to gate its own use of the raw
    ///     value has reintroduced the fail-open path <see cref="Sanitize" /> exists to remove.
    /// </remarks>
    public static bool IsSafe(string? candidate) {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaximumLength) {
            return false;
        }

        // ── 1. It has to be a path, and paths start with exactly one slash. ────────────────────
        //
        // ⚠ The second character is the whole scheme-relative attack. `//evil.example` is a valid
        // absolute URL to every browser — the scheme is inherited and the authority is `evil.example`
        // — while looking like a path to a check that only tested the first character. `/\evil.example`
        // is the same attack spelled with a backslash, which browsers normalize to `/` and naive
        // string checks do not.
        if (candidate[0] != '/') {
            return false;
        }

        if (candidate.Length > 1 && candidate[1] is '/' or '\\') {
            return false;
        }

        // ── 2. No backslash anywhere. ─────────────────────────────────────────────────────────
        //
        // A backslash is not a legal path character in a URL, so rejecting it costs nothing real and
        // removes a family of parser-differential tricks where the browser and the check disagree
        // about where the authority ends.
        if (candidate.Contains('\\', StringComparison.Ordinal)) {
            return false;
        }

        // ── 3. No control characters, and that includes the ones people forget. ───────────────
        //
        // ⚠ Browsers STRIP tab, CR, and LF from a URL before parsing it, so `/\tevil` and
        // `java\nscript:` become something different from what any check saw. Anything below U+0020,
        // plus DEL, is refused outright rather than stripped — stripping would mean re-deriving the
        // browser's exact normalization, which is a moving target across engines.
        foreach (var character in candidate) {
            if (char.IsControl(character)) {
                return false;
            }
        }

        // ── 4. Resolved against an origin, it must still be on that origin. ──────────────────
        //
        // The belt to the braces above, and it asks the question a browser actually answers: "given
        // this page's origin, where does this value navigate to?" Anything that lands on a different
        // host — by a scheme, an authority, or a spelling nobody here anticipated — fails, because
        // the test is on the RESULT rather than on the input's shape.
        //
        // ⚠ NOT `Uri.TryCreate(candidate, UriKind.Absolute, out _)`, which is the obvious version and
        // is wrong on Unix: .NET parses a leading-slash string as an absolute *file* path there, so
        // `/` itself comes back as an absolute URI and every legitimate path is refused. The first
        // draft of this method had exactly that bug and ReturnUrlTests caught it — the accepted-path
        // rows all went red on macOS while the rejection rows stayed green, which is the failure mode
        // a rejection-only suite would have shipped.
        if (!Uri.TryCreate(Probe, candidate, out var resolved)) {
            return false;
        }

        return resolved.IsAbsoluteUri
            && string.Equals(resolved.Host, Probe.Host, StringComparison.Ordinal)
            && string.Equals(resolved.Scheme, Probe.Scheme, StringComparison.Ordinal)
            && resolved.Port == Probe.Port;
    }

    /// <summary>
    ///     The origin a candidate is resolved against, purely to see where it lands.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>.invalid</c> is reserved by RFC 2606 and resolves nowhere, so if this value ever
    ///     escaped into a redirect it would fail loudly instead of reaching a host somebody owns. It
    ///     is never used as an origin — only as the base of a relative-resolution the result is
    ///     compared against.
    /// </remarks>
    static readonly Uri Probe = new("https://return-url-probe.invalid");
}
