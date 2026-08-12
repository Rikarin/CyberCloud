using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.DocumentDB.Application;

/// <summary>
///     The managed-document-database provider's ABP module — what a host <c>[DependsOn]</c> to load
///     it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;DocumentDbProvider&gt;()</c>, which runs against
///         the silo builder before the container is built, and an ABP module's
///         <c>ConfigureServices</c> runs after.
///     </para>
///     <para>
///         ⚠ <b>The fifth empty <c>.Application</c> project.</b> The count stopped being an
///         observation at three and stopped being news at four; what this one adds is that the type
///         with the <i>most</i> reason to want an application service still does not have one. A
///         document-database account has a compatibility surface a caller might reasonably ask the API
///         about — "which commands work" — and that question is answered by
///         <c>DocumentDbAccounts.UnsupportedCommands</c> reaching the generated surfaces through the
///         <b>registry</b>, not by a service the gateway routes to. So the fifth report is not "still
///         empty" but "the one candidate that came up was answered by ADR-012 instead", which is
///         evidence about where the seam should live rather than another tally mark.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class DocumentDbApplicationModule : AbpModule;
