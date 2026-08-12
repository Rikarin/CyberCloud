// What a released assembly published on the wire, and how to compare two of them — the reading half
// of the Wire compatibility gate in Build.Architecture.cs. docs/plan/23 § The architecture gates,
// docs/plan/04 § Failure and upgrade, docs/plan/05 § Serialization and schema evolution.
//
// Not a partial of Build, for the reason build/README.md gives about ArchitectureFacts.cs: what a
// gate reads is a separate concern from what it decides, and a manifest format is checkable without
// running a build.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Nuke.Common.IO;

/// <summary>One <c>[Id(n)]</c> member of a wire type.</summary>
/// <param name="Id">
///     The attribute's argument. ⚠ <b>This, not <see cref="Name" />, is the contract.</b>
///     docs/plan/05 § Serialization and schema evolution: numbers are never reused and never
///     reordered, and <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c> says the same from the
///     other side — <i>"renaming a member is free (the number is the contract, the name is not)"</i>.
///     Everything <see cref="WireContract.Compare" /> does is keyed on this.
/// </param>
/// <param name="Name">Carried for the failure message, and compared only to report a rename.</param>
/// <param name="Type">
///     The member's type, as its metadata signature spells it. A member whose number stayed put and
///     whose type changed is the break that reads as innocent in a diff: the far side decodes the
///     old bytes into the new shape and either throws or, worse, does not.
/// </param>
sealed record WireMember(int Id, string Name, string Type);

/// <summary>
///     One named constant of an aliased enum, keyed by its value for the same reason
///     <see cref="WireMember" /> is keyed by its number: the value is what travels.
/// </summary>
sealed record WireValue(long Value, string Name);

/// <summary>
///     One type as a peer of another version sees it.
/// </summary>
/// <param name="Alias">
///     The <c>[Alias]</c> string. ⚠ <b>The primary key of this whole gate.</b> docs/plan/04
///     § Failure and upgrade makes the alias — not the CLR name, not the namespace — what a silo of
///     version N looks up when a silo of version N+1 sends it a payload. So a type that moved
///     assembly, changed namespace and was renamed is the same wire type here, and a type that kept
///     every one of those and re-spelled its alias is a different one.
/// </param>
/// <param name="Kind">
///     <c>class</c>, <c>struct</c>, <c>interface</c> or <c>enum</c>. Compared because Orleans encodes
///     a value type and a reference type differently, and because a record turned into an enum is a
///     rewrite wearing the same alias.
/// </param>
/// <param name="Assembly">Where it was found, so a failure names a file somebody can open.</param>
/// <param name="ClrName">
///     The full metadata name. Reported, never compared: renaming a type is free under the rule
///     above and comparing it would make a legal refactor fail the build.
/// </param>
sealed record WireType(
    string Alias,
    string Kind,
    string Assembly,
    string ClrName,
    IReadOnlyList<WireMember> Members,
    IReadOnlyList<WireValue> Values);

/// <summary>
///     Reading wire types out of compiled assemblies, writing them to a manifest, and comparing a
///     released manifest against the tree.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The subject is every type carrying <c>[Alias]</c>, which is wider than every type
///         carrying <c>[GenerateSerializer]</c>, and the width is not an accident.</b> An aliased
///         enum deliberately carries no <c>[GenerateSerializer]</c> —
///         <c>CyberCloud.Tenancy.Contracts</c>' enum file says so in as many words: <i>"Orleans
///         serialises enums through its built-in enum codec without annotation. The [Alias] is what
///         matters for a rolling upgrade"</i>. A gate that took <c>[GenerateSerializer]</c> as its
///         subject would silently skip every enum on the wire, which is a large share of the
///         payload surface and the easiest place to renumber something by accident.
///     </para>
///     <para>
///         ⚠ <b>This is a comparison of wire <i>shape</i>, not a live serializer round-trip, and the
///         trade is worth stating.</b> docs/plan/23 § The architecture gates words the row as
///         "round-trip every wire type". A round-trip would need both versions of one assembly
///         identity loaded into one process behind two <c>AssemblyLoadContext</c>s with Orleans in
///         each, from a Nuke build that references neither Orleans nor the product tree. What it
///         would prove is that the members it happened to populate survived; what it would miss is
///         every member whose value stayed <c>default</c>. This compares every member of every type
///         unconditionally, in both directions, against what the release actually published — which
///         is the property the round-trip was a way of reaching. It cannot catch a hand-written
///         <c>ISerializable</c>-style codec whose behaviour changed without its shape changing;
///         nothing in this tree has one, and if one appears this comment is where the gap is.
///     </para>
/// </remarks>
static class WireContract
{
    // ⚠ Matched on the unqualified attribute name, for the reason ArchitectureFacts.cs gives about
    // PersistentStateAttribute: Orleans has moved these between namespaces across major versions,
    // and a gate that goes quietly green on the next Orleans bump is worse than one that
    // over-matches. Nothing else in this tree declares a type by either name.
    const string AliasAttribute = "AliasAttribute";
    const string IdAttribute = "IdAttribute";
    const string GenerateSerializerAttribute = "GenerateSerializerAttribute";

    /// <summary>
    ///     Where the deliberately-retired aliases are declared, for the failure messages here.
    ///     <see cref="Build.BurnedAliasesFile" /> is the path.
    /// </summary>
    public const string BurnedFileName = "build/wire/burned.txt";

    /// <summary>The directory the per-release manifests live in.</summary>
    public const string ManifestDirectoryName = "wire";

    // ── Reading ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every aliased type in the given assemblies, ordered so that two manifests of the same tree
    ///     are byte-identical.
    /// </summary>
    public static List<WireType> Read(IEnumerable<AbsolutePath> assemblies)
        => assemblies
            .SelectMany(ReadOne)
            .OrderBy(x => x.Alias, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Every <c>[GenerateSerializer]</c> type in the given assemblies, and whether it carries an
    ///     <c>[Alias]</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the complement of <see cref="Read" />, not a variation on it, and the two
    ///         subjects are different sets rather than one set read twice.</b> <see cref="Read" />
    ///         takes <c>[Alias]</c> as its subject, which is wider in one direction — an aliased enum
    ///         carries no <c>[GenerateSerializer]</c> — and narrower in the other, because a
    ///         <c>[GenerateSerializer]</c> type with no alias is invisible to it. That second gap is
    ///         exactly what <c>CC1003</c> exists to close, so it is exactly what a gate reporting
    ///         "CC1003 enforces this" has to be able to see for itself.
    ///     </para>
    ///     <para>
    ///         Nested types are included. A <c>[GenerateSerializer]</c> nested in a grain's state
    ///         class serialises like any other and is renamed like any other.
    ///     </para>
    /// </remarks>
    public static List<(string Assembly, string ClrName, bool Aliased)> Serializable(IEnumerable<AbsolutePath> assemblies)
        => assemblies
            .SelectMany(SerializableIn)
            .OrderBy(x => x.ClrName, StringComparer.Ordinal)
            .ToList();

    static List<(string Assembly, string ClrName, bool Aliased)> SerializableIn(AbsolutePath dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        var found = new List<(string, string, bool)>();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);

            if (!HasAttribute(metadata, type.GetCustomAttributes(), GenerateSerializerAttribute))
                continue;

            found.Add((
                assembly,
                FullName(metadata, handle),
                StringArgument(metadata, type.GetCustomAttributes(), AliasAttribute) is not null));
        }

        return found;
    }

    static bool HasAttribute(MetadataReader metadata, CustomAttributeHandleCollection attributes, string name)
    {
        foreach (var handle in attributes)
        {
            if (string.Equals(AttributeTypeName(metadata, metadata.GetCustomAttribute(handle)), name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static List<WireType> ReadOne(AbsolutePath dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        var types = new List<WireType>();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);

            if (StringArgument(metadata, type.GetCustomAttributes(), AliasAttribute) is not { } alias)
                continue;

            var kind = KindOf(metadata, type);

            types.Add(new WireType(
                alias,
                kind,
                assembly,
                FullName(metadata, handle),
                Members(metadata, type),
                string.Equals(kind, "enum", StringComparison.Ordinal) ? Values(metadata, type) : []));
        }

        return types;
    }

    /// <summary>
    ///     ⚠ Fields <i>and</i> properties. A record declares <c>[Id(0)] public string Path { get;
    ///     init; }</c> — the attribute sits on the property and never reaches the backing field — and
    ///     a grain state class usually numbers fields instead. Reading one and not the other would
    ///     produce an empty member list for half the tree, and an empty list compares equal to an
    ///     empty list, so the gate would have gone green over nothing.
    /// </summary>
    static List<WireMember> Members(MetadataReader metadata, TypeDefinition type)
    {
        var members = new List<WireMember>();

        foreach (var handle in type.GetFields())
        {
            var field = metadata.GetFieldDefinition(handle);

            if (NumberArgument(metadata, field.GetCustomAttributes(), IdAttribute) is { } id)
            {
                members.Add(new WireMember(
                    id,
                    metadata.GetString(field.Name),
                    field.DecodeSignature(SignatureTypeProvider.Instance, null)));
            }
        }

        foreach (var handle in type.GetProperties())
        {
            var property = metadata.GetPropertyDefinition(handle);

            if (NumberArgument(metadata, property.GetCustomAttributes(), IdAttribute) is { } id)
            {
                members.Add(new WireMember(
                    id,
                    metadata.GetString(property.Name),
                    property.DecodeSignature(SignatureTypeProvider.Instance, null).ReturnType));
            }
        }

        return members.OrderBy(x => x.Id).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The named constants of an enum, from the literal fields it declares.</summary>
    static List<WireValue> Values(MetadataReader metadata, TypeDefinition type)
    {
        var values = new List<WireValue>();

        foreach (var handle in type.GetFields())
        {
            var field = metadata.GetFieldDefinition(handle);

            // The one instance field on an enum is its `value__` storage, which has no constant.
            if (!field.Attributes.HasFlag(FieldAttributes.Literal))
                continue;

            if (ConstantValue(metadata, field.GetDefaultValue()) is { } value)
                values.Add(new WireValue(value, metadata.GetString(field.Name)));
        }

        return values.OrderBy(x => x.Value).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    static long? ConstantValue(MetadataReader metadata, ConstantHandle handle)
    {
        if (handle.IsNil)
            return null;

        var constant = metadata.GetConstant(handle);
        var reader = metadata.GetBlobReader(constant.Value);

        return constant.TypeCode switch
        {
            ConstantTypeCode.SByte => reader.ReadSByte(),
            ConstantTypeCode.Byte => reader.ReadByte(),
            ConstantTypeCode.Int16 => reader.ReadInt16(),
            ConstantTypeCode.UInt16 => reader.ReadUInt16(),
            ConstantTypeCode.Int32 => reader.ReadInt32(),
            ConstantTypeCode.UInt32 => reader.ReadUInt32(),
            ConstantTypeCode.Int64 => reader.ReadInt64(),
            // A ulong enum past long.MaxValue would wrap here. None exists, and one appearing is a
            // stranger thing than this comment.
            ConstantTypeCode.UInt64 => (long)reader.ReadUInt64(),
            _ => null,
        };
    }

    static string KindOf(MetadataReader metadata, TypeDefinition type)
    {
        if (type.Attributes.HasFlag(TypeAttributes.Interface))
            return "interface";

        var @base = BaseTypeName(metadata, type.BaseType);

        return @base switch
        {
            "Enum" => "enum",
            "ValueType" => "struct",
            _ => "class",
        };
    }

    static string? BaseTypeName(MetadataReader metadata, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)handle).Name),
            HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            _ => null,
        };

    static string? StringArgument(MetadataReader metadata, CustomAttributeHandleCollection attributes, string name)
        => FirstArgument(metadata, attributes, name) as string;

    static int? NumberArgument(MetadataReader metadata, CustomAttributeHandleCollection attributes, string name)
        => FirstArgument(metadata, attributes, name) switch
        {
            uint value => (int)value,
            int value => value,
            ushort value => value,
            _ => null,
        };

    static object? FirstArgument(MetadataReader metadata, CustomAttributeHandleCollection attributes, string name)
    {
        foreach (var handle in attributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);

            if (!string.Equals(AttributeTypeName(metadata, attribute), name, StringComparison.Ordinal))
                continue;

            return attribute
                .DecodeValue(CustomAttributeTypeProvider.Instance)
                .FixedArguments
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        return null;
    }

    static string? AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
        => attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference =>
                metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
                    ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name)
                    : null,
            HandleKind.MethodDefinition =>
                metadata.GetString(metadata.GetTypeDefinition(
                    metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()).Name),
            _ => null,
        };

    static string FullName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name);

        if (type.IsNested)
            return FullName(metadata, type.GetDeclaringType()) + "+" + name;

        var @namespace = metadata.GetString(type.Namespace);

        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    // ── The manifest ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     What a released assembly published, as a text file: one tab-separated record per line,
    ///     each starting with the word for what it is.
    ///     <para>
    ///         A text file rather than JSON because the thing anybody will ever do with it is read a
    ///         diff of it. A wire break should be one visible line in a pull request, which is the
    ///         same argument <c>durable-grains.txt</c> makes for its own format.
    ///     </para>
    ///     <para>
    ///         ⚠ Generated, never hand-edited — the header says so, and
    ///         <see cref="Build.WireCompatibilityGate" /> checks the recorded commit against
    ///         <c>git rev-parse</c> so a manifest produced from the wrong tree is caught. It cannot
    ///         catch a hand-edited one; the honest statement of that trust boundary is that this file
    ///         is exactly as trusted as <c>openapi/2026-08-01.json</c>, and is verified the same way —
    ///         by regenerating it, which <c>--wire-record</c> does.
    ///     </para>
    /// </summary>
    public static string Write(string tag, string commit, IReadOnlyList<WireType> types)
    {
        var text = new StringBuilder();

        text.AppendLine("# Generated by ./build.sh Architecture --wire-record. Do not edit by hand.");
        text.AppendLine("#");
        text.AppendLine("# What the assemblies at this release tag published on the wire — the baseline the Wire");
        text.AppendLine("# compatibility gate in build/Build.Architecture.cs diffs the working tree against.");
        text.AppendLine("# docs/plan/23 § The architecture gates, docs/plan/04 § Failure and upgrade.");
        text.AppendLine("#");
        text.AppendLine("#   type    <alias>  <kind>      <assembly>  <clr name>");
        text.AppendLine("#   member  <alias>  <[Id(n)]>   <name>      <type>");
        text.AppendLine("#   value   <alias>  <constant>  <name>");
        text.AppendLine(Invariant($"tag\t{tag}"));
        text.AppendLine(Invariant($"commit\t{commit}"));

        foreach (var type in types.OrderBy(x => x.Alias, StringComparer.Ordinal))
        {
            text.AppendLine(Invariant($"type\t{type.Alias}\t{type.Kind}\t{type.Assembly}\t{type.ClrName}"));

            foreach (var member in type.Members)
                text.AppendLine(Invariant($"member\t{type.Alias}\t{member.Id}\t{member.Name}\t{member.Type}"));

            foreach (var value in type.Values)
                text.AppendLine(Invariant($"value\t{type.Alias}\t{value.Value}\t{value.Name}"));
        }

        return text.ToString();
    }

    /// <summary>The inverse of <see cref="Write" />. Throws on a line it does not understand.</summary>
    public static (string Tag, string Commit, List<WireType> Types) Parse(string name, string[] lines)
    {
        var tag = string.Empty;
        var commit = string.Empty;
        var members = new Dictionary<string, List<WireMember>>(StringComparer.Ordinal);
        var values = new Dictionary<string, List<WireValue>>(StringComparer.Ordinal);
        var types = new List<WireType>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split('\t');

            switch (parts[0])
            {
                case "tag" when parts.Length == 2:
                    tag = parts[1];

                    break;

                case "commit" when parts.Length == 2:
                    commit = parts[1];

                    break;

                case "type" when parts.Length == 5:
                    types.Add(new WireType(parts[1], parts[2], parts[3], parts[4], [], []));

                    break;

                case "member" when parts.Length == 5:
                    Bucket(members, parts[1]).Add(new WireMember(
                        int.Parse(parts[2], CultureInfo.InvariantCulture),
                        parts[3],
                        parts[4]));

                    break;

                case "value" when parts.Length == 4:
                    Bucket(values, parts[1]).Add(new WireValue(
                        long.Parse(parts[2], CultureInfo.InvariantCulture),
                        parts[3]));

                    break;

                default:
                    throw new FormatException(
                        $"{name}:{i + 1} is not a record this manifest format has: \"{line}\". The file is "
                        + "generated — regenerate it with ./build.sh Architecture --wire-record rather than "
                        + "repairing it by hand.");
            }
        }

        var joined = types
            .Select(x => x with
            {
                Members = members.TryGetValue(x.Alias, out var m) ? m : [],
                Values = values.TryGetValue(x.Alias, out var v) ? v : [],
            })
            .ToList();

        return (tag, commit, joined);

        static List<T> Bucket<T>(Dictionary<string, List<T>> into, string alias)
            => into.TryGetValue(alias, out var list) ? list : into[alias] = [];
    }

    // ── Comparing ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every way the tree is incompatible with what <paramref name="tag" /> released, each named
    ///     in terms of the type and the member.
    /// </summary>
    /// <param name="tag">The release being compared against, for the message.</param>
    /// <param name="released">What that release published — a parsed manifest.</param>
    /// <param name="current">What the working tree publishes.</param>
    /// <param name="burned">
    ///     Aliases deliberately taken out of circulation — see <c>build/wire/burned.txt</c>.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both directions, and they are different failures rather than one failure said
    ///         twice.</b> An old silo reading a new payload fails when an alias it knows has stopped
    ///         being produced, or when a number it knows now carries a different type. A new silo
    ///         reading an old payload fails on the same two, from the other end. Both reduce to
    ///         "the alias and the numbered slots that were published must still mean what they meant",
    ///         which is what this checks — so the two directions are two sentences in the message
    ///         rather than two passes over the data.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is deliberately not a break.</b> Renaming a type, renaming a member, moving a
    ///         type between assemblies or namespaces, and appending a member at a fresh number are all
    ///         legal — docs/plan/05 § Serialization and schema evolution. Removing a member is legal
    ///         too and leaves its number burned; the number staying out of circulation is enforced
    ///         here only for as long as a release that used it is still in the window, which is the
    ///         first-order reason docs/plan/23 wants three releases rather than one.
    ///     </para>
    /// </remarks>
    public static List<string> Compare(
        string tag,
        IReadOnlyList<WireType> released,
        IReadOnlyList<WireType> current,
        IReadOnlySet<string> burned)
    {
        var tree = ByAlias(current);
        var violations = new List<string>();

        foreach (var was in released.OrderBy(x => x.Alias, StringComparer.Ordinal))
        {
            if (!tree.TryGetValue(was.Alias, out var now))
            {
                if (burned.Contains(was.Alias))
                    continue;

                violations.Add(
                    $"the alias \"{was.Alias}\" was published by {tag} ({was.Assembly}, as {was.ClrName}) and "
                    + "nothing in the tree declares it now. A payload written under it is a payload nothing "
                    + "can read — docs/plan/04 § Failure and upgrade. If the retirement is deliberate, the "
                    + $"alias goes in {BurnedFileName} with the reason and stays out of circulation forever");

                continue;
            }

            if (!string.Equals(was.Kind, now.Kind, StringComparison.Ordinal))
            {
                violations.Add(
                    $"\"{was.Alias}\" was a {was.Kind} in {tag} and is a {now.Kind} now ({now.ClrName}). "
                    + "Orleans encodes the two differently, so every payload either side wrote is "
                    + "unreadable by the other");
            }

            CompareMembers(tag, was, now, violations);
            CompareValues(tag, was, now, violations);
        }

        return violations;
    }

    /// <summary>
    ///     The other half of "out of circulation": a burned alias must not come back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Orleans would hand a peer's old payload to whatever type claimed the name next, which
    ///         is data corruption rather than a deserialization failure — nothing throws, and the
    ///         members line up by number into a type that means something else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Once, not once per baseline</b>, which is why it is not inside
    ///         <see cref="Compare" />: a revived alias has nothing to do with which release is being
    ///         compared against, and reporting it three times would read as three problems.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>There is deliberately no check in the other direction, and the omission is the
    ///         interesting half.</b> <c>durable-grains.txt</c> and <c>module-layering.txt</c> are both
    ///         enforced both ways, because a stale line in either is standing permission nobody
    ///         granted twice. Doing the same here — "every burned alias must appear in some baseline"
    ///         — would be a bug: an alias burned four releases ago is outside the three-release window
    ///         and appears in no manifest, and the entry still has to stay, forever, because forever
    ///         is what "out of circulation" means. The list only grows.
    ///     </para>
    /// </remarks>
    public static List<string> Revived(IReadOnlyList<WireType> current, IReadOnlySet<string> burned)
    {
        var tree = ByAlias(current);

        return burned
            .Order(StringComparer.Ordinal)
            .Where(tree.ContainsKey)
            .Select(alias =>
                $"\"{alias}\" is burned in {BurnedFileName} and {tree[alias].ClrName} "
                + $"({tree[alias].Assembly}) declares it again. A burned alias is out of circulation "
                + "forever — a peer still holding a payload under it would deserialize into the wrong "
                + "type and succeed")
            .ToList();
    }

    /// <summary>
    ///     ⚠ Keyed on <see cref="WireMember.Id" /> throughout. Matching members by name instead would
    ///     report a legal rename as a break and, worse, would miss the renumber — the member would be
    ///     found under its name at its new number and compare equal.
    /// </summary>
    static void CompareMembers(string tag, WireType was, WireType now, List<string> violations)
    {
        var byId = now.Members.ToDictionary(x => x.Id);

        foreach (var member in was.Members)
        {
            // Removed, which is legal: the number is burned and Orleans skips a field the far side
            // does not know. It becomes a break only if something reappears at that number, and the
            // type comparison below is what sees that.
            if (!byId.TryGetValue(member.Id, out var current))
                continue;

            if (string.Equals(member.Type, current.Type, StringComparison.Ordinal))
                continue;

            violations.Add(
                $"\"{was.Alias}\" [Id({member.Id})] was {member.Type} {member.Name} in {tag} and is "
                + $"{current.Type} {current.Name} now ({now.ClrName}). A number is never reused and never "
                + "reordered — docs/plan/05 § Serialization and schema evolution. An old peer decodes the "
                + "new bytes as the old type, which either throws or silently does not");
        }

        // The renumber, seen from the other end. Swapping two members' numbers changes no type at
        // either number when the two have the same type, so the loop above sees nothing — and a
        // renumber into a free slot leaves the vacated one empty, which is legal on its own. This is
        // the sabotage docs/plan/23's row is actually about, and it is only visible by name.
        var byName = now.Members.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var member in was.Members)
        {
            if (byName.TryGetValue(member.Name, out var current) && current.Id != member.Id)
            {
                violations.Add(
                    $"\"{was.Alias}\".{member.Name} was [Id({member.Id})] in {tag} and is [Id({current.Id})] "
                    + $"now ({now.ClrName}). Numbers are never reordered — every payload {tag} wrote puts "
                    + $"that member's bytes at {member.Id}");
            }
        }
    }

    /// <summary>
    ///     ⚠ Keyed on the constant, not the name, because the constant is what is on the wire. A
    ///     renamed enum member is free; a renumbered one silently means something else on the far
    ///     side, which is the worst failure mode this gate has — nothing throws.
    /// </summary>
    static void CompareValues(string tag, WireType was, WireType now, List<string> violations)
    {
        var byValue = now.Values.Select(x => x.Value).ToHashSet();
        var byName = now.Values.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var value in was.Values)
        {
            if (!byValue.Contains(value.Value))
            {
                violations.Add(
                    $"\"{was.Alias}\" published {value.Name} = {value.Value} in {tag} and has no member with "
                    + $"that value now ({now.ClrName}). A peer sending {value.Value} gets an enum this "
                    + "version has no case for");

                continue;
            }

            if (byName.TryGetValue(value.Name, out var current) && current.Value != value.Value)
            {
                violations.Add(
                    $"\"{was.Alias}\".{value.Name} was {value.Value} in {tag} and is {current.Value} now "
                    + $"({now.ClrName}). Nothing throws on this one: a payload written by {tag} arrives and "
                    + "means a different case");
            }
        }
    }

    /// <summary>
    ///     The tree's wire types by alias, first declaration winning a tie.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Tolerant of a duplicate alias on purpose, and the tolerance is not a shrug.</b> Two
    ///     types claiming one alias is a real and serious violation — it is a coin flip at
    ///     deserialization — but it is <see cref="Build.SerializerDisciplineGate" />'s violation to
    ///     report, over the whole tree, once. A plain <c>ToDictionary</c> here would instead throw an
    ///     <see cref="ArgumentException" /> out of the middle of the wire gate, which aborts the run
    ///     before <c>Report</c> prints anything at all: the roster would vanish and the message would
    ///     name a key rather than a rule.
    /// </remarks>
    static Dictionary<string, WireType> ByAlias(IReadOnlyList<WireType> types)
    {
        var byAlias = new Dictionary<string, WireType>(StringComparer.Ordinal);

        foreach (var type in types)
            byAlias.TryAdd(type.Alias, type);

        return byAlias;
    }

    static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>
    ///     Enough of an <see cref="ISignatureTypeProvider{TType,TGenericContext}" /> to spell a
    ///     member's type as a comparable string.
    ///     <para>
    ///         ⚠ Names are unqualified for a <c>TypeRef</c>/<c>TypeDef</c>, which is deliberate: a
    ///         type that moved namespace is not a wire change (the alias is the identity), so a
    ///         namespace-qualified spelling here would report every legal move as a break. What has
    ///         to be caught is <c>string</c> becoming <c>int</c>, or <c>Foo</c> becoming <c>Bar</c>,
    ///         and the unqualified name is enough for both.
    ///     </para>
    /// </summary>
    sealed class SignatureTypeProvider : ISignatureTypeProvider<string, object?>
    {
        public static readonly SignatureTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeReference(handle).Name);

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape)
            => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index.ToString(CultureInfo.InvariantCulture);

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index.ToString(CultureInfo.InvariantCulture);

        // A modifier is `modopt`/`modreq` — `volatile`, `IsExternalInit` on an init-only setter. None
        // of them is on the wire, and every record property in this tree carries the last one.
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }

    /// <summary>The <see cref="ICustomAttributeTypeProvider{T}" /> that decoding an attribute needs.</summary>
    sealed class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly CustomAttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeReference(handle).Name);

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => string.Equals(type, "System.Type", StringComparison.Ordinal);
    }
}
