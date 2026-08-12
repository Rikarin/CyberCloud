using CyberCloud.Authorization.Contracts;
using Shouldly;
using static CyberCloud.Authorization.Rewrite;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     ⚠ <b>The negation restriction, enforced rather than documented.</b>
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/07 § Caching across requests:
///         <i>
///             "Negative relations break monotonic caching
///             and this is the subtlest thing in the document. A permission of the form <c>A &amp; !B</c>
///             is not monotone: adding a tuple can remove access … The rule,
///             <b>
///                 enforced by the schema
///                 builder
///             </b>
///             : negation may only appear at the top level of a permission, over a relation
///             that is computed from direct tuples on the same object."
///         </i>
///     </para>
///     <para>
///         Every illegal shape below <b>fails to build</b>. That is the whole point of this file: if
///         the rule were merely written down, a schema that broke it would compile, deploy, and make
///         the check cache wrong in a way that no later test would catch — because every later test
///         would be written against a schema its author believed was legal.
///     </para>
/// </remarks>
public sealed class SchemaBuilderTests {
    // ── The document's own example ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheExampleInTheDocumentCompilesAndBuilds() {
        // docs/plan/07 § The model, verbatim apart from the type it hangs off.
        var schema = Schema.DefineType("resourceGroup")
            .Relation("parent")
            .Relation("owner", This | From("parent", "owner"))
            .Relation("contributor", This | From("parent", "contributor") | Rel("owner"))
            .Relation("reader", This | From("parent", "reader") | Rel("contributor"))
            .Relation("suspended")
            .Permission("delete", Rel("owner"))
            .Permission("write", Rel("contributor"))
            .Permission("read", Rel("reader"))
            .Permission("assignRole", Rel("owner") & !Rel("suspended"))
            .Build();

        schema.Type("resourceGroup").ShouldNotBeNull();
        schema.Member("resourceGroup", "assignRole")!.ContainsNegation.ShouldBeTrue();
        schema.Member("resourceGroup", "suspended")!.IsDirectOnly.ShouldBeTrue();
    }

    [Fact]
    public void TheBuiltInSchemaBuilds() {
        // If CyberCloudSchema ever violates a rule, this fails at type-initialisation time, which
        // is the earliest a compiled schema can fail.
        CyberCloudSchema.Instance.Version.ShouldBe(CyberCloudSchema.SchemaVersion);
        CyberCloudSchema.Instance.TypeNames.ShouldContain(ObjectTypes.ResourceGroup);
        CyberCloudSchema.Instance.Type(ObjectTypes.Subscription)!.Roles
            .ShouldBe([Relations.Contributor, Relations.Owner, Relations.Reader]);
    }

    // ── ⚠ The negation rules. Each of these MUST fail to build. ────────────────────────────────

    [Fact]
    public void NegatingATuplesetIsIllegal() {
        // The exact case docs/plan/07 names: "`!Rel("suspended")`, never `!From(…)`".
        var problems = Schema.DefineType("doc")
            .Relation("parent")
            .Relation("owner", This)
            .Relation("blocked", This | From("parent", "blocked"))
            .Permission("act", Rel("owner") & !From("parent", "blocked"))
            .Validate();

        problems.ShouldContain(x => x.Contains("may only be applied to `Rel(name)`", StringComparison.Ordinal));

        // ⚠ And it does not merely report — it refuses to produce a schema.
        Should.Throw<SchemaDefinitionException>(() =>
            Schema.DefineType("doc")
                .Relation("parent")
                .Relation("owner", This)
                .Relation("blocked", This | From("parent", "blocked"))
                .Permission("act", Rel("owner") & !From("parent", "blocked"))
                .Build()
        );
    }

    [Fact]
    public void NegatingARelationThatIsNotDirectIsIllegal() {
        // `blocked` is computed from the parent, so "the same object changed" no longer covers its
        // invalidation — which is the reason the restriction exists.
        var problems = Schema.DefineType("doc")
            .Relation("parent")
            .Relation("owner", This)
            .Relation("blocked", This | From("parent", "blocked"))
            .Permission("act", Rel("owner") & !Rel("blocked"))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("computed from direct tuples on the", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void NegationInsideARelationIsIllegal() {
        // A relation is reachable from another object through From(…), so a `!` in one is a `!`
        // that is not at any top level.
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Relation("suspended")
            .Relation("effectiveOwner", Rel("owner") & !Rel("suspended"))
            .Permission("act", Rel("effectiveOwner"))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("is a relation and its rewrite contains `!`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ANegationNestedBelowTheRootIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Relation("editor", This)
            .Relation("suspended")
            .Permission("act", Rel("owner") | (Rel("editor") & !Rel("suspended")))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("not a top-level intersection", StringComparison.Ordinal)
            || x.Contains("nests a `!`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ANegationWithNoPositiveTermIsIllegal() {
        // `!Rel("suspended")` alone grants to every subject in the universe that is not suspended.
        var problems = Schema.DefineType("doc")
            .Relation("suspended")
            .Permission("act", !Rel("suspended"))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("nothing but negations", StringComparison.Ordinal)
            || x.Contains("not a top-level intersection", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void DoubleNegationIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Relation("suspended")
            .Permission("act", Rel("owner") & !!Rel("suspended"))
            .Validate();

        problems.ShouldNotBeEmpty();
    }

    [Fact]
    public void NegatingThisIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Permission("act", Rel("owner") & !This)
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("may only be applied to `Rel(name)`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void NegatingAUnionIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Relation("suspended")
            .Relation("banned")
            .Permission("act", Rel("owner") & !(Rel("suspended") | Rel("banned")))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("may only be applied to `Rel(name)`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ReferencingAPermissionThatCarriesANegationIsIllegal() {
        // Rel(p) would move p's `!` below a top level.
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Relation("suspended")
            .Permission("act", Rel("owner") & !Rel("suspended"))
            .Permission("alsoAct", Rel("act"))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("permission containing `!`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void InheritingAPermissionThatCarriesANegationIsIllegal() {
        // From(_, p) is the same mistake at a distance: it evaluates p from ANOTHER object.
        var problems = Schema.DefineType("doc")
            .Relation("parent")
            .Relation("owner", This)
            .Relation("suspended")
            .Permission("act", Rel("owner") & !Rel("suspended"))
            .DefineType("folder")
            .Relation("parent")
            .Relation("owner", This)
            .Permission("inheritedAct", Rel("owner") | From("parent", "act"))
            .Validate();

        problems.ShouldContain(x =>
            x.Contains("permission containing `!`", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void TheLegalShapeIsExactlyTheOneTheDocumentWrites() {
        var schema = Schema.DefineType("doc")
            .Relation("owner", This)
            .Relation("suspended")
            .Permission("act", Rel("owner") & !Rel("suspended"))
            .Build();

        schema.Member("doc", "act")!.ContainsNegation.ShouldBeTrue();
    }

    // ── The rest of the rules ──────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnresolvableRelationReferenceIsIllegal() {
        // This is the typo docs/plan/07 § The model credits an analyzer with catching.
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Permission("act", Rel("ownr"))
            .Validate();

        problems.ShouldContain(x => x.Contains("declares no 'ownr'", StringComparison.Ordinal));
    }

    [Fact]
    public void AComputedTuplesetIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("parent")
            .Relation("grandparent", From("parent", "parent"))
            .Relation("owner", This | From("grandparent", "owner"))
            .Validate();

        problems.ShouldContain(x => x.Contains("must be a direct relation", StringComparison.Ordinal));
    }

    [Fact]
    public void ATuplesetPointingAtAPermissionIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Permission("act", Rel("owner"))
            .Relation("inherited", From("act", "owner"))
            .Validate();

        problems.ShouldContain(x => x.Contains("is a permission", StringComparison.Ordinal));
    }

    [Fact]
    public void AComputedNameNoTypeDeclaresIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("parent")
            .Relation("owner", This | From("parent", "ownr"))
            .Validate();

        problems.ShouldContain(x => x.Contains("no object type in the schema", StringComparison.Ordinal));
    }

    [Fact]
    public void ThisInsideAPermissionIsIllegal() {
        // A permission has no tuples of its own; This there is a silent allow-nothing.
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Permission("act", This | Rel("owner"))
            .Validate();

        problems.ShouldContain(x => x.Contains("contains `This`", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaringOneNameTwiceIsIllegal() {
        var problems = Schema.DefineType("doc")
            .Relation("owner", This)
            .Permission("owner", Rel("owner"))
            .Validate();

        problems.ShouldContain(x => x.Contains("twice", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Owner", "starts upper-case")]
    [InlineData("own er", "contains a space")]
    [InlineData("own:er", "contains the type/id separator")]
    [InlineData("own#er", "contains the object/relation separator")]
    [InlineData("own@er", "contains the relation/subject separator")]
    [InlineData("own/er", "contains the grain key separator")]
    [InlineData("own|er", "contains the Orleans.Multitenant separator")]
    [InlineData("own-er", "contains a hyphen")]
    [InlineData("own_er", "contains an underscore")]
    [InlineData("", "is empty")]
    public void AnIllegalRelationNameIsIllegal(string name, string why) {
        var problems = Schema.DefineType("doc")
            .Relation(name, This)
            .Validate();

        problems.ShouldNotBeEmpty($"'{name}' {why}");
    }

    [Fact]
    public void AnEmptySchemaIsIllegal() =>
        Schema.Create()
            .Validate()
            .ShouldContain(x =>
                x.Contains("no object types", StringComparison.Ordinal)
            );

    [Fact]
    public void ACycleIsLegalBecauseTheMemoBreaksItNotTheBuilder() {
        // docs/plan/07 § Check: "Cycles are broken by the memo, not by cycle detection." Rejecting
        // them here would be a second and less complete mechanism — a cycle can be formed by TUPLES
        // at runtime, which no schema check can see.
        var schema = Schema.DefineType("doc")
            .Relation("parent")
            .Relation("a", This | Rel("b"))
            .Relation("b", This | Rel("a") | From("parent", "a"))
            .Build();

        schema.Type("doc").ShouldNotBeNull();
    }

    [Fact]
    public void BuildThrowsAndListsEveryProblemAtOnce() {
        var thrown = Should.Throw<SchemaDefinitionException>(() =>
            Schema.DefineType("doc")
                .Relation("owner", This)
                .Relation("suspended")
                .Permission("act", Rel("ownr") & !Rel("nope"))
                .Build()
        );

        thrown.Problems.Length.ShouldBeGreaterThanOrEqualTo(2);
        thrown.Message.ShouldContain("docs/plan/07");
    }
}
