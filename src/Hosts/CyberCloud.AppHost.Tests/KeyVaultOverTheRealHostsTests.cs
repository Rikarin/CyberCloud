using CyberCloud.Core.Resources;
using CyberCloud.Gateway.Host;
using CyberCloud.Providers.KeyVault;
using CyberCloud.Providers.KeyVault.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Multitenant;
using System.Globalization;
using System.Text.Json;
// Orleans has an ErrorCode too.
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.AppHost.Tests;

/// <summary>
///     The key vault's one process boundary, crossed for real: the gateway's
///     <see cref="KeyVaultActionHandler" /> calling <see cref="IKeyVaultGrain" /> on a silo that is
///     another process.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why this file exists.</b> The #30 review found that nothing ever crossed this
///         boundary. <c>KeyVaultOverTheGatewayTests</c> runs one silo in the test's own process, and a
///         <c>TestCluster</c> client shares its silo's type manifest. That is exactly how #39's
///         <c>ShardMapMirrorController</c> passed every test and then failed the first sign-up against
///         the AppHost (17313ed). <c>KeyVaultDeclarationTests.EveryWireTypeCarriesAnAlias</c> checks
///         that the attributes are there. It can't check that a silo process accepts what the
///         gateway sends. Here the handler is resolved from the real <c>GatewayComposition</c>
///         container in this process, and the grain lives in one of the AppHost's two
///         <c>CyberCloud.Silo.Host</c> processes, on the AppHost's PostgreSQL.
///     </para>
///     <para>
///         ⚠
///         <b>
///             It asserts the refusal a running silo gives today, not a sealed secret, and that is
///             the other half of the review.
///         </b> The AppHost declares no OpenBao, so its silos keep
///         <c>UnavailableSecretResolver</c>. A vault on this topology opens and lists, and then refuses every
///         call that needs its root with that type's sentence. A create through the write path would
///         fail one step earlier, at the reconciler's mint, which is why this file opens the grain
///         itself instead of driving a <c>PUT</c> to <c>Succeeded</c>. docs/plan/18 § What landed,
///         and what is owed, <c>openbao-on-the-platform-topology</c>. When the AppHost gains a vault,
///         the refusal assertion becomes a <c>setSecret</c> and <c>getSecret</c> round trip.
///     </para>
///     <para>
///         ⚠ <b>No HTTP and no ReBAC here, deliberately.</b> Those run in the gateway's process and
///         <c>KeyVaultOverTheGatewayTests</c> covers them against a real OpenBao. What only this
///         file can say is whether <see cref="VaultCall" />, <see cref="VaultSettings" />,
///         <see cref="VaultDescriptor" /> and the invokables around them survive a second process's
///         type manifest.
///     </para>
/// </remarks>
/// <param name="topology">The running AppHost: two silo processes, Redis, PostgreSQL, k3s.</param>
[Collection(LocalTopologySuite.Name)]
public sealed class KeyVaultOverTheRealHostsTests(LocalTopology topology) : IAsyncLifetime {
    static readonly Guid Tenant = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0030");
    static readonly Guid Subscription = new("0d1f0dfe-4c7e-4f2c-9b5b-2f9b4d0a0031");

    const string ResourceGroup = "key-vault-boundary";

    /// <summary>The real gateway host, built the way its <c>Program.cs</c> builds it.</summary>
    WebApplication gateway = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        gateway = await GatewayComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort="
                + CyberCloudResources.SiloOneGatewayPort.ToString(CultureInfo.InvariantCulture),
                "--CyberCloud:Gateway:Identity:Issuer=http://127.0.0.1:1"
            ]
        );

        await gateway.StartAsync(TestContext.Current.CancellationToken);

        // ⚠ The tenant record only. The vault grain persists to the durable tier, and the tenant is
        // what routes that write to a shard. No scope or grant is needed, because the handler is
        // called below the resource manager.
        var record = await topology.Client.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<ITenantGrain>(GrainKeys.Tenant(Tenant))
            .CreateAsync("key-vault-boundary", "Key vault boundary", "eu-central");

        record.IsSuccess.ShouldBeTrue(record.Error?.Message);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (gateway is not null) {
            await gateway.StopAsync(CancellationToken.None);
            await gateway.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheGatewaysHandlerReachesAVaultOnASiloProcessAndTheSiloAnswersWithoutAVault() {
        var vaultId = Guid.NewGuid();

        // ⚠ From the gateway's own container, the way ActionDispatcher resolves it, so the grain
        // factory behind it is the gateway's Orleans client and not the fixture's.
        var handler = gateway.Services.GetRequiredService<KeyVaultActionHandler>();
        var grains = gateway.Services.GetRequiredService<IGrainFactory>();

        // ── A VaultCall to a vault nothing opened: the grain's own refusal comes back ───────────
        var unopened = await handler.InvokeAsync(Call(vaultId, KeyVaults.ListSecretsAction, "{}"), TestContext.Current.CancellationToken);

        unopened.IsSuccess.ShouldBeFalse("a vault nothing opened served a call");
        unopened.Error!.Code.ShouldBe(
            ErrorCode.Conflict,
            "the gateway's call did not reach KeyVaultGrain's own check. An InternalError naming "
            + "TypeManifestOptions.AllowedTypes or an unknown alias is the silo process refusing a "
            + $"wire type: {unopened.Error.Message}"
        );

        // ── Open it across the boundary: VaultSettings out, VaultDescriptor back ─────────────────
        var opened = await grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IKeyVaultGrain>(GrainKeys.Resource(vaultId))
            .OpenAsync(new() { VaultId = vaultId, PurgeProtection = true });

        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        opened.GetValueOrThrow().IsOpen.ShouldBeTrue();
        opened.GetValueOrThrow().PurgeProtection.ShouldBeTrue("VaultSettings lost a field crossing into the silo");

        // ── A call that needs no root: served on the silo, answered in this process ─────────────
        var listed = await handler.InvokeAsync(Call(vaultId, KeyVaults.ListSecretsAction, "{}"), TestContext.Current.CancellationToken);

        listed.IsSuccess.ShouldBeTrue(listed.Error?.Message);

        using (var body = JsonDocument.Parse(listed.GetValueOrThrow())) {
            body.RootElement.GetProperty("count").GetInt32().ShouldBe(0);
        }

        // ── A call that needs the root: the silo process has no vault, and says so ──────────────
        var set = await handler.InvokeAsync(
            Call(vaultId, KeyVaults.SetSecretAction, """{"secretName":"boundary","value":"not-stored"}"""),
            TestContext.Current.CancellationToken
        );

        set.IsSuccess.ShouldBeFalse(
            "setSecret succeeded on the AppHost, so its silos resolve a root. That means the AppHost "
            + "gained an OpenBao, and this assertion should become a getSecret round trip."
        );

        set.Error!.Message.ShouldContain(
            "No secret resolver is wired",
            Case.Sensitive,
            "a silo with no CyberCloud:Vault must refuse with UnavailableSecretResolver's sentence, "
            + $"which names the configuration an operator is missing. It answered: {set.Error.Message}"
        );
    }

    /// <summary>The context <c>ActionDispatcher</c> would build for one action on the vault.</summary>
    /// <param name="vaultId">The vault's resource GUID, which keys its grain.</param>
    /// <param name="action">The action's name.</param>
    /// <param name="json">The already-validated body.</param>
    static ActionContext Call(Guid vaultId, string action, string json) {
        using var body = JsonDocument.Parse(json);

        return new(
            new(Tenant, Subscription, ResourceGroup, KeyVaults.Type, "boundary", vaultId),
            KeyVaults.V2026,
            action,
            body.RootElement.Clone(),
            default,
            string.Empty,
            null,
            new UnavailableSecretResolver()
        );
    }
}
