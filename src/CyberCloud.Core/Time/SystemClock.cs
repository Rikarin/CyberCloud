namespace CyberCloud.Core.Time;

/// <summary>The real clock. Register as a singleton.</summary>
/// <remarks>
///     Delegates to a <see cref="TimeProvider" /> so that a test can pass
///     <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c> without needing a second
///     <see cref="IClock" /> implementation; the parameterless form uses
///     <see cref="TimeProvider.System" />.
/// </remarks>
public sealed class SystemClock : IClock {
    readonly TimeProvider provider;

    /// <inheritdoc />
    public DateTimeOffset UtcNow => provider.GetUtcNow();

    /// <summary>Creates a clock over <see cref="TimeProvider.System" />.</summary>
    public SystemClock()
        : this(TimeProvider.System) { }

    /// <summary>Creates a clock over <paramref name="timeProvider" />.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider" /> is null.</exception>
    public SystemClock(TimeProvider timeProvider) {
        ArgumentNullException.ThrowIfNull(timeProvider);
        provider = timeProvider;
    }
}
