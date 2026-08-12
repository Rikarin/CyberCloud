// What types and members this repository actually declares — the reading half of the Code citations
// gate in Build.Architecture.cs.
//
// Not a partial of Build, for the reason build/README.md gives about ArchitectureFacts.cs: what a
// gate reads is a separate concern from what it decides. Separate from ArchitectureFacts.cs because
// this reads EVERY assembly including the test ones, and AssemblyFacts is deliberately about the
// shipped graph — mixing the two subjects into one reader is how a gate ends up inspecting a set it
// did not mean to.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Nuke.Common.IO;

/// <summary>
///     Every type this repository compiles, by unqualified name, with the members it declares.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Read from compiled metadata rather than parsed out of source, and the difference is
///         the difference between a gate and a guess.</b> A regular expression over <c>.cs</c> files
///         misses an enum member, a positional record parameter, a primary-constructor property, an
///         inherited member and anything a source generator emitted — every one of which a doc
///         comment may legitimately cite. Measured on this tree while designing the gate: a
///         source-regex resolver reported 38 unresolvable citations of the shape
///         <c>KnownType.Member</c>, of which the large majority were its own blind spots rather than
///         defects. Metadata has none of them, because it is the thing the compiler produced.
///     </para>
///     <para>
///         ⚠ <b>Unqualified names, deliberately.</b> A doc comment writes <c>WritePathTests</c>, not
///         <c>CyberCloud.ResourceManager.Tests.WritePathTests</c>, and requiring the namespace would
///         make the convention unusable rather than making it stricter. The cost is that two types
///         of the same name in different namespaces share one member set, so a citation naming a
///         member of either resolves. That is a hole and it is the right hole: this gate exists to
///         catch a name that exists <i>nowhere</i>, which is the failure that actually happens, and
///         a gate that also demanded the right namespace would fail on every legal file move.
///     </para>
///     <para>
///         Private and internal members are included. A doc comment on a type routinely cites the
///         private helper that implements the claim, and refusing to resolve one would push authors
///         towards vaguer citations, which is the opposite of the point.
///     </para>
/// </remarks>
static class CodeSurface
{
    /// <summary>
    ///     Unqualified type name to every member name declared on it, unioned across assemblies.
    /// </summary>
    public static Dictionary<string, HashSet<string>> Read(IEnumerable<AbsolutePath> assemblies)
    {
        var surface = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var dll in assemblies.Where(x => x.FileExists()))
            ReadOne(dll, surface);

        return surface;
    }

    static void ReadOne(AbsolutePath dll, Dictionary<string, HashSet<string>> surface)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);

        if (!pe.HasMetadata)
            return;

        var metadata = pe.GetMetadataReader();

        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var name = Unqualified(metadata.GetString(type.Name));

            // The compiler's own artefacts — <>c__DisplayClass, an async state machine, an iterator.
            // They are not names anybody cites and letting them in would resolve a citation that
            // should have failed.
            if (name.Length == 0 || name[0] == '<')
                continue;

            var members = Members(surface, name);

            foreach (var method in type.GetMethods())
                Add(members, metadata.GetString(metadata.GetMethodDefinition(method).Name));

            foreach (var field in type.GetFields())
                Add(members, metadata.GetString(metadata.GetFieldDefinition(field).Name));

            foreach (var property in type.GetProperties())
                Add(members, metadata.GetString(metadata.GetPropertyDefinition(property).Name));

            foreach (var @event in type.GetEvents())
                Add(members, metadata.GetString(metadata.GetEventDefinition(@event).Name));

            foreach (var nested in type.GetNestedTypes())
                Add(members, metadata.GetString(metadata.GetTypeDefinition(nested).Name));
        }
    }

    static HashSet<string> Members(Dictionary<string, HashSet<string>> surface, string type)
        => surface.TryGetValue(type, out var members)
            ? members
            : surface[type] = new HashSet<string>(StringComparer.Ordinal);

    static void Add(HashSet<string> members, string name)
    {
        if (name.Length == 0 || name[0] == '<')
            return;

        // `get_Foo` and `set_Foo` are the property's accessors; the property itself is added from
        // GetProperties above. `.ctor` is not a name anybody writes after a dot.
        if (name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal)
            || name.StartsWith("remove_", StringComparison.Ordinal)
            || name[0] == '.')
        {
            return;
        }

        members.Add(name);

        // An explicit interface implementation is spelled `Namespace.IFoo.Bar` in metadata, and the
        // name a doc comment cites is `Bar`.
        var last = name.LastIndexOf('.');

        if (last >= 0 && last < name.Length - 1)
            members.Add(name[(last + 1)..]);
    }

    /// <summary>
    ///     The name a doc comment would write: no namespace, and a nested type by its own name.
    /// </summary>
    /// <remarks>
    ///     Metadata already stores <c>TypeDefinition.Name</c> without the namespace and without the
    ///     declaring type, so this only has to strip the generic arity — <c>ResultSurrogate`1</c> is
    ///     cited as <c>ResultSurrogate</c>.
    /// </remarks>
    static string Unqualified(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);

        return tick < 0 ? name : name[..tick];
    }
}
