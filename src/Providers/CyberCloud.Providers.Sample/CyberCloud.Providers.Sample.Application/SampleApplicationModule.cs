using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Sample.Application;

/// <summary>
///     The sample provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here, and the reason is worth stating rather than discovering: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;SampleProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's
///         <c>ConfigureServices</c> runs after. So a host loads this module for the application layer
///         and calls <c>AddCyberCloudProvider</c> for the reconciler, and the two halves are separate
///         calls.
///     </para>
///     <para>
///         ⚠ <b>That split is a platform observation, not a provider quirk.</b> "One provider, one
///         registration" would be a better story than "one module plus one silo-builder call", and
///         closing it means either a module-collection seam on <c>ISiloBuilder</c> or a provider
///         registry that is built lazily from the container rather than from the builder. Both are
///         changes to <c>CyberCloud.ResourceManager</c>'s wiring, and neither belongs here.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class SampleApplicationModule : AbpModule;
