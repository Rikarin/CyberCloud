namespace CyberCloud.Sdk.Tests;

/// <summary>
///     A clock the test moves by hand. Only <see cref="GetUtcNow" /> is overridden, which is all
///     <see cref="SigningKeyCache" /> reads — it schedules nothing.
/// </summary>
/// <remarks>
///     ⚠ Here rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>: that package is not in
///     docs/plan/02's dependency register, and docs/plan/02's own rule is that one which is not needs
///     an ADR. Eleven lines is not worth an ADR.
/// </remarks>
public sealed class TestTimeProvider : TimeProvider {
    DateTimeOffset now;

    public TestTimeProvider(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan delta) => now += delta;
}
