using CyberCloud.Providers.ContainerRegistry.Application;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace CyberCloud.Registry.Feeds.Host;

/// <summary>
///     The feeds host's ABP module graph — docs/plan/03 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             ONE provider module, and the difference from the gateway's fifteen is a decision
///             rather than an omission.
///         </b> The gateway routes every resource path against its registry, so a provider missing
///         from its list is a type nobody can reach. This host reads exactly one type —
///         <c>CyberCloud.ContainerRegistry/feeds</c> — through <c>IResourceManager.ReadAsync</c>,
///         and the registry the resource manager builds needs that type's registration and no
///         other's. Loading fourteen more modules here would put fourteen providers' implementation
///         assemblies into a data-plane process for nothing; <c>HostCompositionTests</c> asserts the
///         registry this host builds holds the feeds type and pins that it is the ContainerRegistry
///         family alone.
///     </para>
///     <para>
///         <c>AbpAutofacModule</c> is required: <c>OrleansApplication.CreateClient</c> calls
///         <c>builder.Host.UseAutofac()</c>, and ABP's service-provider factory demands an
///         <c>IModuleContainer</c> at <c>Build()</c> time.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpAutofacModule))]
[DependsOn(typeof(ContainerRegistryApplicationModule))]
public sealed class FeedsHostModule : AbpModule;
