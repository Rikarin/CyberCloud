using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.Analytics.Application;

/// <summary>
///     The managed-analytics provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;AnalyticsProvider&gt;()</c>, which runs against the
///         silo builder before the container is built, and an ABP module's <c>ConfigureServices</c>
///         runs after.
///     </para>
///     <para>
///         ⚠ <b>The fifth empty <c>.Application</c> project, and the count has stopped being a
///         surprise and started being a measurement.</b> The sample's emptiness could be dismissed as
///         triviality; the second provider called its own <i>"evidence about the
///         registry-as-routing-source decision"</i>; the third said three was the number at which
///         <i>"the project's existence, rather than its emptiness, is the thing worth questioning"</i>;
///         the fifth said <i>"the seam has never been exercised by anything"</i>. This is the sixth
///         namespace, over a resource type that renders two custom resources in two API groups and
///         wires one to the other — and PUT, GET, DELETE and POST <c>listKeys</c> are still all
///         generic resource-manager verbs the gateway routes from the provider registry (ADR-012).
///         ⚠ <b>What would have made a case for the project is exactly what this type does NOT do</b>:
///         docs/plan/12 forbids it managing tables, so the one plausible per-provider application
///         service — a DDL endpoint — is out of scope by decision rather than by absence.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class AnalyticsApplicationModule : AbpModule;
