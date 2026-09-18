// The OCI distribution API, read with HttpClient and nothing else — what charts/bundle/oci.sh does
// with curl, for the one target that needs an answer richer than a digest.
//
// Build.Licence.cs asks two things of a registry that oci.sh does not: the IMAGE CONFIG behind a
// manifest, because that is where `org.opencontainers.image.licenses` lives, and a fetch BY DIGEST
// rather than by tag, because the scan is over what a component.yaml records and not over whatever
// the tag serves this afternoon. Both are three requests deep — manifest, platform manifest, config
// blob — and neither is a shape `docker` offers without a daemon or a pull.
//
// ⚠ NO DOCKER, AND NOT BECAUSE IT IS ABSENT. `docker buildx imagetools inspect --format '{{json
// .Image}}'` returns the same config, and it was how the labels below were first measured on
// 2026-09-15. It is not used because it needs a daemon, and because every manifest it fetches from
// docker.io is a GET that counts against Docker Hub's anonymous pull limit — measured the same day,
// as a 429 on the fourth inspection of an Altinity image. The registry's HEAD is free; its GET is
// not, and this client makes exactly the GETs it needs and no HEAD it does not.
//
// ⚠ THE INDEX DIGEST IS THE DIGEST, AND THE PLATFORM MANIFEST IS A STEP ON THE WAY TO THE CONFIG. A
// multi-architecture tag resolves to an index whose digest is what a kubelet resolves and what
// component.yaml records; the config with the labels hangs off ONE platform's manifest beneath it.
// So this walks index → linux/amd64 manifest → config, and reports the labels of the amd64 image,
// which is the one docs/plan/09's clusters run. A label that differs between architectures of one
// tag would be an upstream defect this cannot see, and it is not one anybody has reported.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
///     What a registry says about one image: the digest it served, the labels on its config, and
///     the media type of the document the reference resolved to.
/// </summary>
/// <param name="Reference">The reference as it was asked for, digest included.</param>
/// <param name="Digest">The digest of the document the reference resolved to.</param>
/// <param name="MediaType">Its media type — an index or a single-platform manifest.</param>
/// <param name="Labels">The <c>config.Labels</c> of the linux/amd64 image, or of the only image.</param>
sealed record OciImage(
    string Reference,
    string Digest,
    string MediaType,
    IReadOnlyDictionary<string, string> Labels
) {
    /// <summary>The OCI annotation an image carries its licence under, when it carries one.</summary>
    public const string LicencesLabel = "org.opencontainers.image.licenses";

    /// <summary>The value of <see cref="LicencesLabel" />, or <see langword="null" />.</summary>
    public string? Licences => Labels.TryGetValue(LicencesLabel, out var value) && value.Length > 0 ? value : null;
}

/// <summary>
///     A pull-only client for the OCI distribution API, anonymous except where a registry rations
///     anonymous reads.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ Anonymous on purpose. Every image the bundle records is public, and a client that
///         carried credentials for a private registry would scan a mirror as though it were upstream
///         — a different artefact and a different licence question. A registry that needs a login
///         for a public image is reported as a failure to read, not worked around.
///     </para>
///     <para>
///         ⚠ <b>Docker Hub is the exception, and it changes what is asked for, not what is asked.</b>
///         Its anonymous manifest budget is 100 pulls per six hours per address, and it was
///         exhausted on 2026-09-15 by the measurements that wrote this file — the first full run of
///         the scan then read every quay.io, ghcr.io and registry.k8s.io image and got HTTP 429 for
///         ten of the eleven on docker.io. GitHub-hosted runners share addresses, so a weekly run
///         there can start already over the limit. When <c>DOCKERHUB_USERNAME</c> and
///         <c>DOCKERHUB_TOKEN</c> are set, the token request to Docker Hub's realm carries them and
///         the budget becomes the account's; the registry asked, the repository and the digest are
///         the same either way.
///     </para>
/// </remarks>
sealed class OciRegistry : IDisposable {
    static readonly string? DockerHubUser = Environment.GetEnvironmentVariable("DOCKERHUB_USERNAME");
    static readonly string? DockerHubToken = Environment.GetEnvironmentVariable("DOCKERHUB_TOKEN");

    static readonly string Accept = string.Join(
        ", ",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json"
    );

    static readonly Regex Challenge = new(
        "(?<key>realm|service|scope)=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled
    );

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    ///     Resolves a reference and reads the labels of the image it names.
    /// </summary>
    /// <param name="reference">
    ///     <c>repository:tag@sha256:…</c> as <c>images:</c> records it, or <c>repository:tag</c>. With a
    ///     digest the manifest is fetched BY the digest, so what is read is what was recorded and not
    ///     what the tag serves now; the tag is kept for the message.
    /// </param>
    /// <exception cref="HttpRequestException">The registry did not serve the reference.</exception>
    public OciImage Inspect(string reference) {
        var (host, repository, tag, digest) = Parse(reference);
        var token = (string?)null;

        var manifest = Fetch($"https://{host}/v2/{repository}/manifests/{digest ?? tag}", ref token, host, repository);
        var servedDigest = manifest.Headers.TryGetValues("Docker-Content-Digest", out var served)
            ? served.First()
            : digest ?? string.Empty;
        var mediaType = manifest.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var document = JsonNode.Parse(manifest.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            ?? throw new HttpRequestException($"{reference}: the manifest is not JSON");

        if (document["manifests"] is JsonArray manifests) {
            // An index. Walk to the linux/amd64 manifest, or the first one when no platform matches.
            var chosen = manifests
                .FirstOrDefault(entry =>
                    entry?["platform"]?["os"]?.GetValue<string>() == "linux"
                    && entry?["platform"]?["architecture"]?.GetValue<string>() == "amd64"
                )
                ?? manifests.FirstOrDefault()
                ?? throw new HttpRequestException($"{reference}: the index lists no manifest");

            var platformDigest = chosen["digest"]?.GetValue<string>()
                ?? throw new HttpRequestException($"{reference}: an index entry carries no digest");

            var platform = Fetch(
                $"https://{host}/v2/{repository}/manifests/{platformDigest}",
                ref token,
                host,
                repository
            );

            document = JsonNode.Parse(platform.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                ?? throw new HttpRequestException($"{reference}: the platform manifest is not JSON");
        }

        var configDigest = document["config"]?["digest"]?.GetValue<string>()
            ?? throw new HttpRequestException($"{reference}: the manifest names no config blob");

        var config = Fetch($"https://{host}/v2/{repository}/blobs/{configDigest}", ref token, host, repository);
        var image = JsonNode.Parse(config.Content.ReadAsStringAsync().GetAwaiter().GetResult())
            ?? throw new HttpRequestException($"{reference}: the config blob is not JSON");

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (image["config"]?["Labels"] is JsonObject found) {
            foreach (var (key, value) in found) {
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) {
                    labels[key] = text;
                }
            }
        }

        return new(reference, servedDigest, mediaType, labels);
    }

    /// <summary>
    ///     One GET, with the bearer-token dance on a 401 — the same three steps oci.sh makes with curl.
    /// </summary>
    HttpResponseMessage Fetch(string url, ref string? token, string host, string repository) {
        var response = Send(url, token);

        if (response.StatusCode == HttpStatusCode.Unauthorized
            && response.Headers.WwwAuthenticate.FirstOrDefault(x => x.Scheme == "Bearer") is { } bearer) {
            var parameters = Challenge.Matches(bearer.Parameter ?? string.Empty)
                .ToDictionary(x => x.Groups["key"].Value, x => x.Groups["value"].Value, StringComparer.Ordinal);

            if (parameters.TryGetValue("realm", out var realm)) {
                parameters.TryGetValue("service", out var service);

                if (!parameters.TryGetValue("scope", out var scope) || scope.Length == 0) {
                    scope = $"repository:{repository}:pull";
                }

                var tokenUrl =
                    $"{realm}?service={Uri.EscapeDataString(service ?? host)}&scope={Uri.EscapeDataString(scope)}";

                using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenUrl);

                if (host == "registry-1.docker.io"
                    && !string.IsNullOrEmpty(DockerHubUser)
                    && !string.IsNullOrEmpty(DockerHubToken)) {
                    tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{DockerHubUser}:{DockerHubToken}"))
                    );
                }

                using var tokenResponse = http.SendAsync(tokenRequest).GetAwaiter().GetResult();
                var grant = tokenResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var issued = JsonNode.Parse(grant);

                token = issued?["token"]?.GetValue<string>() ?? issued?["access_token"]?.GetValue<string>();
                response.Dispose();
                response = Send(url, token);
            }
        }

        if (!response.IsSuccessStatusCode) {
            var status = (int)response.StatusCode;
            response.Dispose();

            throw new HttpRequestException($"{url} answered HTTP {status}");
        }

        return response;
    }

    HttpResponseMessage Send(string url, string? token) {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", Accept);

        if (token is not null) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return http.SendAsync(request).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Splits a reference into the host to ask, the repository path, the tag, and the digest if
    ///     one was given.
    /// </summary>
    /// <remarks>
    ///     The same rules oci.sh applies: a first segment with a dot or <c>localhost</c> is a registry,
    ///     anything else is Docker Hub, and a Hub repository with no slash is under <c>library/</c>.
    ///     Docker Hub's API host is <c>registry-1.docker.io</c>, which is why <c>docker.io</c> in a
    ///     reference is never the host that is asked.
    /// </remarks>
    static (string Host, string Repository, string Tag, string? Digest) Parse(string reference) {
        var digest = (string?)null;
        var at = reference.IndexOf('@', StringComparison.Ordinal);

        if (at >= 0) {
            digest = reference[(at + 1)..];
            reference = reference[..at];
        }

        var lastSlash = reference.LastIndexOf('/');
        var colon = reference.IndexOf(':', lastSlash < 0 ? 0 : lastSlash);
        var tag = colon < 0 ? "latest" : reference[(colon + 1)..];
        var repository = colon < 0 ? reference : reference[..colon];
        var firstSlash = repository.IndexOf('/', StringComparison.Ordinal);
        var first = firstSlash < 0 ? string.Empty : repository[..firstSlash];
        var host = "registry-1.docker.io";

        if (first.Contains('.', StringComparison.Ordinal) || first == "localhost") {
            host = first == "docker.io" ? "registry-1.docker.io" : first;
            repository = repository[(firstSlash + 1)..];
        }

        if (host == "registry-1.docker.io" && !repository.Contains('/', StringComparison.Ordinal)) {
            repository = "library/" + repository;
        }

        return (host, repository, tag, digest);
    }

    /// <summary>Downloads a text document, for licence files and chart indexes.</summary>
    public string GetText(string url) => http.GetStringAsync(url).GetAwaiter().GetResult();

    /// <summary>Downloads a binary document, for packaged charts.</summary>
    public byte[] GetBytes(string url) => http.GetByteArrayAsync(url).GetAwaiter().GetResult();

    public void Dispose() => http.Dispose();
}
