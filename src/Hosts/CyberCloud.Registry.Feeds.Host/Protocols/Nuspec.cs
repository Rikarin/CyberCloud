using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CyberCloud.Registry.Feeds.Host.Protocols;

/// <summary>
///     What a pushed <c>.nupkg</c> says about itself: the nuspec inside the zip, read into the
///     fields the v3 API serves back.
/// </summary>
/// <param name="Id">The package id, in the nuspec's own case.</param>
/// <param name="Version">The version, normalised.</param>
/// <param name="Description">The description, or empty.</param>
/// <param name="Authors">The authors, comma-separated as the nuspec carries them.</param>
/// <param name="Tags">The tags, space-separated as the nuspec carries them.</param>
/// <param name="DependencyGroups">The dependency groups, in nuspec order.</param>
/// <param name="NuspecBytes">The nuspec's own bytes, served at <c>{id}/{version}/{id}.nuspec</c>.</param>
public sealed record Nuspec(
    string Id,
    NuGetVersion Version,
    string Description,
    string Authors,
    string Tags,
    IReadOnlyList<NuspecDependencyGroup> DependencyGroups,
    byte[] NuspecBytes
) {
    /// <summary>The id as the flat container and the registration key it — lower-cased, ordinally.</summary>
    public string IdLower => Id.ToLowerInvariant();

    /// <summary>
    ///     The catalogue metadata the registration and the search answer are rendered from, as JSON.
    /// </summary>
    public string ToMetadataJson() {
        var groups = new JsonArray();

        foreach (var group in DependencyGroups) {
            var dependencies = new JsonArray();

            foreach (var dependency in group.Dependencies) {
                dependencies.Add(new JsonObject { ["id"] = dependency.Id, ["range"] = dependency.Range });
            }

            groups.Add(new JsonObject { ["targetFramework"] = group.TargetFramework, ["dependencies"] = dependencies });
        }

        return new JsonObject {
            ["id"] = Id,
            ["version"] = Version.Normalized,
            ["description"] = Description,
            ["authors"] = Authors,
            ["tags"] = Tags,
            ["dependencyGroups"] = groups
        }.ToJsonString();
    }

    /// <summary>
    ///     Reads the nuspec out of a package's bytes.
    /// </summary>
    /// <param name="nupkg">The whole <c>.nupkg</c>.</param>
    /// <returns>The nuspec, or <see cref="ErrorCode.InvalidRequestBody" /> saying what a client got wrong.</returns>
    /// <remarks>
    ///     ⚠ The nuspec is the one entry at the zip's root whose name ends in <c>.nuspec</c>, and a
    ///     package with none, or two, is refused rather than guessed at. Its XML namespace varies by
    ///     schema year, so elements are matched by local name.
    /// </remarks>
    public static Result<Nuspec> Read(byte[] nupkg) {
        ArgumentNullException.ThrowIfNull(nupkg);

        ZipArchive archive;

        try {
            archive = new ZipArchive(new MemoryStream(nupkg, writable: false), ZipArchiveMode.Read);
        } catch (InvalidDataException) {
            return Result<Nuspec>.Failure(ErrorCode.InvalidRequestBody, "The pushed file is not a zip, so it is not a .nupkg.");
        }

        using (archive) {
            var candidates = archive.Entries
                .Where(x => !x.FullName.Contains('/', StringComparison.Ordinal))
                .Where(x => x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count != 1) {
                return Result<Nuspec>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"A .nupkg carries exactly one .nuspec at its root, and this one carries {candidates.Count}."
                );
            }

            byte[] bytes;

            using (var stream = candidates[0].Open())
            using (var buffer = new MemoryStream()) {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            return Parse(bytes);
        }
    }

    /// <summary>Parses a nuspec document.</summary>
    /// <param name="bytes">The XML.</param>
    public static Result<Nuspec> Parse(byte[] bytes) {
        ArgumentNullException.ThrowIfNull(bytes);

        XDocument document;

        try {
            document = XDocument.Load(new MemoryStream(bytes, writable: false));
        } catch (System.Xml.XmlException exception) {
            return Result<Nuspec>.Failure(ErrorCode.InvalidRequestBody, "The .nuspec is not well-formed XML: " + exception.Message);
        }

        var metadata = document.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "metadata");

        if (metadata is null) {
            return Result<Nuspec>.Failure(ErrorCode.InvalidRequestBody, "The .nuspec has no <metadata> element.");
        }

        var id = Text(metadata, "id");

        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) {
            return Result<Nuspec>.Failure(ErrorCode.InvalidRequestBody, "The .nuspec's <id> is missing or is not a package id.");
        }

        var version = NuGetVersion.Parse(Text(metadata, "version"));

        if (version.TryGetError(out var badVersion)) {
            return Result<Nuspec>.Failure(badVersion);
        }

        var groups = new List<NuspecDependencyGroup>();
        var dependencies = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "dependencies");

        if (dependencies is not null) {
            var direct = dependencies.Elements().Where(x => x.Name.LocalName == "dependency").Select(Dependency).ToList();

            if (direct.Count > 0) {
                groups.Add(new("", direct));
            }

            foreach (var group in dependencies.Elements().Where(x => x.Name.LocalName == "group")) {
                groups.Add(
                    new(
                        group.Attribute("targetFramework")?.Value ?? "",
                        group.Elements().Where(x => x.Name.LocalName == "dependency").Select(Dependency).ToList()
                    )
                );
            }
        }

        return Result<Nuspec>.Success(
            new(
                id,
                version.GetValueOrThrow(),
                Text(metadata, "description"),
                Text(metadata, "authors"),
                Text(metadata, "tags"),
                groups,
                bytes
            )
        );
    }

    /// <summary>Reads the metadata JSON <see cref="ToMetadataJson" /> wrote back into its parts.</summary>
    /// <param name="json">The entry's metadata.</param>
    public static JsonObject MetadataOf(string json) =>
        JsonNode.Parse(json) as JsonObject ?? new JsonObject { ["id"] = "", ["version"] = "" };

    static string Text(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";

    static NuspecDependency Dependency(XElement element) =>
        new(element.Attribute("id")?.Value ?? "", element.Attribute("version")?.Value ?? "");
}

/// <summary>One dependency group of a nuspec.</summary>
/// <param name="TargetFramework">The group's framework, or empty for the ungrouped dependencies.</param>
/// <param name="Dependencies">Its dependencies.</param>
public sealed record NuspecDependencyGroup(string TargetFramework, IReadOnlyList<NuspecDependency> Dependencies);

/// <summary>One dependency of a nuspec.</summary>
/// <param name="Id">The dependency's package id.</param>
/// <param name="Range">Its version range, as the nuspec spells it.</param>
public sealed record NuspecDependency(string Id, string Range);
