namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Runs a synchronous action on a silo, for a process that cannot reach the resource's cluster
///     itself — the gateway.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Why a synchronous action needs a silo at all.</b> A non-long-running action runs inside
///         <c>ResourceManagerService.ActionAsync</c>, in whichever process holds the manager, and for a
///         request that's the gateway. The gateway is an Orleans client and composes no cluster
///         connection: its <see cref="IClusterConnectionFactory" /> is the refusing default, and a
///         grain-backed one wouldn't help, because <c>ClusterConnectionGrain</c> refuses a client
///         caller (its tenancy check can only see a calling grain's tenant). So every action on a type
///         that declares <c>RequiresCluster</c> — <c>connect</c> on a cloud console, <c>start</c> on a
///         virtual machine — was refused in the gateway before its handler ran, and passed in every
///         harness that hands its dispatcher a direct connection.
///     </para>
///     <para>
///         ⚠ <b>Tenant-qualified, and that's what lets the cluster connection say yes.</b> The grain
///         runs the handler inside a grain turn qualified to the resource's tenant, which is the same
///         caller a reconcile pass is: the connection grain sees that tenant and applies its ordinary
///         owner check. The grain refuses a resource from any other tenant than its key's.
///     </para>
///     <para>
///         ⚠ <b>Nothing is authorized here.</b> The manager has already asked ReBAC for the action's
///         permission before it relays, and the grain runs the handler with the caller it was handed,
///         as a fact to bind to — the same contract as <see cref="ActionContext.Caller" />.
///     </para>
///     <para>
///         <b>Kind</b> Worker · <b>Tier</b> <b>none</b> · <b>Key</b> <see cref="ClusterActionKeys.Worker" />,
///         tenant-qualified.
///     </para>
/// </remarks>
[Alias("Rm.ClusterAction")]
public interface IClusterActionGrain : IGrainWithStringKey {
    /// <summary>Runs one declared action's handler on this silo and returns its response body.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="action">The declared action's name.</param>
    /// <param name="input">The resource as stored, which the gateway has already read.</param>
    /// <param name="body">The validated <c>POST</c> body, as JSON text.</param>
    /// <param name="caller">Who asked, or <see langword="null" /> when the manager had nobody to hand over.</param>
    /// <returns>
    ///     What the handler answered, checked against the action's declared response. A resource of
    ///     another tenant than the key's is <see cref="ErrorCode.AuthorizationFailed" />.
    /// </returns>
    [Alias("Invoke")]
    Task<Result<string>> InvokeAsync(
        ResourceId id,
        string action,
        ReconcileInput input,
        string body,
        CallerContext? caller
    );
}

/// <summary>The key of <see cref="IClusterActionGrain" />.</summary>
public static class ClusterActionKeys {
    /// <summary>The one key per tenant: the tenant qualification is the whole identity.</summary>
    public const string Worker = "cluster-actions";
}
