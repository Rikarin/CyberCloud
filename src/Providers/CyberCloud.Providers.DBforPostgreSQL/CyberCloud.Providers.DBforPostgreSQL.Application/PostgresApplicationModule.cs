using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.DBforPostgreSQL.Application;

/// <summary>
///     The managed-PostgreSQL provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers its
///         resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;PostgresProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's <c>ConfigureServices</c>
///         runs after.
///     </para>
///     <para>
///         ⚠ <b>The sample said the same and called it "a platform observation, not a provider
///         quirk". This is the observation happening a second time, which is what turns it into
///         evidence.</b> Two providers now each need one <c>[DependsOn]</c> and one separate
///         silo-builder call, and the second call is the one nobody adding the third provider will
///         remember — its symptom is a namespace whose endpoints answer <c>404</c> with nothing in
///         the log. Closing it means either a module-collection seam on <c>ISiloBuilder</c> or a
///         registry built lazily from the container; both are changes to
///         <c>CyberCloud.ResourceManager</c>'s wiring and neither belongs here.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class PostgresApplicationModule : AbpModule;
