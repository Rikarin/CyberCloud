using CyberCloud.Registry.Feeds.Host.Feeds;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CyberCloud.Registry.Feeds.Host.Protocols;

/// <summary>
///     Maven's repository layout over a feed: <c>PUT</c> and <c>GET</c> by path, and a
///     <c>maven-metadata.xml</c> generated for an artifact nobody has deployed one for.
///     docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         <b>What <c>mvn deploy</c> actually does.</b> It <c>GET</c>s the artifact's
///         <c>maven-metadata.xml</c> (and accepts a <c>404</c>), <c>PUT</c>s the <c>.jar</c>, the
///         <c>.pom</c> and a <c>.sha1</c> and <c>.md5</c> beside each, then <c>PUT</c>s a merged
///         <c>maven-metadata.xml</c> with its own checksums. A resolver does the reverse:
///         <c>GET</c> the metadata to learn the versions, then <c>GET</c> the files. There is no
///         document format to speak beyond the metadata's XML; the repository is the layout.
///     </para>
///     <para>
///         ⚠ <b>A release version is immutable, checksums included; everything else is replaceable.</b>
///         A second <c>PUT</c> of <c>org/x/y/1.0/y-1.0.jar</c> is <c>409</c>, which is what a
///         repository manager's release policy says and what keeps a build reproducible — and so is
///         a second <c>PUT</c> of <c>y-1.0.jar.sha1</c> or <c>y-1.0.jar.asc</c>, because a release
///         whose checksum can be rewritten is only as immutable as the checksum: the review of #29
///         pointed out that a writer could make every resolver's verification of a released jar fail,
///         or pass against a substituted signature, without touching the jar. A checksum or signature
///         is therefore exactly as replaceable as the file it is beside. <c>maven-metadata.xml</c> and
///         its checksums are rewritten by every deploy and are replaceable by design, and a version
///         directory ending in <c>-SNAPSHOT</c> is replaceable throughout — that is what a snapshot is.
///     </para>
///     <para>
///         ⚠
///         <b>
///             The path is caller-supplied and reaches the object store, so it is checked before
///             anything else is.
///         </b> <see cref="IsRepositoryPath" /> allows the segment grammar a
///         Maven coordinate can produce and refuses <c>..</c>, empty segments and anything that is
///         not a repository file; the feed's storage prefix goes in front of whatever survives, so
///         no path can name another feed's bytes.
///     </para>
/// </remarks>
/// <param name="access">Resolves and authorises the feed.</param>
/// <param name="objects">Where the files go.</param>
/// <param name="options">The cap.</param>
public sealed class MavenProtocol(FeedAccess access, IObjectStore objects, FeedsOptions options) {
    const string PathPrefix = "maven/";
    const string MetadataFile = "maven-metadata.xml";

    static readonly string[] ChecksumSuffixes = [".md5", ".sha1", ".sha256", ".sha512", ".asc"];

    /// <summary><c>PUT {base}/{path}</c> — a deploy of one file.</summary>
    public async Task<IResult> PutAsync(HttpContext http, Guid subscription, string group, string feed, string path) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(path);

        if (!IsRepositoryPath(path)) {
            return Results.NotFound();
        }

        var resolved = await access.ResolveAsync(
            http,
            FeedKind.Maven,
            subscription,
            group,
            feed,
            FeedIntent.Write,
            http.RequestAborted
        );

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var context = resolved.GetValueOrThrow();
        var body = await FeedResponses.ReadBodyAsync(http, options.MaxArtifactBytes, http.RequestAborted);

        if (body.TryGetError(out var unreadable)) {
            return FeedResponses.Refuse(unreadable, http);
        }

        var replaceable = IsReplaceable(path);
        var entryPath = PathPrefix + path;

        if (!replaceable && (await context.Catalogue.GetAsync(entryPath)).IsSuccess) {
            return FeedResponses.Refuse(
                new(
                    ErrorCode.ResourceAlreadyExists,
                    $"'{path}' is a released artifact and is already in this feed. A release is immutable; deploy a new version."
                ),
                http
            );
        }

        var bytes = body.GetValueOrThrow();
        var sha256 = FeedResponses.Sha256Of(bytes);

        // A replaceable file lives at its own path and the last write wins, which is what replaceable
        // means. A release file lives under its hash, so two concurrent deploys of one release cannot
        // land on each other's bytes, and the claim below removes the loser's — ImmutablePublish.
        var storedAt = replaceable
            ? entryPath
            : ImmutablePublish.StoredAt(
                ImmutablePublish.DirectoryOf(entryPath),
                sha256,
                path[(path.LastIndexOf('/') + 1)..]
            );

        var stored = await objects.PutAsync(
            context.StoragePrefix + storedAt,
            bytes,
            ContentTypeOf(path),
            http.RequestAborted
        );

        if (stored.TryGetError(out var storeError)) {
            return FeedResponses.Refuse(storeError, http);
        }

        FeedEntry entry = new() {
            Path = entryPath,
            StoredAt = storedAt,
            Size = bytes.Length,
            Sha256 = sha256,
            ContentType = ContentTypeOf(path),
            PublishedBy = context.Subject
        };

        var claimed = replaceable
            ? await context.Catalogue.PutAsync(entry, true)
            : await ImmutablePublish.ClaimAsync(context, objects, entry, [storedAt], http.RequestAborted);

        return claimed.TryGetError(out var catalogueError)
            ? FeedResponses.Refuse(catalogueError, http)
            : Results.StatusCode(StatusCodes.Status201Created);
    }

    /// <summary><c>GET</c> or <c>HEAD {base}/{path}</c> — one file, or a generated <c>maven-metadata.xml</c>.</summary>
    public async Task<IResult> GetAsync(HttpContext http, Guid subscription, string group, string feed, string path) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(path);

        if (!IsRepositoryPath(path)) {
            return Results.NotFound();
        }

        var resolved = await access.ResolveAsync(
            http,
            FeedKind.Maven,
            subscription,
            group,
            feed,
            FeedIntent.Read,
            http.RequestAborted
        );

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var context = resolved.GetValueOrThrow();
        var head = HttpMethods.IsHead(http.Request.Method);
        var entry = await context.Catalogue.GetAsync(PathPrefix + path);

        if (entry.IsSuccess) {
            if (head) {
                return Results.Ok();
            }

            var read = await objects.GetAsync(
                context.StoragePrefix + entry.GetValueOrThrow().StoredAt,
                http.RequestAborted
            );

            if (read.TryGetError(out var missing)) {
                return missing.Code == ErrorCode.ResourceNotFound
                    ? Results.NotFound()
                    : FeedResponses.Refuse(missing, http);
            }

            return Results.Stream(read.GetValueOrThrow().Content, entry.GetValueOrThrow().ContentType);
        }

        // ── A metadata file nobody deployed: generated from the versions that were ─────────────
        var generated = await GeneratedMetadataAsync(context, path);

        if (generated is null) {
            return Results.NotFound();
        }

        if (head) {
            return Results.Ok();
        }

        return Results.Text(generated, ContentTypeOf(path), Encoding.UTF8);
    }

    /// <summary>
    ///     A <c>maven-metadata.xml</c> — or a checksum of one — built from the version directories
    ///     under an artifact, or <see langword="null" /> when the path is not one or there are none.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "Maven's checksum files are `.sha1` and `.md5` beside every artifact, and a resolver "
            + "verifies whichever it fetches. They are transfer checksums the protocol fixes, served "
            + "here for a generated maven-metadata.xml; the catalogue entry of every deployed file "
            + "carries SHA-256."
    )]
    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "The same: `.md5` is a checksum file Maven resolvers ask for by name, not a security decision this host makes."
    )]
    async Task<string?> GeneratedMetadataAsync(FeedContext context, string path) {
        var checksum = ChecksumSuffixes.FirstOrDefault(x => path.EndsWith(x, StringComparison.Ordinal) && x != ".asc");
        var metadataPath = checksum is null ? path : path[..^checksum.Length];

        if (!metadataPath.EndsWith("/" + MetadataFile, StringComparison.Ordinal)) {
            return null;
        }

        var artifactDirectory = metadataPath[..^(MetadataFile.Length + 1)];
        var segments = artifactDirectory.Split('/');

        if (segments.Length < 2) {
            return null;
        }

        var listed = await context.Catalogue.ListAsync(PathPrefix + artifactDirectory + "/");

        if (listed.IsFailure) {
            return null;
        }

        var versions = listed.GetValueOrThrow()
            .Select(x => x.Path[(PathPrefix.Length + artifactDirectory.Length + 1)..])
            .Where(static x => x.Contains('/', StringComparison.Ordinal))
            .Select(static x => x[..x.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        if (versions.Count == 0) {
            return null;
        }

        var releases = versions.Where(static x => !x.EndsWith("-SNAPSHOT", StringComparison.Ordinal)).ToList();
        var latest = versions[^1];

        var xml = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                "metadata",
                new XElement("groupId", string.Join('.', segments[..^1])),
                new XElement("artifactId", segments[^1]),
                new XElement(
                    "versioning",
                    new XElement("latest", latest),
                    new XElement("release", releases.Count > 0 ? releases[^1] : ""),
                    new XElement("versions", versions.Select(static x => new XElement("version", x))),
                    new XElement(
                        "lastUpdated",
                        DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                    )
                )
            )
        );

        var document = xml.Declaration + Environment.NewLine + xml;
        var bytes = Encoding.UTF8.GetBytes(document);

        return checksum switch {
            null => document,
            ".md5" => Convert.ToHexStringLower(MD5.HashData(bytes)),
            ".sha1" => Convert.ToHexStringLower(SHA1.HashData(bytes)),
            ".sha256" => Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ".sha512" => Convert.ToHexStringLower(SHA512.HashData(bytes)),
            _ => null
        };
    }

    /// <summary>
    ///     Whether a deployed file may be deployed again — metadata and anything in a snapshot
    ///     version. A checksum or signature is as replaceable as the file it is beside, so a
    ///     release's are not.
    /// </summary>
    internal static bool IsReplaceable(string path) {
        var file = path[(path.LastIndexOf('/') + 1)..];

        if (file.StartsWith(MetadataFile, StringComparison.Ordinal)) {
            return true;
        }

        // ⚠ A checksum is not a file of its own here; it is a claim about the file beside it, and
        // rewriting the claim over an immutable release is the hole the class remarks describe.
        var checksum = ChecksumSuffixes.FirstOrDefault(x => file.EndsWith(x, StringComparison.Ordinal));

        if (checksum is not null) {
            return IsReplaceable(path[..^checksum.Length]);
        }

        var segments = path.Split('/');
        return segments.Length >= 2 && segments[^2].EndsWith("-SNAPSHOT", StringComparison.Ordinal);
    }

    /// <summary>The segment grammar a Maven coordinate can produce, and nothing that could step outside a prefix.</summary>
    internal static bool IsRepositoryPath(string path) {
        if (path.Length is 0 or > 900 || path.StartsWith('/') || path.EndsWith('/')) {
            return false;
        }

        foreach (var segment in path.Split('/')) {
            if (segment.Length == 0
                || segment is "." or ".."
                || !segment.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+')) {
                return false;
            }
        }

        return true;
    }

    static string ContentTypeOf(string path) {
        var file = path[(path.LastIndexOf('/') + 1)..];

        if (file.EndsWith(".pom", StringComparison.Ordinal) || file.EndsWith(".xml", StringComparison.Ordinal)) {
            return "application/xml";
        }

        if (file.EndsWith(".jar", StringComparison.Ordinal) || file.EndsWith(".war", StringComparison.Ordinal)) {
            return "application/java-archive";
        }

        return ChecksumSuffixes.Any(x => file.EndsWith(x, StringComparison.Ordinal))
            ? "text/plain"
            : "application/octet-stream";
    }
}
