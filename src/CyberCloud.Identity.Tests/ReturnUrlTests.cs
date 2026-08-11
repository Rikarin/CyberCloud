using CyberCloud.Identity.SignIn;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     The open-redirect suite for <see cref="ReturnUrl" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assertion that matters is the refusal, not the acceptance.</b> A suite that only
///         checks "a relative path survives" passes against a <see cref="ReturnUrl.Sanitize" /> that
///         returns its input unchanged, which is the vulnerability. Every off-origin case below is
///         therefore stated as its own theory row with the spelling written out, so a regression names
///         the exact bypass that came back rather than reporting "one of thirty inputs".
///     </para>
///     <para>
///         <b>Where the rows come from.</b> Each is a spelling that has beaten a real block-list
///         implementation: the scheme-relative <c>//host</c>, the backslash variants browsers
///         normalize to slashes, the whitespace and control characters browsers strip before parsing,
///         and the <c>javascript:</c> and <c>data:</c> schemes that turn a redirect into script
///         execution on this origin.
///     </para>
/// </remarks>
public sealed class ReturnUrlTests {
    /// <summary>
    ///     The off-origin and hostile spellings. Every one must resolve to
    ///     <see cref="ReturnUrl.Default" />.
    /// </summary>
    public static TheoryData<string> Rejected => [.. RejectedSpellings];

    /// <summary>The same-origin paths a caller is entitled to be sent back to.</summary>
    public static TheoryData<string> Accepted => [.. AcceptedSpellings];

    /// <summary>
    ///     The rejected corpus as plain strings, so <see cref="EveryOutputIsASameOriginPath" /> can
    ///     iterate it without unwrapping xUnit's row type.
    /// </summary>
    static string[] RejectedSpellings =>
    [
        // ── Plainly absolute ──────────────────────────────────────────────────────────────────
        "https://evil.example",
        "https://evil.example/signin",
        "http://evil.example",
        "HTTPS://EVIL.EXAMPLE",

        // ── Scheme-relative: an absolute URL that looks like a path ───────────────────────────
        //
        // ⚠ The single most common bypass. `//evil.example` inherits this page's scheme and sets the
        // authority to `evil.example`, so a check that tested only `candidate[0] == '/'` passes it.
        "//evil.example",
        "//evil.example/path",
        "///evil.example",

        // ── The same attack spelled with backslashes, which browsers normalize to slashes ─────
        "/\\evil.example",
        "\\\\evil.example",
        "/\\/evil.example",
        "https:/\\evil.example",

        // ── Schemes that execute rather than navigate ────────────────────────────────────────
        "javascript:alert(1)",
        "javascript:alert(document.cookie)",
        "data:text/html,<script>alert(1)</script>",
        "vbscript:msgbox(1)",

        // ── Control characters browsers strip before parsing, so the check and the browser
        //    would otherwise disagree about what the value even is ───────────────────────────
        "/\tevil",
        "/\nevil",
        "/\revil",
        "\t//evil.example",
        "java\nscript:alert(1)",

        // ── Not a path at all ────────────────────────────────────────────────────────────────
        "evil.example",
        "signin",
        "../admin",
        ""
    ];

    /// <summary>The accepted corpus as plain strings.</summary>
    static string[] AcceptedSpellings =>
    [
        "/",
        "/signin",
        "/authorize?client_id=portal&response_type=code",
        "/resource-groups/prod?tab=access",
        "/a/b/c",
        "/path#fragment",
        // A single leading slash followed by a colon later in the PATH is fine — the colon is not
        // in a scheme position, so this is an ordinary path segment.
        "/redirect:target"
    ];

    [Theory]
    [MemberData(nameof(Rejected))]
    public void AnOffOriginTargetIsRefusedAndReplacedWithTheDefault(string candidate) {
        ReturnUrl.IsSafe(candidate).ShouldBeFalse(
            $"'{candidate}' can send a user who signed in on this origin somewhere else, which is "
            + "the phishing primitive ReturnUrl exists to remove."
        );

        // ⚠ The second half is the one that catches a fail-open refactor: a Sanitize that returned
        // its input regardless would still satisfy the IsSafe assertion above.
        ReturnUrl.Sanitize(candidate).ShouldBe(ReturnUrl.Default);
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public void ASameOriginPathSurvivesUnchanged(string candidate) {
        ReturnUrl.IsSafe(candidate).ShouldBeTrue();
        ReturnUrl.Sanitize(candidate).ShouldBe(candidate);
    }

    [Fact]
    public void ANullOrOverlongValueBecomesTheDefault() {
        ReturnUrl.Sanitize(null).ShouldBe(ReturnUrl.Default);
        ReturnUrl.Sanitize("/" + new string('a', ReturnUrl.MaximumLength)).ShouldBe(ReturnUrl.Default);
    }

    /// <summary>
    ///     Whatever comes out is safe to use without a second check.
    /// </summary>
    /// <remarks>
    ///     ⚠ This is the property a caller actually relies on, and it is worth asserting over the
    ///     whole corpus rather than per row: every output starts with exactly one <c>/</c>, so
    ///     putting it in a <c>Location</c> header cannot leave this origin no matter what arrived.
    /// </remarks>
    [Fact]
    public void EveryOutputIsASameOriginPath() {
        foreach (var candidate in RejectedSpellings.Concat(AcceptedSpellings)) {
            var sanitized = ReturnUrl.Sanitize(candidate);

            sanitized[0].ShouldBe('/');
            (sanitized.Length > 1 && sanitized[1] is '/' or '\\').ShouldBeFalse();

            // Resolved against any origin, the output stays on it. This is the property a caller
            // relies on when it puts the value straight into a `Location` header.
            Uri.TryCreate(new("https://id.cybercloud.test"), sanitized, out var resolved).ShouldBeTrue();
            resolved!.Host.ShouldBe("id.cybercloud.test");
        }
    }
}
