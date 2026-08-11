// Images — docs/plan/23 § Build, row `Images`:
// "Build, SBOM (Syft), sign (cosign), push by digest".
//
// The requirement this target exists to satisfy is docs/plan/18 § Platform security, row Supply
// chain, and it is worth quoting in full because every decision below is downstream of one clause of
// it: "Every image built in CI, SBOM generated, signed with cosign, verified at admission. A pinned
// digest, never a tag."
//
// ⚠ NOTHING CAN BE DEPLOYED UNTIL THIS TARGET HAS RUN. `deploy/bootstrap/30-durable-schema-job.yaml`
// carries `image: CYBERCLOUD_IMAGE_PLACEHOLDER` and `deploy/bootstrap/bootstrap.sh` warns when
// `--image` is a tag rather than a digest, so the digest this target prints is the one input the
// documented install procedure has no other source for. That is why the final log line is the
// `bootstrap.sh --image …` invocation, spelled out: the handoff is the product of the target.
//
// ── ⚠ THERE IS NO DOCKERFILE IN THIS REPOSITORY, AND THERE MUST NOT BE ONE ────────────────────────
//
// Verified on SDK 10.0.302 against `src/Hosts/CyberCloud.Silo.Host`: `dotnet publish -t:PublishContainer`
// built `cybercloud-silo-host` on `mcr.microsoft.com/dotnet/aspnet:10.0` and reported
// `GeneratedContainerDigest = sha256:ef9a4e42…` — with no Dockerfile, no BuildKit, and no container
// runtime of any kind on the machine. The SDK's container tooling composes the layers itself and
// speaks the registry API directly.
//
// ⚠ SO "A CONTAINER RUNTIME IS MISSING" IS NOT THIS TARGET'S BLOCKER, AND SAYING IT WAS WOULD SEND
// THE NEXT PERSON TO INSTALL THE WRONG THING. What it genuinely cannot do without are the three
// preconditions below — a registry to push to, `syft`, and `cosign` — because all three sit on the
// far side of the word "digest": an image that never left the machine has no repository digest to
// pin, nothing to attach an SBOM to, and nothing to sign.
//
// The one place a runtime would come back is `ContainerRegistry` left unset, which makes the SDK
// push into the local daemon. That mode is deliberately not offered — see RequiredRegistry below.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Serilog;

partial class Build
{
    /// <summary>
    ///     The registry and repository prefix images are pushed to — <c>registry.example.com/cybercloud</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Required, with no default, and the absence of a default is the decision. The .NET SDK's
    ///     container tooling treats an unset registry as "push into the local daemon", which produces
    ///     an image that exists on exactly one machine and has an image ID rather than a repository
    ///     digest. docs/plan/18 § Platform security says "a pinned digest, never a tag"; an image ID
    ///     is neither, and a target that silently produced one would satisfy nobody's admission policy
    ///     while looking green.
    /// </remarks>
    [Parameter("Registry and repository prefix to push images to, e.g. registry.example.com/cybercloud. Required.")]
    readonly string? ContainerRegistry;

    /// <summary>
    ///     The tag pushed alongside the digest. Defaults to the short commit sha.
    /// </summary>
    /// <remarks>
    ///     ⚠ A tag is still pushed, and that is not a contradiction of "never a tag". A registry needs
    ///     a reference to write under, and a human reading `docker images` needs something other than
    ///     64 hex characters. The rule is about what is <i>consumed</i>: everything this target hands
    ///     downstream — <see cref="ImageManifestFile" />, the SBOM subject, the cosign subject, the
    ///     `bootstrap.sh --image` line — is by digest.
    /// </remarks>
    [Parameter("Tag to push alongside the digest. Defaults to the short commit sha.")]
    readonly string? ContainerImageTag;

    /// <summary>
    ///     A cosign private key reference (<c>--key</c>). Absent means keyless, which needs an OIDC
    ///     identity — a CI workload identity, or `cosign login` on a workstation.
    /// </summary>
    [Parameter("cosign key reference (file:, k8s://, azurekms://…). Absent means keyless signing, which needs an OIDC identity.")]
    readonly string? CosignKey;

    /// <summary>Where SBOMs are written, one per image.</summary>
    AbsolutePath SbomDirectory => ArtifactsDirectory / "sbom";

    /// <summary>
    ///     The digest of every image this run pushed, which is the only durable output of the target.
    ///     <para>
    ///         ⚠ Under <c>artifacts/</c> and therefore gitignored, unlike <c>openapi/</c>. A digest is
    ///         not a contract to diff — it changes on every commit by design — and a checked-in file
    ///         that changes on every commit is a merge conflict on every branch. What consumes it is
    ///         the same pipeline run: `deploy/bootstrap/bootstrap.sh --image` and the Helm
    ///         <c>--set image=</c> in <c>deploy/README.md</c>.
    ///     </para>
    /// </summary>
    AbsolutePath ImageManifestFile => ArtifactsDirectory / "images.json";

    /// <summary>
    ///     The hosts that ship as images: everything under <c>src/Hosts</c> that is not a test project
    ///     and not the Aspire AppHost.
    /// </summary>
    /// <remarks>
    ///     ⚠ The AppHost is excluded by ADR-014, not by convenience. docs/plan/02 § ADR-014 makes
    ///     Aspire the local orchestrator and says production deployment is Helm charts and Kubernetes
    ///     manifests, so an <c>IsAspireHost</c> project is a development entry point that must never
    ///     acquire a published image — an image is exactly the thing somebody would then deploy.
    /// </remarks>
    IReadOnlyList<AbsolutePath> ImageHostProjects
    {
        get
        {
            var hosts = RootDirectory / "src" / "Hosts";

            if (!hosts.DirectoryExists())
                return [];

            return hosts
                .GlobFiles("**/*.csproj")
                .Where(project => SuiteOwning(project) is null)
                .Where(project => !IsAspireHost(project))
                .OrderBy(x => x.NameWithoutExtension, StringComparer.Ordinal)
                .ToList();
        }
    }

    static bool IsAspireHost(AbsolutePath project)
        => project.ReadAllText().Contains("<IsAspireHost>true</IsAspireHost>", StringComparison.Ordinal);

    void BuildImages()
    {
        var hosts = ImageHostProjects;

        if (hosts.Count == 0)
        {
            // ○, not ✔ — the same distinction Build.Architecture.cs § GateStatus draws and
            // Build.Charts.cs prints over an empty charts/ directory.
            Log.Warning(
                "Images: inspected 0 host project(s). src/Hosts contains nothing publishable, so no "
                + "image was built, no SBOM was generated and nothing was signed. That is a pass and "
                + "it is worth nobody's trust — docs/plan/03 § Hosts lists four hosts. ○, not ✔.");

            return;
        }

        Log.Information(
            "Images: {Count} host(s) — {Hosts}",
            hosts.Count,
            string.Join(", ", hosts.Select(x => x.NameWithoutExtension)));

        var preconditions = new TargetPreconditions(nameof(Images));

        preconditions.Require(
            !string.IsNullOrWhiteSpace(ContainerRegistry),
            "no container registry is configured",
            "pass --container-registry registry.example.com/cybercloud. Without one the SDK pushes "
            + "into a local daemon, which yields an image id and not the repository digest "
            + "docs/plan/18 § Platform security requires");

        var syft = preconditions.Tool(
            "syft",
            "install Syft — `brew install syft`, or the release binary from anchore/syft on CI. "
            + "docs/plan/23 § Build, row Images names it specifically, and docs/plan/18 § Platform "
            + "security makes the SBOM part of the artefact rather than a report about it");

        var cosign = preconditions.Tool(
            "cosign",
            "install cosign — `brew install cosign`, or the release binary from sigstore/cosign on "
            + "CI. docs/plan/18 § Platform security: signatures are verified at admission, so an "
            + "unsigned image is one the cluster will refuse");

        preconditions.AssertSatisfied(
            "docs/plan/18 § Platform security, row Supply chain: \"Every image built in CI, SBOM "
            + "generated, signed with cosign, verified at admission. A pinned digest, never a tag.\" "
            + "Every clause of that is one of the checks above, and an image missing any of them is "
            + "an image that cannot be admitted.");

        SbomDirectory.CreateOrCleanDirectory();

        var pushed = new List<(string Host, string Reference, string Digest)>();

        foreach (var host in hosts)
        {
            var name = host.NameWithoutExtension;
            var repository = ImageRepository(name);
            var digest = PublishContainer(host, repository);
            var reference = $"{ContainerRegistry}/{repository}@{digest}";

            Sbom(syft!, name, reference);
            Sign(cosign!, name, reference);

            pushed.Add((name, reference, digest));

            Log.Information("Images: {Host} → {Reference}", name, reference);
        }

        WriteImageManifest(pushed);
    }

    /// <summary>
    ///     The repository an assembly name is pushed to. Lowercased because the OCI distribution spec
    ///     restricts repository names to lowercase, and a registry rejects the mixed-case form with a
    ///     404 that reads like a missing repository rather than a naming rule.
    /// </summary>
    static string ImageRepository(string assemblyName)
        => assemblyName.Replace('.', '-').ToLowerInvariant();

    /// <summary>
    ///     Builds and pushes one image, returning its digest.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <c>-getProperty:GeneratedContainerDigest</c> is how the digest comes back, and there
    ///         is no alternative worth having. The SDK prints "Pushed image 'x:tag' to registry 'y'"
    ///         and does not print the digest, so scraping the log would yield the tag — the one thing
    ///         docs/plan/18 § Platform security says never to pin. MSBuild's property-output mode runs
    ///         the target and then reports the property, so this is one invocation rather than two.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>EnableSdkContainerSupport</c> is passed on the command line rather than added to
    ///         each host's <c>.csproj</c>. It has to be set for a <c>Microsoft.NET.Sdk</c> project
    ///         (the Web SDK opts in on its own, and none of these hosts uses it), and setting it in
    ///         the project files would put "this project is containerised" in a place where the
    ///         container build is not — docs/plan/23 § Build makes the target the authority on what
    ///         ships as an image.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>--os linux --arch x64</c>: the platform runs on Kubernetes (docs/plan/09), so the
    ///         image architecture is a property of the deployment target and never of the machine the
    ///         build happens to run on. An arm64 workstation that published arm64 images would produce
    ///         images that pass every check here and fail to schedule.
    ///     </para>
    /// </remarks>
    string PublishContainer(AbsolutePath project, string repository)
    {
        var tag = ContainerImageTag ?? ShortCommitSha;

        // ⚠ Interpolation holes, not a pre-joined string: Nuke's ArgumentStringHandler quotes each
        // hole and passes literal text through. See the note in Build.Generate.cs § RunGenerator for
        // what a pre-joined string does instead.
        var output = DotNetTasks.DotNet(
            $"publish {project} --configuration {Configuration} --os linux --arch x64 "
            + $"-p:EnableSdkContainerSupport=true -p:ContainerRegistry={ContainerRegistry} "
            + $"-p:ContainerRepository={repository} -p:ContainerImageTag={tag} "
            + "-t:PublishContainer -getProperty:GeneratedContainerDigest",
            workingDirectory: RootDirectory,
            logOutput: false);

        var text = string.Join('\n', output.Where(x => x.Type == OutputType.Std).Select(x => x.Text));
        var digest = DigestPattern.Match(text);

        Assert.True(
            digest.Success,
            $"`dotnet publish -t:PublishContainer` for {project.NameWithoutExtension} reported no "
            + $"sha256 digest. MSBuild said:\n{text}\nWithout a digest there is nothing to sign and "
            + "nothing to pin, and docs/plan/18 § Platform security allows neither to be skipped.");

        return digest.Value;
    }

    /// <summary>A digest anywhere in MSBuild's property output, quoted or bare.</summary>
    static readonly Regex DigestPattern = new("sha256:[0-9a-f]{64}", RegexOptions.Compiled);

    /// <summary>
    ///     The short commit sha, which is the default tag.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a version number. docs/plan/23 § CI shape builds images on every merge to main
    ///     (row `main.yml`) and publishes versioned artefacts only on a tag (row `release.yml`), so
    ///     most images this target ever pushes belong to no release at all. A sha is the only handle
    ///     that is always true.
    /// </remarks>
    string ShortCommitSha =>
        GitTasks.Git("rev-parse --short HEAD", RootDirectory, logOutput: false, logInvocation: false)
            .Select(x => x.Text.Trim())
            .First(x => x.Length > 0);

    /// <summary>
    ///     The SBOM, generated from the pushed image rather than from the project.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Syft is pointed at the registry reference, not at the publish directory, and the two
    ///     are different documents.</b> The publish directory holds the application's own assemblies;
    ///     the image also holds the base image's Debian packages, its OpenSSL, its ICU. docs/plan/18
    ///     § Platform security pairs the SBOM with admission verification and with "an SBOM diff per
    ///     release", and a diff that cannot see a base-image CVE is a diff that misses the class of
    ///     finding SBOMs exist for.
    /// </remarks>
    void Sbom(Tool syft, string host, string reference)
    {
        var sbom = SbomDirectory / $"{host}.spdx.json";

        syft(
            $"scan registry:{reference} --output spdx-json={sbom}",
            workingDirectory: RootDirectory);

        Assert.FileExists(
            sbom,
            $"syft reported success for {host} and wrote no SBOM to {sbom}. docs/plan/18 § Platform "
            + "security makes the SBOM an artefact of the build, so a missing one is a failed build "
            + "and not a missing report.");
    }

    /// <summary>
    ///     The signature, and the SBOM attestation that goes with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Two cosign calls, not one, because they answer different questions at admission.
    ///         <c>sign</c> says "this digest came from us"; <c>attest</c> binds the SBOM to the same
    ///         digest so a policy can require that the SBOM it reads is the one for the image it is
    ///         admitting. An SBOM that merely sits in a build artefact store proves nothing about the
    ///         running image.
    ///     </para>
    ///     <para>
    ///         ⚠ Always <c>@sha256:…</c>, never the tag. Signing a tag signs whatever the tag points
    ///         at when the verifier looks, which is the substitution attack the signature is for.
    ///     </para>
    /// </remarks>
    void Sign(Tool cosign, string host, string reference)
    {
        var key = CosignKey is null ? string.Empty : $"--key {CosignKey} ";

        cosign($"sign --yes {key}{reference}", workingDirectory: RootDirectory);

        cosign(
            $"attest --yes {key}--type spdxjson --predicate {SbomDirectory / $"{host}.spdx.json"} {reference}",
            workingDirectory: RootDirectory);
    }

    /// <summary>
    ///     Writes <see cref="ImageManifestFile" /> and logs the <c>bootstrap.sh</c> line that consumes
    ///     it — the handoff docs/plan/09 § The platform's own cluster describes as phase 0.
    /// </summary>
    void WriteImageManifest(List<(string Host, string Reference, string Digest)> pushed)
    {
        var manifest = new JsonObject
        {
            ["commit"] = ShortCommitSha,
            ["images"] = new JsonArray(pushed
                .Select(x => (JsonNode)new JsonObject
                {
                    ["host"] = x.Host,
                    ["reference"] = x.Reference,
                    ["digest"] = x.Digest,
                })
                .ToArray()),
        };

        ImageManifestFile.WriteAllText(manifest.ToString());

        Log.Information(
            "Images: {Count} image(s) pushed by digest, SBOM'd and signed. Manifest: {Manifest}",
            pushed.Count,
            RootDirectory.GetRelativePathTo(ImageManifestFile));

        var silo = pushed.FirstOrDefault(x => string.Equals(x.Host, "CyberCloud.Silo.Host", StringComparison.Ordinal));

        if (silo.Reference is not null)
        {
            Log.Information(
                "Images: the bootstrap this unblocks — ./deploy/bootstrap/bootstrap.sh --image {Reference} --shards <file>",
                silo.Reference);
        }
    }
}
