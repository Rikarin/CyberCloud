using CyberCloud.Authorization.Contracts;
using Shouldly;
using System.Reflection;

namespace CyberCloud.Authorization.Contracts.Tests;

/// <summary>
///     Every ReBAC name this platform spells, pinned to its exact bytes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FAILURE CLASS THIS SUITE EXISTS FOR IS ONE CHARACTER OF CASING, AND IT HAS
///         ALREADY HAPPENED HERE ONCE.</b> <c>resourcegroup</c> where the schema says
///         <c>resourceGroup</c> once failed <i>every create in the platform</i> and surfaced as a 404
///         whose real reason was in a log line —
///         <c>ReBacResourceAuthorizer.CheckedObject</c>'s remarks carry that account. Relation names
///         are worse still: <c>ReBacResourceRelationWriter</c>'s remarks point out that a tuple
///         written against a relation no rewrite follows is written <i>successfully</i>, so every
///         create reports 202 and every resource is invisible with nothing in any log.
///     </para>
///     <para>
///         ⚠ <b>Why literals rather than a comparison against the schema.</b>
///         <c>PermissionNameTests</c> already asserts that every constant here is a name
///         <c>CyberCloudSchema</c> defines — which is the <i>internal</i> agreement, and it stays
///         green if a rename changes both sides at once. These are the <i>external</i> agreement: a
///         tuple already written to a durable shard, a <c>sub_typ</c> claim in a token minted by a
///         silo of the previous version, and <c>portal/</c>'s own copies all carry the strings below
///         and none of them recompiles. A rename that passes every other test in the tree fails here,
///         which is the point.
///     </para>
///     <para>
///         ⚠ <b>These four classes changed assemblies, and that is the other reason this file
///         exists.</b> <c>ObjectTypes</c>, <c>Relations</c> and <c>Permissions</c> came from
///         <c>CyberCloud.Authorization</c>; <c>SubjectTypes</c> came from
///         <c>CyberCloud.Identity.Contracts</c>. A move is the moment a literal is retyped, and it is
///         also the moment a wire alias is dropped — see
///         <see cref="NothingInTheVocabularyIsAWireTypeSoNoAliasCouldHaveBeenRetired" /> for why the
///         second hazard does not apply to any of them.
///     </para>
///     <para>
///         ⚠ <b>One thing this suite deliberately does not claim.</b> It cannot observe that
///         <c>SubjectTypes.User</c> is <i>declared as</i> <c>ObjectTypes.User</c> rather than as a
///         second <c>"user"</c> — both are <c>const</c>, the compiler inlines them, and two identical
///         literals in one assembly are one interned instance. See
///         <see cref="TheSubjectSpellingOfAUserIsTheObjectSpelling" />, which asserts the divergence
///         that matters instead of a reference identity that would always hold.
///     </para>
/// </remarks>
public sealed class AuthorizationVocabularyTests {
    [Fact]
    public void EveryObjectTypeKeepsItsExactSpelling() {
        ObjectTypes.Tenant.ShouldBe("tenant");
        ObjectTypes.Subscription.ShouldBe("subscription");
        ObjectTypes.ResourceGroup.ShouldBe("resourceGroup");
        ObjectTypes.Resource.ShouldBe("resource");
        ObjectTypes.Group.ShouldBe("group");
        ObjectTypes.User.ShouldBe("user");
        ObjectTypes.Platform.ShouldBe("platform");

        // ⚠ The one that has actually cost a platform-wide outage. Asserted twice over — the literal
        // above, and the shape below — because `resourcegroup` is what a careless retype produces and
        // it differs from the truth in exactly one bit.
        ObjectTypes.ResourceGroup.ShouldNotBe("resourcegroup");
        Literals(typeof(ObjectTypes)).Count.ShouldBe(7, "a new object type is a schema change");
    }

    [Fact]
    public void EveryRelationKeepsItsExactSpelling() {
        Relations.Parent.ShouldBe("parent");
        Relations.Owner.ShouldBe("owner");
        Relations.Contributor.ShouldBe("contributor");
        Relations.Reader.ShouldBe("reader");
        Relations.Suspended.ShouldBe("suspended");
        Relations.Member.ShouldBe("member");
        Relations.Operator.ShouldBe("operator");

        Literals(typeof(Relations)).Count.ShouldBe(7);
    }

    [Fact]
    public void EveryPermissionKeepsItsExactSpelling() {
        Permissions.Read.ShouldBe("read");
        Permissions.Write.ShouldBe("write");
        Permissions.Delete.ShouldBe("delete");
        Permissions.AssignRole.ShouldBe("assignRole");
        Permissions.Administer.ShouldBe("administer");

        // The other camelCase one, and the only permission with a negation in its rewrite.
        Permissions.AssignRole.ShouldNotBe("assignrole");
        Literals(typeof(Permissions)).Count.ShouldBe(5);
    }

    [Fact]
    public void EverySubjectTypeKeepsItsExactSpelling() {
        SubjectTypes.User.ShouldBe("user");
        SubjectTypes.ServicePrincipal.ShouldBe("servicePrincipal");
        SubjectTypes.ManagedIdentity.ShouldBe("managedIdentity");

        // ⚠ `serviceprincipal` names a subject no tuple mentions, so every Check denies and it reads
        // as a permissions bug rather than as a typo. AccessTokenContractTests asserts the same thing
        // about what a token may carry; this asserts it about the constant itself.
        SubjectTypes.ServicePrincipal.ShouldNotBe("serviceprincipal");
        SubjectTypes.ManagedIdentity.ShouldNotBe("managedidentity");

        SubjectTypes.All.ShouldBe(["user", "servicePrincipal", "managedIdentity"], ignoreOrder: true);
    }

    [Fact]
    public void TheSubjectSpellingOfAUserIsTheObjectSpelling() {
        // ⚠ THE WHOLE REASON THESE TWO CLASSES ARE NOW IN ONE ASSEMBLY. SubjectTypes lived in
        // CyberCloud.Identity.Contracts, which could not name ObjectTypes without a reference on the
        // authorization IMPLEMENTATION assembly — so `user` was written out twice, in two assemblies,
        // and the two agreed only because a test said so. The declaration is now
        // `SubjectTypes.User = ObjectTypes.User`.
        //
        // ⚠ WHAT THIS ROW CAN AND CANNOT SEE, STATED RATHER THAN IMPLIED. It cannot see the link
        // itself: both are `const`, the compiler inlines them, and two identical literals in one
        // assembly are one interned instance — so a reference-identity assertion here would pass
        // against `const string User = "user";` too and would be no assertion at all. What it sees is
        // the failure that actually matters, which is the two DIVERGING. The link is a source-level
        // property and its enforcement is that it is one line in one file.
        SubjectTypes.User.ShouldBe(
            ObjectTypes.User,
            "a subject `user:alice` and an object `user:alice` are the same ReBAC node; two spellings "
            + "would make a membership write and a membership check disagree"
        );
    }

    [Fact]
    public void TheTwoSubjectOnlyTypesAreDeliberatelyNotObjectTypes() {
        // ⚠ The asymmetry is load-bearing, not an omission. ObjectTypes is what CyberCloudSchema
        // DEFINES — PermissionNameTests.EveryObjectTypeConstantIsInTheSchema enforces exactly that —
        // and TupleStoreGrain.Validate checks a tuple's Object.Type against the schema and its
        // Subject against nothing. Adding these two to ObjectTypes without a DefineType would go red
        // over there; adding the DefineType is a SchemaVersion bump, which invalidates every cached
        // check for no behaviour anything gains. If this row ever goes red, that trade has been
        // revisited and CyberCloudSchema.SchemaVersion is what to look at.
        var objectTypes = Literals(typeof(ObjectTypes));

        objectTypes.ShouldNotContain(SubjectTypes.ServicePrincipal);
        objectTypes.ShouldNotContain(SubjectTypes.ManagedIdentity);

        // ⚠ And the reverse: `group` is a ReBAC object and never a token subject. Nothing signs in as
        // a group, so a token whose subject were one is a bearer credential for everyone in it.
        SubjectTypes.All.ShouldNotContain(ObjectTypes.Group);
        objectTypes.ShouldContain(ObjectTypes.Group);
    }

    [Fact]
    public void NothingInTheVocabularyIsAWireTypeSoNoAliasCouldHaveBeenRetired() {
        // ⚠ FAILURE CLASS: A WIRE ALIAS RETIRED BY ACCIDENT. IdentityWireTypes.cs' header block sets
        // out the rule — docs/plan/04 § Failure and upgrade makes the [Alias], not the CLR name, what
        // a silo of version N looks up in a payload from version N+1, so retiring one is a payload
        // nothing can read. It is free today only because `git tag` is empty, and it stops being free
        // at the first release tag.
        //
        // Moving a type between assemblies is precisely when that gets done by accident. It could not
        // have happened to these four: every member is a `const string`, the compiler inlines them
        // into every call site, and a static class with no instance state is not something
        // [GenerateSerializer] or [Alias] can meaningfully be put on. This asserts that rather than
        // assuming it — a future member that was a record, or a class that grew an [Alias], would be
        // a wire type that had quietly changed assemblies.
        foreach (var type in (Type[])[typeof(ObjectTypes), typeof(Relations), typeof(Permissions), typeof(SubjectTypes)]) {
            type.IsAbstract.ShouldBeTrue(type.Name);
            type.IsSealed.ShouldBeTrue($"{type.Name} should be a static class");

            type.GetCustomAttributes()
                .Select(x => x.GetType().Name)
                .ShouldNotContain(
                    "AliasAttribute",
                    $"{type.Name} carries a wire alias, so moving it between assemblies is a "
                    + "compatibility event rather than a refactor"
                );

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)) {
                field.IsLiteral.ShouldBeTrue(
                    $"{type.Name}.{field.Name} is not a const, so a call site binds it at runtime "
                    + "rather than inlining it — which makes this assembly's identity part of the "
                    + "contract in a way the const members deliberately are not"
                );

                field.FieldType.ShouldBe(typeof(string));
            }
        }
    }

    /// <summary>Every <c>public const string</c> declared on a vocabulary class.</summary>
    /// <param name="type">The class.</param>
    static IReadOnlyList<string> Literals(Type type) =>
    [
        .. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
    ];
}
