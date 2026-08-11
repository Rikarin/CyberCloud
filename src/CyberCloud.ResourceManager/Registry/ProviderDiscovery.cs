using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;
using System.Reflection;

namespace CyberCloud.ResourceManager.Registry;

/// <summary>
///     Finds the <see cref="IResourceProvider" /> implementations in an assembly, for the build step
///     that has no silo to read them out of.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The silo does not use this, and it must not start.</b>
///         <c>ResourceManagerSiloBuilderExtensions.AddCyberCloudProvider&lt;TProvider&gt;</c> is how a
///         host registers a provider, and naming each one is the point: a silo whose API surface
///         depended on which assemblies happened to be next to it on disk would answer <c>404</c> for
///         a provider somebody forgot to copy, with nothing in the log. This exists for ADR-012's
///         generation step, which has the opposite problem — it is handed a list of assemblies and no
///         container.
///     </para>
///     <para>
///         ⚠ <b>A provider must have a public parameterless constructor</b>, which is the same
///         constraint <c>AddCyberCloudProvider&lt;TProvider&gt;</c> already spells
///         <c>where TProvider : class, IResourceProvider, new()</c>. A provider that needed an
///         injected dependency could not be described at build time, and
///         <see cref="IResourceProvider.Describe" />'s remarks already forbid one: <i>"A Describe that
///         consulted configuration, a clock or a database would make the platform's API surface depend
///         on when the silo happened to start — and the generated CLI, which runs Describe in a build
///         step with none of those available, would describe a different platform than the one
///         running."</i> This is that build step.
///     </para>
/// </remarks>
public static class ProviderDiscovery {
    /// <summary>Instantiates every provider an assembly declares.</summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>One instance per declared provider, ordered by full type name.</returns>
    /// <exception cref="InvalidOperationException">
    ///     A provider has no public parameterless constructor. It fails rather than being skipped: a
    ///     provider silently missing from the generated OpenAPI document is the same symptom as a
    ///     provider that was never written, and only one of the two has anybody looking for it.
    /// </exception>
    public static ImmutableArray<IResourceProvider> FromAssembly(Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        // ⚠ Exported types only. A provider a host cannot name is a provider a host cannot pass to
        // AddCyberCloudProvider<TProvider>, so an internal one is not a provider this platform can
        // serve — and generating a document for endpoints no silo will route would be worse than
        // missing it.
        return FromTypes(assembly.GetExportedTypes());
    }

    /// <summary>Instantiates every provider among a set of types.</summary>
    /// <param name="types">The candidates. Non-providers are ignored.</param>
    /// <returns>One instance per provider, ordered by full type name.</returns>
    /// <exception cref="InvalidOperationException">A provider has no public parameterless constructor.</exception>
    public static ImmutableArray<IResourceProvider> FromTypes(IEnumerable<Type> types) {
        ArgumentNullException.ThrowIfNull(types);

        var found = new List<IResourceProvider>();

        var candidates = types
            .Where(x => x is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .Where(x => typeof(IResourceProvider).IsAssignableFrom(x))
            .OrderBy(x => x.FullName, StringComparer.Ordinal);

        foreach (var candidate in candidates) {
            if (candidate.GetConstructor(Type.EmptyTypes) is null) {
                throw new InvalidOperationException(
                    $"'{candidate.FullName}' implements IResourceProvider and has no public parameterless "
                    + "constructor, so the generation step cannot describe it. Describe must be pure — "
                    + "see the remarks on IResourceProvider — so a provider that needs construction "
                    + "arguments is a provider whose API surface depends on how it was constructed."
                );
            }

            found.Add((IResourceProvider)Activator.CreateInstance(candidate)!);
        }

        return [.. found];
    }

    /// <summary>
    ///     Loads each assembly by path and returns every provider across all of them.
    /// </summary>
    /// <param name="assemblyPaths">Paths to built provider assemblies.</param>
    /// <returns>Every provider, ordered by assembly path and then by type name.</returns>
    /// <remarks>
    ///     ⚠ <see cref="Assembly.LoadFrom(string)" /> rather than a private
    ///     <c>AssemblyLoadContext</c>: this process already references
    ///     <c>CyberCloud.ResourceManager</c>, so a provider loaded into a private context would see a
    ///     <i>second</i> <see cref="IResourceProvider" /> and the cast below would fail with the
    ///     famously unhelpful "unable to cast X to X". The default context is the only one where the
    ///     provider's interface and ours are the same type.
    /// </remarks>
    public static ImmutableArray<IResourceProvider> FromAssemblies(IEnumerable<string> assemblyPaths) {
        ArgumentNullException.ThrowIfNull(assemblyPaths);

        var found = new List<IResourceProvider>();

        foreach (var path in assemblyPaths.OrderBy(x => x, StringComparer.Ordinal)) {
            found.AddRange(FromAssembly(Assembly.LoadFrom(Path.GetFullPath(path))));
        }

        return [.. found];
    }
}
