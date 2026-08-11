namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     Whether the caller may reach a cluster connection it does not own — the platform → connection
///     edge of docs/plan/06 § Platform administration.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06 § Platform administration, row 1:
///         <i>
///             platform tenant → any tenant, allowed
///             when the caller holds an active <c>platform:root#operator</c> relation, logged always,
///             with the operator's user id.
///         </i>
///         docs/plan/06 § Grain keys names the connection grain
///         specifically:
///         <i>
///             "<c>PlatformCrossTenantAuthorizer</c> explicitly allows the platform →
///             connection edge and logs it."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>This is a seam and the default DENIES</b>, for exactly the reason
///         <c>CyberCloud.Tenancy.Separation.IPlatformOperatorAuthority</c> gives: a default-allow
///         stand-in is a hole with a comment on it, and the comment is the first thing to be
///         forgotten.
///     </para>
///     <para>
///         ⚠ <b>Why this is not simply <c>IPlatformOperatorAuthority</c>.</b> That interface lives in
///         <c>CyberCloud.Tenancy</c> — an <i>implementation</i> assembly — and its own remarks say it
///         is expected to move to <c>CyberCloud.Authorization</c> when the ReBAC engine lands
///         (ADR-007). Binding the Kubernetes fabric to it would make that move a rewrite here rather
///         than a registration change there, which is the thing the seam exists to prevent. The two
///         are the same shape on purpose: adapting one onto the other is a lambda, and that is what
///         <c>AddCyberCloudKubernetes</c> should be handed once the relation has a real owner.
///     </para>
///     <para>
///         It returns the operator's identity rather than a <see langword="bool" /> because the
///         document requires the edge to be logged "with the operator's user id", and an authorizer
///         that returned only <see langword="true" /> could not write that line.
///     </para>
/// </remarks>
public interface IClusterOperatorAuthority {
    /// <summary>
    ///     The operator's user id when the caller may reach this cluster; <see langword="null" /> when
    ///     not.
    /// </summary>
    /// <param name="clusterId">The cluster being reached.</param>
    /// <param name="owningTenantId">The tenant that owns it.</param>
    string? OperatorFor(Guid clusterId, Guid owningTenantId);
}

/// <summary>The default: nobody is a cluster operator. See <see cref="IClusterOperatorAuthority" />.</summary>
public sealed class DenyClusterOperatorAuthority : IClusterOperatorAuthority {
    /// <inheritdoc />
    public string? OperatorFor(Guid clusterId, Guid owningTenantId) => null;
}
