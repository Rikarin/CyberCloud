using Volo.Abp.Application;
using Volo.Abp.Modularity;

namespace CyberCloud.Providers.ContainerService.Application;

/// <summary>
///     The managed-Kubernetes provider's ABP module — what a host <c>[DependsOn]</c> to load it.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/03 § Providers: <i>"Each is an ABP module (<c>[DependsOn]</c>), each registers
///         its resource types into <c>CyberCloud.ResourceManager</c>."</i> The second half is not done
///         here: registering a provider is
///         <c>ISiloBuilder.AddCyberCloudProvider&lt;ContainerServiceProvider&gt;()</c>, which runs
///         against the silo builder before the container is built, and an ABP module's
///         <c>ConfigureServices</c> runs after.
///     </para>
///     <para>
///         ⚠ <b>The tenth empty <c>.Application</c> project, and this is the family that was expected
///         to be the exception.</b> Nine namespaces had reported the emptiness with the same sentence
///         and the same caveat — that each of them was a <i>data service</i>, whose whole surface is
///         PUT, GET, DELETE and one <c>POST</c>. This one is not a data service. It has an upgrade
///         ordering rule, a version-skew constraint, a child type that resizes a running cluster, and
///         an action that hands back a credential. Every one of those is still a generic
///         resource-manager verb the gateway routes from the provider registry (ADR-012): the upgrade
///         rule is schema validation plus reconciler ordering, the skew constraint is a property of a
///         body, the child is a second registration, and the action is a declaration. ⚠ <b>So the
///         count is now evidence about the SEAM rather than about the services</b>: ten namespaces,
///         one of them structurally unlike the other nine, and no application service between them.
///     </para>
/// </remarks>
[DependsOn(typeof(AbpDddApplicationModule))]
public sealed class ContainerServiceApplicationModule : AbpModule;
