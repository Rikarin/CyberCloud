using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.KeyVault.Tests;

/// <summary>A clock a test moves by hand.</summary>
public sealed class TestClock : IClock {
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
///     A one-silo cluster running the real <see cref="KeyVaultGrain" />, with the root in an
///     in-memory platform vault and the durable tier in memory where a test can read it back.
/// </summary>
/// <remarks>
///     ⚠ <b>The platform vault is the resource manager's own test double, and it is not the thing
///     under test here.</b> What this suite asserts is the vault's rules and its sealing; the root
///     round trip through a real OpenBao is <c>KeyVaultOverTheGatewayTests</c>', which runs the same
///     grain behind the real gateway with <c>openbao/openbao:2.4.1</c> holding the root.
/// </remarks>
public sealed class VaultSilo : IAsyncLifetime {
    /// <summary>The tenant every vault here belongs to.</summary>
    public static Guid Tenant { get; } = Guid.Parse("7a7a7a7a-0000-4000-8000-000000000001");

    /// <summary>The platform vault the grain resolves its root from.</summary>
    public static InMemorySecretVault PlatformVault { get; } = new();

    /// <summary>The clock the grain reads.</summary>
    public static TestClock Clock { get; } = new();

    TestCluster cluster = null!;

    /// <summary>The cluster's grain factory.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    /// <summary>A vault's grain.</summary>
    /// <param name="vaultId">The vault's resource GUID.</param>
    public IKeyVaultGrain Vault(Guid vaultId) =>
        Grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture)).GetGrain<IKeyVaultGrain>(GrainKeys.Resource(vaultId));

    /// <summary>A fresh vault: its root minted, its grain opened.</summary>
    /// <param name="purgeProtection">Whether purge protection is on.</param>
    public async Task<(Guid Id, IKeyVaultGrain Grain)> OpenVaultAsync(bool purgeProtection = false) {
        var id = Guid.NewGuid();

        (await PlatformVault.MintAsync(
            KeyVaults.RootPath(Tenant, id),
            new Dictionary<string, string> { [KeyVaults.RootField] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) }
        )).IsSuccess.ShouldBeTrue();

        var grain = Vault(id);
        (await grain.OpenAsync(new() { VaultId = id, PurgeProtection = purgeProtection })).IsSuccess.ShouldBeTrue();

        return (id, grain);
    }

    /// <summary>
    ///     A vault's durable state as the storage tier holds it, serialized the way Orleans writes it.
    /// </summary>
    /// <param name="vaultId">The vault.</param>
    /// <remarks>
    ///     ⚠ Read from the storage provider rather than from the grain — the assertion is about what
    ///     was <i>written</i> — and then serialized to bytes, so a search covers every member,
    ///     including ones a later edit adds.
    /// </remarks>
    public async Task<byte[]> StoredBytesAsync(Guid vaultId) {
        var services = cluster.Silos.OfType<InProcessSiloHandle>().First().SiloHost.Services;
        var storage = services.GetRequiredKeyedService<IGrainStorage>(StorageTiers.Durable);
        var state = new GrainState<KeyVaultState>(new());

        await storage.ReadStateAsync("vault", Vault(vaultId).GetGrainId(), state);

        state.RecordExists.ShouldBeTrue("the vault's state was never written");
        return services.GetRequiredService<Serializer>().SerializeToArray(state.State);
    }

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.ConfigureServices(static services => {
                    services.AddSingleton<IClock>(Clock);
                    services.AddSingleton<ISecretResolver>(PlatformVault);
                }
            );
        }
    }
}

/// <summary>Builds request bodies and reads responses the way the gateway would hand them over.</summary>
public static class Calls {
    /// <summary>Runs an action and fails the test if it was refused.</summary>
    /// <param name="vault">The vault's grain.</param>
    /// <param name="action">The action.</param>
    /// <param name="body">The request.</param>
    public static async Task<JsonElement> OkAsync(this IKeyVaultGrain vault, string action, object? body = null) {
        var result = await vault.RunAsync(action, body);
        result.IsSuccess.ShouldBeTrue($"{action} was refused: {result.Error?.Code} {result.Error?.Message}");

        using var document = JsonDocument.Parse(result.GetValueOrThrow());
        return document.RootElement.Clone();
    }

    /// <summary>Runs an action.</summary>
    /// <param name="vault">The vault's grain.</param>
    /// <param name="action">The action.</param>
    /// <param name="body">The request, serialized as the gateway would pass it.</param>
    public static Task<Result<string>> RunAsync(this IKeyVaultGrain vault, string action, object? body = null) =>
        vault.InvokeAsync(
            new() {
                Action = action,
                Body = body is null ? "{}" : body as string ?? JsonSerializer.Serialize(body)
            }
        );

    /// <summary>A JSON object from name-value pairs, for a body with an array in it.</summary>
    /// <param name="pairs">The members.</param>
    public static string Json(params (string Name, JsonNode? Value)[] pairs) {
        var json = new JsonObject();
        foreach (var (name, value) in pairs) {
            json[name] = value;
        }

        return json.ToJsonString();
    }
}
