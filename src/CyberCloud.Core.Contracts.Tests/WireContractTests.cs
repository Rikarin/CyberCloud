using CyberCloud.Core.Contracts.Serialization;
using Shouldly;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Core.Contracts.Tests;

/// <summary>
///     The two rules that make a rolling silo upgrade possible, as tests rather than as intentions.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/00 § Coding standards and docs/plan/04 § Failure and upgrade both say an
///         <b>analyzer</b> enforces these. One now does — <c>CC1003</c> in
///         <c>src/CyberCloud.Analyzers</c>, which <c>CyberCloud.Core.Contracts</c> (the assembly
///         these tests reflect over, not this one) references as an analyzer asset. So
///         <see cref="EveryGenerateSerializerTypeHasAnAlias" /> should never fail again: a type
///         declared without an alias stops compiling first.
///     </para>
///     <para>
///         ⚠ <b>These tests are kept anyway, and not because they are hard to delete.</b> The
///         analyzer answers "is there an alias?". Everything else here is a different question that
///         a compile-time rule cannot answer: are the alias <i>strings</i> the ones recorded in this
///         file (a rename must not change them), is every alias unique, and is the <c>[Id(n)]</c>
///         baseline still append-only. <see cref="EveryGenerateSerializerTypeHasAnAlias" /> stays as
///         the belt to the analyzer's braces — it is what would notice if the analyzer reference
///         were ever dropped from this project.
///     </para>
/// </remarks>
public sealed class WireContractTests {
    static readonly Assembly Contracts = typeof(ResultSurrogate).Assembly;

    /// <summary>
    ///     The <c>[Id(n)]</c> baseline. docs/plan/05 § Serialization and schema evolution —
    ///     <i>
    ///         "numbers are never reused, never
    ///         reordered. Removing a member leaves its number burned."
    ///     </i>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This list is append-only.</b> A failure here is not a test to update — it is the
    ///     wire-compatibility gate firing. Adding a member adds a line with the next unused number.
    ///     Removing a member deletes its <i>line</i> from the type and leaves the number out of
    ///     circulation forever; it does not free the number for the next member. Renaming a member
    ///     is free (the number is the contract, the name is not) and the string here changes with
    ///     it.
    /// </remarks>
    static readonly (string Type, int Id, string Member)[] Baseline = [
        ("ErrorSurrogate", 0, "Code"),
        ("ErrorSurrogate", 1, "Message"),
        ("ErrorSurrogate", 2, "Target"),
        ("ErrorSurrogate", 3, "Details"),

        ("ResultSurrogate", 0, "IsSuccess"),
        ("ResultSurrogate", 1, "Error"),

        ("ResultSurrogate`1", 0, "IsSuccess"),
        ("ResultSurrogate`1", 1, "Value"),
        ("ResultSurrogate`1", 2, "Error"),

        ("ResourceTypeNameSurrogate", 0, "Namespace"),
        ("ResourceTypeNameSurrogate", 1, "Type"),

        ("ResourceIdSurrogate", 0, "TenantId"),
        ("ResourceIdSurrogate", 1, "SubscriptionId"),
        ("ResourceIdSurrogate", 2, "ResourceGroup"),
        ("ResourceIdSurrogate", 3, "Type"),
        ("ResourceIdSurrogate", 4, "Name"),
        ("ResourceIdSurrogate", 5, "Id")

        // ⚠ ResourceKeySurrogate's four entries were REMOVED, not burned, and that is the one
        // exception this list's own rule allows. The append-only rule protects numbers that a
        // deployed peer might send; nothing has shipped, ResourceKey no longer exists (a grain key
        // is now built by CyberCloud.Core.Resources.GrainKeys and never travels as a value), and its
        // alias "CyberCloud.Core.ResourceKey" is retired with it. If any silo had ever run, this
        // would instead be a burned group with a comment. See ADR-002, docs/plan/02 § ADR-002.
    ];

    /// <summary>The aliases this assembly publishes. Changing one is a wire break.</summary>
    /// <remarks>
    ///     The alias, not the CLR name, is what a silo of version N looks up when a silo of version
    ///     N+1 sends it a payload (docs/plan/04 § Failure and upgrade). The strings are therefore chosen to survive a
    ///     rename of the surrogate <i>and</i> of the type it stands in for: they name the Core type
    ///     by its full name, since that is the concept, and carry no "Surrogate" suffix, since the
    ///     surrogate is an implementation detail that the far side never sees.
    /// </remarks>
    static readonly (string Type, string Alias)[] Aliases = [
        ("ErrorSurrogate", "CyberCloud.Core.Error"),
        ("ResultSurrogate", "CyberCloud.Core.Result"),
        ("ResultSurrogate`1", "CyberCloud.Core.Result`1"),
        ("ResourceTypeNameSurrogate", "CyberCloud.Core.ResourceTypeName"),
        ("ResourceIdSurrogate", "CyberCloud.Core.ResourceId")
    ];

    static IEnumerable<Type> GeneratedSerializerTypes =>
        Contracts.GetTypes().Where(t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null);

    [Fact]
    public void EveryGenerateSerializerTypeHasAnAlias() {
        var missing = GeneratedSerializerTypes
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty(
            "docs/plan/04 § Failure and upgrade makes a rolling upgrade depend on every [GenerateSerializer] type "
            + "having a stable [Alias]. A type without one is renamed-into-a-data-loss-bug waiting "
            + "to happen."
        );
    }

    [Fact]
    public void EveryAliasIsUnique() {
        var duplicates = GeneratedSerializerTypes
            .Select(t => t.GetCustomAttribute<AliasAttribute>()?.Alias)
            .Where(a => a is not null)
            .GroupBy(a => a, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key!)
            .ToList();

        duplicates.ShouldBeEmpty("two types claiming one alias is a coin flip at deserialization.");
    }

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() {
        var actual = GeneratedSerializerTypes
            .Select(t => (Type: t.Name, Alias: t.GetCustomAttribute<AliasAttribute>()?.Alias ?? "<none>"))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ToList();

        actual.ShouldBe(
            Aliases.OrderBy(x => x.Type, StringComparer.Ordinal).ToList(),
            "an alias changed, or a [GenerateSerializer] type was added without recording its "
            + "alias. Both are wire-contract changes."
        );
    }

    [Fact]
    public void EveryGenerateSerializerTypeIsPublic() =>
        // The gateway, the CLI and the SDK reference this assembly. A surrogate Orleans cannot
        // instantiate from another assembly's generic instantiation is a runtime failure, not a
        // compile one.
        GeneratedSerializerTypes
            .Where(t => !t.IsPublic && !t.IsNestedPublic)
            .Select(t => t.Name)
            .ShouldBeEmpty();

    [Fact]
    public void TheIdManifestMatchesTheBaseline() {
        var actual = GeneratedSerializerTypes
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Select(member => (member, id: member.GetCustomAttribute<IdAttribute>()))
                .Where(x => x.id is not null)
                .Select(x => (Type: type.Name, Id: (int)x.id!.Id, Member: x.member.Name))
            )
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Id)
            .ToList();

        actual.ShouldBe(
            Baseline.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Id).ToList(),
            "docs/plan/05 § Serialization and schema evolution: [Id(n)] numbers are never reused and "
            + "never reordered. If this fails "
            + "because a member was added, append it to Baseline with the next unused number. If it "
            + "fails for any other reason, the wire contract just broke."
        );
    }

    [Fact]
    public void NoTypeReusesAnIdNumber() {
        foreach (var group in Baseline.GroupBy(x => x.Type, StringComparer.Ordinal)) {
            var ids = group.Select(x => x.Id).ToList();
            ids.Distinct()
                .Count()
                .ShouldBe(
                    ids.Count,
                    $"{group.Key} declares the same [Id(n)] twice."
                );
        }
    }

    [Fact]
    public void EveryConverterIsRegistered() {
        // A surrogate with no [RegisterConverter] compiles, publishes an alias, passes every test
        // above, and silently never participates in serialization.
        var surrogates = GeneratedSerializerTypes.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var converted = Contracts.GetTypes()
            .Where(t => t.GetCustomAttribute<RegisterConverterAttribute>() is not null)
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConverter<,>))
            .Select(i => i.GetGenericArguments()[1].Name)
            .ToHashSet(StringComparer.Ordinal);

        surrogates.Except(converted).ShouldBeEmpty("surrogate types with no registered converter.");
    }

    [Fact]
    public void TheBaselineNamesEveryPublicMemberOfEverySurrogate() {
        // The direction the manifest test cannot cover on its own: a member added WITHOUT an [Id]
        // is invisible to Orleans and is silently dropped on the wire.
        var unnumbered = GeneratedSerializerTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<IdAttribute>() is null)
                .Select(p => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{type.Name}.{p.Name}"
                    )
                )
            )
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unnumbered.ShouldBeEmpty(
            "a public property on a [GenerateSerializer] type with no [Id(n)] is not serialised. "
            + "docs/plan/00 § Coding standards requires an explicit [Id(n)] on every member of every wire type."
        );
    }
}
