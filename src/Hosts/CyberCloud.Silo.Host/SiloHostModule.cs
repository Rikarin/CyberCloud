using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace CyberCloud.Silo.Host;

/// <summary>
///     The silo's ABP module graph — docs/plan/03 § Hosts, "loads every provider module".
/// </summary>
/// <remarks>
///     <para>
///         There are no provider modules yet, so this depends on Autofac's module and nothing else.
///         It is not optional even so: <c>OrleansApplication.CreateSilo</c> calls
///         <c>builder.Host.UseAutofac()</c>, and ABP's service-provider factory demands an
///         <c>IModuleContainer</c> at <c>Build()</c> time. Its remarks spell out the exact
///         <c>InvalidOperationException</c> that a host with no module dies with.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpAutofacModule))]
public sealed class SiloHostModule : AbpModule;
