using CyberCloud.Core.Time;
using CyberCloud.Kubernetes.Health;
using Shouldly;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>A clock a test can move — the same device the tenancy suite uses, for the same reason.</summary>
public sealed class TestClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves time forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
///     Connection health as a first-class property — docs/plan/09 § Cluster connections.
/// </summary>
/// <remarks>
///     ⚠ The transition is asserted by advancing an injected clock rather than by waiting 90
///     seconds. A test that slept for the real window is a test that would be marked
///     <c>[Skip]</c> within a month, and the transition it guards is the one that decides whether a
///     tenant's network outage reads as a platform bug.
/// </remarks>
public sealed class ClusterHealthTests {
    static readonly Guid ClusterId = Guid.Parse("3a8f0c22-5e6d-4a7b-8c9d-0e1f2a3b4c5d");

    [Fact]
    public void TheWindowIsNinetySecondsExactlyAsTheDocumentStates() {
        // docs/plan/09 § Cluster connections states a number; it is transcribed rather than
        // approximated, and asserted so a "tidy up to a round minute" is a failing test.
        ClusterHealthTracker.StalenessWindow.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void ANeverPingedClusterIsUnknownRatherThanDegraded() {
        // ⚠ Not the same thing. "We have not asked yet" must not suspend a tenant's reconciles, and
        // it must not tell them their cluster is unreachable either.
        var (tracker, _) = New();

        tracker.Current.State.ShouldBe(ClusterHealthState.Unknown);
        tracker.Current.ReconcilesSuspended.ShouldBeFalse();
    }

    [Fact]
    public void ASuccessfulPingIsHealthy() {
        var (tracker, _) = New();
        tracker.RecordSuccess();

        var health = tracker.Current;
        health.State.ShouldBe(ClusterHealthState.Healthy);
        health.ConsecutiveFailures.ShouldBe(0);
        health.ReconcilesSuspended.ShouldBeFalse();
        health.Message.ShouldBeEmpty();
    }

    [Fact]
    public void JustUnderNinetySecondsIsStillHealthy() {
        var (tracker, clock) = New();
        tracker.RecordSuccess();

        clock.Advance(TimeSpan.FromSeconds(89));

        tracker.Current.State.ShouldBe(ClusterHealthState.Healthy);
    }

    [Fact]
    public void AtNinetySecondsItIsDegraded() {
        // ⚠ THE TRANSITION. docs/plan/09 § Cluster connections: "A cluster that has not answered a
        // ping in 90 seconds is Degraded".
        var (tracker, clock) = New();
        tracker.RecordSuccess();

        clock.Advance(TimeSpan.FromSeconds(90));

        var health = tracker.Current;
        health.State.ShouldBe(ClusterHealthState.Degraded);
        health.ReconcilesSuspended.ShouldBeTrue();
    }

    [Fact]
    public void TheDegradedTransitionHappensWithNoFailuresRecordedAtAll() {
        // ⚠ THE CASE A FAILURE COUNTER WOULD MISS, and the reason the rule is elapsed time.
        // A silo that simply stops pinging — busy, or a timer that did not fire — records zero
        // failures. A count-based rule would call that cluster Healthy indefinitely while nothing
        // had confirmed it for an hour.
        var (tracker, clock) = New();
        tracker.RecordSuccess();

        clock.Advance(TimeSpan.FromHours(1));

        tracker.Current.ConsecutiveFailures.ShouldBe(0);
        tracker.Current.State.ShouldBe(ClusterHealthState.Degraded);
    }

    [Fact]
    public void TheDegradedMessageSaysCannotReachYourClusterAndNotProvisioningFailed() {
        // ⚠ The wording is part of the requirement, not decoration. docs/plan/09 § Cluster
        // connections: "the portal says 'cannot reach your cluster' instead of 'provisioning
        // failed'. The distinction between our failure and unreachable is what stops a tenant's
        // network outage from looking like a platform bug."
        var (tracker, clock) = New();
        tracker.RecordSuccess();
        tracker.RecordFailure("connection refused");
        clock.Advance(TimeSpan.FromSeconds(120));

        var message = tracker.Current.Message;

        message.ShouldContain("Cannot reach your cluster");
        message.ShouldContain("suspended, not failed");
        message.ShouldContain("connection refused");
        message.ShouldNotContain("provisioning failed");
    }

    [Fact]
    public void ARecoveryClearsTheDegradedStateAndTheFailureCount() {
        var (tracker, clock) = New();
        tracker.RecordSuccess();
        clock.Advance(TimeSpan.FromSeconds(200));
        tracker.RecordFailure("timeout");
        tracker.RecordFailure("timeout");

        tracker.Current.State.ShouldBe(ClusterHealthState.Degraded);
        tracker.Current.ConsecutiveFailures.ShouldBe(2);

        tracker.RecordSuccess();

        tracker.Current.State.ShouldBe(ClusterHealthState.Healthy);
        tracker.Current.ConsecutiveFailures.ShouldBe(0);
        tracker.Current.ReconcilesSuspended.ShouldBeFalse();
    }

    [Fact]
    public void AClusterThatHasNeverAnsweredButHasFailedIsDegradedNotUnknown() {
        // A cluster attached with a bad kubeconfig has never succeeded. "Unknown" would leave its
        // reconciles running against a cluster we demonstrably cannot reach.
        var (tracker, _) = New();
        tracker.RecordFailure("no such host");

        tracker.Current.State.ShouldBe(ClusterHealthState.Degraded);
        tracker.Current.Message.ShouldContain("since the platform first tried");
    }

    [Fact]
    public void ARehomedActivationRestoresItsWindowRatherThanStartingOver() {
        // ⚠ Without SeedLastSuccess, a grain that moved silos would report Unknown — and could not
        // report Degraded, because the transition is measured from the last success and it would
        // have none. A cluster unreachable for an hour would read as "no information" for a full
        // window after every rebalance.
        var clock = new TestClock();
        var restored = new ClusterHealthTracker(ClusterId, clock);

        restored.SeedLastSuccess(clock.UtcNow - TimeSpan.FromMinutes(10));

        restored.Current.State.ShouldBe(ClusterHealthState.Degraded);
    }

    [Fact]
    public void TheWindowIsConfigurableForAHostileByoClusterWithoutChangingTheDefault() {
        // docs/plan/09 § Testing the fabric wants "a deliberately hostile BYO cluster" to work.
        var clock = new TestClock();
        var patient = new ClusterHealthTracker(ClusterId, clock, TimeSpan.FromMinutes(5));

        patient.RecordSuccess();
        clock.Advance(TimeSpan.FromSeconds(120));

        patient.Current.State.ShouldBe(ClusterHealthState.Healthy);
        ClusterHealthTracker.StalenessWindow.ShouldBe(TimeSpan.FromSeconds(90), "the default is unchanged.");
    }

    [Fact]
    public void PingIntervalIsWellInsideTheWindowSoTwoLostPingsAreSurvivable() {
        // One lost ping is a dropped packet; three in a row is an outage. The interval must divide
        // the window enough times for that distinction to exist.
        (ClusterHealthTracker.StalenessWindow / ClusterHealthTracker.PingInterval)
            .ShouldBeGreaterThanOrEqualTo(3);
    }

    static (ClusterHealthTracker Tracker, TestClock Clock) New() {
        var clock = new TestClock();
        return (new(ClusterId, clock), clock);
    }
}
