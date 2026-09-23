using CyberCloud.Conformance;
using CyberCloud.Core;
using CyberCloud.Core.Resources;
using CyberCloud.Providers.KeyVault.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using Shouldly;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.KeyVault.Conformance;

/// <summary>
///     <c>CyberCloud.KeyVault/vaults</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The world is the vault's grain, read through <see cref="KeyVaultModule" />.</b> A
///         vault applies nothing to a cluster; what a pass converges is the grain's settings, which
///         the body decides and a pass can put back — so the suite's drift and hand-edit assertions
///         run here as they do for <c>CyberCloud.Communication/services</c>, with a seal standing
///         for "removed behind the reconciler's back" and a changed recovery window for "edited".
///     </para>
///     <para>
///         ⚠ <b>What a green run here does not prove</b>, because the suite's caller is doubled:
///         anything about who may call the data plane. <c>ProviderTestCluster</c> authorizes with a
///         test double, so the six data-plane permissions are not evaluated here at all.
///         <c>KeyVaultOverTheGatewayTests</c> in <c>CyberCloud.Gateway.Host.Tests</c> drives them
///         through the real <c>CyberCloudSchema</c>, the real gateway and a real OpenBao.
///     </para>
/// </remarks>
public sealed class KeyVaultCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.KeyVault/vaults",
            CreateProvider = static () => new KeyVaultProvider(),
            ReconcilerType = typeof(KeyVaultReconciler),
            // ⚠ Read when the factory RUNS, after the harness attached — KeyVaultModule.Grains refuses
            // by name if that order is ever wrong.
            CreateReconciler = static clock => new KeyVaultReconciler(KeyVaultModule.Instance.Grains, clock),
            Type = KeyVaults.Type,
            ApiVersion = KeyVaults.V2026,
            Body = static _ => KeyVaults.Body(false, "secrets for the build"),
            // ⚠ Turns purge protection ON, which the grain holds and a pass has to carry to it — the
            // description alone reaches nothing the world holds. On is the one direction the manager
            // allows, so no later assertion is refused for it; the suite's purges run on resources
            // created from Body, never on the updated one.
            ChangedBody = static _ => KeyVaults.Body(true, "secrets for the build"),
            // Drops the required location.
            InvalidBody = static _ => WithoutLocation(KeyVaults.Body()),
            InvalidBodyTarget = "/location",
            // The one action that takes no body, which is what the suite's POST sends.
            ActionName = KeyVaults.ListSecretsAction,
            Objects = static (_, _) => [],
            OperatorWritten = static (_, _) => [],
            // The world is the module — ConvergedModule below — and a vault keeps nothing on the
            // platform's object store. Stated rather than defaulted; see ClusterlessWorld.
            DataPlane = null,
            StoragePrefix = null,
            ObjectMatchesDesired = static _ => false
        };

    /// <inheritdoc />
    public static IConvergedModule? ConvergedModule => KeyVaultModule.Instance;

    static string WithoutLocation(string body) {
        var node = JsonNode.Parse(body)!.AsObject();
        node.Remove("location");
        return node.ToJsonString();
    }
}

/// <summary>
///     A vault's grain as the suite's world: held when open and unsealed, matching when its settings
///     are the body's, removed by a seal, corrupted by a recovery window the body never asked for.
/// </summary>
public sealed class KeyVaultModule : IConvergedModule {
    IGrainFactory? grains;

    /// <summary>The one module the case and its suite share.</summary>
    public static KeyVaultModule Instance { get; } = new();

    /// <summary>The harness's grain factory, once it has attached.</summary>
    public IGrainFactory Grains =>
        grains
        ?? throw new InvalidOperationException(
            "The harness has not attached yet. KeyVaultModule.Grains is read by the case's "
            + "CreateReconciler, which the suite calls only after ProviderTestCluster.InitializeAsync."
        );

    /// <inheritdoc />
    /// <remarks>
    ///     Nothing to add: the grain is discovered from the provider's assembly, and the silo already
    ///     carries the durable tier, the clock and the platform vault the harness registers.
    /// </remarks>
    public void ConfigureSilo(ISiloBuilder silo) { }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ What the gateway's container holds for <c>KeyVaultActionHandler</c>: the cluster client,
    ///     which here is the harness's grain factory.
    /// </remarks>
    public void ConfigureHandlers(IServiceCollection services, IGrainFactory grains) => services.AddSingleton(grains);

    /// <inheritdoc />
    public void Attach(IGrainFactory grains) => this.grains = grains;

    /// <inheritdoc />
    public void Reset() { }

    /// <inheritdoc />
    public async Task<bool> HoldsAsync(ResourceId id, CancellationToken cancellationToken) {
        var described = await Vault(id).DescribeAsync();
        return described.IsSuccess && described.GetValueOrThrow() is { IsOpen: true, IsSealed: false, IsDestroyed: false };
    }

    /// <inheritdoc />
    public async Task<bool> MatchesAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        using var desired = JsonDocument.Parse(desiredJson);
        var described = await Vault(id).DescribeAsync();

        return described.IsSuccess
            && KeyVaultReconciler.Matches(described.GetValueOrThrow(), KeyVaults.SettingsOf(id.Id, desired.RootElement));
    }

    /// <inheritdoc />
    public async Task RemoveAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) =>
        (await Vault(id).SealAsync()).IsSuccess.ShouldBeTrue();

    /// <inheritdoc />
    public async Task CorruptAsync(ResourceId id, string desiredJson, CancellationToken cancellationToken) {
        using var desired = JsonDocument.Parse(desiredJson);
        var settings = KeyVaults.SettingsOf(id.Id, desired.RootElement);

        (await Vault(id).OpenAsync(settings with { RecoveryDays = settings.RecoveryDays + 23 })).IsSuccess.ShouldBeTrue();
    }

    IKeyVaultGrain Vault(ResourceId id) =>
        Grains.ForTenant(id.TenantId.ToString("D", CultureInfo.InvariantCulture)).GetGrain<IKeyVaultGrain>(GrainKeys.Resource(id.Id));
}

/// <summary>The shared suite, run against the key-vault type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class KeyVaultConformance(ProviderTestCluster<KeyVaultCase> cluster)
    : ProviderConformanceTests<KeyVaultCase>(cluster), IClassFixture<ProviderTestCluster<KeyVaultCase>>;

/// <summary>The container-backed half, skipped loudly, against the key-vault type.</summary>
public sealed class KeyVaultClusterBackedConformance() : ClusterBackedConformanceTests(KeyVaultCase.ProviderCase);
