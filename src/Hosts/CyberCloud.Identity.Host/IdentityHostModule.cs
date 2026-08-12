using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace CyberCloud.Identity.Host;

/// <summary>
///     The identity host's ABP module graph — docs/plan/03 § Hosts.
/// </summary>
/// <remarks>
///     Empty, and required anyway. <c>OrleansApplication.CreateClient</c> calls
///     <c>builder.Host.UseAutofac()</c>, and ABP's service-provider factory resolves
///     <c>IModuleContainer</c> during <c>Build()</c> — with no module registered the host dies with
///     "Could not find singleton service: Volo.Abp.Modularity.IModuleContainer", a message that names
///     neither <c>UseAutofac</c> nor the missing call. <c>OrleansApplication</c>'s own remarks spell
///     this out; <c>GatewayHostModule</c> is the same three lines at the gateway for the same reason.
///     <para>
///         ⚠ No provider modules, and none belong here. docs/plan/03 § Assembly graph rules keeps a
///         provider's implementation out of every host but the silo, and this host in particular
///         serves one thing — the OIDC surface — over a credential the gateway must never accept.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpAutofacModule))]
public sealed class IdentityHostModule : AbpModule;
