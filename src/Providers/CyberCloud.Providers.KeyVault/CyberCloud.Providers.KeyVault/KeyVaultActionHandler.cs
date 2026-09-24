using Orleans.Multitenant;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.Providers.KeyVault;

/// <summary>
///     Serves every data-plane action on a vault by handing it to the vault's grain.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One handler for twenty-five actions, and it decides nothing.</b> By the time it runs
///         the resource manager has validated the body against the action's request schema, checked
///         the action's own permission against the caller — a data-plane permission, see
///         <see cref="KeyVaults.ReadSecretsPermission" /> — and refused a vault that does not exist or
///         is soft-deleted. What is left is the vault's own rules, and those are the grain's. An
///         empty <see cref="Action" /> is how a handler says it serves every action on its type.
///     </para>
///     <para>
///         ⚠ <b>It runs in the gateway, and the grain on a silo.</b> <c>ResourceManagerService</c>
///         is hosted by the gateway as an Orleans client, so this call is the process boundary the
///         aliases in <c>IKeyVaultGrain.cs</c> exist for — a secret's value crosses it in the
///         request or the response and nothing else does.
///     </para>
/// </remarks>
/// <param name="grains">The host's grain factory — the cluster client in the gateway.</param>
public sealed class KeyVaultActionHandler(IGrainFactory grains) : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => KeyVaults.Type;

    /// <inheritdoc />
    public string Action => string.Empty;

    /// <inheritdoc />
    public Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) =>
        grains.ForTenant(context.Id.TenantId.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IKeyVaultGrain>(GrainKeys.Resource(context.Id.Id))
            .InvokeAsync(
                new() {
                    Action = context.Action,
                    Body = context.Body.ValueKind == JsonValueKind.Undefined ? "{}" : context.Body.GetRawText(),
                    TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty
                }
            );
}
