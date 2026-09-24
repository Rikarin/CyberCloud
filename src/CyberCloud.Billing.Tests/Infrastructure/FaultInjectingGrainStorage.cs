using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using System.Collections.Concurrent;

namespace CyberCloud.Billing.Tests.Infrastructure;

/// <summary>How an armed write fails.</summary>
public enum StorageFault {
    /// <summary>Throws without writing — a write that never reached storage.</summary>
    BeforeWrite,

    /// <summary>
    ///     Writes, then throws — a write that committed and whose answer was lost. The caller can't
    ///     tell it from <see cref="BeforeWrite" />, and that's the point.
    /// </summary>
    AfterWrite
}

/// <summary>
///     The Durable tier's in-memory storage, with a write that can be made to fail once for one grain.
/// </summary>
/// <remarks>
///     ⚠ Every test before this one wrote successfully, so nothing drove a grain through a failed write,
///     and <c>InvoiceNumberingGrain</c> answered a retry from a number it never stored. Arming is per
///     grain id, not per state name, so a fault meant for one account can't land on another's write.
/// </remarks>
public sealed class FaultInjectingGrainStorage(IGrainStorage inner) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle> {
    readonly ConcurrentDictionary<GrainId, ConcurrentQueue<StorageFault>> armed = new();

    /// <summary>Makes the grain's next write fail the given way.</summary>
    /// <param name="grain">The grain whose write fails.</param>
    /// <param name="fault">Whether the write reaches storage before it throws.</param>
    public void FailNextWrite(GrainId grain, StorageFault fault) => armed.GetOrAdd(grain, static _ => new()).Enqueue(fault);

    /// <summary>What storage holds for a grain now, read around the grain.</summary>
    /// <param name="stateName">The <c>[PersistentState]</c> name.</param>
    /// <param name="grain">The grain.</param>
    public async Task<T> ReadAsync<T>(string stateName, GrainId grain) where T : new() {
        var state = new GrainState<T>(new());
        await inner.ReadStateAsync(stateName, grain, state);
        return state.State;
    }

    /// <inheritdoc />
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) =>
        inner.ReadStateAsync(stateName, grainId, grainState);

    /// <inheritdoc />
    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) {
        if (!armed.TryGetValue(grainId, out var faults) || !faults.TryDequeue(out var fault)) {
            await inner.WriteStateAsync(stateName, grainId, grainState);
            return;
        }

        if (fault == StorageFault.AfterWrite) {
            await inner.WriteStateAsync(stateName, grainId, grainState);
        }

        throw new IOException($"Injected {fault} fault on {stateName} for {grainId}.");
    }

    /// <inheritdoc />
    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) =>
        inner.ClearStateAsync(stateName, grainId, grainState);

    /// <inheritdoc />
    public void Participate(ISiloLifecycle lifecycle) => (inner as ILifecycleParticipant<ISiloLifecycle>)?.Participate(lifecycle);

    /// <summary>Wraps the storage already registered under <paramref name="name" />.</summary>
    /// <param name="services">The silo's services, after the storage was added.</param>
    /// <param name="name">The storage's name.</param>
    public static void Decorate(IServiceCollection services, string name) {
        var registered = services.Last(x => x.ServiceType == typeof(IGrainStorage) && x.IsKeyedService && Equals(x.ServiceKey, name));
        var create = registered.KeyedImplementationFactory
            ?? throw new InvalidOperationException($"The '{name}' storage is not registered by a factory; there is nothing to wrap.");

        services.Remove(registered);
        services.AddKeyedSingleton<IGrainStorage>(name, (provider, key) => new FaultInjectingGrainStorage((IGrainStorage)create(provider, key)));
    }
}
