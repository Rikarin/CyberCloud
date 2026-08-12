using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Search.Application;

/// <summary>
///     The managed-search provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;SearchProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's <c>ConfigureServices</c>
///         runs after.
///     </para>
///     <para>
///         ⚠ <b>The fifth empty <c>.Application</c> project, and the count has stopped being news.</b>
///         The sample's emptiness could be dismissed as triviality; the second provider called its own
///         <i>"evidence about the registry-as-routing-source decision"</i>; the third said three was
///         the number at which <i>"the project's existence, rather than its emptiness, is the thing
///         worth questioning"</i>; the fifth said <i>"nothing new was learned here, and that is itself
///         the report"</i>. This one adds the only thing left to add, which is a <b>negative result
///         about a different kind of service</b>: the four namespaces before it are stores and
///         brokers, and a search service has an obvious candidate for an application service — a query
///         proxy — and it is still not one, because a query goes to the data plane and the data plane
///         is not something the resource manager routes. Five namespaces in, no provider has needed an
///         application service, and now none of the two shapes has.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class SearchApplicationModule : AbpModule;
