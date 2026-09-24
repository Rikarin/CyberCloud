using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Frozen;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Registry;

/// <summary>
///     The built provider registry — the one description of the platform's API surface.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § The provider registry:
///         <i>
///             "This object is the source for the four generated surfaces (ADR-012) <b>and</b> for the
///             runtime write path — the same registry that generates the CLI is the one that validates
///             the request body. That identity is what makes drift impossible rather than merely
///             detectable."
///         </i>
///     </para>
///     <para>
///         ⚠ <b>Built once and frozen.</b> The lookup is a <see cref="FrozenDictionary{TKey,TValue}" />
///         keyed on the canonical (lower-cased) type string, because Azure treats types
///         case-insensitively and <see cref="ResourceTypeName" /> preserves the casing it was given.
///         Freezing matters because this is read on every request from every silo thread, and a
///         mutable registry would need a lock on the hottest read in the platform.
///     </para>
///     <para>
///         ⚠ <b>A provider that declares nothing is a build failure, not an empty entry.</b> The most
///         likely way to get one is a <c>Describe</c> that returned early behind a feature flag, and
///         the symptom would be a provider whose whole API surface silently vanished from the OpenAPI
///         document and from the router. Failing at silo start is louder and cheaper.
///     </para>
/// </remarks>
public sealed class ProviderRegistry : IProviderRegistry {
    readonly FrozenDictionary<string, ResourceTypeRegistration> byType;

    /// <inheritdoc />
    public ImmutableArray<ResourceTypeRegistration> Types { get; }

    /// <inheritdoc />
    public ImmutableArray<string> Namespaces { get; }

    ProviderRegistry(ImmutableArray<ResourceTypeRegistration> types, ImmutableArray<string> namespaces) {
        Types = types;
        Namespaces = namespaces;
        byType = types.ToFrozenDictionary(static x => Key(x.Type), StringComparer.Ordinal);
    }

    /// <summary>The refusal of a provider in <see cref="KubeLabels.ReservedNamespace" />.</summary>
    /// <param name="providerNamespace">The namespace, as the provider spelled it.</param>
    /// <param name="why">Which condition it failed.</param>
    static InvalidOperationException ReservedNamespaceRefusal(string providerNamespace, string why) =>
        new(
            $"Provider '{providerNamespace}' declares the reserved namespace '{KubeLabels.ReservedNamespace}'. "
            + "The platform stamps that namespace on the cluster objects it owns on a resource group's "
            + "behalf — the group's namespace — and the drift scan and the conformance suite both read it "
            + "to mean 'not attributed to a resource'. A provider that rendered objects under it would "
            + "have its output excluded from both, so the namespace admits only types that render nothing: "
            + $"no reconciler, no action handler, no cluster. {why} See KubeLabels.ReservedNamespace."
        );

    /// <summary>Builds the registry from every provider in the process.</summary>
    /// <param name="providers">
    ///     The providers, in any order. ⚠ Two providers declaring the same namespace is a build
    ///     failure: the second would silently shadow the first's types, and "my provider's endpoints
    ///     404" is a bad way to find out about a copy-pasted namespace string.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     A provider declares no types, two providers share a namespace, two types collide, a type
    ///     declares no api-version, or a reconciler's <c>Type</c> names a type its provider did not
    ///     declare.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         Throws rather than returning a <see cref="Result" /> because every one of these is a bug
    ///         in code, discovered at silo start, with nobody to hand a <see cref="Result" /> to —
    ///         docs/plan/00 § Coding standards.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             An empty provider set is <i>not</i> one of them, and the refusal it deserves lives
    ///             one layer up.
    ///         </b> A <b>host</b> with no providers is a wiring mistake whose whole
    ///         symptom is a <c>404</c> on every path — see
    ///         <c>ResourceManagerSiloBuilderExtensions.AddCyberCloudResourceManager</c>, which is where
    ///         that is refused. A <b>build step</b> with no providers is an ordinary state:
    ///         <c>Build.Generate</c> runs the generator over whatever assemblies the solution has, and
    ///         its own vacuity report depends on telling "no assembly was handed to me" from "an
    ///         assembly was handed to me and declared nothing" — a distinction that needs both runs to
    ///         succeed. Putting the check here made
    ///         <c>CyberCloud.ResourceManager.Generator.Tests.GenerationReportTests</c> fail, which is
    ///         the tripwire that exists because a discovery predicate once found no providers and the
    ///         target reported success.
    ///     </para>
    /// </remarks>
    public static ProviderRegistry Build(IEnumerable<IResourceProvider> providers) {
        ArgumentNullException.ThrowIfNull(providers);

        var types = new List<ResourceTypeRegistration>();
        var namespaces = new List<string>();
        var seenNamespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var provider in providers) {
            ArgumentNullException.ThrowIfNull(provider);

            // ⚠ RESERVED, AND THE RESERVATION IS LOAD-BEARING RATHER THAN TIDY. A namespace object
            // carries `cybercloud.io/resource-type = cybercloud.resources_resourcegroups`, and three
            // components read that label to mean "this object belongs to a resource GROUP, so its
            // resource-id is a derived GUID and must not be compared to a resource grain" —
            // DriftScanner's orphan join, ProviderConformanceTests' labels assertion, and any future
            // cluster inventory. A provider declaring this namespace would render objects that all
            // three would then decline to check, which is a way for a reconciler to opt its output
            // out of orphan detection and out of the labels gate at once. The type label itself is
            // one of ADR-013's seven and cannot be set by a caller; this closes the other door.
            //
            // ⚠ NARROWED FOR #39, AND THE NARROWING KEEPS THE PROPERTY RATHER THAN SPENDING IT. The
            // platform's own CyberCloud.Resources/deployments lives here, as Azure's deployments live
            // in Microsoft.Resources. What the reservation protects is that no object a provider
            // RENDERS can carry a group-scoped label, so the namespace now admits a provider whose
            // every type renders nothing — no reconciler, no action handler, no cluster, and not the
            // group type itself — and refuses anything else exactly as before. A type that renders
            // nothing emits no label at all, so there is nothing for the drift scan or the labels gate
            // to decline to check. See ReservedNamespaceRefusal.
            var reserved = string.Equals(
                provider.ProviderNamespace,
                KubeLabels.ReservedNamespace,
                StringComparison.OrdinalIgnoreCase
            );

            // ⚠ RESERVED TOO, AND FOR A ROUTING REASON RATHER THAN A LABELLING ONE. A role assignment
            // is addressed as {scope}/providers/CyberCloud.Authorization/roleAssignments/{name}
            // (RoleAssignmentId), and on a resource group that address is a well-formed resource id
            // of type CyberCloud.Authorization/roleAssignments. The gateway tries the assignment
            // grammar first, so a provider that registered this namespace would have every one of
            // its types shadowed by an address the router had already claimed — and the symptom
            // would be a 400 naming a role assignment for a PUT that never mentioned one.
            if (string.Equals(
                    provider.ProviderNamespace,
                    RoleAssignmentId.ProviderNamespace,
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new InvalidOperationException(
                    $"Provider '{provider.ProviderNamespace}' declares the reserved namespace "
                    + $"'{RoleAssignmentId.ProviderNamespace}'. Every address under it is a role "
                    + "assignment — docs/plan/07 § Azure RBAC, expressed in it — and the gateway "
                    + "routes those before it looks at the registry, so no type this provider "
                    + "declared could ever be reached. See RoleAssignmentId.ProviderNamespace."
                );
            }

            // ⚠ THE THIRD RESERVATION, FOR THE SECOND ROUTING REASON (#54). The resource graph is
            // queried at /tenants/{t}/providers/CyberCloud.ResourceGraph/resources
            // (ResourceGraphAddress), and the gateway routes everything under that namespace to the
            // query service before it looks at the registry. A provider that registered it would
            // have every one of its types answered as "not the resource graph's address" — a 400
            // naming a query endpoint for a PUT that never mentioned one.
            if (string.Equals(
                    provider.ProviderNamespace,
                    ResourceGraphAddress.ProviderNamespace,
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new InvalidOperationException(
                    $"Provider '{provider.ProviderNamespace}' declares the reserved namespace "
                    + $"'{ResourceGraphAddress.ProviderNamespace}'. The one address under it is the "
                    + "resource graph's query endpoint — docs/plan/08 § The resource-graph projection — "
                    + "and the gateway routes it before it looks at the registry, so no type this "
                    + "provider declared could ever be reached. See ResourceGraphAddress.ProviderNamespace."
                );
            }

            // ⚠ THE FOURTH RESERVATION (#43, widened by #41), FOR THE RESOURCE GRAPH'S REASON: the
            // gateway routes everything under /providers/CyberCloud.Identity/ to the identity
            // administration API before it looks at the registry (IdentityAddress), so a provider
            // that registered the namespace would have every type it declared answered as "not an
            // identity address".
            if (string.Equals(
                    provider.ProviderNamespace,
                    IdentityAddress.ProviderNamespace,
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new InvalidOperationException(
                    $"Provider '{provider.ProviderNamespace}' declares the reserved namespace "
                    + $"'{IdentityAddress.ProviderNamespace}'. The addresses under it are the tenant's "
                    + "directory — invitations, members, applications and the caller's sessions, "
                    + "docs/plan/11 § The object model — and the gateway routes them before it looks at "
                    + "the registry, so no type this provider declared could ever be reached. See "
                    + "IdentityAddress.ProviderNamespace."
                );
            }

            // ⚠ RESERVED FOR THE COST QUERY, FOR THE RESOURCE GRAPH'S ROUTING REASON (#38). It is served at
            // {scope}/providers/CyberCloud.CostManagement/query (CostQueryAddress), which on a
            // resource group is a nine-segment resource collection path, and the gateway routes the
            // whole namespace to the cost query — or, on a tenant, to its invoices (InvoiceAddress,
            // #41) — before it looks at the registry.
            if (string.Equals(
                    provider.ProviderNamespace,
                    CostQueryAddress.ProviderNamespace,
                    StringComparison.OrdinalIgnoreCase
                )) {
                throw new InvalidOperationException(
                    $"Provider '{provider.ProviderNamespace}' declares the reserved namespace "
                    + $"'{CostQueryAddress.ProviderNamespace}'. The addresses under it are the cost query and "
                    + "the tenant's invoices — docs/plan/22 § Cost visibility — and the gateway routes them before it looks at the "
                    + "registry, so no type this provider declared could ever be reached. See "
                    + "CostQueryAddress.ProviderNamespace."
                );
            }

            if (!seenNamespaces.Add(provider.ProviderNamespace)) {
                throw new InvalidOperationException(
                    $"Two providers declare the namespace '{provider.ProviderNamespace}'. A namespace names "
                    + "exactly one provider; the second would shadow the first's resource types and "
                    + "the symptom would be endpoints answering 404 with nothing in the log."
                );
            }

            namespaces.Add(provider.ProviderNamespace);

            var builder = new ProviderBuilder(provider.ProviderNamespace);
            provider.Describe(builder);

            ImmutableArray<ResourceTypeRegistration> declared;

            try {
                declared = builder.Build();
            } catch (InvalidOperationException incomplete) when (reserved) {
                throw ReservedNamespaceRefusal(provider.ProviderNamespace, incomplete.Message);
            }

            if (reserved) {
                var rendering = declared.FirstOrDefault(static x => x.ReconcilerType is not null
                    || x.RequiresCluster
                    || x.Actions.Any(static a => a.HandlerType is not null)
                    || string.Equals(x.Type.Type, KubeLabels.ResourceGroupType.Type, StringComparison.OrdinalIgnoreCase)
                );

                if (declared.Length == 0 || rendering is not null) {
                    throw ReservedNamespaceRefusal(
                        provider.ProviderNamespace,
                        rendering is null
                            ? "It declares no type."
                            : $"'{rendering.Type}' declares a reconciler, an action handler or a cluster, or is the "
                            + "group type itself, so it could render an object carrying the group's label."
                    );
                }
            }
            if (declared.Length == 0) {
                throw new InvalidOperationException(
                    $"Provider '{provider.ProviderNamespace}' declared no resource types. A provider with no "
                    + "types contributes nothing to the API, the CLI or the OpenAPI document, and the "
                    + "usual cause is a Describe that returned early. It fails here rather than "
                    + "disappearing quietly — docs/plan/08 § The provider registry."
                );
            }

            foreach (var type in declared) {
                if (!seenTypes.Add(Key(type.Type))) {
                    throw new InvalidOperationException(
                        $"'{type.Type}' is declared twice. One type has one registration; a duplicate "
                        + "would make which schema validates a request depend on iteration order."
                    );
                }

                types.Add(type);
            }
        }

        // ⚠ THE CLI SURFACE IS PART OF WHAT A REGISTRATION MEANS, AND THIS IS WHERE THAT STOPS BEING
        // A COMMENT. IResourceTypeBuilder.Display's own remarks have always promised that "a duplicate
        // is a silo-start failure rather than a CLI that resolves one of two verbs" — and nothing
        // checked it, so the defence was a literal list in one provider's test suite that went stale
        // twice in consecutive passes and was green by luck both times. Derived from what is actually
        // registered, it cannot go stale: a provider added tomorrow is in `types` the moment it is
        // discovered. See CliTokens for what collides and why the scope is the parent command.
        //
        // ⚠ WHAT THIS SET CONTAINS IS WHAT THIS CHECK COVERS. A silo and the generator build every
        // provider in the process, so both see cross-provider collisions; a provider's own suite
        // builds one provider and sees only its own. src/Providers/README.md § Hard rule forbids the
        // reference that would let a provider's suite see more, which is why the whole-tree answer
        // lives here and in CliEmitter.Emit rather than in fourteen test files.
        var collisions = CliTokens.Collisions(
            types.Select(static x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        );

        if (collisions.Length > 0) {
            throw new InvalidOperationException(string.Join(" ", collisions));
        }

        return new(
            [.. types.OrderBy(static x => Key(x.Type), StringComparer.Ordinal)],
            [.. namespaces]
        );
    }

    /// <inheritdoc />
    public bool TryGetType(ResourceTypeName type, out ResourceTypeRegistration registration) {
        if (type.IsEmpty) {
            registration = null!;
            return false;
        }

        return byType.TryGetValue(Key(type), out registration!);
    }

    /// <inheritdoc />
    public Result<TypeResolution> Resolve(ResourceTypeName type, string? apiVersion) {
        if (!TryGetType(type, out var registration)) {
            return Result<TypeResolution>.Failure(
                ErrorCode.InvalidResourceType,
                $"'{type}' is not a resource type this platform serves. The registered namespaces are "
                + $"[{string.Join(", ", Namespaces)}]."
            );
        }

        var version = ApiVersion.Create(apiVersion);
        if (version.TryGetError(out var versionError)) {
            return Result<TypeResolution>.Failure(versionError);
        }

        var parsed = version.GetValueOrThrow();
        var schema = registration.SchemaFor(parsed);
        return schema.TryGetError(out var schemaError)
            ? Result<TypeResolution>.Failure(schemaError)
            : Result<TypeResolution>.Success(new(registration, parsed, schema.GetValueOrThrow()));
    }

    /// <summary>
    ///     The lookup key: the canonical, lower-cased <c>{namespace}/{type}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Canonicalised through <see cref="ResourceTypeName.Canonical" /> rather than through
    ///     <c>ToLowerInvariant</c>, for the reason that type's remarks give: invariant lower-casing
    ///     folds U+212A KELVIN SIGN onto <c>k</c>, and a lookup key that maps two distinct code points
    ///     onto one is how two types come to share a registration.
    /// </remarks>
    static string Key(ResourceTypeName type) => type.Canonical.ToString();
}
