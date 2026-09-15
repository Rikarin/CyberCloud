using CyberCloud.Registry.Feeds.Host.Feeds;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Registry.Feeds.Host.Protocols;

/// <summary>
///     The npm registry API over a feed: publish, the packument, the tarball, dist-tags, ping and
///     search. docs/plan/13 § Artifact feeds.
/// </summary>
/// <remarks>
///     <para>
///         <b>What the npm client actually calls.</b> <c>npm publish</c> <c>PUT</c>s the package
///         name with a document that carries the version's manifest under <c>versions</c>, the tags
///         under <c>dist-tags</c>, and the tarball base64-encoded under <c>_attachments</c> — one
///         request, everything in it. <c>npm install</c> <c>GET</c>s the name for the packument,
///         picks a version, and <c>GET</c>s the tarball URL the packument's <c>dist</c> named.
///         <c>npm dist-tag</c> reads and writes <c>-/package/{name}/dist-tags</c>; <c>npm ping</c>
///         and <c>npm search</c> are what they say.
///     </para>
///     <para>
///         ⚠ <b>One route, dispatched by hand, because a scoped name has a slash in it.</b>
///         <c>@scope/name</c> and <c>name/-/name-1.0.0.tgz</c> and <c>-/v1/search</c> are all "some
///         segments after the feed", and ASP.NET Core's routing cannot tell a scope from a name
///         from a verb. <see cref="DispatchAsync" /> reads the segments once and says which of the
///         six shapes they are; the client also sends <c>@scope%2Fname</c> and the segments are
///         unescaped first.
///     </para>
///     <para>
///         <b>Catalogue paths:</b> <c>npm/{name}/{version}</c> for each published version, with the
///         manifest as its metadata and the tarball stored at <c>npm/{name}/-/{sha256}/{bare}-{version}.tgz</c>
///         — the entry's <c>StoredAt</c>, which is where a download reads and the reason a
///         second publish's bytes cannot land on a first's (<see cref="ImmutablePublish" />); and
///         <c>npm-tags/{name}</c> for the dist-tags, which change and are therefore a replaceable
///         entry rather than a field of an immutable one. The <c>dist.tarball</c> URL is computed
///         when the packument is served, never stored, because it names this host's origin.
///     </para>
/// </remarks>
/// <param name="access">Resolves and authorises the feed.</param>
/// <param name="objects">Where the tarballs go.</param>
/// <param name="options">The cap and the public origin.</param>
public sealed class NpmProtocol(FeedAccess access, IObjectStore objects, FeedsOptions options) {
    const string PathPrefix = "npm/";
    const string TagsPrefix = "npm-tags/";

    /// <summary>Everything under <c>{base}/</c>, by segment shape.</summary>
    public async Task<IResult> DispatchAsync(HttpContext http, Guid subscription, string group, string feed, string rest) {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(rest);

        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var method = http.Request.Method;

        // -/ping, -/v1/search, -/package/{name}/dist-tags[/{tag}]
        if (segments.Length >= 1 && segments[0] == "-") {
            if (segments is ["-", "ping"] && HttpMethods.IsGet(method)) {
                return await PingAsync(http, subscription, group, feed);
            }

            if (segments is ["-", "v1", "search"] && HttpMethods.IsGet(method)) {
                return await SearchAsync(http, subscription, group, feed);
            }

            if (segments.Length >= 3 && segments[1] == "package") {
                var tagsAt = Array.IndexOf(segments, "dist-tags", 2);

                if (tagsAt > 2) {
                    var name = string.Join('/', segments[2..tagsAt]);
                    var tag = segments.Length > tagsAt + 1 ? segments[tagsAt + 1] : null;

                    if (HttpMethods.IsGet(method) && tag is null) {
                        return await DistTagsAsync(http, subscription, group, feed, name);
                    }

                    if (HttpMethods.IsPut(method) && tag is not null) {
                        return await SetDistTagAsync(http, subscription, group, feed, name, tag);
                    }

                    if (HttpMethods.IsDelete(method) && tag is not null) {
                        return await RemoveDistTagAsync(http, subscription, group, feed, name, tag);
                    }
                }
            }

            return Results.NotFound();
        }

        // {name}/-/{file}
        var dash = Array.IndexOf(segments, "-");

        if (dash > 0 && segments.Length == dash + 2) {
            return HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
                ? await TarballAsync(http, subscription, group, feed, string.Join('/', segments[..dash]), segments[^1])
                : Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }

        // {name} — @scope/name or name
        var packageName = string.Join('/', segments);

        if (!IsPackageName(packageName)) {
            return Results.NotFound();
        }

        if (HttpMethods.IsPut(method)) {
            return await PublishAsync(http, subscription, group, feed, packageName);
        }

        if (HttpMethods.IsGet(method)) {
            return await PackumentAsync(http, subscription, group, feed, packageName);
        }

        return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    async Task<IResult> PingAsync(HttpContext http, Guid subscription, string group, string feed) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        return resolved.TryGetError(out var refused) ? FeedResponses.Refuse(refused, http) : Results.Json(new JsonObject());
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
        "npm's `dist.shasum` is defined as the tarball's SHA-1 and every client compares it. It is a "
        + "transfer checksum the protocol fixes, not a security decision this host makes; the "
        + "`integrity` beside it is SHA-512, and the catalogue entry carries SHA-256 as well."
    )]
    async Task<IResult> PublishAsync(HttpContext http, Guid subscription, string group, string feed, string name) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Write, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var context = resolved.GetValueOrThrow();
        // The whole document, base64 and all, which is why the server's limit is twice the cap; the
        // decoded tarball is held to the cap itself below.
        var body = await FeedResponses.ReadBodyAsync(http, options.MaxRequestBodyBytes, http.RequestAborted);

        if (body.TryGetError(out var unreadable)) {
            return FeedResponses.Refuse(unreadable, http);
        }

        JsonObject document;

        try {
            document = JsonNode.Parse(body.GetValueOrThrow()) as JsonObject ?? throw new JsonException("not an object");
        } catch (JsonException exception) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "A publish is a JSON document: " + exception.Message), http);
        }

        if (!string.Equals(document["name"]?.GetValue<string>(), name, StringComparison.Ordinal)) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "The document's name does not match the URL."), http);
        }

        if (document["versions"] is not JsonObject versions || versions.Count != 1) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "A publish carries exactly one version under 'versions'."), http);
        }

        var (version, manifestNode) = versions.First();

        if (manifestNode is not JsonObject manifest || !IsVersion(version)) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, $"'{version}' is not a version with a manifest."), http);
        }

        if (document["_attachments"] is not JsonObject attachments || attachments.Count != 1) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "A publish carries exactly one tarball under '_attachments'."), http);
        }

        var (fileName, attachmentNode) = attachments.First();

        if (attachmentNode is not JsonObject attachment || attachment["data"]?.GetValue<string>() is not { } base64) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "The attachment carries no base64 'data'."), http);
        }

        byte[] tarball;

        try {
            tarball = Convert.FromBase64String(base64);
        } catch (FormatException) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "The attachment's 'data' is not base64."), http);
        }

        if (tarball.Length > options.MaxArtifactBytes) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, $"The tarball is larger than this host accepts ({options.MaxArtifactBytes} bytes)."), http);
        }

        // ⚠ THE ATTACHMENT'S NAME IS THE CALLER'S AND THE TARBALL'S IS NOT. The review of #29 published
        // a new version whose attachment was named after an existing one and overwrote the existing
        // version's bytes, because the name went straight into the object key. The tarball of
        // {name}@{version} is `{bare}-{version}.tgz` — what registry.npmjs.org serves at
        // `{name}/-/` — and a publish whose attachment says otherwise is not a publish of this
        // version. libnpmpublish keys the attachment with the scope still on the name, so that
        // spelling is accepted as the same thing; either way the name is derived and never stored.
        var tarballName = TarballNameOf(name, version);

        if (fileName != tarballName && fileName != $"{name}-{version}.tgz") {
            return FeedResponses.Refuse(
                new(ErrorCode.InvalidRequestBody, $"The attachment is named '{fileName}', and the tarball of {name}@{version} is '{tarballName}'. A publish carries the tarball of the version it publishes."),
                http
            );
        }

        var path = EntryPath(name, version);

        if ((await context.Catalogue.GetAsync(path)).IsSuccess) {
            return FeedResponses.Refuse(
                new(ErrorCode.ResourceAlreadyExists, $"{name}@{version} is already in this feed. A published version is immutable."),
                http
            );
        }

        var sha256 = FeedResponses.Sha256Of(tarball);
        var tarballAt = ImmutablePublish.StoredAt($"{PathPrefix}{name}/-", sha256, tarballName);
        var stored = await objects.PutAsync(context.StoragePrefix + tarballAt, tarball, "application/octet-stream", http.RequestAborted);

        if (stored.TryGetError(out var storeError)) {
            return FeedResponses.Refuse(storeError, http);
        }

        // The manifest as the client sent it, with the dist block reduced to what is stored: the
        // hashes. The tarball URL is this host's to spell when the packument is served.
        manifest["dist"] = new JsonObject {
            ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(tarball)),
            ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)),
            ["fileName"] = tarballName
        };

        // ⚠ The claim, not a put: a second publish of this version that raced past the check above
        // loses here and takes its bytes with it — ImmutablePublish says why.
        var entry = await ImmutablePublish.ClaimAsync(
            context,
            objects,
            new() {
                Path = path,
                StoredAt = tarballAt,
                Size = tarball.Length,
                Sha256 = sha256,
                ContentType = "application/octet-stream",
                Metadata = manifest.ToJsonString(),
                PublishedBy = context.Subject
            },
            [tarballAt],
            http.RequestAborted
        );

        if (entry.TryGetError(out var catalogueError)) {
            return FeedResponses.Refuse(catalogueError, http);
        }

        // dist-tags: what the client sent, merged over what is there — `latest` at least.
        var tags = await ReadTagsAsync(context, name);

        if (document["dist-tags"] is JsonObject sent) {
            foreach (var (tag, target) in sent) {
                if (target?.GetValue<string>() is { } targetVersion) {
                    tags[tag] = targetVersion;
                }
            }
        }

        if (!tags.ContainsKey("latest")) {
            tags["latest"] = version;
        }

        var tagged = await WriteTagsAsync(context, name, tags);

        return tagged.TryGetError(out var tagError)
            ? FeedResponses.Refuse(tagError, http)
            : Results.Json(new JsonObject { ["ok"] = true, ["id"] = name, ["rev"] = version }, statusCode: StatusCodes.Status201Created);
    }

    async Task<IResult> PackumentAsync(HttpContext http, Guid subscription, string group, string feed, string name) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var context = resolved.GetValueOrThrow();
        var listed = await context.Catalogue.ListAsync(PathPrefix + name + "/");

        if (listed.TryGetError(out var listError)) {
            return FeedResponses.Refuse(listError, http);
        }

        var entries = listed.GetValueOrThrow().Where(x => IsVersion(VersionOf(x.Path, name))).ToList();

        if (entries.Count == 0) {
            return Results.NotFound();
        }

        var @base = FeedUrls.BaseOf(http, options, FeedKind.Npm, subscription, group, feed);
        var versions = new JsonObject();
        var time = new JsonObject();
        DateTimeOffset? created = null;
        DateTimeOffset? modified = null;
        string description = "";

        foreach (var entry in entries) {
            var version = VersionOf(entry.Path, name);
            var manifest = JsonNode.Parse(entry.Metadata) as JsonObject ?? new JsonObject();

            if (manifest["dist"] is JsonObject dist) {
                var file = dist["fileName"]?.GetValue<string>() ?? "";
                dist.Remove("fileName");
                dist["tarball"] = $"{@base}/{name}/-/{file}";
            }

            versions[version] = manifest;
            time[version] = entry.PublishedAt.ToString("O", CultureInfo.InvariantCulture);
            created = created is null || entry.PublishedAt < created ? entry.PublishedAt : created;
            modified = modified is null || entry.PublishedAt > modified ? entry.PublishedAt : modified;
            description = manifest["description"]?.GetValue<string>() ?? description;
        }

        time["created"] = created!.Value.ToString("O", CultureInfo.InvariantCulture);
        time["modified"] = modified!.Value.ToString("O", CultureInfo.InvariantCulture);

        var tags = await ReadTagsAsync(context, name);
        var distTags = new JsonObject();

        foreach (var (tag, target) in tags.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            distTags[tag] = target;
        }

        return Results.Json(
            new JsonObject {
                ["_id"] = name,
                ["name"] = name,
                ["description"] = description,
                ["dist-tags"] = distTags,
                ["versions"] = versions,
                ["time"] = time
            }
        );
    }

    async Task<IResult> TarballAsync(HttpContext http, Guid subscription, string group, string feed, string name, string file) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        if (!IsFileName(file)) {
            return Results.NotFound();
        }

        // The file name is `{bare}-{version}.tgz` and nothing else, so the version is in it and the
        // entry is one exact lookup — and an object under the prefix that no entry names (a publish
        // that lost its claim, a half-finished one) is not reachable, because the bytes are read
        // from the entry's StoredAt and from nowhere the URL spells.
        var version = VersionOfTarball(name, file);
        var context = resolved.GetValueOrThrow();
        var entry = version is null ? null : (await context.Catalogue.GetAsync(EntryPath(name, version))).ValueOrDefault;

        if (entry is null) {
            return Results.NotFound();
        }

        if (HttpMethods.IsHead(http.Request.Method)) {
            return Results.Ok();
        }

        var read = await objects.GetAsync(context.StoragePrefix + entry.StoredAt, http.RequestAborted);

        if (read.TryGetError(out var missing)) {
            return missing.Code == ErrorCode.ResourceNotFound ? Results.NotFound() : FeedResponses.Refuse(missing, http);
        }

        return Results.Stream(read.GetValueOrThrow().Content, "application/octet-stream");
    }

    async Task<IResult> DistTagsAsync(HttpContext http, Guid subscription, string group, string feed, string name) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var tags = await ReadTagsAsync(resolved.GetValueOrThrow(), name);

        if (tags.Count == 0) {
            return Results.NotFound();
        }

        var rendered = new JsonObject();

        foreach (var (tag, target) in tags.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            rendered[tag] = target;
        }

        return Results.Json(rendered);
    }

    async Task<IResult> SetDistTagAsync(HttpContext http, Guid subscription, string group, string feed, string name, string tag) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Write, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var context = resolved.GetValueOrThrow();
        var body = await FeedResponses.ReadBodyAsync(http, 1024, http.RequestAborted);

        if (body.TryGetError(out var unreadable)) {
            return FeedResponses.Refuse(unreadable, http);
        }

        string? version;

        try {
            version = JsonNode.Parse(body.GetValueOrThrow())?.GetValue<string>();
        } catch (JsonException) {
            version = null;
        } catch (InvalidOperationException) {
            version = null;
        }

        if (version is null || !IsVersion(version)) {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "A dist-tag's body is a JSON string naming a version."), http);
        }

        if ((await context.Catalogue.GetAsync(EntryPath(name, version))).IsFailure) {
            return Results.NotFound();
        }

        var tags = await ReadTagsAsync(context, name);
        tags[tag] = version;

        var written = await WriteTagsAsync(context, name, tags);
        return written.TryGetError(out var error) ? FeedResponses.Refuse(error, http) : Results.Json(new JsonObject { ["ok"] = true });
    }

    async Task<IResult> RemoveDistTagAsync(HttpContext http, Guid subscription, string group, string feed, string name, string tag) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Write, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        if (tag == "latest") {
            return FeedResponses.Refuse(new(ErrorCode.InvalidRequestBody, "The 'latest' tag cannot be removed; point it elsewhere instead."), http);
        }

        var context = resolved.GetValueOrThrow();
        var tags = await ReadTagsAsync(context, name);

        if (!tags.Remove(tag)) {
            return Results.NotFound();
        }

        var written = await WriteTagsAsync(context, name, tags);
        return written.TryGetError(out var error) ? FeedResponses.Refuse(error, http) : Results.Json(new JsonObject { ["ok"] = true });
    }

    async Task<IResult> SearchAsync(HttpContext http, Guid subscription, string group, string feed) {
        var resolved = await access.ResolveAsync(http, FeedKind.Npm, subscription, group, feed, FeedIntent.Read, http.RequestAborted);

        if (resolved.TryGetError(out var refused)) {
            return FeedResponses.Refuse(refused, http);
        }

        var text = http.Request.Query["text"].ToString().Trim();
        var size = Math.Clamp(int.TryParse(http.Request.Query["size"], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 20, 1, 250);

        var context = resolved.GetValueOrThrow();
        var listed = await context.Catalogue.ListAsync(PathPrefix);

        if (listed.TryGetError(out var listError)) {
            return FeedResponses.Refuse(listError, http);
        }

        var objectsFound = new JsonArray();
        var packages = listed.GetValueOrThrow()
            .Select(x => (Entry: x, Name: NameOf(x.Path)))
            .Where(x => x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var total = 0;

        foreach (var package in packages) {
            var latestEntry = package.OrderBy(x => x.Entry.PublishedAt).Last().Entry;
            var manifest = JsonNode.Parse(latestEntry.Metadata) as JsonObject ?? new JsonObject();
            var description = manifest["description"]?.GetValue<string>() ?? "";

            if (text.Length > 0
                && !package.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !description.Contains(text, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            total++;

            if (objectsFound.Count < size) {
                objectsFound.Add(
                    new JsonObject {
                        ["package"] = new JsonObject {
                            ["name"] = package.Key,
                            ["version"] = VersionOf(latestEntry.Path, package.Key),
                            ["description"] = description
                        }
                    }
                );
            }
        }

        return Results.Json(new JsonObject { ["objects"] = objectsFound, ["total"] = total, ["time"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
    }

    static async Task<Dictionary<string, string>> ReadTagsAsync(FeedContext context, string name) {
        var entry = await context.Catalogue.GetAsync(TagsPrefix + name);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (entry.IsSuccess && JsonNode.Parse(entry.GetValueOrThrow().Metadata) is JsonObject stored) {
            foreach (var (tag, target) in stored) {
                if (target?.GetValue<string>() is { } version) {
                    tags[tag] = version;
                }
            }
        }

        return tags;
    }

    static async Task<Result> WriteTagsAsync(FeedContext context, string name, Dictionary<string, string> tags) {
        var rendered = new JsonObject();

        foreach (var (tag, target) in tags) {
            rendered[tag] = target;
        }

        var written = await context.Catalogue.PutAsync(
            new() { Path = TagsPrefix + name, StoredAt = "", Metadata = rendered.ToJsonString(), PublishedBy = context.Subject },
            replace: true
        );

        return written.TryGetError(out var error) ? Result.Failure(error) : Result.Success;
    }

    static string EntryPath(string name, string version) => PathPrefix + name + "/" + version;

    /// <summary>The tarball's file name — <c>{bare}-{version}.tgz</c>, the scope left off, as registry.npmjs.org spells it.</summary>
    internal static string TarballNameOf(string name, string version) =>
        $"{name[(name.IndexOf('/', StringComparison.Ordinal) + 1)..]}-{version}.tgz";

    /// <summary>The version a tarball's file name carries for a package, or <see langword="null" /> when the name is not that package's.</summary>
    internal static string? VersionOfTarball(string name, string file) {
        var bare = name[(name.IndexOf('/', StringComparison.Ordinal) + 1)..];

        if (!file.StartsWith(bare + "-", StringComparison.Ordinal) || !file.EndsWith(".tgz", StringComparison.Ordinal)) {
            return null;
        }

        var version = file[(bare.Length + 1)..^4];
        return IsVersion(version) ? version : null;
    }

    /// <summary>The version segment of an entry path under a package — empty for a tarball path.</summary>
    static string VersionOf(string path, string name) {
        var rest = path[(PathPrefix.Length + name.Length + 1)..];
        return rest.Contains('/', StringComparison.Ordinal) ? "" : rest;
    }

    /// <summary>The package name of an entry path — <c>@scope/name</c> or <c>name</c> — or empty for a path that is not a version entry.</summary>
    static string NameOf(string path) {
        var rest = path[PathPrefix.Length..];
        var segments = rest.Split('/');

        return segments.Length switch {
            2 when IsVersion(segments[1]) => segments[0],
            3 when segments[0].StartsWith('@') && IsVersion(segments[2]) => segments[0] + "/" + segments[1],
            _ => ""
        };
    }

    /// <summary>npm's own rule: lower case, URL-safe, at most 214 characters, an optional <c>@scope/</c>.</summary>
    internal static bool IsPackageName(string name) {
        if (name.Length is 0 or > 214) {
            return false;
        }

        var parts = name.Split('/');

        // A scope is `@scope/name` and nothing else: no bare `@scope`, no `a/b` without the `@`.
        if (parts.Length > 2 || (parts.Length == 2 && !parts[0].StartsWith('@')) || (parts.Length == 1 && parts[0].StartsWith('@'))) {
            return false;
        }

        foreach (var part in parts) {
            var bare = part.StartsWith('@') ? part[1..] : part;

            if (bare.Length == 0 || bare.StartsWith('.') || bare.StartsWith('_') || !bare.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.')) {
                return false;
            }
        }

        return true;
    }

    static bool IsVersion(string version) =>
        version.Length is > 0 and <= 256 && version != "-" && !version.StartsWith('.') && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+');

    static bool IsFileName(string file) =>
        file.Length is > 0 and <= 256 && file.EndsWith(".tgz", StringComparison.Ordinal) && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');
}
