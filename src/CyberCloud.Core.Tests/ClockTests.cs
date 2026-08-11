using CyberCloud.Core.Time;
using Shouldly;

namespace CyberCloud.Core.Tests;

/// <summary><see cref="IClock" /> and <see cref="SystemClock" /> — docs/plan/03 § Foundation.</summary>
public class ClockTests {
    [Fact]
    public void TheSystemClockReadsTheSystemClock() {
        var before = DateTimeOffset.UtcNow;
        var now = new SystemClock().UtcNow;
        var after = DateTimeOffset.UtcNow;

        now.ShouldBeGreaterThanOrEqualTo(before);
        now.ShouldBeLessThanOrEqualTo(after);
    }

    [Fact]
    public void TheClockIsAlwaysUtc() => new SystemClock().UtcNow.Offset.ShouldBe(TimeSpan.Zero);

    [Fact]
    public void ATimeProviderCanBeSubstitutedWithoutASecondImplementation() {
        // The reason SystemClock takes a TimeProvider: every duration in the platform is minutes
        // to days (a 5-minute index lease, a 60-minute operation timeout, a 30-day tombstone), and
        // none of them is testable against a clock you cannot advance.
        var fixedInstant = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);
        var clock = new SystemClock(new FrozenTimeProvider(fixedInstant));

        // Read through the interface, which is how every consumer sees it.
        Read(clock).ShouldBe(fixedInstant);
        Read(clock).ShouldBe(fixedInstant);

        static DateTimeOffset Read(IClock c) => c.UtcNow;
    }

    [Fact]
    public void ANullTimeProviderIsARejectedProgrammerError() =>
        Should.Throw<ArgumentNullException>(() => new SystemClock(null!));

    [Fact]
    public void TheInterfaceExposesNothingButTheInstant() {
        // Narrowing TimeProvider to one property is the point (see the remarks on IClock): a grain
        // that can start a timer outside Orleans' scheduler breaks its own single-threaded
        // contract, so the timer surface must not be reachable through this interface.
        typeof(IClock).GetProperties().Select(x => x.Name).ShouldBe([nameof(IClock.UtcNow)]);
        typeof(IClock).GetMethods().Select(x => x.Name).ShouldBe(["get_UtcNow"]);
    }

    sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
