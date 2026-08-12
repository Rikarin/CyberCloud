using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Storage.Application;

/// <summary>
///     The managed-object-storage provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;StorageProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's <c>ConfigureServices</c>
///         runs after.
///     </para>
///     <para>
///         ⚠ <b>The fourth empty <c>.Application</c> project, and the count is now the finding.</b>
///         The sample's emptiness could be dismissed as triviality; the second provider called its own
///         <i>"evidence about the registry-as-routing-source decision"</i>; the third said three was
///         the number at which <i>"the project's existence, rather than its emptiness, is the thing
///         worth questioning"</i>. This is the fourth <i>namespace</i> to report it, over a resource
///         type whose action returns a credential and whose body has nine tenant-facing properties —
///         and PUT, GET, DELETE and POST <c>listKeys</c> are still all generic resource-manager verbs
///         the gateway routes from the provider registry (ADR-012). Nothing new was learned here, and
///         that is itself the report: four namespaces in, no provider has needed an application
///         service, so the seam has never been exercised by anything.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class StorageApplicationModule : AbpModule;
