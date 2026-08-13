using CyberCloud.Providers.Analytics.Application;
using CyberCloud.Providers.Cache.Application;
using CyberCloud.Providers.ContainerService.Application;
using CyberCloud.Providers.DBforMySQL.Application;
using CyberCloud.Providers.DBforPostgreSQL.Application;
using CyberCloud.Providers.DocumentDB.Application;
using CyberCloud.Providers.Messaging.Application;
using CyberCloud.Providers.Network.Application;
using CyberCloud.Providers.Sample.Application;
using CyberCloud.Providers.Search.Application;
using CyberCloud.Providers.Storage.Application;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace CyberCloud.Gateway.Host;

/// <summary>
///     The gateway's ABP module graph — docs/plan/03 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same eleven provider modules the silo loads, and the gateway needs them for a
///         different reason.</b> The silo loads them to run reconcilers; this host loads them to have
///         an <c>IProviderRegistry</c> with anything in it. Stage 6 resolves a request path against
///         that registry, so a gateway with no providers routes nothing — it answers the canonical
///         <c>404</c> to every resource and every action, which is exactly what a caller gets for a
///         type that does not exist. That is what this host shipped as: it composed the resource
///         manager and registered no provider, and no test could tell, because every suite that
///         exercises the registry builds its own.
///     </para>
///     <para>
///         ⚠ <b>The module and never the implementation</b> — docs/plan/03 § Assembly graph rules,
///         rule 5. The eleven <c>using</c> lines above name <c>*.Application</c> assemblies; no type
///         from a provider implementation is bound anywhere in this host, which is what
///         <c>GatewayIsolationTests</c> reads the <c>AssemblyRef</c> table for.
///     </para>
///     <para>
///         ⚠ <b>This list and <c>SiloHostModule</c>'s must be identical.</b> Nothing in the assembly
///         graph can make that true — rule 4 lets only a host reference a <c>*.Application</c>
///         assembly, so there is no shared third place to hold one list — and a difference between
///         them is silent in both directions: a provider here and not in the silo accepts creates that
///         never converge, and one in the silo and not here reconciles resources nobody can reach.
///         <c>HostCompositionTests</c> composes both hosts and compares the registries they built.
///     </para>
///     <para>
///         <c>AbpAutofacModule</c> is still required: <c>OrleansApplication.CreateClient</c> calls
///         <c>builder.Host.UseAutofac()</c>, and ABP's service-provider factory demands an
///         <c>IModuleContainer</c> at <c>Build()</c> time.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpAutofacModule))]
[DependsOn(typeof(AnalyticsApplicationModule))]
[DependsOn(typeof(ValkeyCacheApplicationModule))]
[DependsOn(typeof(ContainerServiceApplicationModule))]
[DependsOn(typeof(MariaDbApplicationModule))]
[DependsOn(typeof(PostgresApplicationModule))]
[DependsOn(typeof(DocumentDbApplicationModule))]
[DependsOn(typeof(MessagingApplicationModule))]
[DependsOn(typeof(NetworkApplicationModule))]
[DependsOn(typeof(SampleApplicationModule))]
[DependsOn(typeof(SearchApplicationModule))]
[DependsOn(typeof(StorageApplicationModule))]
public sealed class GatewayHostModule : AbpModule;
