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
        byType = types.ToFrozenDictionary(x => Key(x.Type), StringComparer.Ordinal);
    }

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
    ///         ⚠ <b>An empty provider set is <i>not</i> one of them, and the refusal it deserves lives
    ///         one layer up.</b> A <b>host</b> with no providers is a wiring mistake whose whole
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
            if (string.Equals(provider.ProviderNamespace, KubeLabels.ReservedNamespace, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    $"Provider '{provider.ProviderNamespace}' declares the reserved namespace "
                    + $"'{KubeLabels.ReservedNamespace}'. The platform stamps that namespace on the "
                    + "cluster objects it owns on a resource group's behalf — the group's namespace — "
                    + "and the drift scan and the conformance suite both read it to mean 'not "
                    + "attributed to a resource'. A provider that rendered objects under it would "
                    + "have its output excluded from both. See KubeLabels.ReservedNamespace."
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

            var declared = builder.Build();
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

        return new(
            [.. types.OrderBy(x => Key(x.Type), StringComparer.Ordinal)],
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
