// The metadata half of Build.Architecture.cs — what the gates read, separated from what they
// decide. docs/plan/03 § Assembly graph rules and docs/plan/05 § Choosing a tier.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Nuke.Common.IO;

/// <summary>
///     One <c>[PersistentState]</c> binding, as it survives into the compiled assembly.
/// </summary>
/// <param name="DeclaringType">
///     The grain type whose constructor takes the annotated parameter, by full metadata name.
/// </param>
/// <param name="StateName">The attribute's first argument — the state name within the grain.</param>
/// <param name="Tier">
///     The attribute's second argument, the storage-provider name — <c>Hot</c> or <c>Durable</c>
///     (<c>CyberCloud.Core.Contracts.StorageTiers</c>). <see langword="null" /> when the one-argument
///     constructor was used, which binds the grain to Orleans' default provider.
/// </param>
sealed record PersistentStateBinding(string DeclaringType, string StateName, string? Tier);

/// <summary>
///     Everything the architecture gates need from one compiled assembly.
/// </summary>
/// <param name="Name">The assembly's simple name, from its <c>AssemblyDef</c> row.</param>
/// <param name="Path">Where it was read from, so a failure message can name a file.</param>
/// <param name="ReferencedAssemblies">
///     The simple names in the <c>AssemblyRef</c> table.
///     <para>
///         ⚠ <b>This is a list of assemblies the compiler actually bound a type from, not a list of
///         packages the project restored</b>, and that distinction is the whole reason the gates read
///         metadata instead of project files. Roslyn omits a reference from <c>AssemblyRef</c> when
///         no type in it was used. docs/plan/03 § Assembly graph rules, rule 3 is a rule about types
///         — <c>Microsoft.Orleans.Hosting.Kubernetes</c> and <c>Orleans.Clustering.Kubernetes</c>
///         (both required by docs/plan/02 § ADR-004) drag <c>KubernetesClient</c> into
///         <c>CyberCloud.ServiceDefaults</c>' restore closure and always will, so a gate phrased over
///         packages would be permanently and unfixably red.
///     </para>
/// </param>
/// <param name="PersistentStateBindings">Every <c>[PersistentState]</c> constructor parameter.</param>
/// <param name="DurableStateRationales">
///     Types carrying <c>[DurableStateRationale]</c> — the escape hatch of docs/plan/05
///     § Choosing a tier — by full metadata name, against the reason each gives. The reason is kept
///     because an empty one is the obvious way to use the hatch without using it.
/// </param>
/// <param name="InterfaceBases">
///     For every interface the assembly declares, the unqualified names of the interfaces it extends
///     <i>directly</i>. Enough for the "every tenant-scoped grain interface is
///     <c>IGrainWithStringKey</c>" half of docs/plan/23 § The architecture gates, row Tenant keys:
///     the key kind is always named on the interface that introduces it, and an interface that
///     extends a CyberCloud grain interface is reached through that one.
/// </param>
/// <param name="OwningHosts">
///     The hosts named by assembly-level <c>[OwningHost]</c> attributes — the escape hatch of
///     docs/plan/03 § Assembly graph rules, rule 4, read the same way
///     <see cref="DurableStateRationales" /> is. Empty for every assembly that declares none, which
///     is the honest default: an application assembly with no declared host has no own host, and
///     rule 4 then permits nothing to reference it.
/// </param>
sealed record AssemblyFacts(
    string Name,
    AbsolutePath Path,
    IReadOnlyList<string> ReferencedAssemblies,
    IReadOnlyList<PersistentStateBinding> PersistentStateBindings,
    IReadOnlyDictionary<string, string?> DurableStateRationales,
    IReadOnlyDictionary<string, IReadOnlyList<string>> InterfaceBases,
    IReadOnlyList<string?> OwningHosts)
{
    /// <summary>
    ///     Reads one assembly without loading it.
    ///     <para>
    ///         <c>System.Reflection.Metadata</c> rather than <c>Assembly.LoadFrom</c> or a
    ///         <c>MetadataLoadContext</c>: the gates run inside the Nuke build process, which is a
    ///         <c>net10.0</c> app that has no business resolving Orleans, ABP or a
    ///         <c>netstandard2.0</c> analyzer into its own load context. It also means the analyzer
    ///         assembly — the one documented exception to docs/plan/02 § Platform baseline's
    ///         single-TFM rule — is read exactly like every other one, with no special case.
    ///     </para>
    /// </summary>
    public static AssemblyFacts Read(AbsolutePath dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var bindings = new List<PersistentStateBinding>();
        var rationales = new Dictionary<string, string?>(StringComparer.Ordinal);
        var interfaceBases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var owningHosts = new List<string?>();

        // ⚠ Assembly-level attributes hang off the AssemblyDef row, not off any TypeDef, so the type
        // walk below never sees them. A null entry is an [OwningHost] whose argument is not a string
        // constant; the gate reports it rather than dropping it, because a silently dropped one
        // would read as "declares no host" and fail somewhere else with the wrong message.
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);

            if (string.Equals(AttributeTypeName(metadata, attribute), OwningHostAttribute, StringComparison.Ordinal))
                owningHosts.Add(FixedStringArguments(metadata, attribute).ElementAtOrDefault(0));
        }

        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeName = FullName(metadata, typeHandle);
            var rationale = FindAttribute(metadata, type.GetCustomAttributes(), DurableStateRationaleAttribute);

            if (rationale is not null)
                rationales[typeName] = FixedStringArguments(metadata, rationale.Value).ElementAtOrDefault(0);

            if (type.Attributes.HasFlag(TypeAttributes.Interface))
                interfaceBases[typeName] = BaseInterfaceNames(metadata, type);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);

                // Only constructors: [PersistentState] is an Orleans DI marker on an activation
                // constructor parameter, and a primary constructor is a constructor in metadata.
                if (!string.Equals(metadata.GetString(method.Name), ".ctor", StringComparison.Ordinal))
                    continue;

                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = metadata.GetParameter(parameterHandle);

                    foreach (var attributeHandle in parameter.GetCustomAttributes())
                    {
                        var attribute = metadata.GetCustomAttribute(attributeHandle);

                        if (!string.Equals(AttributeTypeName(metadata, attribute), PersistentStateAttribute, StringComparison.Ordinal))
                            continue;

                        var arguments = FixedStringArguments(metadata, attribute);

                        bindings.Add(new PersistentStateBinding(
                            typeName,
                            arguments.ElementAtOrDefault(0) ?? "(unnamed)",
                            arguments.ElementAtOrDefault(1)));
                    }
                }
            }
        }

        return new AssemblyFacts(
            metadata.GetString(metadata.GetAssemblyDefinition().Name),
            dll,
            references,
            bindings.OrderBy(x => x.DeclaringType, StringComparer.Ordinal).ThenBy(x => x.StateName, StringComparer.Ordinal).ToList(),
            rationales,
            interfaceBases,
            owningHosts);
    }

    /// <summary>
    ///     The unqualified names of the interfaces a type extends directly. A base living in another
    ///     assembly is a <c>TypeRef</c> and a base in this one is a <c>TypeDef</c>; both spell the
    ///     name the same way, which is all any rule here needs.
    /// </summary>
    static List<string> BaseInterfaceNames(MetadataReader metadata, TypeDefinition type)
    {
        var names = new List<string>();

        foreach (var handle in type.GetInterfaceImplementations())
        {
            var @interface = metadata.GetInterfaceImplementation(handle).Interface;

            var name = @interface.Kind switch
            {
                HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)@interface).Name),
                HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle)@interface).Name),
                _ => null,
            };

            if (name is not null)
                names.Add(name);
        }

        return names;
    }

    // ⚠ Matched on the unqualified attribute name, not the full one. Orleans has moved
    // PersistentStateAttribute between the Orleans and Orleans.Runtime namespaces across major
    // versions, and a gate that goes quietly green on the next Orleans bump because a namespace
    // moved is worse than one that occasionally over-matches. Nothing else in this tree declares a
    // type by either name — CyberCloud.Core.Contracts owns DurableStateRationaleAttribute.
    const string PersistentStateAttribute = "PersistentStateAttribute";
    const string DurableStateRationaleAttribute = "DurableStateRationaleAttribute";
    const string OwningHostAttribute = "OwningHostAttribute";

    static CustomAttribute? FindAttribute(MetadataReader metadata, CustomAttributeHandleCollection attributes, string name)
    {
        foreach (var handle in attributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);

            if (string.Equals(AttributeTypeName(metadata, attribute), name, StringComparison.Ordinal))
                return attribute;
        }

        return null;
    }

    /// <summary>
    ///     The unqualified name of an attribute's type, reached through its constructor handle —
    ///     a <c>MemberRef</c> for an attribute defined elsewhere, a <c>MethodDef</c> for one defined
    ///     in the same assembly.
    /// </summary>
    static string? AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);

                return member.Parent.Kind == HandleKind.TypeReference
                    ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)member.Parent).Name)
                    : null;

            case HandleKind.MethodDefinition:
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);

                return metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name);

            default:
                return null;
        }
    }

    /// <summary>
    ///     The attribute's fixed constructor arguments, as strings, with <c>null</c> for any argument
    ///     that is not one. Named arguments are ignored — no rule here reads one.
    /// </summary>
    static List<string?> FixedStringArguments(MetadataReader metadata, CustomAttribute attribute)
        => attribute
            .DecodeValue(AttributeTypeProvider.Instance)
            .FixedArguments
            .Select(x => x.Value as string)
            .ToList();

    /// <summary>
    ///     The full metadata name of a type — <c>Namespace.Type</c>, and <c>Namespace.Outer+Inner</c>
    ///     for a nested one, which is the spelling <c>durable-grains.txt</c> uses.
    /// </summary>
    static string FullName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(type.Name);

        if (type.IsNested)
            return FullName(metadata, type.GetDeclaringType()) + "+" + name;

        var @namespace = metadata.GetString(type.Namespace);

        return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
    }

    /// <summary>
    ///     The minimum <see cref="ICustomAttributeTypeProvider{T}" /> that
    ///     <see cref="CustomAttribute.DecodeValue{T}" /> insists on.
    ///     <para>
    ///         Every gate here reads string arguments only, so types are represented by their own
    ///         names and nothing needs resolving. Reading the blob by hand instead would mean
    ///         guessing the constructor's arity from the bytes left over, and <c>[PersistentState]</c>
    ///         has both a one- and a two-argument constructor.
    ///     </para>
    /// </summary>
    sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeDefinition(handle).Name);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => reader.GetString(reader.GetTypeReference(handle).Name);

        public string GetTypeFromSerializedName(string name) => name;

        // Every enum-valued attribute argument in this tree is int-backed, and no gate reads one.
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => string.Equals(type, "System.Type", StringComparison.Ordinal);
    }
}
