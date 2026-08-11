using CyberCloud.Core.Resources;
using System.Globalization;

namespace CyberCloud.Authorization;

/// <summary>
///     The entry point docs/plan/07 § The model writes its example against.
/// </summary>
public static class Schema {
    /// <summary>Starts a schema and defines its first type.</summary>
    /// <param name="type">The object type name.</param>
    public static SchemaTypeBuilder DefineType(string type) => new SchemaBuilder().DefineType(type);

    /// <summary>Starts an empty schema at version 1.</summary>
    public static SchemaBuilder Create() => new();

    /// <summary>Starts an empty schema at a given version.</summary>
    /// <param name="version">The schema version — a component of the check cache key.</param>
    public static SchemaBuilder Create(int version) => new SchemaBuilder().WithVersion(version);
}

/// <summary>
///     Builds an <see cref="AuthorizationSchema" /> and <b>refuses to build an unsafe one</b>.
/// </summary>
/// <remarks>
///     <para>
///         <b>The rules, and which document sentence each one is.</b> Every rule below produces a
///         problem string naming the offending member; <see cref="Build" /> throws with all of them
///         and <see cref="Validate" /> returns them.
///     </para>
///     <list type="number">
///         <item>
///             <b>Names.</b> Type, relation and permission names satisfy
///             <see cref="RelationNaming" />; a name is not declared twice on a type; a relation and
///             a permission do not share a name. A duplicate is not a merge — it is two authors
///             believing different things about one word.
///         </item>
///         <item>
///             <b><c>This</c> only in a relation.</b> A permission has no tuples of its own; tuples
///             are written against relations. <c>Permission("read", This)</c> would silently mean
///             "nobody", which is docs/plan/07 § The model's "silent allow-nothing" in a different
///             costume.
///         </item>
///         <item>
///             <b><c>Rel(x)</c> resolves.</b> <c>x</c> must be declared on the same type. This is
///             the typo gate the document credits an analyzer with, applied where it actually works.
///         </item>
///         <item>
///             <b><c>From(t, c)</c>: <c>t</c> is a direct relation on this type.</b> A tupleset is
///             read as tuples — "the object I point to via <c>t</c>" is only meaningful if <c>t</c>
///             is written, not computed. A computed tupleset would make the walk follow objects
///             nobody ever wrote a tuple for.
///         </item>
///         <item>
///             <b><c>From(t, c)</c>: <c>c</c> is declared on at least one type.</b> The target type
///             is only known at check time (it comes from the tuple), so this is as far as static
///             resolution goes — but a <c>c</c> that no type declares is unambiguously a typo.
///         </item>
///         <item>
///             <b>⚠ <c>!</c> only in a permission.</b> A relation is reachable from another object
///             through <c>From(…)</c>, so a negation inside one is a negation that is not at any
///             top level.
///         </item>
///         <item>
///             <b>⚠ <c>!</c> only at the top level.</b> The permission's root must be an
///             intersection whose <i>direct children</i> are the negations; a negation nested any
///             deeper is rejected. <c>Rel("owner") &amp; !Rel("suspended")</c> is legal;
///             <c>Rel("owner") | (Rel("x") &amp; !Rel("y"))</c> is not.
///         </item>
///         <item>
///             <b>⚠ <c>!</c> only over <c>Rel(name)</c>.</b> <c>!From(…)</c>, <c>!This</c>,
///             <c>!(a | b)</c> and <c>!!a</c> are all rejected — docs/plan/07 § Caching across
///             requests names the first of these explicitly.
///         </item>
///         <item>
///             <b>
///                 ⚠ <c>!Rel(name)</c>: <c>name</c> is computed from direct tuples on
///                 the same object.
///             </b>
///             The document's exact restriction. It is what keeps invalidation
///             to "the same object changed".
///         </item>
///         <item>
///             <b>⚠ A negation must be intersected with something positive.</b> A permission that is
///             only <c>!Rel("suspended")</c> grants to every subject in the universe that is not
///             suspended, which is an allow-everything with a negative sign in front of it.
///         </item>
///         <item>
///             <b>⚠ Nothing a relation or permission <i>references</i> may contain a negation.</b>
///             <c>Rel(p)</c> and <c>From(_, p)</c> where <c>p</c> is a permission with a <c>!</c>
///             would move that <c>!</c> out of any top level. Only a permission's own root may
///             carry one.
///         </item>
///     </list>
///     <para>
///         <b>Why all of this is a build failure rather than a comment.</b> docs/plan/07 § Caching
///         across requests:
///         <i>
///             "Negative relations break monotonic caching and this is the subtlest
///             thing in the document … The rule, enforced by the schema builder."
///         </i>
///         If it were merely
///         documented, adding a tuple could <i>remove</i> access from a cached path, and the cache
///         would be wrong in a way no later test would catch — because every test would be written
///         against a schema somebody believed was legal.
///     </para>
///     <para>
///         <b>What is deliberately NOT a rule: cycles.</b> <c>a → b → a</c> across types, or a
///         <c>parent</c> chain that loops, builds fine. docs/plan/07 § Check:
///         <i>
///             "Cycles are broken
///             by the memo, not by cycle detection."
///         </i>
///         Rejecting them here would be a second,
///         redundant, and less complete mechanism — a cycle can be formed by <b>tuples</b> at
///         runtime, which no schema check can see.
///     </para>
/// </remarks>
public sealed class SchemaBuilder {
    readonly Dictionary<string, List<SchemaMember>> byType = new(StringComparer.Ordinal);
    readonly List<string> typeOrder = [];
    readonly List<string> problems = [];

    int version = 1;

    /// <summary>Sets the schema version.</summary>
    /// <param name="schemaVersion">The version — a component of the check cache key.</param>
    public SchemaBuilder WithVersion(int schemaVersion) {
        version = schemaVersion;
        return this;
    }

    /// <summary>Begins (or reopens) an object type.</summary>
    /// <param name="type">The object type name.</param>
    public SchemaTypeBuilder DefineType(string type) {
        var valid = RelationNaming.ValidateName(type, "object type");
        if (valid.TryGetError(out var error)) {
            problems.Add(error.Message);
        } else if (!byType.ContainsKey(type)) {
            byType[type] = [];
            typeOrder.Add(type);
        }

        return new(this, type);
    }

    /// <summary>
    ///     Every rule violation, in the order found. Empty means <see cref="Build" /> will succeed.
    /// </summary>
    public IReadOnlyList<string> Validate() => [.. problems, .. CheckRules()];

    /// <summary>Builds the schema.</summary>
    /// <exception cref="SchemaDefinitionException">The schema breaks one or more rules.</exception>
    public AuthorizationSchema Build() {
        var found = Validate();
        if (found.Count > 0) {
            throw new SchemaDefinitionException(
                "The authorization schema is not valid and was not built. "
                + found.Count.ToString(CultureInfo.InvariantCulture)
                + " problem(s) — see docs/plan/07 § The model and § Caching across requests:",
                found
            );
        }

        return new(
            version,
            typeOrder.Select(type => new SchemaType(type, byType[type]))
        );
    }

    static string Kind(SchemaMember member) => member.IsPermission ? "permission" : "relation";

    IEnumerable<string> CheckRules() {
        if (byType.Count == 0) {
            yield return "The schema defines no object types. An empty schema denies everything, "
                + "which is safe and is almost certainly a wiring mistake rather than an intent.";
            yield break;
        }

        // Names declared anywhere, for the From(_, computed) resolution that cannot know the type.
        var namesAnywhere = byType.Values
            .SelectMany(x => x)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Names that carry a negation, anywhere. Rule 11's lookup table.
        var negatingNames = byType.Values
            .SelectMany(x => x)
            .Where(x => x.ContainsNegation)
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var type in typeOrder) {
            var members = byType[type];
            var byName = members.ToDictionary(x => x.Name, StringComparer.Ordinal);

            foreach (var member in members) {
                foreach (var problem in CheckMember(type, member, byName, namesAnywhere, negatingNames)) {
                    yield return problem;
                }
            }
        }
    }

    static IEnumerable<string> CheckMember(
        string type,
        SchemaMember member,
        Dictionary<string, SchemaMember> byName,
        HashSet<string> namesAnywhere,
        HashSet<string> negatingNames
    ) {
        var where = $"{type}#{member.Name}";

        foreach (var node in member.Expression.DescendantsAndSelf()) {
            switch (node) {
                // Rule 2 — This only in a relation.
                case ThisExpression when member.IsPermission:
                    yield return
                        $"{where} is a permission and its rewrite contains `This`. Tuples are "
                        + "written against relations, never against permissions, so `This` in a "
                        + "permission can only ever be false — a silent allow-nothing "
                        + "(docs/plan/07 § The model). Declare a relation and write "
                        + $"`Permission(\"{member.Name}\", Rel(\"…\"))`.";
                    break;

                // Rules 3 and 11 — Rel(x) resolves on this type, and does not carry a negation.
                case RelationRefExpression reference:
                    if (!byName.TryGetValue(reference.Relation, out var target)) {
                        yield return
                            $"{where} references `Rel(\"{reference.Relation}\")` and '{type}' "
                            + $"declares no '{reference.Relation}'. It declares "
                            + $"[{Join(byName.Keys)}]. This is the typo docs/plan/07 § The model "
                            + "credits an analyzer with catching; it is caught here instead.";
                    } else if (target.ContainsNegation) {
                        yield return
                            $"{where} references `Rel(\"{reference.Relation}\")`, which is a "
                            + "permission containing `!`. Referencing it would move that `!` below "
                            + "a top level and break the invalidation rule of docs/plan/07 "
                            + "§ Caching across requests. Only a permission's own root may carry a "
                            + "negation.";
                    }

                    break;

                // Rules 4, 5 and 11 — From(t, c).
                case TuplesetExpression tupleset:
                    foreach (var problem in CheckTupleset(
                                 where,
                                 type,
                                 tupleset,
                                 byName,
                                 namesAnywhere,
                                 negatingNames
                             )) {
                        yield return problem;
                    }

                    break;

                // Rules 6, 8 and 9 — where a negation may be and what it may be over.
                case ExclusionExpression exclusion:
                    foreach (var problem in CheckExclusion(where, member, exclusion, byName)) {
                        yield return problem;
                    }

                    break;
            }
        }

        // Rules 7 and 10 — the shape of a permission that carries a negation.
        if (member.ContainsNegation) {
            foreach (var problem in CheckNegationIsTopLevel(where, member)) {
                yield return problem;
            }
        }
    }

    static IEnumerable<string> CheckTupleset(
        string where,
        string type,
        TuplesetExpression tupleset,
        Dictionary<string, SchemaMember> byName,
        HashSet<string> namesAnywhere,
        HashSet<string> negatingNames
    ) {
        if (!byName.TryGetValue(tupleset.Tupleset, out var pointer)) {
            yield return
                $"{where} uses `From(\"{tupleset.Tupleset}\", …)` and '{type}' declares no "
                + $"'{tupleset.Tupleset}'. It declares [{Join(byName.Keys)}].";
        } else if (pointer.IsPermission) {
            yield return
                $"{where} uses `From(\"{tupleset.Tupleset}\", …)` and '{tupleset.Tupleset}' is a "
                + "permission. A tupleset is read as tuples — \"the object I point to via x\" only "
                + "means something if x is written, not computed.";
        } else if (!pointer.IsDirectOnly) {
            yield return
                $"{where} uses `From(\"{tupleset.Tupleset}\", …)` and '{tupleset.Tupleset}' is "
                + $"computed ({pointer.Expression}) rather than written. A tupleset must be a "
                + "direct relation, or the walk follows objects that no tuple points at.";
        }

        if (!namesAnywhere.Contains(tupleset.Computed)) {
            yield return
                $"{where} uses `From(…, \"{tupleset.Computed}\")` and no object type in the schema "
                + $"declares '{tupleset.Computed}'. The target type is only known at check time, so "
                + "this is as far as static resolution goes — but a name no type declares is a typo.";
        } else if (negatingNames.Contains(tupleset.Computed)) {
            yield return
                $"{where} uses `From(…, \"{tupleset.Computed}\")` and '{tupleset.Computed}' is a "
                + "permission containing `!`. Evaluating it from another object puts that `!` below "
                + "a top level, which is the case docs/plan/07 § Caching across requests forbids.";
        }
    }

    static IEnumerable<string> CheckExclusion(
        string where,
        SchemaMember member,
        ExclusionExpression exclusion,
        Dictionary<string, SchemaMember> byName
    ) {
        // Rule 6 — never inside a relation.
        if (!member.IsPermission) {
            yield return
                $"{where} is a relation and its rewrite contains `!`. Negation is legal only in a "
                + "permission (docs/plan/07 § Caching across requests): a relation is reachable "
                + "from another object through `From(…)`, so a `!` inside one is a `!` that is not "
                + "at any top level, and the check cache would be wrong in the way that section "
                + "describes.";
            yield break;
        }

        // Rule 8 — only over Rel(name).
        if (exclusion.Operand is not RelationRefExpression negated) {
            yield return
                $"{where} negates `{exclusion.Operand}`. `!` may only be applied to `Rel(name)` — "
                + "docs/plan/07 § Caching across requests: \"`!Rel(\"suspended\")`, never "
                + "`!From(…)`\". Negating anything that recurses to another object makes "
                + "invalidation a graph problem, which is the second consistency problem that "
                + "section refuses to take on.";
            yield break;
        }

        // Rule 9 — over a relation computed from direct tuples on the same object.
        if (!byName.TryGetValue(negated.Relation, out var target)) {
            yield return
                $"{where} negates `Rel(\"{negated.Relation}\")` and the type declares no "
                + $"'{negated.Relation}'.";
        } else if (target.IsPermission || !target.IsDirectOnly) {
            yield return
                $"{where} negates `Rel(\"{negated.Relation}\")`, which is "
                + (target.IsPermission ? "a permission" : $"computed ({target.Expression})")
                + ". Negation is legal only over a relation computed from direct tuples on the "
                + "same object — docs/plan/07 § Caching across requests. That restriction is what "
                + "keeps invalidation to \"the same object changed\", which the tenant version "
                + "stamp already covers.";
        }
    }

    static IEnumerable<string> CheckNegationIsTopLevel(string where, SchemaMember member) {
        if (member.Expression is not IntersectionExpression root) {
            yield return
                $"{where} contains `!` but its rewrite is not a top-level intersection (it is "
                + $"{member.Expression.GetType().Name}). Negation may appear only as a direct "
                + "operand of the permission's root `&` — docs/plan/07 § Caching across requests, "
                + "\"negation may only appear at the top level of a permission\". Write "
                + "`Rel(\"owner\") & !Rel(\"suspended\")`.";
            yield break;
        }

        var positives = 0;
        foreach (var operand in root.Operands) {
            if (operand is ExclusionExpression) {
                continue;
            }

            positives++;

            if (operand.DescendantsAndSelf().Any(x => x is ExclusionExpression)) {
                yield return
                    $"{where} nests a `!` inside `{operand}`. Negation may appear only as a direct "
                    + "operand of the permission's root `&`, never deeper.";
            }
        }

        if (positives == 0) {
            yield return
                $"{where} is nothing but negations. A permission of the form `!Rel(\"suspended\")` "
                + "grants to every subject that is not suspended — an allow-everything with a minus "
                + "sign in front of it. Intersect it with at least one positive term.";
        }
    }

    static string Join(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal));

    internal void Add(string type, SchemaMember member) {
        if (!byType.TryGetValue(type, out var members)) {
            problems.Add(
                $"'{member.Name}' is declared on the object type '{type}', which failed validation "
                + "and was never opened."
            );
            return;
        }

        var valid = RelationNaming.ValidateName(member.Name, member.IsPermission ? "permission name" : "relation name");

        if (valid.TryGetError(out var error)) {
            problems.Add(error.Message);
            return;
        }

        var existing = members.Find(x => string.Equals(x.Name, member.Name, StringComparison.Ordinal));
        if (existing is not null) {
            problems.Add(
                $"'{type}' declares '{member.Name}' twice — once as a "
                + $"{Kind(existing)} and once as a {Kind(member)}. A duplicate is not a merge: it "
                + "is two authors believing different things about one word, and whichever one wins "
                + "is arbitrary."
            );
            return;
        }

        members.Add(member);
    }
}

/// <summary>
///     The per-type half of the fluent surface, shaped so that the example in docs/plan/07 § The
///     model compiles as written.
/// </summary>
public sealed class SchemaTypeBuilder {
    readonly SchemaBuilder builder;
    readonly string type;

    internal SchemaTypeBuilder(SchemaBuilder builder, string type) {
        this.builder = builder;
        this.type = type;
    }

    /// <summary>A relation written directly as tuples — <c>This</c>.</summary>
    /// <param name="name">The relation name.</param>
    public SchemaTypeBuilder Relation(string name) => Relation(name, Rewrite.This);

    /// <summary>A relation with a rewrite.</summary>
    /// <param name="name">The relation name.</param>
    /// <param name="expression">How it is computed.</param>
    public SchemaTypeBuilder Relation(string name, RelationExpression expression) {
        builder.Add(type, new(name, expression, false, false));
        return this;
    }

    /// <summary>
    ///     A relation that is also a <b>named role</b> for the Azure view — docs/plan/07 § Azure
    ///     RBAC, expressed in it.
    /// </summary>
    /// <param name="name">The role, which is the relation name: <c>owner</c>, <c>contributor</c>.</param>
    /// <param name="expression">How it is computed.</param>
    /// <remarks>
    ///     Marking it here rather than keeping a separate list is what makes
    ///     "<c>GET /roleAssignments</c> lists tuples whose relation is a named role" a property of
    ///     the schema rather than a convention two files apart.
    /// </remarks>
    public SchemaTypeBuilder Role(string name, RelationExpression expression) {
        builder.Add(type, new(name, expression, false, true));
        return this;
    }

    /// <summary>A permission.</summary>
    /// <param name="name">The permission name — what <c>[RequiresPermission]</c> names.</param>
    /// <param name="expression">How it is computed.</param>
    public SchemaTypeBuilder Permission(string name, RelationExpression expression) {
        builder.Add(type, new(name, expression, true, false));
        return this;
    }

    /// <summary>Moves on to another object type.</summary>
    /// <param name="other">The next object type name.</param>
    public SchemaTypeBuilder DefineType(string other) => builder.DefineType(other);

    /// <summary>Every rule violation. See <see cref="SchemaBuilder.Validate" />.</summary>
    public IReadOnlyList<string> Validate() => builder.Validate();

    /// <summary>Builds the schema. See <see cref="SchemaBuilder.Build" />.</summary>
    /// <exception cref="SchemaDefinitionException">The schema breaks one or more rules.</exception>
    public AuthorizationSchema Build() => builder.Build();
}
