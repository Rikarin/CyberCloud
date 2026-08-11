using CyberCloud.Core.Contracts;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>A grain whose state lives in the hot tier.</summary>
[Alias("CyberCloud.Tests.Hot")]
public interface IHotStateGrain : IGrainWithStringKey {
    /// <summary>Reads the stored note, or the empty string if nothing is stored.</summary>
    Task<string> ReadAsync();

    /// <summary>Stores a note.</summary>
    /// <param name="note">The note.</param>
    Task WriteAsync(string note);

    /// <summary>Whether storage reported a record for this grain.</summary>
    Task<bool> ExistsAsync();
}

/// <summary>A grain whose state lives in the durable tier.</summary>
[Alias("CyberCloud.Tests.Durable")]
public interface IDurableStateGrain : IGrainWithStringKey {
    /// <summary>Reads the stored note, or the empty string if nothing is stored.</summary>
    Task<string> ReadAsync();

    /// <summary>Stores a note.</summary>
    /// <param name="note">The note.</param>
    Task WriteAsync(string note);

    /// <summary>Whether storage reported a record for this grain.</summary>
    Task<bool> ExistsAsync();
}

/// <summary>
///     The persisted shape. Deliberately ordinary: a string somebody could recognise in
///     <c>psql</c>, which is what docs/plan/05 § Serialization says the durable tier is for.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Tests.NoteState")]
public sealed class NoteState {
    /// <summary>The note.</summary>
    [Id(0)]
    public string Note { get; set; } = string.Empty;

    /// <summary>How many times it has been written.</summary>
    [Id(1)]
    public int Revision { get; set; }
}

/// <summary>
///     <see cref="IHotStateGrain" />, bound to <see cref="StorageTiers.Hot" /> — the attribute
///     spelling of docs/plan/05 § Choosing a tier.
/// </summary>
public sealed class HotStateGrain(
    [PersistentState("note", StorageTiers.Hot)] IPersistentState<NoteState> state
)
    : Grain, IHotStateGrain {
    /// <inheritdoc />
    public Task<string> ReadAsync() => Task.FromResult(state.State.Note);

    /// <inheritdoc />
    public async Task WriteAsync(string note) {
        state.State.Note = note;
        state.State.Revision++;
        await state.WriteStateAsync();
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync() => Task.FromResult(state.RecordExists);
}

/// <summary><see cref="IDurableStateGrain" />, bound to <see cref="StorageTiers.Durable" />.</summary>
public sealed class DurableStateGrain(
    [PersistentState("note", StorageTiers.Durable)] IPersistentState<NoteState> state
)
    : Grain, IDurableStateGrain {
    /// <inheritdoc />
    public Task<string> ReadAsync() => Task.FromResult(state.State.Note);

    /// <inheritdoc />
    public async Task WriteAsync(string note) {
        state.State.Note = note;
        state.State.Revision++;
        await state.WriteStateAsync();
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync() => Task.FromResult(state.RecordExists);
}
