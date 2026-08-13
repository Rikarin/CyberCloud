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

namespace CyberCloud.Silo.Host;

/// <summary>
///     The silo's ABP module graph — docs/plan/03 § Hosts, "loads every provider module".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The eleven lines below are the platform's API surface, and until they existed this
///         module depended on Autofac's and nothing else.</b> Each provider module's
///         <c>ConfigureServices</c> registers its <c>IResourceProvider</c>, its reconcilers and its
///         action handlers; <c>ProviderRegistry</c> is then built from the union at first resolve. A
///         provider missing from this list is a provider that reconciles nothing, and its endpoints
///         answer the canonical <c>404</c> with nothing in the log.
///     </para>
///     <para>
///         ⚠ <b>This list and <c>GatewayHostModule</c>'s have to be identical.</b> The gateway routes
///         from its own registry, built in its own process from its own <c>[DependsOn]</c> list, so a
///         provider here and not there is a type that reconciles and cannot be reached, and a provider
///         there and not here is a type that accepts a create and never converges. Two lists is what
///         the assembly graph leaves available — nothing but a host may reference a <c>*.Application</c>
///         assembly (docs/plan/03 § Assembly graph rules, rule 4), so there is no third place to put
///         one list. <c>HostCompositionTests</c> composes both hosts and fails on a difference.
///     </para>
///     <para>
///         <c>AbpAutofacModule</c> is not optional either: <c>OrleansApplication.CreateSilo</c> calls
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
public sealed class SiloHostModule : AbpModule;
