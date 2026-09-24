using CyberCloud.ResourceManager.Contracts;
using CyberCloud.ResourceManager.Contracts.Registry;

namespace CyberCloud.Providers.Resources;

/// <summary>
///     The <c>CyberCloud.Resources</c> provider — <c>deployments</c>, a template of resources deployed
///     at a resource group. docs/plan/08 § Long-running operations, "Nested operations".
/// </summary>
/// <remarks>
///     ⚠ <b>The declaration is <see cref="Deployments.Describe" />, and this class only hands it the
///     builder.</b> The resource manager has to know the type's pointers and its action to drive a
///     deployment and to validate its body, and it cannot reference a provider; so the declaration sits
///     in the contracts assembly both of them reach, and the generated surfaces see it through here.
///     A second copy of the schema in this file would be two declarations of one type waiting to drift.
/// </remarks>
public sealed class ResourcesProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => Deployments.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) => Deployments.Describe(builder);
}
