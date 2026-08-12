using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Messaging.Application;

/// <summary>
///     The managed-Kafka provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;MessagingProvider&gt;()</c>, which runs against
///         the silo builder before the container is built, and an ABP module's
///         <c>ConfigureServices</c> runs after.
///     </para>
///     <para>
///         ⚠ <b>The sample called this "a platform observation, not a provider quirk"; the second
///         provider called the repeat "the observation happening a second time, which is what turns
///         it into evidence"; this is the third, and it is the one the second provider predicted.</b>
///         Its note said the separate silo-builder call <i>"is the one nobody adding the third
///         provider will remember — its symptom is a namespace whose endpoints answer <c>404</c> with
///         nothing in the log"</i>. The prediction was accurate as a hazard and wrong about how it
///         would be found: <c>./build.sh Generate</c> reads provider <b>assemblies</b> rather than a
///         host's wiring, so a forgotten call shows up as a resource type missing from the generated
///         OpenAPI document — visible, and at build time. The gap is real for a running silo and the
///         generated surfaces are not the thing that closes it. Closing it means either a
///         module-collection seam on <c>ISiloBuilder</c> or a registry built lazily from the
///         container; both are changes to <c>CyberCloud.ResourceManager</c>'s wiring and neither
///         belongs here.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class MessagingApplicationModule : AbpModule;
