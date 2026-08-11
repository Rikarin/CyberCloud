using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace CyberCloud.Gateway.Host;

/// <summary>
///     The gateway's ABP module graph — docs/plan/03 § Hosts.
/// </summary>
/// <remarks>
///     No provider modules yet, and the gateway would not load their implementations anyway
///     (docs/plan/03 § Assembly graph rules, rule 5: the gateway references a provider's
///     <c>.Contracts</c> and <c>.Application</c>, never its implementation). It is still required:
///     <c>OrleansApplication.CreateClient</c> calls <c>builder.Host.UseAutofac()</c>, and ABP's
///     service-provider factory demands an <c>IModuleContainer</c> at <c>Build()</c> time.
/// </remarks>
[DependsOn(typeof(AbpAutofacModule))]
public sealed class GatewayHostModule : AbpModule;
