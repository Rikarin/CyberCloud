using CyberCloud.Registry.Feeds.Host.Feeds;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CyberCloud.Registry.Feeds.Host.Protocols;

/// <summary>
///     The NuGet v3 API over a feed: the service index, push, unlist and relist, the flat
///     container, the registration and search. docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the NuGet client actually calls, and where each is met.</b> <c>dotnet nuget push</c>
///         reads the service index for <c>PackagePublish/2.0.0</c> and <c>PUT</c>s a multipart body
///         there. <c>dotnet restore</c> reads the index for <c>PackageBaseAddress/3.0.0</c> and asks
///         it for a version list and a <c>.nupkg</c>. <c>dotnet add package</c> without a version,
///         <c>dotnet list package --outdated</c> and the IDE's package manager read
///         <c>RegistrationsBaseUrl</c> and <c>SearchQueryService</c>. <c>dotnet nuget delete</c>
///         <c>DELETE</c>s the publish URL, which in NuGet's vocabulary is an unlist.
///     </para>
///     <para>
///         ⚠ <b>The catalogue path is the lower-cased id and the normalised version</b> —
///         <c>nuget/{id}/{version}</c> — because that is how every client addresses the flat
///         container, and a push whose nuspec spelled <c>My.Package</c> has to be found by a restore
///         asking for <c>my.package</c>. The id's own case is kept in the entry's metadata for the
///         registration to serve back.
///     </para>
///     <para>
///         ⚠ <b>Unlisted versions stay restorable.</b> The flat container lists every version,
///         listed or not, exactly as nuget.org's does: a lock file that pins an unlisted version
///         must keep restoring. What an unlist removes a version from is search and the
///         registration's <c>listed</c> flag, which is what the client's resolution reads.
///     </para>
/// </remarks>
/// <param name="access">Resolves and authorises the feed.</param>
/// <param name="objects">Where the bytes go.</param>
/// <param name="options">The cap and the public origin.</param>
public sealed class NuGetProtocol(FeedAccess access, IObjectStore objects, FeedsOptions options) {
    const string PathPrefix = "nuget/";

    /// <summary>The service index — the one URL a client is configured with.</summary>
    public async Task<IResult> ServiceIndexAsync(HttpContext http, Guid subscription, string group, string feed) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var @base = FeedUrls.BaseOf(http, options, FeedKind.NuGet, subscription, group, feed);

        var resources = new JsonArray();

        foreach (var (type, path) in new[] {
                     ("PackagePublish/2.0.0", "/v2/package"),
                     ("PackageBaseAddress/3.0.0", "/v3/flatcontainer/"),
                     ("RegistrationsBaseUrl", "/v3/registration/"),
                     ("RegistrationsBaseUrl/3.0.0-rc", "/v3/registration/"),
                     ("RegistrationsBaseUrl/3.0.0-beta", "/v3/registration/"),
                     ("RegistrationsBaseUrl/3.4.0", "/v3/registration/"),
                     ("RegistrationsBaseUrl/3.6.0", "/v3/registration/"),
                     ("SearchQueryService", "/v3/query"),
                     ("SearchQueryService/3.0.0-rc", "/v3/query"),
                     ("SearchQueryService/3.0.0-beta", "/v3/query")
                 }) {
            resources.Add(new JsonObject { ["@id"] = @base + path, ["@type"] = type });
        }

        return Results.Json(new JsonObject { ["version"] = "3.0.0", ["resources"] = resources });
    }

    /// <summary>
    ///     <c>PUT {base}/v2/package</c> — a push. Multipart with one file, as the client sends it,
    ///     or a raw body.
    /// </summary>
    public async Task<IResult> PushAsync(HttpContext http, Guid subscription, string group, string feed) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Write, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var context = resolved.GetValueOrThrow();
        var bytes = await ReadPackageAsync(http);

        if (bytes.TryGetError(out var unreadable)) {
            return FeedResponses.Refuse(unreadable, http);
        }

        var nuspec = Nuspec.Read(bytes.GetValueOrThrow());

        if (nuspec.TryGetError(out var malformed)) {
            return FeedResponses.Refuse(malformed, http);
        }

        var package = nuspec.GetValueOrThrow();
        var path = EntryPath(package.IdLower, package.Version.PathForm);

        // ⚠ The catalogue is asked FIRST, so a duplicate is refused before any byte is stored and a
        // serial second push leaves nothing behind. The bytes then go in before the entry, so an
        // entry never names an object that is not there — under a key that carries their own hash,
        // so a CONCURRENT second push cannot land on the first's bytes either; the claim at the end
        // removes a loser's. ImmutablePublish says why the serial check alone was not enough.
        var taken = await context.Catalogue.GetAsync(path);

        if (taken.IsSuccess) {
            return FeedResponses.Refuse(
                new(ErrorCode.ResourceAlreadyExists, $"{package.Id} {package.Version.Normalized} is already in this feed. A published version is immutable."),
                http
            );
        }

        var content = bytes.GetValueOrThrow();
        var sha256 = FeedResponses.Sha256Of(content);
        var nupkgAt = ImmutablePublish.StoredAt(path, sha256, $"{package.IdLower}.{package.Version.PathForm}.nupkg");
        var nuspecAt = NuspecBeside(nupkgAt, package.IdLower);

        var stored = await objects.PutAsync(context.StoragePrefix + nupkgAt, content, "application/octet-stream", http.RequestAborted);

        if (stored.TryGetError(out var storeError)) {
            return FeedResponses.Refuse(storeError, http);
        }

        var nuspecStored = await objects.PutAsync(context.StoragePrefix + nuspecAt, package.NuspecBytes, "application/xml", http.RequestAborted);

        if (nuspecStored.TryGetError(out var nuspecError)) {
            return FeedResponses.Refuse(nuspecError, http);
        }

        var entry = await ImmutablePublish.ClaimAsync(
            context,
            objects,
            new() {
                Path = path,
                StoredAt = nupkgAt,
                Size = content.Length,
                Sha256 = sha256,
                ContentType = "application/octet-stream",
                Metadata = package.ToMetadataJson(),
                Listed = true,
                PublishedBy = context.Subject
            },
            [nupkgAt, nuspecAt],
            http.RequestAborted
        );

        if (entry.TryGetError(out var catalogueError)) {
            return FeedResponses.Refuse(catalogueError, http);
        }

        return Results.StatusCode(StatusCodes.Status201Created);
    }

    /// <summary><c>DELETE {base}/v2/package/{id}/{version}</c> — an unlist, as NuGet defines it.</summary>
    public Task<IResult> UnlistAsync(HttpContext http, Guid subscription, string group, string feed, string id, string version) =>
        SetListedAsync(http, subscription, group, feed, id, version, listed: false);

    /// <summary><c>POST {base}/v2/package/{id}/{version}</c> — a relist.</summary>
    public Task<IResult> RelistAsync(HttpContext http, Guid subscription, string group, string feed, string id, string version) =>
        SetListedAsync(http, subscription, group, feed, id, version, listed: true);

    /// <summary><c>GET {base}/v3/flatcontainer/{id}/index.json</c> — every version, listed or not, ascending.</summary>
    public async Task<IResult> VersionsAsync(HttpContext http, Guid subscription, string group, string feed, string id) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var versions = await VersionsOf(resolved.GetValueOrThrow(), id.ToLowerInvariant());

        if (versions.Length == 0) {
            return Results.NotFound();
        }

        return Results.Json(new JsonObject { ["versions"] = new JsonArray([.. versions.Select(x => (JsonNode)x.Version.PathForm)]) });
    }

    /// <summary>
    ///     <c>GET {base}/v3/flatcontainer/{id}/{version}/{file}</c> — the <c>.nupkg</c> or the
    ///     <c>.nuspec</c>.
    /// </summary>
    public async Task<IResult> DownloadAsync(HttpContext http, Guid subscription, string group, string feed, string id, string version, string file) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var parsed = NuGetVersion.Parse(version);

        if (parsed.IsFailure) {
            return Results.NotFound();
        }

        var context = resolved.GetValueOrThrow();
        var idLower = id.ToLowerInvariant();
        var normalized = parsed.GetValueOrThrow().PathForm;
        var entry = await context.Catalogue.GetAsync(EntryPath(idLower, normalized));

        if (entry.IsFailure) {
            return Results.NotFound();
        }

        var lowerFile = file.ToLowerInvariant();
        string at;
        string contentType;

        if (lowerFile == $"{idLower}.{normalized}.nupkg") {
            at = entry.GetValueOrThrow().StoredAt;
            contentType = "application/octet-stream";
        } else if (lowerFile == $"{idLower}.nuspec") {
            at = NuspecBeside(entry.GetValueOrThrow().StoredAt, idLower);
            contentType = "application/xml";
        } else {
            return Results.NotFound();
        }

        var read = await objects.GetAsync(context.StoragePrefix + at, http.RequestAborted);

        if (read.TryGetError(out var missing)) {
            return missing.Code == ErrorCode.ResourceNotFound ? Results.NotFound() : FeedResponses.Refuse(missing, http);
        }

        var found = read.GetValueOrThrow();
        return Results.Stream(found.Content, contentType);
    }

    /// <summary><c>GET {base}/v3/registration/{id}/index.json</c> — one inline page of every version.</summary>
    public async Task<IResult> RegistrationIndexAsync(HttpContext http, Guid subscription, string group, string feed, string id) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var idLower = id.ToLowerInvariant();
        var versions = await VersionsOf(resolved.GetValueOrThrow(), idLower);

        if (versions.Length == 0) {
            return Results.NotFound();
        }

        var @base = FeedUrls.BaseOf(http, options, FeedKind.NuGet, subscription, group, feed);
        var index = $"{@base}/v3/registration/{idLower}/index.json";

        var items = new JsonArray();

        foreach (var (entry, version) in versions) {
            items.Add(Leaf(@base, idLower, entry, version, index));
        }

        var page = new JsonObject {
            ["@id"] = $"{index}#page/{versions[0].Version.Normalized}/{versions[^1].Version.Normalized}",
            ["count"] = versions.Length,
            ["lower"] = versions[0].Version.Normalized,
            ["upper"] = versions[^1].Version.Normalized,
            ["parent"] = index,
            ["items"] = items
        };

        return Results.Json(new JsonObject { ["@id"] = index, ["count"] = 1, ["items"] = new JsonArray(page) });
    }

    /// <summary><c>GET {base}/v3/registration/{id}/{version}.json</c> — one leaf.</summary>
    public async Task<IResult> RegistrationLeafAsync(HttpContext http, Guid subscription, string group, string feed, string id, string version) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var parsed = NuGetVersion.Parse(version);

        if (parsed.IsFailure) {
            return Results.NotFound();
        }

        var idLower = id.ToLowerInvariant();
        var entry = await resolved.GetValueOrThrow().Catalogue.GetAsync(EntryPath(idLower, parsed.GetValueOrThrow().PathForm));

        if (entry.IsFailure) {
            return Results.NotFound();
        }

        var @base = FeedUrls.BaseOf(http, options, FeedKind.NuGet, subscription, group, feed);
        return Results.Json(Leaf(@base, idLower, entry.GetValueOrThrow(), parsed.GetValueOrThrow(), $"{@base}/v3/registration/{idLower}/index.json"));
    }

    /// <summary><c>GET {base}/v3/query?q=&amp;skip=&amp;take=&amp;prerelease=</c> — search over listed versions.</summary>
    public async Task<IResult> SearchAsync(HttpContext http, Guid subscription, string group, string feed) {
        ArgumentNullException.ThrowIfNull(http);

        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var query = http.Request.Query["q"].ToString().Trim();
        var skip = Math.Max(0, ReadInt(http.Request.Query["skip"], 0));
        var take = Math.Clamp(ReadInt(http.Request.Query["take"], 20), 1, 100);
        var prerelease = string.Equals(http.Request.Query["prerelease"], "true", StringComparison.OrdinalIgnoreCase);

        var listed = await resolved.GetValueOrThrow().Catalogue.ListAsync(PathPrefix);

        if (listed.TryGetError(out var listError)) {
            return FeedResponses.Refuse(listError, http);
        }

        var @base = FeedUrls.BaseOf(http, options, FeedKind.NuGet, subscription, group, feed);

        var packages = listed.GetValueOrThrow()
            .Where(x => x.Listed)
            .Select(x => (Entry: x, Version: NuGetVersion.Parse(Nuspec.MetadataOf(x.Metadata)["version"]?.GetValue<string>())))
            .Where(x => x.Version.IsSuccess)
            .Select(x => (x.Entry, Version: x.Version.GetValueOrThrow()))
            .Where(x => prerelease || !x.Version.IsPrerelease)
            .GroupBy(x => IdOf(x.Entry.Path), StringComparer.Ordinal)
            .Where(g => query.Length == 0 || Matches(g.First().Entry, query))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var data = new JsonArray();

        foreach (var package in packages.Skip(skip).Take(take)) {
            var ordered = package.OrderBy(x => x.Version).ToList();
            var latest = ordered[^1];
            var metadata = Nuspec.MetadataOf(latest.Entry.Metadata);
            var registration = $"{@base}/v3/registration/{package.Key}/index.json";

            var versions = new JsonArray();

            foreach (var (entry, version) in ordered) {
                versions.Add(
                    new JsonObject {
                        ["@id"] = $"{@base}/v3/registration/{package.Key}/{version.PathForm}.json",
                        ["version"] = version.Normalized,
                        ["downloads"] = 0
                    }
                );
            }

            data.Add(
                new JsonObject {
                    ["@id"] = registration,
                    ["@type"] = "Package",
                    ["registration"] = registration,
                    ["id"] = metadata["id"]?.GetValue<string>() ?? package.Key,
                    ["version"] = latest.Version.Normalized,
                    ["description"] = metadata["description"]?.GetValue<string>() ?? "",
                    ["authors"] = new JsonArray([.. Split(metadata["authors"]?.GetValue<string>(), ',').Select(x => (JsonNode)x)]),
                    ["tags"] = new JsonArray([.. Split(metadata["tags"]?.GetValue<string>(), ' ').Select(x => (JsonNode)x)]),
                    ["totalDownloads"] = 0,
                    ["verified"] = false,
                    ["versions"] = versions
                }
            );
        }

        return Results.Json(new JsonObject { ["totalHits"] = packages.Count, ["data"] = data });
    }

    async Task<IResult> SetListedAsync(HttpContext http, Guid subscription, string group, string feed, string id, string version, bool listed) {
        var resolved = await access.ResolveAsync(http, FeedKind.NuGet, subscription, group, feed, FeedIntent.Write, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var parsed = NuGetVersion.Parse(version);

        if (parsed.IsFailure) {
            return Results.NotFound();
        }

        var catalogue = resolved.GetValueOrThrow().Catalogue;
        var path = EntryPath(id.ToLowerInvariant(), parsed.GetValueOrThrow().PathForm);
        var entry = await catalogue.GetAsync(path);

        if (entry.IsFailure) {
            return Results.NotFound();
        }

        var updated = await catalogue.PutAsync(entry.GetValueOrThrow() with { Listed = listed }, replace: true);

        return updated.TryGetError(out var error)
            ? FeedResponses.Refuse(error, http)
            : Results.StatusCode(listed ? StatusCodes.Status200OK : StatusCodes.Status204NoContent);
    }

    /// <summary>The package's bytes, from the multipart the client sends or from a raw body.</summary>
    /// <summary>Where a version's <c>.nuspec</c> is: beside its <c>.nupkg</c>, in the same hash-named directory.</summary>
    static string NuspecBeside(string nupkgAt, string idLower) => $"{ImmutablePublish.DirectoryOf(nupkgAt)}/{idLower}.nuspec";

    async Task<Result<byte[]>> ReadPackageAsync(HttpContext http) {
        if (http.Request.HasFormContentType) {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);

            if (form.Files.Count != 1) {
                return Result<byte[]>.Failure(ErrorCode.InvalidRequestBody, "A push carries exactly one file part.");
            }

            var file = form.Files[0];

            if (file.Length > options.MaxArtifactBytes) {
                return Result<byte[]>.Failure(ErrorCode.InvalidRequestBody, $"The package is larger than this host accepts ({options.MaxArtifactBytes} bytes). {FeedsOptions.SectionName}:MaxArtifactBytes is the cap.");
            }

            await using var stream = file.OpenReadStream();
            return await FeedResponses.ReadBoundedAsync(stream, options.MaxArtifactBytes, http.RequestAborted);
        }

        return await FeedResponses.ReadBodyAsync(http, options.MaxArtifactBytes, http.RequestAborted);
    }

    async Task<ImmutableArray<(FeedEntry Entry, NuGetVersion Version)>> VersionsOf(FeedContext context, string idLower) {
        var listed = await context.Catalogue.ListAsync(PathPrefix + idLower + "/");

        if (listed.IsFailure) {
            return [];
        }

        return [
            .. listed.GetValueOrThrow()
                .Select(x => (Entry: x, Version: NuGetVersion.Parse(Nuspec.MetadataOf(x.Metadata)["version"]?.GetValue<string>())))
                .Where(x => x.Version.IsSuccess)
                .Select(x => (x.Entry, x.Version.GetValueOrThrow()))
                .OrderBy(x => x.Item2)
        ];
    }

    static JsonObject Leaf(string @base, string idLower, FeedEntry entry, NuGetVersion version, string registration) {
        var metadata = Nuspec.MetadataOf(entry.Metadata);
        var content = $"{@base}/v3/flatcontainer/{idLower}/{version.PathForm}/{idLower}.{version.PathForm}.nupkg";
        var leaf = $"{@base}/v3/registration/{idLower}/{version.PathForm}.json";

        var groups = new JsonArray();

        if (metadata["dependencyGroups"] is JsonArray declared) {
            foreach (var group in declared.OfType<JsonObject>()) {
                var dependencies = new JsonArray();

                if (group["dependencies"] is JsonArray list) {
                    foreach (var dependency in list.OfType<JsonObject>()) {
                        dependencies.Add(
                            new JsonObject {
                                ["@type"] = "PackageDependency",
                                ["id"] = dependency["id"]?.GetValue<string>() ?? "",
                                ["range"] = dependency["range"]?.GetValue<string>() ?? ""
                            }
                        );
                    }
                }

                var rendered = new JsonObject { ["@type"] = "PackageDependencyGroup", ["dependencies"] = dependencies };
                var framework = group["targetFramework"]?.GetValue<string>() ?? "";

                if (framework.Length > 0) {
                    rendered["targetFramework"] = framework;
                }

                groups.Add(rendered);
            }
        }

        return new JsonObject {
            ["@id"] = leaf,
            ["@type"] = "Package",
            ["catalogEntry"] = new JsonObject {
                ["@id"] = leaf,
                ["@type"] = "PackageDetails",
                ["id"] = metadata["id"]?.GetValue<string>() ?? idLower,
                ["version"] = version.Normalized,
                ["description"] = metadata["description"]?.GetValue<string>() ?? "",
                ["authors"] = metadata["authors"]?.GetValue<string>() ?? "",
                ["tags"] = metadata["tags"]?.GetValue<string>() ?? "",
                ["listed"] = entry.Listed,
                ["published"] = entry.PublishedAt.ToString("O", CultureInfo.InvariantCulture),
                ["packageContent"] = content,
                ["dependencyGroups"] = groups
            },
            ["packageContent"] = content,
            ["registration"] = registration
        };
    }

    static string EntryPath(string idLower, string normalizedVersion) => PathPrefix + idLower + "/" + normalizedVersion;

    static string IdOf(string entryPath) => entryPath[PathPrefix.Length..entryPath.LastIndexOf('/')];

    static bool Matches(FeedEntry entry, string query) {
        var metadata = Nuspec.MetadataOf(entry.Metadata);

        foreach (var field in new[] { "id", "description", "tags" }) {
            if ((metadata[field]?.GetValue<string>() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    static string[] Split(string? value, char separator) =>
        (value ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static int ReadInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
