using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     One resource type's contribution to the <c>cyc</c> verb tree's token namespace.
/// </summary>
/// <param name="ProviderNamespace">The provider namespace — <c>CyberCloud.Network</c>.</param>
/// <param name="TypePath">The type path within the provider — <c>virtualNetworks/subnets</c>.</param>
/// <param name="ShortName">
///     The declared short name, or empty for a type with none. <c>DisplayMetadata.Alias</c>.
/// </param>
public readonly record struct CliDeclaration(string ProviderNamespace, string TypePath, string ShortName) {
    /// <summary>The fully qualified type, for a message a reader can act on.</summary>
    public string ResourceType => ProviderNamespace + "/" + TypePath;
}

/// <summary>
///     The tokens the <c>cyc</c> verb tree is addressed by, and the one check that they do not
///     collide.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The failure this exists to refuse is not local to the colliding command.</b>
///         <c>System.CommandLine</c> builds one token dictionary per command out of that command's own
///         name and aliases plus every child's name and aliases, and a duplicate throws
///         <c>ArgumentException: An item with the same key has already been added</c> — naming neither
///         the provider nor the surface, on every <c>cyc</c> invocation that reaches the group.
///         Measured against <c>System.CommandLine</c> 2.0.10, which is the version
///         <c>Directory.Packages.props</c> pins.
///     </para>
///     <para>
///         ⚠ <b>The scope is the parent command, and getting that wrong is what made the previous
///         defence check the wrong question.</b> Measured, same version: a short name equal to a
///         <i>different</i> group's key parses cleanly, because the two live under different parents;
///         a short name equal to its <i>own</i> group's key throws, because the group command's
///         dictionary holds its own name. So "this short name is none of the fourteen group keys" was
///         both too strong — it forbids thirteen strings that cannot collide — and too weak: it never
///         compared a short name against the command names and short names it actually shares a parent
///         with. This compares what shares a parent, and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Derived from what is registered, never from a list.</b> The two lists this replaces
///         went stale twice in consecutive passes and were green by luck both times — see
///         <c>src/Providers/README.md</c>. A list is maintained by whoever remembers it exists; this is
///         maintained by the registry. docs/plan/21 § Grammar.
///     </para>
/// </remarks>
public static class CliTokens {
    /// <summary>
    ///     The top-level CLI group a provider namespace produces — the last segment, lower-cased.
    /// </summary>
    /// <param name="providerNamespace">The provider namespace, for example <c>CyberCloud.Network</c>.</param>
    /// <returns>The group key, for example <c>network</c>.</returns>
    /// <remarks>
    ///     ⚠ Lower-cased whole rather than kebab-cased. A namespace segment is a proper noun with its
    ///     own capitalisation — <c>DBforPostgreSQL</c> — and kebab-casing it on case transitions
    ///     produces <c>dbfor-postgre-sql</c>, which is not a word anybody would type.
    /// </remarks>
    public static string GroupOf(string providerNamespace) {
        ArgumentNullException.ThrowIfNull(providerNamespace);

        var segments = providerNamespace.Split('.');

        return segments[^1].ToLowerInvariant();
    }

    /// <summary>The command name within a group — the type path, kebab-cased, <c>/</c> to <c>-</c>.</summary>
    /// <param name="typePath">The type path, for example <c>virtualNetworks/subnets</c>.</param>
    /// <returns>The command name, for example <c>virtual-networks-subnets</c>.</returns>
    public static string CommandOf(string typePath) {
        ArgumentNullException.ThrowIfNull(typePath);

        return string.Join('-', typePath.Split('/').Select(CliEmitter.Kebab));
    }

    /// <summary>
    ///     Every way the declarations handed in would give one <c>cyc</c> token two meanings.
    /// </summary>
    /// <param name="declarations">
    ///     What is declared. ⚠ The answer is only as complete as the input: a set holding one provider
    ///     finds that provider's own collisions and cannot find a cross-provider one, which is why the
    ///     whole-tree callers are <c>ProviderRegistry.Build</c> at silo start and
    ///     <see cref="CliEmitter.Emit" /> at generation.
    /// </param>
    /// <returns>One sentence per collision, ordered, empty when there is none.</returns>
    /// <remarks>
    ///     ⚠ <b>Returns rather than throws</b>, because the two callers want different consequences:
    ///     the registry refuses the process, the derived-surface self-check reports a row. The same
    ///     split <c>DerivedSurfaces</c> already draws between facts and verdicts.
    /// </remarks>
    public static ImmutableArray<string> Collisions(IEnumerable<CliDeclaration> declarations) {
        ArgumentNullException.ThrowIfNull(declarations);

        var problems = new List<string>();
        var scopes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // ⚠ Ordered, so which of a colliding pair is reported as "already" does not depend on the
        // order providers happened to be discovered in. A message that changes between runs is a
        // message somebody stops trusting.
        foreach (var declaration in declarations.OrderBy(x => x.ResourceType, StringComparer.Ordinal)) {
            var group = GroupOf(declaration.ProviderNamespace);

            if (!scopes.TryGetValue(group, out var taken)) {
                // ⚠ Seeded with the group's own key. The group command's token dictionary holds its
                // own name, so a short name equal to it collides — `cyc network network` throws where
                // `cyc monitor network` does not.
                taken = new Dictionary<string, string>(StringComparer.Ordinal) {
                    [group] = $"the group key '{group}' produces from the namespace "
                        + $"'{declaration.ProviderNamespace}'"
                };

                scopes[group] = taken;
            }

            Take(
                taken,
                group,
                CommandOf(declaration.TypePath),
                $"the command name of '{declaration.ResourceType}'",
                problems
            );

            if (declaration.ShortName.Length > 0) {
                Take(
                    taken,
                    group,
                    declaration.ShortName,
                    $"the short name of '{declaration.ResourceType}'",
                    problems
                );
            }
        }

        problems.Sort(StringComparer.Ordinal);
        return [.. problems];
    }

    static void Take(
        Dictionary<string, string> taken,
        string group,
        string token,
        string owner,
        List<string> problems
    ) {
        if (taken.TryGetValue(token, out var existing)) {
            problems.Add(
                $"'cyc {group} {token}' would mean two things: it is {existing} and it is {owner}. "
                + "System.CommandLine keeps one token dictionary per command, so the second one "
                + $"registered throws 'An item with the same key has already been added. Key: {token}' "
                + $"on every cyc invocation that reaches the '{group}' group — naming neither the "
                + "provider nor the string. Change one of the two: a shortName on "
                + "IResourceTypeBuilder.Display, or the type path — docs/plan/21 § Grammar."
            );

            return;
        }

        taken.Add(token, owner);
    }
}
