using CyberCloud.ServiceDefaults.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CyberCloud.ServiceDefaults.Tests;

/// <summary>
///     Which checks carry which tag — the registration-level guard against a health check that lies.
/// </summary>
/// <remarks>
///     A tag is one word in one argument and it decides whether a failing check restarts a pod,
///     evicts it from a Service, or does neither. That makes it exactly the kind of thing that gets
///     copied onto a new check without thought, so it is asserted rather than reviewed.
/// </remarks>
public sealed class HealthCheckWiringTests
{
    static HealthCheckServiceOptions Registrations(Action<IHostApplicationBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        configure(builder);
        return builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;
    }

    [Fact]
    public void ExactlyOneCheckIsLiveAndItDependsOnNothingRemote()
    {
        var live = Registrations(b => b.AddServiceDefaults())
            .Registrations
            .Where(x => x.Tags.Contains(HealthCheckTags.Live))
            .ToList();

        // A liveness failure RESTARTS the pod. Anything that can fail because another machine
        // failed must never be tagged Live, or a Redis blip restarts thirty silos and moves two
        // million grains.
        live.Count.ShouldBe(1);
        live[0].Name.ShouldBe("self");
    }

    [Fact]
    public void AddServiceDefaultsAloneTagsNothingReady()
    {
        // A process that has not said what "ready" means for it must not accidentally inherit one.
        Registrations(b => b.AddServiceDefaults())
            .Registrations
            .Where(x => x.Tags.Contains(HealthCheckTags.Ready))
            .ShouldBeEmpty();
    }

    [Fact]
    public void ExactlyOneCheckIsReadyOnASiloAndItIsTheSiloReadinessCheck()
    {
        var ready = Registrations(b =>
            {
                b.AddServiceDefaults();
                b.AddOrleansHealthChecks();
            })
            .Registrations
            .Where(x => x.Tags.Contains(HealthCheckTags.Ready))
            .ToList();

        ready.Count.ShouldBe(
            1,
            "readiness answers exactly one question — would a request routed here be served? — and "
            + "only SiloReadinessHealthCheck answers it. A second Ready check is a second reason "
            + "for Kubernetes to pull a healthy silo out of the load balancer.");

        ready[0].Name.ShouldBe("silo-ready");
    }

    [Fact]
    public void TheClusterCheckIsNeverReady()
    {
        // The cascading-eviction guard: `cluster` describes PEERS. Wiring it into readiness means
        // one silo dying makes every survivor report the same degradation and get evicted too.
        var registrations = Registrations(b =>
        {
            b.AddServiceDefaults();
            b.AddOrleansHealthChecks();
        }).Registrations;

        var cluster = registrations.Single(x => x.Name == "cluster");
        cluster.Tags.ShouldBeEmpty();

        var participants = registrations.Single(x => x.Name == "silo-participants");
        participants.Tags.ShouldBeEmpty();
    }

    [Fact]
    public void TheClientRegistersNoSiloOnlyChecks()
    {
        // SiloReadinessHealthCheck resolves ILocalSiloDetails, which a client does not have. If it
        // were registered here the gateway would report Unhealthy forever with a DI error.
        var names = Registrations(b =>
            {
                b.AddServiceDefaults();
                b.AddOrleansClientHealthChecks();
            })
            .Registrations
            .Select(x => x.Name)
            .ToList();

        names.ShouldNotContain("silo-ready");
        names.ShouldNotContain("silo-participants");
        names.ShouldContain("cluster");
    }

    [Fact]
    public void TheParticipantsCheckIsASingletonBecauseItCarriesState()
    {
        // It remembers when it last ran and asks the participants about that window. Two instances
        // would each see half the window and neither would be right.
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        builder.AddOrleansHealthChecks();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<SiloParticipantsHealthCheck>()
            .ShouldBeSameAs(provider.GetRequiredService<SiloParticipantsHealthCheck>());
    }
}
