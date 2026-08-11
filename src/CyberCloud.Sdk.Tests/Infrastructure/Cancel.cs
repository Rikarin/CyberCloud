namespace CyberCloud.Sdk.Tests;

/// <summary>
///     The running test's cancellation token, so every awaited call in this suite is cancellable and
///     xUnit1051 stays satisfied.
/// </summary>
/// <remarks>
///     ⚠ The tests in <c>OperationCancellationTests</c> deliberately do <b>not</b> use it — they are
///     about cancellation itself and pass their own token, which is the thing under test.
/// </remarks>
public static class Cancel {
    /// <summary>The current test's token.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;
}
