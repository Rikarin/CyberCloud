using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.ResourceManager.Contracts.Generation;

/// <summary>
///     ADR-012's third surface: the .NET SDK's models, collections, resources and
///     <c>Operation&lt;T&gt;</c> pollers.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/21 § The .NET SDK fixes the shape — <c>{Type}Resource</c> /
///         <c>{Type}Collection</c> / <c>{Type}Data</c> from <c>Azure.ResourceManager</c>,
///         <c>Operation&lt;T&gt;</c> + <c>WaitUntil</c> from <c>Azure.Core</c>, and
///         <c>GetProgressAsync()</c>, which is ours because
///         <i>
///             "Azure's LROs expose no progress; ours
///             do and the SDK should not hide it"
///         </i>.
///     </para>
///     <para>
///         ⚠ <b>Half of the SDK is hand-written and none of it is here.</b> docs/plan/21 § Generation:
///         <i>
///             "Hand-written on top: the credential types, the pipeline policies, the convenience
///             methods … and the tests. Everything else is regenerated per release and never edited."
///         </i>
///         This emitter produces the "everything else" and produces <c>partial</c> types throughout,
///         so the hand-written half extends the generated one in the same type rather than wrapping
///         it — a wrapper is a second surface with a second set of names.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The output is checked in, is in no <c>.csproj</c>, and IS NOW COMPILED ANYWAY —
///             issue #73.
///         </b> Until 2026-09-05 this paragraph said the file's absence from a project was
///         "a stated limitation rather than an oversight", and the limitation it stated was real: the
///         clients name <c>Response&lt;T&gt;</c>, <c>Operation&lt;T&gt;</c>, <c>WaitUntil</c> and
///         <c>AsyncPageable&lt;T&gt;</c>, which are <c>CyberCloud.Sdk</c>'s (the 2026-08-11 decision to
///         reimplement rather than take <c>Azure.Core</c> — see that .csproj), and half of every
///         <c>partial</c> member here is hand-written and does not exist yet. What did not follow was
///         the conclusion. <b>Two defects shipped in this file</b> — <c>CS0101</c> from a duplicated
///         enum name and <c>CS0246</c> from an action's undeclared one — green under every gate,
///         because the <c>Generated surfaces</c> row compares BYTES and byte-identical is not valid.
///         The <c>Generated SDK compiles</c> gate in <c>build/Build.Architecture.cs</c> now hands each
///         checked-in file to Roslyn against the real <c>CyberCloud.Sdk</c>; it is the C# half of what
///         <c>pnpm typecheck:api</c> already did for the TypeScript client, and it found a THIRD
///         family the same day — see <see cref="MemberNaming" />.
///     </para>
///     <para>
///         ⚠ <b>A body is emitted in the shape the wire has, and until issue #79 it was not.</b>
///         The document says <c>{"location":…,"properties":{"persistence":{"mode":"AOF"}}}</c>
///         and this emitter flattened every leaf onto one <c>{Model}Data</c> class, so
///         <c>/properties/persistence/mode</c> became <c>PersistenceMode</c> with
///         <c>[JsonPropertyName("mode")]</c> — a name that collided with the top-level
///         <c>mode</c>'s and that would not have round-tripped even alone, because no flat class has
///         a correct wire name for a nested leaf. Fourteen such duplicates over eight types compiled
///         and would have thrown from <c>System.Text.Json</c> on first use. Each container is now a
///         nested <c>partial</c> class — <see cref="AppendObject" /> — which is what
///         <see cref="TypeScriptEmitter" /> had done from the start over the same document, and
///         docs/plan/21 § Generation's conventions table is where the two are held to one shape.
///     </para>
///     <para>
///         ⚠ <b>Determinism, in a language rather than in JSON.</b> No timestamp, no machine name, no
///         path, no <c>GeneratedCodeAttribute</c> version stamp — every one of those is a file that
///         differs on two machines. Members are emitted in the document's own sorted order, and every
///         number is formatted with <see cref="CultureInfo.InvariantCulture" />.
///     </para>
/// </remarks>
public static class SdkEmitter {
    /// <summary>The directory, under the generated root, this surface is written to.</summary>
    public const string DirectoryName = "sdk";

    /// <summary>The namespace the generated types live in.</summary>
    public const string Namespace = "CyberCloud.Sdk.Generated";

    /// <summary>Emits the SDK source for one api-version's document.</summary>
    /// <param name="document">An emitted OpenAPI document.</param>
    /// <returns>One C# compilation unit, as text.</returns>
    public static string Emit(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        var version = DocumentReader.VersionOf(document);
        var types = DocumentReader.TypesOf(document);
        var names = ModelNames(types);

        var built = new StringBuilder();

        built.Append("// <auto-generated />\n")
            .Append("//\n")
            .Append("// The Cyber Cloud .NET SDK at api-version ")
            .Append(version)
            .Append(".\n")
            .Append("//\n")
            .Append("// Generated from ")
            .Append(OpenApiArtifacts.DirectoryName)
            .Append('/')
            .Append(version)
            .Append(".json by CyberCloud.ResourceManager.Contracts.Generation.SdkEmitter —\n")
            .Append("// docs/plan/02 § ADR-012 and docs/plan/21 § Generation. Hand edits are overwritten by\n")
            .Append("// ./build.sh Generate and fail the Generated surfaces gate.\n")
            .Append("//\n")
            .Append("// ⚠ Every type is partial. The credential types, the pipeline policies and the\n")
            .Append("// convenience methods are hand-written on top and extend these in place —\n")
            .Append("// docs/plan/21 § Generation.\n")
            .Append('\n')
            .Append("#nullable enable\n")
            .Append('\n')
            .Append("using System;\n")
            .Append("using System.Collections.Generic;\n")
            .Append("using System.Text.Json.Nodes;\n")
            .Append("using System.Text.Json.Serialization;\n")
            .Append("using System.Threading;\n")
            .Append("using System.Threading.Tasks;\n")
            .Append('\n')
            .Append("namespace ")
            .Append(Namespace)
            .Append(";\n");

        built.Append("\n/// <summary>The api-version every client in this file sends.</summary>\n")
            .Append("public static class GeneratedApiVersion {\n")
            .Append(
                "    /// <summary>⚠ A date, immutable, and there is no 'latest' — docs/plan/10 § API versioning.</summary>\n"
            )
            .Append("    public const string Value = ")
            .Append(Quote(version))
            .Append(";\n}\n");

        AppendEnvelopeEnums(built, document);

        foreach (var type in types) {
            AppendType(built, type, names[type.ResourceType], version);
        }

        AppendScopes(built, document, version);
        AppendScopeObjects(built, document, version);

        return built.ToString();
    }

    /// <summary>
    ///     The closed sets of the read envelope — <c>ProvisioningState</c> — declared once for the
    ///     file, before the first <c>{Model}Resource</c> that types a member with one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named with no model prefix, on purpose: the envelope is one schema shared by every
    ///     type, so its enum is <c>ProvisioningState</c> rather than twenty-three copies of
    ///     <c>{Model}ProvisioningState</c>. <see cref="AppendResource" /> names the member's type
    ///     through the same <see cref="EnumNaming" /> with the same empty model, which is what keeps
    ///     the declaration and the use one identifier.
    /// </remarks>
    static void AppendEnvelopeEnums(StringBuilder built, JsonObject document) {
        if (document["components"]?["schemas"]?[OpenApiEmitter.ResourceEnvelopeSchema] is not JsonObject envelope) {
            return;
        }

        var leaves = DocumentReader.LeavesOf(envelope);
        AppendEnums(built, EnumNaming.For(string.Empty, leaves), leaves);
    }

    /// <summary>The file one api-version's SDK is written to.</summary>
    /// <param name="apiVersion">The api-version.</param>
    public static string FileNameOf(string apiVersion) => apiVersion + ".cs";

    // ── Model naming ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One model name per resource type, unique across the document.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Built from the declared display name, not pluralised down from the type path.</b>
    ///         English pluralisation is not computable — the same reason
    ///         <c>GroupVersionKind.Plural</c> is carried rather than derived — so <c>servers</c> to
    ///         <c>Server</c> is a guess that works until <c>addresses</c> or <c>indices</c>. When a
    ///         type declares no display name the path segment is used verbatim and the resulting
    ///         <c>DatabasesResource</c> is ugly on purpose: it is visible pressure to declare one.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Collisions are resolved by prefixing the provider, and only for the colliding
    ///             types.
    ///         </b> docs/plan/03 § Providers plans a <c>DBforPostgreSQL/servers</c> and a
    ///         <c>DBforMySQL/servers</c>; prefixing every type would give the other eighteen providers
    ///         names nobody wants to type, and prefixing none would give two types one class.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The provider prefix cannot separate two colliding types in the <i>same</i>
    ///             namespace, so the result is checked rather than assumed.
    ///         </b> A
    ///         <c>Streaming/kafkaClusters/topics</c> and a <c>Streaming/kafkaTopics</c> that both
    ///         declare the display name <c>Topic</c> both resolve to <c>StreamingTopic</c> — the
    ///         prefix is the same because the namespace is. That produced two C# classes with one
    ///         name in a generated file, which is a compiler error in whatever consumes the SDK
    ///         rather than in this build, arriving with no hint of where it came from. The second
    ///         pass falls back to the type path, which is unique by construction — the registry keys
    ///         on it — and throwing is the last resort for the case where even that collides.
    ///     </para>
    /// </remarks>
    internal static ImmutableDictionary<string, string> ModelNames(ImmutableArray<DocumentType> types) {
        var bare = new Dictionary<string, string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var type in types) {
            var name = Pascal(type.DisplayName.Length > 0 ? type.DisplayName : type.TypePath);
            bare[type.ResourceType] = name;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        var resolved = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in types) {
            var name = bare[type.ResourceType];
            var segments = type.ProviderNamespace.Split('.');

            if (counts[name] > 1) {
                name = Pascal(segments[^1]) + name;
            }

            // The prefix separates namespaces and nothing else. Two types in one namespace that
            // still agree fall back to the type path, whose uniqueness the registry already
            // guarantees — `kafkaClusters/topics` gives `KafkaClustersTopics`.
            if (taken.ContainsKey(name)) {
                name = Pascal(segments[^1]) + Pascal(type.TypePath);
            }

            if (taken.TryGetValue(name, out var owner)) {
                throw new InvalidOperationException(
                    $"'{type.ResourceType}' and '{owner}' both generate the SDK model name "
                    + $"'{name}'. Two classes with one name do not compile, and the error would "
                    + "surface in whatever consumes the SDK rather than here. Give one of them a "
                    + "distinct IResourceTypeBuilder.Display."
                );
            }

            taken[name] = type.ResourceType;
            resolved[type.ResourceType] = name;
        }

        return resolved.ToImmutable();
    }

    // ── One resource type ──────────────────────────────────────────────────────────────────────

    static void AppendType(StringBuilder built, DocumentType type, string model, string version) {
        var leaves = DocumentReader.LeavesOf(type.Body);

        AppendEnums(built, EnumNaming.For(model, leaves), leaves);
        AppendData(built, type, model, leaves);
        AppendResource(built, type, model);
        AppendCollection(built, type, model, version);
    }

    /// <summary>
    ///     One C# enum per closed set, so a caller cannot pass a value the API refuses.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is the enum gap's whole payoff on this surface.</b> Without
    ///     <c>SchemaProperty.AllowedValues</c> every one of these was a <c>string</c>, the compiler
    ///     could not catch a typo, and a caller learned the four values from a <c>400</c>.
    ///     <para>
    ///         ⚠ A <c>Unknown = 0</c> member, per this repository's convention and for a sharper
    ///         reason here: a <c>default(T)</c> that named a real sku would silently send one.
    ///     </para>
    /// </remarks>
    static void AppendEnums(
        StringBuilder built,
        EnumNaming naming,
        ImmutableArray<SchemaLeaf> leaves,
        string indent = ""
    ) {
        foreach (var leaf in leaves) {
            var values = DocumentReader.EnumOf(leaf.Schema);

            if (leaf.IsObject || values.IsEmpty) {
                continue;
            }

            built.Append('\n')
                .Append(indent)
                .Append("/// <summary>The values ")
                .Append(Escape(leaf.JsonPointer))
                // A read-only set is the server's vocabulary, not a caller's choice, so "accepts"
                // would describe a write that is refused.
                    .Append(
                        DocumentReader.Flag(leaf.Schema["readOnly"])
                            ? " carries. ⚠ Read-only: the server sets it, and a write that carries it is refused.</summary>\n"
                            : " accepts. ⚠ Closed: the write path refuses anything else.</summary>\n"
                    )
                    .Append(indent)
                    .Append("public enum ")
                    .Append(naming.NameOf(leaf))
                    .Append(" {\n")
                    .Append(indent)
                    .Append("    /// <summary>Never assigned. Not a value the API accepts.</summary>\n")
                    .Append(indent)
                    .Append("    Unknown = 0")
                    .Append(values.IsEmpty ? "\n" : ",\n");

            for (var i = 0; i < values.Length; i++) {
                built.Append('\n')
                    .Append(indent)
                    .Append("    /// <summary>")
                    .Append(Escape(values[i]))
                    .Append("</summary>\n")
                    .Append(indent)
                    .Append("    [JsonStringEnumMemberName(")
                    .Append(Quote(values[i]))
                    .Append(")]\n")
                    .Append(indent)
                    .Append("    ")
                    .Append(Pascal(values[i]))
                    .Append(" = ")
                    .Append((i + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(i == values.Length - 1 ? "\n" : ",\n");
            }

            built.Append(indent).Append("}\n");
        }
    }

    /// <summary>
    ///     How one model's enum leaves are named, and which of them need their nesting to stay
    ///     apart.
    /// </summary>
    /// <param name="Model">The owning model's name.</param>
    /// <param name="Ambiguous">
    ///     The leaf names that appear more than once with an <c>enum</c>. ⚠ Only these take the
    ///     nested form, which is what keeps <c>ClickHouseClusterPreset</c> from becoming
    ///     <c>ClickHouseClusterSizingPreset</c> for no reader's benefit — the same
    ///     "disambiguate only what collides" rule <c>CliEmitter.FlagsOf</c> applies to flag names.
    /// </param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         THIS EXISTS BECAUSE THE NAME WAS <c>model + Pascal(leaf.Name)</c> AND THAT PRODUCED A
    ///         FILE THAT DOES NOT COMPILE.
    ///     </b> <c>CyberCloud.Cache/redis</c> declares <c>mode</c> at
    ///     <c>/properties/mode</c> and again at <c>/properties/persistence/mode</c>, and both are
    ///     closed sets — so <c>generated/sdk/2026-08-01.cs</c> declared
    ///     <c>
    /// public enum
    ///     ValkeyCacheMode
    ///     </c> twice and two properties referred to it. That is <c>CS0101</c> in
    ///     whatever consumes the SDK, it was checked in, and every gate in this repository was green
    ///     over it: nothing here compiled the generated file. Found by running <c>tsc</c> over the
    ///     TypeScript client, which has the same shape and a compiler that was actually run — and
    ///     the C# file has one of its own now, issue #73.
    /// </remarks>
    readonly record struct EnumNaming(string Model, ImmutableHashSet<string> Ambiguous) {
        /// <summary>The naming for one model's leaves.</summary>
        /// <param name="model">The model name.</param>
        /// <param name="leaves">Its leaves.</param>
        public static EnumNaming For(string model, ImmutableArray<SchemaLeaf> leaves) {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var leaf in leaves) {
                if (leaf.IsObject || DocumentReader.EnumOf(leaf.Schema).IsEmpty) {
                    continue;
                }

                counts[leaf.Name] = counts.GetValueOrDefault(leaf.Name) + 1;
            }

            return new(model, [.. counts.Where(static x => x.Value > 1).Select(static x => x.Key)]);
        }

        /// <summary>The C# name one enum leaf takes.</summary>
        /// <param name="leaf">The leaf.</param>
        public string NameOf(SchemaLeaf leaf) =>
            Model + (Ambiguous.Contains(leaf.Name) ? NestedName(leaf.JsonPointer) : Pascal(leaf.Name));
    }

    /// <summary>
    ///     How one emitted class tree's members and nested classes are named, and the three
    ///     collisions that are refused before they reach a compiler.
    /// </summary>
    /// <param name="ByPointer">
    ///     JSON pointer to C# identifier, for every leaf of the body — a container's is the name
    ///     of the property that holds it; its class is <see cref="ClassOf" />. Keyed on the pointer
    ///     because that is the one thing about a leaf the document guarantees is unique;
    ///     <see cref="SchemaLeaf.Name" /> is unique only among its siblings, which is exactly the
    ///     scope a class has.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             THIS EXISTED BECAUSE THE BODY WAS FLATTENED, AND IT SURVIVES THE UN-FLATTENING
    ///             FOR A NARROWER REASON.
    ///         </b> Until issue #79 every leaf of a body was a property of one class, so
    ///         <c>/properties/mode</c> and <c>/properties/persistence/mode</c> both became
    ///         <c>public … Mode { get; set; }</c> on <c>ValkeyCacheData</c> — <c>CS0102</c>, fourteen
    ///         duplicated names over eight declaring types, found by issue #73's gate on its first
    ///         run. The fix then was a nested-form fallback: <c>Mode</c> stayed and the nested one
    ///         became <c>PersistenceMode</c>. That un-collided the identifiers and left both
    ///         properties carrying <c>[JsonPropertyName("mode")]</c>, which is a valid C# program
    ///         <c>System.Text.Json</c> throws on — issue #79. A container is now a nested class
    ///         (<see cref="AppendObject" />), a leaf is named <c>Pascal(name)</c> inside the class its
    ///         parent declares, and two leaves that share a name in the document cannot share a
    ///         scope in C# because they did not share one in JSON. The pair that motivated the
    ///         fallback needs no fallback.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three collisions remain possible, and each throws here naming both pointers</b>,
    ///         for <c>ModelNames</c>' reason: the alternative is a compiler error in generated code
    ///         that names neither the resource type nor the schema that produced it.
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             Two siblings whose names differ in the document and not in C# —
    ///             <c>max_memory</c> beside <c>maxMemory</c> are two JSON members and one identifier.
    ///             <c>CS0102</c>. Counted on <c>Pascal(name)</c> rather than on <c>name</c> for that
    ///             reason; <see cref="EnumNaming" /> has the same hole and is left alone, since no
    ///             document has the shape and a speculative fix is a change nothing can test.
    ///         </item>
    ///         <item>
    ///             A container's class name taken by a sibling leaf — <c>persistence</c> declares the
    ///             nested class <c>PersistenceData</c>, and a sibling leaf named
    ///             <c>persistenceData</c> declares a property of that name in the same class.
    ///             <c>CS0102</c> again, between a type and a property.
    ///         </item>
    ///         <item>
    ///             A member named after the class that holds it — a leaf <c>persistenceData</c>
    ///             INSIDE <c>persistence</c>, or a container <c>properties</c> inside
    ///             <c>properties</c>. <c>CS0542</c>: member names cannot be the same as their
    ///             enclosing type, and that includes a nested type's.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             The class suffix is <c>Data</c>, which is <c>{Type}Data</c>'s suffix one level
    ///             down.
    ///         </b> It cannot be the container's bare name: the property that holds the
    ///         container already has it, and a nested type and a property with one name in one class
    ///         is <c>CS0102</c>. <c>ValkeyCacheData.PropertiesData.PersistenceData</c> reads as what
    ///         it is at the one place a caller writes it, and target-typed <c>new()</c> means the
    ///         caller mostly does not.
    ///     </para>
    /// </remarks>
    readonly record struct MemberNaming(ImmutableDictionary<string, string> ByPointer) {
        /// <summary>The suffix a container's nested class takes.</summary>
        const string ClassSuffix = "Data";

        /// <summary>The naming for one class tree's leaves.</summary>
        /// <param name="owner">The emitted top-level class, for the message when two names collide.</param>
        /// <param name="leaves">Its leaves, containers included — a container declares a property and a class.</param>
        public static MemberNaming For(string owner, ImmutableArray<SchemaLeaf> leaves) {
            var resolved = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

            // Scope + identifier → the pointer that claimed it. A property and a nested type share
            // one declaration space, so both go in the same table.
            var taken = new Dictionary<(string Scope, string Name), string>();

            foreach (var leaf in leaves) {
                var scope = ParentOf(leaf.JsonPointer);
                var enclosing = EnclosingOf(owner, scope);
                var member = Pascal(leaf.Name);

                Claim(taken, scope, enclosing, member, leaf.JsonPointer);
                resolved[leaf.JsonPointer] = member;

                if (leaf.IsObject) {
                    Claim(taken, scope, enclosing, member + ClassSuffix, leaf.JsonPointer + " (its class)");
                }
            }

            return new(resolved.ToImmutable());
        }

        /// <summary>The C# name one leaf's property takes, inside the class its parent declares.</summary>
        /// <param name="leaf">The leaf.</param>
        public string NameOf(SchemaLeaf leaf) => ByPointer[leaf.JsonPointer];

        /// <summary>The nested class one container declares.</summary>
        /// <param name="leaf">The leaf. Must be an object with properties.</param>
        public string ClassOf(SchemaLeaf leaf) => ByPointer[leaf.JsonPointer] + ClassSuffix;

        static void Claim(
            Dictionary<(string Scope, string Name), string> taken,
            string scope,
            string enclosing,
            string name,
            string pointer
        ) {
            if (string.Equals(name, enclosing[(enclosing.LastIndexOf('.') + 1)..], StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"'{pointer}' generates the name '{name}' inside the class '{enclosing}', and a "
                    + "member cannot be named after its enclosing type (CS0542). The error would "
                    + "surface in whatever consumes the SDK rather than here. Rename the schema "
                    + "property."
                );
            }

            if (taken.TryGetValue((scope, name), out var other)) {
                throw new InvalidOperationException(
                    $"'{pointer}' and '{other}' both generate the name '{enclosing}.{name}'. Two "
                    + "declarations with one name in one class do not compile (CS0102), and the "
                    + "error would surface in whatever consumes the SDK rather than here. Rename "
                    + "one of the two schema properties."
                );
            }

            taken[(scope, name)] = pointer;
        }

        /// <summary>
        ///     The dotted C# name of the class a scope's members are declared in —
        ///     <c>ServerData.PropertiesData.PersistenceData</c> for <c>/properties/persistence</c>.
        /// </summary>
        static string EnclosingOf(string owner, string scope) =>
            string.Concat(
                scope.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static segment => "." + Pascal(segment) + ClassSuffix)
                    .Prepend(owner)
            );
    }

    /// <summary>The pointer of a leaf's parent — <c>/properties</c> for <c>/properties/mode</c>, and empty at the top.</summary>
    static string ParentOf(string jsonPointer) => jsonPointer[..jsonPointer.LastIndexOf('/')];

    /// <summary>The direct children of one object, in the document's own order.</summary>
    static ImmutableArray<SchemaLeaf> ChildrenOf(ImmutableArray<SchemaLeaf> leaves, string pointer) => [
        .. leaves.Where(x => string.Equals(ParentOf(x.JsonPointer), pointer, StringComparison.Ordinal))
    ];

    /// <summary>
    ///     The dotted path under <c>/properties</c>, Pascal-cased —
    ///     <c>/properties/persistence/mode</c> is <c>PersistenceMode</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ The <c>properties</c> envelope is dropped for the reason <c>CliEmitter.PathName</c>
    ///     drops it: every provider's body has one and no reader thinks of it as part of the field's
    ///     name. Dropping it also leaves a top-level leaf with the name it already had, so only the
    ///     nested member of a colliding pair moves. ⚠ Enum TYPE names only, since issue #79: they are
    ///     declared at the top of the file, where two leaves named <c>mode</c> do share a scope. A
    ///     property is declared inside its container's class and needs no path in its name.
    /// </remarks>
    static string NestedName(string jsonPointer) {
        var segments = jsonPointer.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length > 1 && string.Equals(segments[0], "properties", StringComparison.Ordinal)) {
            segments = segments[1..];
        }

        return string.Concat(segments.Select(Pascal));
    }

    static void AppendData(
        StringBuilder built,
        DocumentType type,
        string model,
        ImmutableArray<SchemaLeaf> leaves
    ) {
        built.Append("\n/// <summary>The body of a ")
            .Append(Escape(type.ResourceType))
            .Append(".</summary>\n")
            .Append("/// <remarks>")
            .Append(Escape(type.Summary.Length > 0 ? type.Summary : type.DisplayName))
            .Append("</remarks>\n")
            .Append("public sealed partial class ")
            .Append(model)
            .Append("Data {\n");

        AppendObject(
            built,
            EnumNaming.For(model, leaves),
            MemberNaming.For(model + "Data", leaves),
            leaves,
            "",
            "    "
        );

        // ⚠ No special case for tags, and that is the tag fix paying off on this surface. The bag is
        // a property of the emitted body schema now, so it arrives as a leaf like everything else. A
        // branch here would be this emitter knowing a platform fact the document did not carry — which
        // is exactly the arrangement that let the document under-describe the API in the first place.
        built.Append("}\n");
    }

    /// <summary>
    ///     One object of a body: a property per direct child, then a nested class per child that
    ///     is itself an object.
    /// </summary>
    /// <param name="built">The compilation unit being built.</param>
    /// <param name="naming">How this body's closed sets are named.</param>
    /// <param name="members">How this body's properties and nested classes are named — <see cref="MemberNaming" />.</param>
    /// <param name="leaves">Every leaf of the body; this call picks the children of <paramref name="pointer" />.</param>
    /// <param name="pointer">The object's own pointer within the body — empty for the body itself.</param>
    /// <param name="indent">The indentation of the members, one level in from the class.</param>
    /// <param name="annotate">
    ///     Whether a member's <c>&lt;remarks&gt;</c> carries the write-path notes — required on a
    ///     create, read-only, secret, immutable, default. True for a body a caller writes; false
    ///     for an action's payloads and the scope response, where "required on a create" would
    ///     describe a create that does not exist. Those three carried no remarks before issue #79
    ///     routed them through here, and nesting is the only thing that call was meant to change.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             NESTED, NOT FLATTENED, AND THE FLAT FORM WAS A DEFECT RATHER THAN A TASTE —
    ///             issue #79.
    ///         </b> The wire body is nested — <c>CyberCloud.Cache/redis</c> sends
    ///         <c>{"properties":{"persistence":{"mode":"AOF"}}}</c> — and a flat class has no correct
    ///         wire name for a nested leaf: <c>PersistenceMode</c> carried
    ///         <c>[JsonPropertyName("mode")]</c>, colliding with the top-level <c>mode</c>'s (fourteen
    ///         such pairs over eight types, every one a <c>System.Text.Json</c> throw on first use)
    ///         and wrong even alone, because <c>"persistence/mode"</c> is a name the API does not
    ///         have. <see cref="TypeScriptEmitter" /> read the same document and emitted
    ///         <c>persistence?: { mode }</c> from the start; two generated clients disagreed about the
    ///         shape of one API's bodies, and this was the one that was wrong. docs/plan/21
    ///         § Generation's conventions table now states the shape both are held to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Properties first, then the nested classes, in document order.</b> A reader opening
    ///         <c>ValkeyCacheData</c> sees its shape before its parts. The nested class is a
    ///         <c>partial</c> like every other type in the file, so the hand-written half extends a
    ///         container in place too — docs/plan/21 § Generation.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A container is <c>required</c> when the document says so and nullable when it
    ///             does not
    ///         </b>, exactly like a scalar, and never initialised: an optional container whose
    ///         members are <c>required</c> cannot be <c>new()</c>-ed (<c>CS9035</c>, the shape
    ///         issue #73 found 110 of), and an omitted container is what a merge patch means by "not
    ///         changed". A bag — an <c>object</c> with no declared properties, the tag map — stays an
    ///         initialised <c>IDictionary</c>, because it has no members to require.
    ///     </para>
    /// </remarks>
    static void AppendObject(
        StringBuilder built,
        EnumNaming naming,
        MemberNaming members,
        ImmutableArray<SchemaLeaf> leaves,
        string pointer,
        string indent,
        bool annotate = true
    ) {
        var children = ChildrenOf(leaves, pointer);

        foreach (var leaf in children) {
            AppendMember(built, naming, members, leaf, indent, annotate);
        }

        foreach (var leaf in children) {
            if (!leaf.IsObject) {
                continue;
            }

            built.Append('\n')
                .Append(indent)
                .Append("/// <summary>")
                .Append(Escape(Description(leaf)))
                .Append("</summary>\n")
                .Append(indent)
                .Append("public sealed partial class ")
                .Append(members.ClassOf(leaf))
                .Append(" {\n");

            AppendObject(built, naming, members, leaves, leaf.JsonPointer, indent + "    ", annotate);

            built.Append(indent).Append("}\n");
        }
    }

    /// <summary>A leaf's description, or its name when it has none.</summary>
    static string Description(SchemaLeaf leaf) =>
        DocumentReader.Text(leaf.Schema["description"]) is { Length: > 0 } text ? text : leaf.Name;

    /// <summary>One property of an emitted class.</summary>
    /// <param name="built">The compilation unit being built.</param>
    /// <param name="naming">How this class's closed sets are named.</param>
    /// <param name="members">How this class's properties are named — <see cref="MemberNaming" />.</param>
    /// <param name="leaf">The leaf. A container's property is typed as its nested class.</param>
    /// <param name="indent">The property's indentation.</param>
    /// <param name="annotate">Whether the write-path notes go in — <see cref="AppendObject" />.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The <c>[JsonPropertyName]</c> is the leaf's own name, and since issue #79 that is
    ///         the right wire name for every leaf
    ///     </b>, because the property is declared inside the class
    ///     its parent declares — <see cref="AppendObject" />. It was the wrong name for a nested leaf
    ///     for as long as the body was flat, and <c>build/GeneratedSdkSurface.cs</c> now reads every
    ///     one of these attributes off the checked-in file and refuses a name declared twice by one
    ///     type, so the flat form cannot come back green.
    /// </remarks>
    static void AppendMember(
        StringBuilder built,
        EnumNaming naming,
        MemberNaming members,
        SchemaLeaf leaf,
        string indent,
        bool annotate
    ) {
        var schema = leaf.Schema;

        built.Append('\n')
            .Append(indent)
            .Append("/// <summary>")
            .Append(Escape(Description(leaf)))
            .Append("</summary>\n");

        var notes = new List<string>();

        if (annotate && leaf.Required) {
            notes.Add("Required on a create.");
        }

        if (DocumentReader.Flag(schema["readOnly"])) {
            notes.Add("⚠ The server owns this: a body that sets it is refused rather than ignored.");
        }

        if (DocumentReader.Flag(schema["x-cybercloud-secret"])) {
            notes.Add("⚠ Secret. It is never returned — a read leaves this null whatever was set.");
        }

        if (DocumentReader.Flag(schema["x-cybercloud-immutable"])) {
            notes.Add("⚠ Cannot change after create.");
        }

        if (schema["default"] is { } fallback) {
            notes.Add("Defaults to " + fallback.ToJsonString() + " when left unset.");
        }

        if (annotate && notes.Count > 0) {
            built.Append(indent).Append("/// <remarks>").Append(Escape(string.Join(" ", notes))).Append("</remarks>\n");
        }

        built.Append(indent)
            .Append("[JsonPropertyName(")
            .Append(Quote(leaf.Name))
            .Append(")]\n")
            .Append(indent)
            .Append("public ")
            // ⚠ `required` rather than a nullable type or `= null!`. A required property that is not
            // set is a body the API refuses, and C#'s own `required` makes that a compile error at the
            // object initialiser rather than a 400 at run time — which is the whole reason the SDK is
            // generated from the same schema the validator reads.
                .Append(Required(leaf) ? "required " : string.Empty)
                .Append(ClrType(naming, members, leaf))
                .Append(' ')
                .Append(members.NameOf(leaf))
                .Append(" { get; set; }")
                .Append(Initialiser(naming, leaf))
                .Append('\n');
    }

    /// <summary>
    ///     Whether a member takes C#'s <c>required</c> modifier.
    /// </summary>
    /// <remarks>
    ///     ⚠ A read-only property is never <c>required</c>: the server sets it and a caller may not,
    ///     so demanding it at the object initialiser would make the model unusable for a create.
    ///     A collection is never <c>required</c> either — it is initialised empty, which is the same
    ///     thing an omitted array means. A container IS, when its parent lists it: it is a class
    ///     with members of its own and nothing initialises it — <see cref="AppendObject" />.
    /// </remarks>
    static bool Required(SchemaLeaf leaf) =>
        leaf.Required
        && !DocumentReader.Flag(leaf.Schema["readOnly"])
        && (leaf.IsObject
            || DocumentReader.IsJsonValue(leaf.Schema)
            || DocumentReader.TypeOf(leaf.Schema) is not ("array" or "object"));

    /// <summary>
    ///     The CLR type of one leaf.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>An array's element type is real, and this is where gap 2 pays off.</b> Before
    ///     <c>SchemaProperty.ElementKind</c> an array reached the document as <c>items: {}</c> and the
    ///     only honest rendering was <c>IList&lt;object&gt;</c> — a member no caller could use without
    ///     casting and no compiler could check.
    ///     <para>
    ///         ⚠ A container is its own nested class, asked before the JSON type is: to
    ///         <see cref="DocumentReader.TypeOf" /> it is an <c>object</c> like the tag bag, and the
    ///         bag's <c>IDictionary</c> is the wrong answer for anything with declared members.
    ///     </para>
    /// </remarks>
    static string ClrType(EnumNaming naming, MemberNaming members, SchemaLeaf leaf) {
        var schema = leaf.Schema;
        var nullable = DocumentReader.IsNullable(schema) || !leaf.Required;

        if (leaf.IsObject) {
            return members.ClassOf(leaf) + (nullable ? "?" : string.Empty);
        }

        // ⚠ A policy rule, and anything else the document marks as any JSON value: a JsonNode the
        // caller builds or parses, never the tag bag's IDictionary<string, string> an untyped object
        // otherwise reads as — OpenApiEmitter.JsonValueExtension.
        if (DocumentReader.IsJsonValue(schema)) {
            return "JsonNode" + (nullable ? "?" : string.Empty);
        }

        if (!DocumentReader.EnumOf(schema).IsEmpty && DocumentReader.TypeOf(schema) != "array") {
            return naming.NameOf(leaf) + (nullable ? "?" : string.Empty);
        }

        return DocumentReader.TypeOf(schema) switch {
            "array" => "IList<" + Scalar(schema["items"] as JsonObject ?? [], naming, leaf) + ">",
            "object" => "IDictionary<string, string>",
            var scalar => Suffix(Scalar(schema, naming, leaf), scalar, nullable)
        };
    }

    static string Scalar(JsonObject schema, EnumNaming naming, SchemaLeaf leaf) {
        if (!DocumentReader.EnumOf(schema).IsEmpty) {
            return naming.NameOf(leaf);
        }

        return DocumentReader.TypeOf(schema) switch {
            // ⚠ The formats become the CLR types a caller expects rather than strings they have to
            // parse. `Guid` and `DateTimeOffset` are checked by the server too — see SchemaFormat.
            "string" when DocumentReader.Text(schema["format"]) == "uuid" => "Guid",
            "string" when DocumentReader.Text(schema["format"]) == "date-time" => "DateTimeOffset",
            "string" when DocumentReader.Text(schema["format"]) == "uri" => "Uri",
            "string" => "string",
            // long rather than int: an integer in JSON has no width, and a storage size in bytes
            // overflows an int before it overflows anything real.
            "integer" => "long",
            "number" => "double",
            "boolean" => "bool",
            _ => "string"
        };
    }

    static string Suffix(string clr, string jsonType, bool nullable) {
        if (!nullable) {
            return clr;
        }

        // A reference type takes `?` from the nullable context; a value type takes Nullable<T>. Both
        // are spelled `?` and both are correct — this method exists so the reason is written down.
        _ = jsonType;
        return clr + "?";
    }

    static string Initialiser(EnumNaming naming, SchemaLeaf leaf) =>
        leaf.IsObject || DocumentReader.IsJsonValue(leaf.Schema)
            // A container is never initialised — AppendObject says why — and a JSON value is required
            // or null, never an empty dictionary that would go out as a rule with no members.
            ? string.Empty
            : DocumentReader.TypeOf(leaf.Schema) switch {
                // ⚠ Initialised, because a null collection is the member a caller has to new up before
                // using and forgets to. It stays settable so a caller can assign one wholesale.
                "array" => " = new List<" + Scalar(leaf.Schema["items"] as JsonObject ?? [], naming, leaf) + ">();",
                "object" => " = new Dictionary<string, string>(StringComparer.Ordinal);",
                _ => string.Empty
            };

    /// <summary>
    ///     The <c>{Model}Resource</c>: the read envelope's members, then the body, then the
    ///     operations.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The envelope members come from the document, not from a list in this method —
    ///         issue #85.
    ///     </b> Until that issue this class declared <c>Id</c> and nothing else, from a
    ///     string literal here, while the gateway served <c>id</c>, <c>name</c>, <c>type</c>,
    ///     <c>provisioningState</c> and <c>etag</c> and the document described none of them. Every
    ///     member below is a leaf of <see cref="DocumentType.Envelope" />, typed by the same
    ///     <see cref="ClrType" /> every body member is, and always present because
    ///     <see cref="DocumentReader.ReadRequiredOf" /> says so — not <c>required</c> in C#, because
    ///     the hand-written half constructs a resource from a response and sets them after the
    ///     fact, and initialised because CS8618 is a warning the <c>Generated SDK compiles</c> gate
    ///     does not see.
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Declaring the five is half of the promise, and the 2026-09-15 review found the
    ///             other half missing.
    ///         </b> The <c>[JsonPropertyName]</c> emitted on each member reaches
    ///         no serializer — this emitter writes no <c>JsonSerializerContext</c> — so the members
    ///         are populated only if the hand-written half reads the envelope off the response and
    ///         assigns them. It does so through <c>ResourceEnvelope&lt;TProvisioningState&gt;</c> in
    ///         <c>CyberCloud.Sdk</c>, and <c>CyberCloud.Sdk.Tests/StandIn/WidgetStandIn.cs</c> is
    ///         the instance: <c>WidgetResource.Read</c> deserialises the same bytes once as the
    ///         envelope and once as the body. EmitterContract.cs § 1's <c>{Type}Resource</c> row
    ///         states the duty.
    ///     </para>
    /// </remarks>
    static void AppendResource(StringBuilder built, DocumentType type, string model) {
        var envelope = DocumentReader.LeavesOf(type.Envelope);
        var served = DocumentReader.ReadRequiredOf(type.Envelope);
        var naming = EnumNaming.For(string.Empty, envelope);
        var members = MemberNaming.For(model + "Resource", envelope);

        built.Append("\n/// <summary>One ")
            .Append(Escape(type.DisplayName))
            .Append(", as the API returns it, and the operations on it.</summary>\n")
            .Append("public sealed partial class ")
            .Append(model)
            .Append("Resource {\n");

        foreach (var leaf in envelope) {
            var always = served.Contains(leaf.Name);
            var clr = ClrType(naming, members, leaf with { Required = always });

            built.Append("    /// <summary>")
                .Append(Escape(Description(leaf)))
                .Append(always ? " Always present on a read." : string.Empty)
                .Append("</summary>\n")
                .Append("    [JsonPropertyName(")
                .Append(Quote(leaf.Name))
                .Append(")]\n")
                .Append("    public ")
                .Append(clr)
                .Append(' ')
                .Append(members.NameOf(leaf))
                .Append(" { get; init; }")
                // A non-nullable string with no initialiser is CS8618 in the consuming project; an
                // enum's default is its Unknown member, which is the honest value before a response
                // has been read.
                    .Append(clr == "string" ? " = string.Empty;" : string.Empty)
                    .Append('\n');

            if (leaf.JsonPointer != envelope[^1].JsonPointer) {
                built.Append('\n');
            }
        }

        built
            // ⚠ `required`, NOT `= new()`, AND THE INITIALISER WAS CS9035 IN EVERY RESOURCE THAT HAS
            // A REQUIRED BODY MEMBER — 110 of them across 22 {Model}Resource types, found by the
            // `Generated SDK compiles` gate on the day it was added (issue #73). ⚠ 110, and the way
            // to arrive at it is the reason the number is written down: CS9035 is one diagnostic per
            // unset required member per `new()`, so it is the SUM over those 22 sites of each body's
            // required members, not the site count and not the file's 203 `required` lines. Nor is
            // it 111: LoadBalancerData declared `Port` twice, so its seven `required` lines were six
            // required MEMBERS. Taken from Roslyn on 2026-09-05, over the pre-fix
            // generated/sdk/2026-08-01.cs, with the compilation GeneratedSdkSurface.Compile builds.
            //
            // `AppendMember` gives a schema's required properties C#'s own `required`, precisely so
            // that a body the API would refuse does not compile; `new()` is exactly such a body, so
            // the two decisions were in direct contradiction and the second one was in a file no
            // compiler read.
            //
            // `required` rather than dropping the initialiser and leaving it non-nullable, which is
            // CS8618, and rather than making it nullable, which would put a null check on the body of
            // a resource the caller just fetched. A resource without a body is not a resource, and
            // this is that sentence in a form the compiler enforces at every construction site.
            //
            // ⚠ WHAT THIS ASKS OF THE HAND-WRITTEN HALF: a constructor that assigns Data needs
            // [SetsRequiredMembers] — see CyberCloud.Sdk/EmitterContract.cs § 1, where the
            // {Type}Resource row now says so. The stand-in in CyberCloud.Sdk.Tests/StandIn/ is the
            // one instance of that contract and is where it is checked.
                .Append("\n    /// <summary>The body, projected at this api-version.</summary>\n")
                .Append("    public required ")
                .Append(model)
                .Append("Data Data { get; init; }\n")
                .Append("\n    /// <summary>Re-reads the resource.</summary>\n")
                .Append("    public partial Task<Response<")
                .Append(model)
                .Append("Resource>> GetAsync(CancellationToken cancellationToken = default);\n")
                .Append(
                    "\n    /// <summary>Amends the resource. A merge patch: what is not set is not changed.</summary>\n"
                )
                .Append("    public partial Task<Operation<")
                .Append(model)
                .Append("Resource>> UpdateAsync(\n        WaitUntil waitUntil,\n        ")
                .Append(model)
                .Append("Data data,\n        CancellationToken cancellationToken = default);\n")
                .Append("\n    /// <summary>Deletes the resource.")
                .Append(
                    type.SoftDeleteDays > 0
                        ? " ⚠ Recoverable for "
                        + DocumentReader.Count(type.SoftDeleteDays)
                        + " day(s): the resource keeps its quota and its data, and its name is held. "
                        + "Purge to end that window early — a separate permission, '"
                        + type.PurgePermission
                        + "'."
                        : " ⚠ Permanent: this type declares no soft-delete window."
                )
                .Append("</summary>\n")
                .Append("    public partial Task<Operation> DeleteAsync(\n        WaitUntil waitUntil,\n")
                .Append("        CancellationToken cancellationToken = default);\n");

        foreach (var action in type.Actions) {
            AppendAction(built, model, action);
        }

        built.Append("}\n");
    }

    static void AppendAction(StringBuilder built, string model, DocumentAction action) {
        var name = Pascal(action.Name);

        // ⚠ THE ACTION-SCHEMA GAP, CASHED IN. With no declared response an action could only return a
        // JsonElement and the caller had to know its shape from documentation. With one it returns a
        // class, and a secret action's return type is the reviewable list of what leaves the platform.
        // Nested inside the resource, so the names need no model prefix and read as what they are at
        // the call site: `WidgetResource.PingResult`.
        var result = action.Response is null ? "System.Text.Json.JsonElement" : name + "Result";
        var request = action.Request is null ? null : name + "Content";

        if (action.Request is { } requestSchema) {
            AppendPayload(built, request!, requestSchema, "The parameters of " + action.Name + ".");
        }

        if (action.Response is { } responseSchema) {
            AppendPayload(
                built,
                result,
                responseSchema,
                "What "
                + action.Name
                + " returns."
                + (action.Secret ? " ⚠ Secret material: never log or cache this." : string.Empty)
            );
        }

        built.Append("\n    /// <summary>")
            .Append(Escape(name))
            .Append(". ⚠ An action never creates — a POST to a name that does not exist is a 404.")
            .Append(action.Secret ? " ⚠ The response carries secret material and is always audited." : string.Empty)
            .Append("</summary>\n")
            .Append("    public partial Task<")
            .Append(action.LongRunning ? "Operation<" + result + ">" : "Response<" + result + ">")
            .Append("> ")
            .Append(name)
            .Append("Async(\n");

        if (action.LongRunning) {
            built.Append("        WaitUntil waitUntil,\n");
        }

        if (request is { }) {
            built.Append("        ").Append(request).Append(" content,\n");
        }

        built.Append("        CancellationToken cancellationToken = default);\n");
    }

    /// <summary>
    ///     An action's request or response, as a nested class on the resource.
    /// </summary>
    /// <remarks>
    ///     Nested rather than top-level, so <c>listKeys</c> on two resource types cannot collide and
    ///     so the type reads as what it is at the call site: <c>ServerResource.ListKeysResult</c>.
    /// </remarks>
    static void AppendPayload(StringBuilder built, string name, JsonObject schema, string summary) {
        var leaves = DocumentReader.LeavesOf(schema);
        var naming = EnumNaming.For(name, leaves);

        // ⚠ An action payload nests exactly as a body does, because until issue #79 it was
        // flattened exactly as a body was and collided the same way — `CyberCloud.Network/subnets`'
        // listAddressUsage declares `total` and `available` under both `v4` and `v6`, and emitted
        // `SubnetResource.ListAddressUsageResult.Total` twice, then `V4Total` and `V6Total` both
        // named "total" on the wire. It is `ListAddressUsageResult.V4Data.Total` now, which is the
        // shape the TypeScript client had all along.
        var members = MemberNaming.For(name, leaves);

        // ⚠ AN ACTION'S OWN CLOSED SETS, AND LEAVING THEM OUT WAS A GENERATED FILE THAT DOES NOT
        // COMPILE. `ClrType` renders an enum leaf as `{payload}{Member}` whether or not anything
        // declared it, so `CyberCloud.Messaging/kafkaClusters`'s listKeys response emitted
        // `public required ListKeysResultSecurityProtocol SecurityProtocol` against a type that
        // appeared nowhere in the file — CS0246, checked in, and green under every gate here because
        // at the time nothing in this repository compiled generated/sdk/*.cs. ⚠ Something does now:
        // `Generated SDK compiles` in build/Build.Architecture.cs, issue #73. Nested at the payload's
        // own indent because the payload class is nested inside the resource.
        AppendEnums(built, naming, leaves, "    ");

        built.Append("\n    /// <summary>")
            .Append(Escape(summary))
            .Append("</summary>\n")
            .Append("    public sealed partial class ")
            .Append(name)
            .Append(" {\n");

        AppendObject(built, naming, members, leaves, "", "        ", false);

        built.Append("    }\n");
    }

    /// <summary>
    ///     The ancestor parameters a nested type's collection takes, as C# parameter declarations.
    /// </summary>
    /// <param name="type">The resource type.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Until 2026-08-12 this was nothing, and the loss was silent in exactly the way the
    ///             CLI's was.
    ///         </b> A collection for <c>servers/databases</c> emitted
    ///         <c>CreateOrUpdateAsync(WaitUntil, string name, Data, CancellationToken)</c> — a
    ///         signature that compiles, reads perfectly, and cannot build the URL its own
    ///         <c>PathTemplate</c> declares, because <c>{serversName}</c> has no argument. Nothing
    ///         complained: an absent parameter is not a diagnostic anywhere, and the hand-written half
    ///         of the SDK is <c>partial</c>, so the missing piece looked like something a human was
    ///         going to supply.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Ancestors come FIRST, outermost first, which is the order the URL reads in.</b> A
    ///         caller writing <c>GetAsync(serversName, name)</c> is writing the path left to right;
    ///         any other order makes two strings of the same type swappable at the call site with no
    ///         compile error and a 404 at run time.
    ///     </para>
    ///     <para>
    ///         ⚠ Named for the placeholder rather than singularised, for
    ///         <c>CliEmitter.AncestorFlagName</c>'s reason: nothing here knows the singular of a
    ///         provider's own segment.
    ///     </para>
    /// </remarks>
    static ImmutableArray<string> AncestorParametersOf(DocumentType type) {
        var placeholders = DocumentReader.AncestorPlaceholdersOf(type.Path);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var placeholder in placeholders) {
            // ⚠ A type path whose segments repeat — `a/a/b` — would emit `string aName, string aName`,
            // which is CS0100. The `Generated SDK compiles` gate (issue #73) would now catch it, at a
            // line number in a 250 KB file; thrown here so the failure names the type instead.
            if (!seen.Add(Camel(placeholder))) {
                throw new InvalidOperationException(
                    $"'{type.ResourceType}' has two ancestors whose placeholder is '{placeholder}', so "
                    + "the generated collection would declare one parameter name twice and the SDK "
                    + "would not compile. Give the type path distinct segments — docs/plan/12 "
                    + "§ Child resources."
                );
            }
        }

        return [.. placeholders.Select(static x => "string " + Camel(x))];
    }

    /// <summary>An identifier as <c>camelCase</c>, invariantly.</summary>
    static string Camel(string value) {
        var pascal = Pascal(value);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    static void AppendCollection(StringBuilder built, DocumentType type, string model, string version) {
        var ancestors = AncestorParametersOf(type);
        var leading = ancestors.IsEmpty ? string.Empty : string.Join(", ", ancestors) + ", ";

        built.Append("\n/// <summary>The ")
            .Append(Escape(type.DisplayPlural))
            .Append(
                ancestors.IsEmpty
                    ? " in one resource group.</summary>\n"
                    : " in one parent.</summary>\n"
            )
            .Append("/// <remarks>⚠ Every write is long-running: docs/plan/08 § The write path, end to end\n")
            .Append("/// ends in a 202 for every verb, so there is no synchronous overload to offer.")
            .Append(
                ancestors.IsEmpty
                    ? "</remarks>\n"
                    : "\n/// ⚠ The leading parameter(s) name the ancestors this type nests inside —\n"
                    + "/// docs/plan/12 § Child resources addresses a child\n"
                    + "/// '…/{parentType}/{parentName}/{childType}/{childName}', so the parent's name is\n"
                    + "/// part of the address rather than part of the body.</remarks>\n"
            )
            .Append("public sealed partial class ")
            .Append(model)
            .Append("Collection {\n")
            .Append("    /// <summary>The resource type these address.</summary>\n")
            .Append("    public const string ResourceType = ")
            .Append(Quote(type.ResourceType))
            .Append(";\n")
            .Append("\n    /// <summary>The URL template, with the api-version this file was generated at.</summary>\n")
            .Append("    public const string PathTemplate = ")
            .Append(Quote(type.Path))
            .Append(";\n")
            // ⚠ THIS CONST IS THE FIX FOR A URL THIS CLASS HAS ALWAYS PROMISED AND THE PLATFORM
            // NEVER SERVED. GetAllAsync below has been emitted since this file was written, and
            // CyberCloud.Sdk/EmitterContract.cs documents the hand-written half GETting
            // "{scope}/providers/{ns}/{type}" — a path that appeared in no emitted document and on no
            // gateway route, so the only honest thing the hand-written half could do was not exist.
            // The template is now read off the document rather than reassembled by the SDK, which is
            // what stops the two from being "two constants in assemblies that cannot see each other".
                .Append("\n    /// <summary>The collection URL template GetAllAsync pages.</summary>\n")
                .Append("    /// <remarks>⚠ It ends on the type rather than on a name, which is what makes it a\n")
                .Append("    /// collection address and not a resource one — the two grammars are disjoint, see\n")
                .Append("    /// ResourceCollectionId. Empty when this api-version's document declares no such\n")
                .Append("    /// path, in which case GetAllAsync has nothing to page.</remarks>\n")
                .Append("    public const string CollectionPathTemplate = ")
                .Append(Quote(type.CollectionPath))
                .Append(";\n")
                .Append("\n    /// <inheritdoc cref=\"GeneratedApiVersion.Value\" />\n")
                .Append("    public const string ApiVersion = ")
                .Append(Quote(version))
                .Append(";\n")
                .Append("\n    /// <summary>Creates or replaces one ")
                .Append(Escape(type.DisplayName))
                .Append(".</summary>\n")
                .Append("    /// <remarks>⚠ Poll with GetProgressAsync() rather than only WaitForCompletionAsync():\n")
                .Append("    /// docs/plan/21 § The .NET SDK — \"Azure's LROs expose no progress; ours do and the\n")
                .Append("    /// SDK should not hide it\".</remarks>\n")
                .Append("    public partial Task<Operation<")
                .Append(model)
                .Append("Resource>> CreateOrUpdateAsync(\n        WaitUntil waitUntil,\n        ")
                .Append(leading)
                .Append("string name,\n        ")
                .Append(model)
                .Append("Data data,\n        CancellationToken cancellationToken = default);\n")
                .Append("\n    /// <summary>Reads one ")
                .Append(Escape(type.DisplayName))
                .Append(" by name.</summary>\n")
                .Append("    public partial Task<Response<")
                .Append(model)
                .Append("Resource>> GetAsync(")
                .Append(leading)
                .Append("string name, CancellationToken cancellationToken = default);\n")
                .Append("\n    /// <summary>The ")
                .Append(Escape(type.DisplayPlural))
                .Append(ancestors.IsEmpty ? " in this group, paged.</summary>\n" : " in one parent, paged.</summary>\n")
                .Append("    public partial AsyncPageable<")
                .Append(model)
                .Append("Resource> GetAllAsync(")
                // ⚠ The listing takes the ancestors too. A child collection with no ancestor parameter
                // could only list "every database in the group", which is not a scope the API serves —
                // the URL it would GET is the interleaved one with a placeholder left in it.
                .Append(ancestors.IsEmpty ? string.Empty : string.Join(", ", ancestors) + ", ")
                .Append("CancellationToken cancellationToken = default);\n")
                .Append("}\n");
    }

    // ── The scope API, which comes from no provider — issue #63 ────────────────────────────────

    /// <summary>The class name a scope kind takes — <c>resourceGroup</c> is <c>ResourceGroup</c>.</summary>
    static string ScopeName(DocumentScope scope) => Pascal(scope.Kind);

    /// <summary>
    ///     The scope models and the client that reaches them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             One <c>ScopeResource</c> for all three, because the document declares one
    ///             response schema for all three.
    ///         </b> A class per kind would be three identical classes
    ///         whose only difference is the value of <c>Type</c>, and a caller holding a
    ///         <c>SubscriptionResource</c> could not be handed the result of reading a group.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>Task&lt;Response&lt;T&gt;&gt;</c> and never <c>Operation&lt;T&gt;</c>, which
    ///             is the one place a scope differs from every resource in this file.
    ///         </b> Every resource
    ///         write ends in a <c>202</c> and therefore in a poller; a scope converges before the
    ///         call returns, so there is no <c>WaitUntil</c> parameter to take and no operation URL
    ///         to poll. An SDK that offered one would hand every caller a poller for an operation
    ///         that was never started.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The path parameters are the template's own placeholders, in order.</b> The same
    ///         rule <c>AncestorParametersOf</c> follows for a nested resource: a method signature
    ///         written from the kind would be a second place that knows a group is addressed through
    ///         a subscription.
    ///     </para>
    /// </remarks>
    static void AppendScopes(StringBuilder built, JsonObject document, string version) {
        var scopes = DocumentReader.ScopesOf(document);

        if (scopes.IsEmpty) {
            return;
        }

        var shared = document["components"]?["schemas"]?[ScopeResponseComponent] as JsonObject ?? [];
        var leaves = DocumentReader.LeavesOf(shared);

        // ⚠ THE ENUMS FIRST, AND LEAVING THEM OUT IS A GENERATED FILE THAT DOES NOT COMPILE. The
        // scope response's `type` is a closed set of three, so ClrType renders it as
        // `ScopeResourceType` — a name nothing else would ever emit. The first run of this method
        // produced exactly that: a property whose type was undeclared, in a file no build in this
        // repository compiled, so nothing here would have caught it either. ⚠ That last clause is no
        // longer true — `Generated SDK compiles`, issue #73 — and this comment is left standing
        // because it is the reason the gate exists rather than a claim about today.
        AppendEnums(built, EnumNaming.For("ScopeResource", leaves), leaves);

        // A scope body nests like every other, so it is named like every other — see
        // MemberNaming and AppendObject.
        var scopeNaming = EnumNaming.For("ScopeResource", leaves);
        var scopeMembers = MemberNaming.For("ScopeResource", leaves);

        built.Append("\n/// <summary>A tenant, a subscription or a resource group, as the API renders it.</summary>\n")
            .Append("/// <remarks>⚠ There is no provisioningState and no Operation&lt;T&gt; anywhere on this\n")
            .Append("/// path: a scope is one grain activation and converges before the call returns, which is\n")
            .Append("/// the visible half of \"a scope is not a resource\" — docs/plan/10 § Shape.</remarks>\n")
            .Append("public sealed partial class ScopeResource {\n");

        // ⚠ `required` on the members the schema requires, exactly as every body gets it. A
        // non-nullable string with no initialiser and no `required` is CS8618 in whatever project
        // consumes this file, and until issue #73 nothing in THIS repository compiled it at all.
        // ⚠ That gate does NOT close this one: `Generated SDK compiles` is errors-only on purpose
        // (which analysers a consuming project runs is that project's business —
        // GeneratedSdkSurface says so), and CS8618 is a warning. So the first person to find out
        // would still be the first person to use the SDK, and AppendMember's `required` is what
        // stops there being anything to find.
        AppendObject(built, scopeNaming, scopeMembers, leaves, "", "    ", false);

        built.Append("}\n");

        foreach (var scope in scopes) {
            if (!scope.Creatable) {
                continue;
            }

            AppendScopeContent(built, scope);
        }

        built.Append("\n/// <summary>The scope API — docs/plan/06 § The hierarchy.</summary>\n")
            .Append("/// <remarks>⚠ Generated from the OpenAPI document like everything else in this file,\n")
            .Append("/// and the document is where the scope paths were missing until issue #63: a scope has\n")
            .Append("/// no provider, no resource type and no api-version of its own, so nothing emitted from\n")
            .Append("/// the provider registry could have known these addresses existed.</remarks>\n")
            .Append("public sealed partial class ScopeClient {\n")
            .Append("    /// <inheritdoc cref=\"GeneratedApiVersion.Value\" />\n")
            .Append("    public const string ApiVersion = ")
            .Append(Quote(version))
            .Append(";\n");

        foreach (var scope in scopes) {
            var name = ScopeName(scope);
            var parameters = DocumentReader.PlaceholdersOf(scope.Path)
                .Select(static x => "string " + Camel(x))
                .ToList();

            built.Append("\n    /// <summary>The URL template ")
                .Append(Escape(scope.DisplayName.ToLowerInvariant()))
                .Append(" operations address.</summary>\n")
                .Append("    public const string ")
                .Append(name)
                .Append("PathTemplate = ")
                .Append(Quote(scope.Path))
                .Append(";\n")
                .Append("\n    /// <summary>The type string a ")
                .Append(Escape(scope.DisplayName.ToLowerInvariant()))
                .Append(" response carries.</summary>\n")
                .Append("    public const string ")
                .Append(name)
                .Append("Type = ")
                .Append(Quote(scope.TypeName))
                .Append(";\n")
                .Append("\n    /// <summary>Reads one ")
                .Append(Escape(scope.DisplayName.ToLowerInvariant()))
                .Append(". ")
                .Append(Escape(scope.Summary))
                .Append("</summary>\n")
                .Append("    public partial Task<Response<ScopeResource>> Get")
                .Append(name)
                .Append("Async(\n        ")
                .Append(string.Join(",\n        ", parameters))
                .Append(parameters.Count > 0 ? ",\n        " : "\n        ")
                .Append("CancellationToken cancellationToken = default);\n");

            if (scope.CollectionPath.Length > 0) {
                // ⚠ Emitted only when the document declares the collection — the tenant has none,
                // and a ListTenantsAsync would page a URL the gateway does not serve. The template
                // is read off the document for the reason CollectionPathTemplate is on a resource
                // collection: the hand-written half pages what the document says, never a path it
                // reassembled.
                var collectionParameters = DocumentReader.PlaceholdersOf(scope.CollectionPath)
                    .Select(static x => "string " + Camel(x))
                    .ToList();

                built.Append("\n    /// <summary>The collection URL template List")
                    .Append(name)
                    .Append("sAsync pages.</summary>\n")
                    .Append("    public const string ")
                    .Append(name)
                    .Append("CollectionPathTemplate = ")
                    .Append(Quote(scope.CollectionPath))
                    .Append(";\n")
                    .Append("\n    /// <summary>The ")
                    .Append(Escape(scope.DisplayPlural.ToLowerInvariant()))
                    .Append(" the caller may read, paged.</summary>\n")
                    .Append("    /// <remarks>⚠ A short page never means \"that is all there is\": the page holds\n")
                    .Append("    /// what the caller may read and the envelope carries no count.</remarks>\n")
                    .Append("    public partial AsyncPageable<ScopeResource> List")
                    .Append(name)
                    .Append("sAsync(\n        ")
                    .Append(string.Join(",\n        ", collectionParameters))
                    .Append(collectionParameters.Count > 0 ? ",\n        " : "\n        ")
                    .Append("CancellationToken cancellationToken = default);\n");
            }

            if (!scope.Creatable) {
                // ⚠ SAID IN THE GENERATED FILE, because "there is no create" and "the create was
                // forgotten" are indistinguishable to somebody reading a class with one method.
                built.Append("\n    // ⚠ There is no Create")
                    .Append(name)
                    .Append("Async, and the absence is the contract rather than an omission. A ")
                    .Append("request's\n    // tenant is resolved from its token, so a call creating ")
                    .Append("another tenant necessarily\n    // carries a token that is not that ")
                    .Append("tenant's and is refused before routing runs —\n    // ")
                    .Append("IScopeManager.CreateTenantAsync is off the request pipeline entirely.\n");

                continue;
            }

            built.Append("\n    /// <summary>Creates one ")
                .Append(Escape(scope.DisplayName.ToLowerInvariant()))
                .Append(", or returns the existing one unchanged.</summary>\n")
                .Append("    /// <remarks>⚠ No WaitUntil and no Operation&lt;T&gt;: this converges before it\n")
                .Append("    /// returns. Repeating it with the same address is a success — 201 the first time\n")
                .Append("    /// and 200 after, which is what makes the verb PUT.</remarks>\n")
                .Append("    public partial Task<Response<ScopeResource>> Create")
                .Append(name)
                .Append("Async(\n        ")
                .Append(string.Join(",\n        ", parameters))
                .Append(parameters.Count > 0 ? ",\n        " : "\n        ")
                .Append(name)
                .Append("CreateContent content,\n        CancellationToken cancellationToken = default);\n");
        }

        built.Append("}\n");
    }

    static void AppendScopeContent(StringBuilder built, DocumentScope scope) {
        var name = ScopeName(scope) + "CreateContent";
        var leaves = DocumentReader.LeavesOf(scope.Body);
        var naming = EnumNaming.For(name, leaves);
        var members = MemberNaming.For(name, leaves);

        AppendEnums(built, naming, leaves);

        built.Append("\n/// <summary>The body of a PUT that creates a ")
            .Append(Escape(scope.DisplayName.ToLowerInvariant()))
            .Append(".</summary>\n")
            .Append("public sealed partial class ")
            .Append(name)
            .Append(" {\n");

        AppendObject(built, naming, members, leaves, "", "    ");

        built.Append("}\n");
    }

    /// <summary>
    ///     The objects addressed on a scope — issue #46's policy — as one read model and one write
    ///     body per object, and one <c>{Group}Client</c> with a method per object, scope and verb.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No <c>WaitUntil</c> and no <c>Operation&lt;T&gt;</c>, as on <c>ScopeClient</c>.</b> One
    ///         catalog write converges before the call returns, so a poller would poll an operation that
    ///         was never started.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A method per scope rather than a scope parameter</b> — <c>GetPolicyDefinitionAtSubscriptionAsync</c>
    ///         beside <c>…AtManagementGroupAsync</c> — because the scopes take different parameters, and a
    ///         method that took a path would be the caller assembling the URL the SDK exists to assemble.
    ///     </para>
    /// </remarks>
    static void AppendScopeObjects(StringBuilder built, JsonObject document, string version) {
        var objects = DocumentReader.ScopeObjectsOf(document);

        if (objects.IsEmpty) {
            return;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scoped in objects) {
            if (declared.Add(scoped.ModelName)) {
                AppendScopeObjectModel(
                    built,
                    scoped.ModelName,
                    scoped.Resource,
                    scoped.DisplayName + ", as the API renders it. " + scoped.Summary,
                    false
                );
            }

            if (scoped.ContentName.Length > 0 && declared.Add(scoped.ContentName)) {
                AppendScopeObjectModel(
                    built,
                    scoped.ContentName,
                    scoped.Content,
                    "The body of a PUT that writes a " + scoped.DisplayName.ToLowerInvariant() + ".",
                    true
                );
            }
        }

        foreach (var family in objects.GroupBy(static x => x.Group, StringComparer.Ordinal)
                     .OrderBy(static x => x.Key, StringComparer.Ordinal)) {
            built.Append("\n/// <summary>The objects under ")
                .Append(Escape(family.First().ProviderNamespace))
                .Append(", on every scope that takes them — docs/plan/08 § Policy.</summary>\n")
                .Append("/// <remarks>⚠ No WaitUntil and no Operation&lt;T&gt;: a write converges before the call\n")
                .Append("/// returns — 201 the first time and 200 after on a PUT, 204 on a DELETE.</remarks>\n")
                .Append("public sealed partial class ")
                .Append(Pascal(family.Key))
                .Append("Client {\n")
                .Append("    /// <inheritdoc cref=\"GeneratedApiVersion.Value\" />\n")
                .Append("    public const string ApiVersion = ")
                .Append(Quote(version))
                .Append(";\n");

            foreach (var scoped in family) {
                AppendScopeObjectMethods(built, scoped);
            }

            built.Append("}\n");
        }
    }

    static void AppendScopeObjectModel(StringBuilder built, string name, JsonObject schema, string summary, bool writable) {
        var leaves = DocumentReader.LeavesOf(schema);
        var naming = EnumNaming.For(name, leaves);
        var members = MemberNaming.For(name, leaves);

        AppendEnums(built, naming, leaves);

        built.Append("\n/// <summary>")
            .Append(Escape(summary))
            .Append("</summary>\n")
            .Append("public sealed partial class ")
            .Append(name)
            .Append(" {\n");

        AppendObject(built, naming, members, leaves, "", "    ", writable);

        built.Append("}\n");
    }

    static void AppendScopeObjectMethods(StringBuilder built, DocumentScopeObject scoped) {
        var what = Escape(scoped.DisplayName.ToLowerInvariant()) + " on a " + Escape(CliEmitter.Kebab(scoped.Scope).Replace('-', ' '));
        var collectionParameters = DocumentReader.PlaceholdersOf(scoped.CollectionPath)
            .Select(static x => "string " + Camel(x))
            .ToList();

        built.Append("\n    /// <summary>The collection URL template List")
            .Append(scoped.PluralStem)
            .Append("Async pages.</summary>\n")
            .Append("    public const string ")
            .Append(scoped.PluralStem)
            .Append("PathTemplate = ")
            .Append(Quote(scoped.CollectionPath))
            .Append(";\n")
            .Append("\n    /// <summary>The ")
            .Append(Escape(scoped.DisplayPlural.ToLowerInvariant()))
            .Append(" on a ")
            .Append(Escape(CliEmitter.Kebab(scoped.Scope).Replace('-', ' ')))
            .Append(", paged. ")
            .Append(Escape(scoped.Summary))
            .Append("</summary>\n")
            .Append("    public partial AsyncPageable<")
            .Append(scoped.ModelName)
            .Append("> List")
            .Append(scoped.PluralStem)
            .Append("Async(\n        ")
            .Append(string.Join(",\n        ", collectionParameters))
            .Append(collectionParameters.Count > 0 ? ",\n        " : string.Empty)
            .Append("CancellationToken cancellationToken = default);\n");

        if (scoped.Path.Length == 0) {
            return;
        }

        var parameters = DocumentReader.PlaceholdersOf(scoped.Path)
            .Select(static x => "string " + Camel(x))
            .ToList();
        var signature = string.Join(",\n        ", parameters) + ",\n        ";

        built.Append("\n    /// <summary>The URL template one ")
            .Append(what)
            .Append(" is addressed at.</summary>\n")
            .Append("    public const string ")
            .Append(scoped.SingularStem)
            .Append("PathTemplate = ")
            .Append(Quote(scoped.Path))
            .Append(";\n")
            .Append("\n    /// <summary>Reads one ")
            .Append(what)
            .Append(".</summary>\n")
            .Append("    public partial Task<Response<")
            .Append(scoped.ModelName)
            .Append(">> Get")
            .Append(scoped.SingularStem)
            .Append("Async(\n        ")
            .Append(signature)
            .Append("CancellationToken cancellationToken = default);\n");

        if (!scoped.Writable) {
            return;
        }

        built.Append("\n    /// <summary>Creates or replaces one ")
            .Append(what)
            .Append(", written whole.</summary>\n")
            .Append("    /// <remarks>⚠ No WaitUntil: 201 the first time and 200 after, and nothing to poll.</remarks>\n")
            .Append("    public partial Task<Response<")
            .Append(scoped.ModelName)
            .Append(">> CreateOrUpdate")
            .Append(scoped.SingularStem)
            .Append("Async(\n        ")
            .Append(signature)
            .Append(scoped.ContentName)
            .Append(" content,\n        CancellationToken cancellationToken = default);\n")
            .Append("\n    /// <summary>Deletes one ")
            .Append(what)
            .Append(". An object already gone is a success.</summary>\n")
            .Append("    public partial Task<Response> Delete")
            .Append(scoped.SingularStem)
            .Append("Async(\n        ")
            .Append(signature)
            .Append("CancellationToken cancellationToken = default);\n");
    }

    /// <summary>
    ///     The component every scope's <c>200</c> body points at.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="OpenApiEmitter.ScopeSchema" />, not a second spelling of it. A <c>$ref</c>
    ///     string is an unchecked string in both directions, and the failure would be a generated
    ///     <c>ScopeResource</c> with no properties at all — which compiles.
    /// </remarks>
    const string ScopeResponseComponent = OpenApiEmitter.ScopeSchema;

    // ── Small shared machinery ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Any identifier-ish string as <c>PascalCase</c>, invariantly.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="char.ToUpperInvariant(char)" /> throughout: under <c>tr-TR</c> a culture-aware
    ///     upper of <c>i</c> is <c>İ</c>, which would emit a different — and uncompilable — identifier
    ///     than the one CI checked in. A leading digit is prefixed rather than dropped, because
    ///     <c>1x</c> and <c>x</c> would otherwise be one member.
    /// </remarks>
    internal static string Pascal(string value) {
        var built = new StringBuilder(value.Length);
        var upper = true;

        foreach (var current in value) {
            if (!char.IsLetterOrDigit(current)) {
                upper = true;
                continue;
            }

            built.Append(upper ? char.ToUpperInvariant(current) : current);
            upper = false;
        }

        if (built.Length == 0) {
            return "Value";
        }

        return char.IsAsciiDigit(built[0]) ? "N" + built : built.ToString();
    }

    /// <summary>A C# string literal. ⚠ Verbatim-free, so a backslash cannot end the literal early.</summary>
    static string Quote(string value) =>
        "\""
        + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\"";

    /// <summary>
    ///     Text that is safe inside an XML doc comment, on one line.
    /// </summary>
    /// <remarks>
    ///     ⚠ A newline in a description would end the <c>///</c> and turn the rest of the sentence
    ///     into a syntax error, so it is folded rather than escaped.
    /// </remarks>
    static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
