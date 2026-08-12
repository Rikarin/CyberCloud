using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Cache.Application;

/// <summary>
///     The managed-Valkey provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;ValkeyCacheProvider&gt;()</c>, which runs against
///         the silo builder before the container is built, and an ABP module's
///         <c>ConfigureServices</c> runs after.
///     </para>
///     <para>
///         ⚠ <b>The sample called this "a platform observation, not a provider quirk"; the second
///         provider called that observation happening twice "what turns it into evidence". This is the
///         third, and the prediction it made has now had time to be wrong and was not.</b> That note
///         said the separate silo-builder call "is the one nobody adding the third provider will
///         remember — its symptom is a namespace whose endpoints answer <c>404</c> with nothing in the
///         log". Writing the third provider is the experiment, and the only reason the call is not
///         missing here is that the note existed to be read. A seam that works because somebody left a
///         warning is a seam, not a mechanism. Closing it means either a module-collection seam on
///         <c>ISiloBuilder</c> or a registry built lazily from the container; both are changes to
///         <c>CyberCloud.ResourceManager</c>'s wiring and neither belongs here.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class ValkeyCacheApplicationModule : AbpModule;
