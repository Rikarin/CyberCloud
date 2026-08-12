using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Network.Application;

/// <summary>
///     The tenant networking provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;NetworkProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's <c>ConfigureServices</c>
///         runs after.
///     </para>
///     <para>
///         ⚠ <b>Empty again, and this family is the strongest test that seam has had.</b> Every
///         namespace before this one reported an empty <c>.Application</c> over one or two resource
///         types that were each a workload. This one carries <b>five</b> types, three of them
///         children, and one of them — <c>publicIpAddresses</c> — is an <i>allocation</i> rather than
///         a workload. An allocator is the shape with the best claim to needing an application
///         service of its own, because "give me an address" reads like a verb rather than a PUT. It
///         does not need one: docs/plan/14 makes an address a resource, and a resource's create
///         <i>is</i> the allocation. So PUT, GET, DELETE and every <c>POST</c> action across all five
///         types remain generic resource-manager verbs the gateway routes from the provider registry
///         (ADR-012), and the seam is still unexercised by anything in the tree.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class NetworkApplicationModule : AbpModule;
