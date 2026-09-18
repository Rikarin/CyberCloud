using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CyberCloud.Conformance.Harness;

/// <summary>
///     The operator-owned <c>CustomResourceDefinition</c>s this repository commits under
///     <c>charts/bundle/&lt;component&gt;/crds/</c>, read once and indexed by group and kind.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are the REAL definitions, taken from the release each component pins — issue
///         #91.</b> <c>charts/bundle/crds.sh --refresh</c> fetches the pinned chart or manifest,
///         keeps the definition of every kind a chart under <c>charts/managed/</c> renders, and
///         commits it byte for byte; the <c>Definitions</c> row of <c>./build.sh Architecture</c>
///         re-fetches and compares. So what <see cref="FakeKubeCluster" /> validates against here is
///         what the API server would validate against on a cluster the bundle installed, and a shape
///         the operator refuses is a shape this harness refuses.
///     </para>
///     <para>
///         ⚠ <b>Absent is not open.</b> A kind with no committed definition is echoed by the fake
///         exactly as before — every core and <c>apps</c> object, and any custom kind nothing under
///         <c>charts/managed/</c> renders. That is the state every custom kind was in for a month
///         with a live defect behind it, which is why it is not left to chance:
///         <c>ProviderConformanceTests.EveryCustomKindTheCaseRendersHasACommittedDefinition</c>
///         fails a case whose <c>Objects</c> name a custom kind this catalogue does not hold, and
///         names the file <c>crds.sh</c> would write.
///     </para>
///     <para>
///         ⚠ <b>Read from the working tree, by walking up to <c>CyberCloud.slnx</c>.</b> The same
///         locator every other file-reading test in this repository uses, and for the same reason:
///         the number of <c>..</c> segments between a test assembly and the root is a property of the
///         artifacts layout. A copy of the files into the test output would be a second set of bytes
///         for the gate to keep in step.
///     </para>
/// </remarks>
public static class CommittedDefinitions {
    static readonly Lazy<ImmutableDictionary<string, CustomResourceDefinition>> definitions = new(Load);

    /// <summary>Where the definitions live, relative to the repository root.</summary>
    public const string Directory = "charts/bundle";

    /// <summary>Every committed definition, keyed by <c>{group}/{kind}</c>.</summary>
    public static ImmutableDictionary<string, CustomResourceDefinition> All => definitions.Value;

    /// <summary>
    ///     The definition serving this kind, or <see langword="null" /> when none is committed — a
    ///     built-in, or a custom kind no chart renders.
    /// </summary>
    /// <param name="kind">The kind, whose group and kind name select the definition.</param>
    /// <remarks>
    ///     ⚠ By group and kind, not by plural: the plural is what the REST path uses and a case's
    ///     <see cref="ObjectRef" /> supplies it, but the definition is the authority on it, and a case
    ///     that spelled the plural wrong should be refused by the version check rather than silently
    ///     echoed for want of a match.
    /// </remarks>
    public static CustomResourceDefinition? Find(GroupVersionKind kind) {
        ArgumentNullException.ThrowIfNull(kind);

        return All.TryGetValue(kind.Group + "/" + kind.Kind, out var definition) ? definition : null;
    }

    /// <summary>The repository root, found by walking up from the test assembly to <c>CyberCloud.slnx</c>.</summary>
    public static string RepositoryRoot {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException(
                    "No CyberCloud.slnx above " + AppContext.BaseDirectory + ", so " + Directory
                    + "/*/crds/ cannot be found and no custom resource can be validated."
                );
        }
    }

    static ImmutableDictionary<string, CustomResourceDefinition> Load() {
        var root = Path.Combine(RepositoryRoot, Directory.Replace('/', Path.DirectorySeparatorChar));
        var found = ImmutableDictionary.CreateBuilder<string, CustomResourceDefinition>(StringComparer.Ordinal);

        if (!System.IO.Directory.Exists(root)) {
            return found.ToImmutable();
        }

        foreach (var file in System.IO.Directory
                     .EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)
                     .Where(x => string.Equals(new DirectoryInfo(Path.GetDirectoryName(x)!).Name, "crds", StringComparison.Ordinal))
                     .OrderBy(x => x, StringComparer.Ordinal)) {
            // Forward slashes whatever the host, so a message names the file the way the tree does.
            var definition = CustomResourceDefinition.Parse(File.ReadAllText(file), Path.GetRelativePath(RepositoryRoot, file).Replace('\\', '/'));
            var key = definition.Group + "/" + definition.Kind;

            if (found.TryGetValue(key, out var other)) {
                throw new InvalidOperationException(
                    $"{definition.File} and {other.File} both define {definition.Kind} in {definition.Group}. "
                    + "One definition has one owner — charts/bundle/README.md § `serves:` is the load-bearing key."
                );
            }

            found[key] = definition;
        }

        return found.ToImmutable();
    }
}

/// <summary>One committed <c>CustomResourceDefinition</c>, with the schema of each version it serves.</summary>
/// <param name="File">The committed file, relative to the repository root, for a message.</param>
/// <param name="Group">The API group.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Plural">The plural resource name the REST path uses.</param>
/// <param name="IsClusterScoped">Whether <c>spec.scope</c> is <c>Cluster</c>.</param>
/// <param name="Versions">Each served version, by name.</param>
/// <param name="ConvertsThroughWebhook">
///     Whether <c>spec.conversion.strategy</c> is <c>Webhook</c> — in which case a request at any
///     served version but the storage one is a call to a service the definition names, which a
///     cluster holding the definition and not its operator cannot answer.
/// </param>
/// <param name="Document">The whole definition as JSON, for a harness that installs it into a real API server.</param>
public sealed record CustomResourceDefinition(
    string File,
    string Group,
    string Kind,
    string Plural,
    bool IsClusterScoped,
    ImmutableDictionary<string, DefinitionVersion> Versions,
    bool ConvertsThroughWebhook,
    JsonObject Document
) {
    /// <summary>The one served version the API server stores objects at, or empty when the definition marks none.</summary>
    public string StorageVersion => Versions.Values.FirstOrDefault(x => x.IsStorage)?.Name ?? string.Empty;

    /// <summary>Reads one definition from its YAML.</summary>
    /// <param name="yaml">The document, exactly as committed.</param>
    /// <param name="file">The file it came from, for a message.</param>
    /// <exception cref="InvalidOperationException">The document is not one <c>apiextensions.k8s.io/v1</c> definition.</exception>
    public static CustomResourceDefinition Parse(string yaml, string file) {
        var document = YamlToJson.Parse(yaml) as JsonObject
            ?? throw new InvalidOperationException($"{file} is not a YAML mapping.");

        if (document["kind"]?.GetValue<string>() != "CustomResourceDefinition"
            || document["apiVersion"]?.GetValue<string>() != "apiextensions.k8s.io/v1") {
            throw new InvalidOperationException(
                $"{file} is not an apiextensions.k8s.io/v1 CustomResourceDefinition. Only what crds.sh writes belongs in a crds/ directory."
            );
        }

        var spec = document["spec"] as JsonObject
            ?? throw new InvalidOperationException($"{file} has no spec.");
        var names = spec["names"] as JsonObject
            ?? throw new InvalidOperationException($"{file} has no spec.names.");
        var versions = ImmutableDictionary.CreateBuilder<string, DefinitionVersion>(StringComparer.Ordinal);

        foreach (var version in (spec["versions"] as JsonArray ?? []).OfType<JsonObject>()) {
            var name = version["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"{file} has a version with no name.");

            // ⚠ A version that is not served is a version the API server answers 404 for, so it is
            // left out here and the lookup reports it exactly as an unknown version — which is what
            // a reconciler rendering a retired api-version would meet on a real cluster.
            if (version["served"] is JsonValue served && served.TryGetValue<bool>(out var isServed) && !isServed) {
                continue;
            }

            versions[name] = new(
                name,
                version["schema"]?["openAPIV3Schema"] as JsonObject
                ?? throw new InvalidOperationException(
                    $"{file} version {name} has no schema.openAPIV3Schema. An apiextensions.k8s.io/v1 "
                    + "definition must carry a structural schema, so this is not a definition the API server would accept."
                ),
                version["subresources"] is JsonObject subresources && subresources.ContainsKey("status"),
                version["storage"] is JsonValue storage && storage.TryGetValue<bool>(out var isStorage) && isStorage
            );
        }

        return new(
            file,
            spec["group"]?.GetValue<string>() ?? string.Empty,
            names["kind"]?.GetValue<string>() ?? string.Empty,
            names["plural"]?.GetValue<string>() ?? string.Empty,
            string.Equals(spec["scope"]?.GetValue<string>(), "Cluster", StringComparison.Ordinal),
            versions.ToImmutable(),
            string.Equals(spec["conversion"]?["strategy"]?.GetValue<string>(), "Webhook", StringComparison.Ordinal),
            document
        );
    }
}

/// <summary>One served version of a definition.</summary>
/// <param name="Name">The version name, for example <c>v1beta2</c>.</param>
/// <param name="Schema">The <c>openAPIV3Schema</c> of the whole object.</param>
/// <param name="HasStatusSubresource">
///     Whether <c>status</c> is a subresource — in which case a <c>status</c> in an applied body is
///     dropped rather than validated, as the API server drops it.
/// </param>
/// <param name="IsStorage">
///     Whether this is the version objects are stored at — the one version a request needs no
///     conversion to reach, which matters when the definition converts through a webhook.
/// </param>
public sealed record DefinitionVersion(string Name, JsonObject Schema, bool HasStatusSubresource, bool IsStorage);

/// <summary>
///     Turns a YAML document into <see cref="JsonNode" /> with the YAML 1.2 core schema's typing —
///     which is what the API server does to a definition before it reads it.
/// </summary>
/// <remarks>
///     ⚠ <b>Plain scalars are typed; quoted scalars are strings, whatever they spell.</b> A
///     definition's <c>default: 3</c> is a number and its <c>default: "3"</c> is a string, and a
///     converter that read every scalar as text would apply the wrong default and then refuse the
///     object it had just built. <c>enum: ["Off", "Enabled"]</c> stays strings, <c>required: [name]</c>
///     stays strings because a name is no number, and <c>x-kubernetes-preserve-unknown-fields: true</c>
///     is a boolean. Keys are always strings, as JSON requires.
/// </remarks>
public static class YamlToJson {
    /// <summary>Parses one YAML document.</summary>
    /// <param name="yaml">The document text.</param>
    /// <returns>The root node, or <see langword="null" /> for an empty document.</returns>
    public static JsonNode? Parse(string yaml) {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    static JsonNode? Convert(YamlNode node) =>
        node switch {
            YamlMappingNode mapping => ConvertMapping(mapping),
            YamlSequenceNode sequence => new JsonArray([.. sequence.Children.Select(Convert)]),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => throw new InvalidOperationException($"A YAML {node.NodeType} node has no JSON shape.")
        };

    static JsonObject ConvertMapping(YamlMappingNode mapping) {
        var result = new JsonObject();

        foreach (var (key, value) in mapping.Children) {
            var name = key is YamlScalarNode scalar
                ? scalar.Value ?? string.Empty
                : throw new InvalidOperationException("A YAML mapping key is not a scalar, and JSON has no shape for that.");

            result[name] = Convert(value);
        }

        return result;
    }

    static JsonValue? ConvertScalar(YamlScalarNode scalar) {
        var text = scalar.Value ?? string.Empty;

        if (scalar.Style != ScalarStyle.Plain) {
            return JsonValue.Create(text);
        }

        switch (text) {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)) {
            return JsonValue.Create(integer);
        }

        if (text.StartsWith("0x", StringComparison.Ordinal)
            && long.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) {
            return JsonValue.Create(hex);
        }

        // ⚠ A float only when it looks like one: the core schema's pattern, not double.TryParse,
        // which would read `1e3` and also `Infinity` — and would read a version such as `1.30`
        // correctly as a number, which is what YAML says it is.
        if (LooksLikeFloat(text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
            return JsonValue.Create(number);
        }

        return text switch {
            ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" => JsonValue.Create(double.PositiveInfinity),
            "-.inf" or "-.Inf" or "-.INF" => JsonValue.Create(double.NegativeInfinity),
            ".nan" or ".NaN" or ".NAN" => JsonValue.Create(double.NaN),
            _ => JsonValue.Create(text)
        };
    }

    static bool LooksLikeFloat(string text) {
        var index = 0;

        if (index < text.Length && (text[index] == '-' || text[index] == '+')) {
            index++;
        }

        var digits = 0;
        var dot = false;

        for (; index < text.Length; index++) {
            var c = text[index];

            if (char.IsAsciiDigit(c)) {
                digits++;
            } else if (c == '.' && !dot) {
                dot = true;
            } else if ((c == 'e' || c == 'E') && digits > 0) {
                var rest = text[(index + 1)..];

                return rest.Length > 0
                    && rest.TrimStart('+', '-').Length > 0
                    && rest.TrimStart('+', '-').All(char.IsAsciiDigit);
            } else {
                return false;
            }
        }

        return digits > 0 && dot;
    }
}
