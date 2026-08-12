using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.DBforMySQL.Application;

/// <summary>
///     The managed-MariaDB provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;MariaDbProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's <c>ConfigureServices</c>
///         runs after.
///     </para>
///     <para>
///         ⚠ <b>The third provider called the surviving warning "a seam, not a mechanism" and named
///         what closing it would take. Nothing has closed it, and this is the sixth family to depend
///         on a comment.</b> The symptom of forgetting the silo-builder call is a namespace whose
///         endpoints answer <c>404</c> with nothing in the log — and the only reason it is not missing
///         here is, again, that the note existed to be read. Six repetitions is enough evidence to
///         stop calling it an observation: the fix is a module-collection seam on <c>ISiloBuilder</c>
///         or a registry built lazily from the container, both changes to
///         <c>CyberCloud.ResourceManager</c>'s wiring and neither one a provider's to make.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class MariaDbApplicationModule : AbpModule;
