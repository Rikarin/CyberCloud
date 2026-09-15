namespace CyberCloud.Identity.Contracts;

/// <summary>
///     How long a self-serve sign-up may take from the first request to the last.
/// </summary>
/// <remarks>
///     ⚠ <b>One number, read in two places that must agree.</b> <c>SignUpGrain</c> forgets a
///     sign-up this long after <c>BeginAsync</c>, and the identity host's ticket cookie carries the
///     same <c>Max-Age</c> — a cookie that outlived the grain would send a person to a
///     <c>complete</c> that answers "something went wrong" with no way to say why, and a grain that
///     outlived the cookie would hold a proven address nobody can reach. Fifteen minutes is
///     <see cref="OtpPolicy.IssueWindow" />: the code has ten minutes to arrive and be typed, and
///     the person five more to choose a name and a credential.
/// </remarks>
public static class SignUpPolicy {
    /// <summary>Fifteen minutes.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(15);
}
