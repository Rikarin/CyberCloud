// Licence — docs/plan/23 § Build, row `Licence`: "ADR-011 scan over charts and images".
//
// ADR-011 § Enforcement: "a build gate runs a licence scan over the chart set and the container
// images in the platform bundle [and] fails on any SSPL/BUSL/AGPL image outside an allow-list with a
// written reason". This file is that gate, since issue #17. Until then it was `NotImplementedYet`,
// carrying a measurement instead of a scan, and the measurement is kept below because it decided the
// shape of what was written.
//
// ── ⚠ ONE LIST WAS BEING ASKED TWO QUESTIONS, AND THIS SCAN ASKS EACH QUESTION OF THE RIGHT LIST ──
//
// Measured on 2026-09-03 against `mcr.microsoft.com/dotnet/aspnet:10.0`, the base image
// Build.Images publishes every platform host onto: /usr/share/doc holds 99 packaged components and
// 76 of them declare a GPL or LGPL licence in their Debian copyright file — coreutils is
// `License: GPL-3+`. So a scan over image CONTENTS that failed on anything outside
// build/Build.Bundle.cs' `BundleLicenceAllowList` (Apache-2.0, BSD, MIT, MPL-2.0) would fail on our
// own runtime image, on its first run, before it reached a bundle component. Issue #18 records the
// same contradiction from the other end: ADR-011's own table marks LINSTOR (GPL-3.0), DRBD (GPL-2.0)
// and ClamAV (GPL-2.0) ✓, and the allow-list would fail all three.
//
// The fault is not the list and not the ADR. It is that "may this platform OFFER this software as a
// managed service" and "what is LINKED INTO this artefact we ship" are two questions with different
// answers for the same licence — GPL coreutils in a base image is ordinary redistribution; SSPL
// software offered as a service is the thing ADR-011 exists to prevent — and one list was answering
// both. So this scan is two scans:
//
//   1. THE ARTEFACT'S OWN LICENCE, against the offering allow-list. Every bundle component is one
//      upstream project offered as part of the platform, and its licence is read off the artefacts
//      rather than off the component.yaml: the LICENSE file at the pinned release
//      (`licenceEvidence:`, classified by its text), the pinned chart's `artifacthub.io/license`
//      annotation where the chart carries one, and the `org.opencontainers.image.licenses` label on
//      every recorded image where the image carries one. Each must be on `BundleLicenceAllowList`,
//      and each must agree with what the component declares. A mismatch names both sides.
//   2. WHAT IS LINKED INTO EACH IMAGE, against ADR-011's deny-list. Syft produces an SBOM per image
//      — the same Syft Build.Images already runs over our own images — and every package licence is
//      checked against `ServiceRestrictedLicences`: SSPL, BUSL, AGPL, Elastic, RSAL, the licences
//      whose purpose is to forbid offering the software as a service. Every other package licence is
//      REPORTED and not judged. GPL and LGPL inside a layer are the ordinary state of a Linux image,
//      and a gate that failed on them would be the gate the measurement above shows cannot be green.
//
// ⚠ WHAT IS STILL #18's, AND IS DELIBERATELY NOT DECIDED HERE. A second ALLOW-list — "what may be
// linked into an artefact we ship" — would turn scan 2's report into a judgement over every package.
// This file does not write one. It fails only on the licences ADR-011 § Enforcement names by
// family, which needs no list nobody argued for, and it prints the package-level counts so that when
// #18 writes the second list there is a measurement to write it against.
//
// ── ⚠ WHAT WAS MEASURED WHILE WRITING THIS, BECAUSE IT DECIDED THE EVIDENCE ORDER ────────────────
//
// On 2026-09-15, of the thirty-two images charts/bundle/ records, FIVE carry
// `org.opencontainers.image.licenses`: cfssl/cfssl (BSD-2-Clause), chrislusf/seaweedfs-operator
// (Apache-2.0), curlimages/curl (MIT), ghcr.io/cloudnative-pg/cloudnative-pg (Apache-2.0) and
// ghcr.io/rabbitmq/cluster-operator (MPL-2.0). Twenty-seven carry no licence label at all — every
// cert-manager image, every Cluster API image, KubeVirt, CDI, Strimzi, Kamaji. Of the eleven charts
// pulled from a Helm repository, TWO annotate `artifacthub.io/license` (cert-manager and
// victoria-metrics-operator). So a scan whose only evidence was the artefacts' own metadata would
// find no evidence for fourteen of nineteen components, and "unknown" would be the normal state
// rather than the alarm. That is why `licenceEvidence:` exists: every one of the nineteen upstreams
// publishes a LICENSE at the release the pin names — all nineteen were fetched and classified the
// same day, and all nineteen agree with the declaration — and a URL pinned to that release is an
// artefact the scan can read on every run. The labels and annotations are checked wherever they
// exist, and the first run found the first disagreement: cfssl/cfssl, a helper image kamaji
// renders, is BSD-2-Clause, which was not on the allow-list until it was. Build.Bundle.cs
// § BundleLicenceAllowList carries that argument.
//
// ── ⚠ LOCAL AND CI ARE DELIBERATELY ASYMMETRIC, THE SAME WAY Build.Test.cs § the coverage floor IS ──
//
// Syft is not on every workstation. Locally, its absence is a warning and the image-contents scan is
// reported as ○ not inspected, row by row, so the artefact-level scan still runs and still fails on
// a real finding. On CI (`IsServerBuild`) its absence fails the target: weekly.yml installs it, and
// a weekly licence scan that skipped the SBOM half would be green over exactly the row with a legal
// consequence. The registry and the LICENSE fetches need a network and no tool; a network failure is
// a failure everywhere, because a scan that could not read an artefact has not scanned it.
//
// ⚠ Build.Images' SBOMs are read when they exist and reported as ○ when they do not. `Licence`
// depends on `Images`, so on a CI run with a registry they exist; without one `Images` is blocked,
// and the way to run this target — on a workstation, and in weekly.yml until docs/plan/23 § CI
// secrets is acted on — is `./build.sh Licence --skip Images` (add `Charts` locally without helm).
// ScanPlatformImages tells a skip that was asked for by name from an `Images` that ran and wrote
// nothing: the first is a ○ row and a warning on CI too, the second is still a failure there.

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

partial class Build {
    /// <summary>Where the report goes: one Markdown file a person reads, one JSON file a tool does.</summary>
    AbsolutePath LicenceReportDirectory => ArtifactsDirectory / "licence";

    AbsolutePath LicenceReportFile => LicenceReportDirectory / "report.md";

    AbsolutePath LicenceReportJson => LicenceReportDirectory / "report.json";

    /// <summary>
    ///     The licence families ADR-011 § Enforcement names — the ones whose terms exist to forbid
    ///     offering the software as a service. A package under any of these, in any image, fails the
    ///     scan unless <see cref="LicenceExceptions" /> carries a written reason for that artefact.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A deny-list, and this is the one place in the build where a deny-list is right.</b>
    ///         <c>BundleLicenceAllowList</c> is an allow-list because a component's licence is one
    ///         string and the question is whether we may offer it. A package inside an image is one of
    ///         hundreds, most of them GPL or LGPL, and the question there is only whether any of them
    ///         is the kind ADR-011 refuses. Matched by prefix and family rather than exact id, because
    ///         SBOM tools spell these in several ways (<c>AGPL-3.0</c>, <c>AGPL-3.0-only</c>,
    ///         <c>AGPL-3.0-or-later</c>, <c>LicenseRef-SSPL</c>) and a list that missed one spelling
    ///         would pass the thing it exists to catch.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>AGPL is here and Grafana is an exception, written where this paragraph said it
    ///         would be.</b> ADR-011 marks Grafana "⚠ Offerable as a managed instance (we distribute,
    ///         we do not modify)". That is a condition, and it is written into
    ///         <see cref="LicenceExceptions" /> next to the artefact it excuses — not into this list.
    ///         This paragraph used to end "the day a Grafana image enters the bundle"; the image
    ///         entered the tree as a workload <c>charts/managed/grafana</c> renders (#32), which is
    ///         not the bundle and not yet a thing this scan reads — the entry's own remarks say so.
    ///     </para>
    /// </remarks>
    static readonly string[] ServiceRestrictedLicences = [
        "SSPL",
        "BUSL",
        "AGPL",
        "Elastic",
        "RSAL",
        "Redis Source Available",
        "Server Side Public",
        "Business Source"
    ];

    /// <summary>
    ///     Artefacts allowed to carry a service-restricted licence, each with the reason written
    ///     beside it — ADR-011's "outside an allow-list with a written reason".
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Keyed by the artefact as the report names it: an image reference without its digest, a
    ///         component name, or an SBOM package as <c>name@version</c>. It was empty until #32's
    ///         third noun, on purpose: an allowance with no artefact behind it is a permission nobody
    ///         argued for. The one entry has an artefact behind it — <c>charts/managed/grafana</c>
    ///         renders <c>grafana/grafana</c> by digest — and the argument beside it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The scan does not reach the artefact the entry excuses, and the entry is written
    ///         anyway.</b> <see cref="ScanComponent" /> reads <c>charts/bundle/</c> components' images
    ///         and <see cref="ScanPlatformImages" /> the platform's own; an image a chart under
    ///         <c>charts/managed/</c> renders — <c>haproxy</c>'s GPL-2.0 before this, Grafana's AGPL-3.0
    ///         and the collector's Apache-2.0 now — is outside both. So today nothing in this target
    ///         reads the entry, <c>GrafanaDeclarationTests.TheLicenceGateCarriesTheGrafanaExceptionBesideTheImage</c>
    ///         is its only reader, and the day the scan widens to <c>image:</c> values under
    ///         <c>charts/managed/*/values.yaml</c> it finds an argued exception rather than a red
    ///         row. <c>charts/managed/grafana/conformance.yaml § owed</c>,
    ///         <c>licence-scan-does-not-read-workload-images</c>, is the debt.
    ///     </para>
    /// </remarks>
    static readonly Dictionary<string, string> LicenceExceptions = new(StringComparer.Ordinal) {
        // ⚠ ADR-011 § The licence audit, the Grafana row, read for a DEPLOYED component: "Offerable as
        // a managed instance (we distribute, we do not modify). Our portal must not embed or link
        // Grafana code — it embeds rendered dashboards by URL." CyberCloud.Dashboard/grafanas runs
        // upstream's image by digest in a tenant's namespace, configured through GF_* variables and a
        // provisioning file — distributed unmodified, its network clause binding whoever MODIFIES and
        // serves — and the portal's one integration is the URL the `url` action returns. Grafanas'
        // remarks carry the three-part reading; charts/managed/grafana/SOURCE repeats it beside the
        // pin. Had the row refused AGPL for a deployed component, this entry would not exist and
        // GrafanaReconciler would fail every pass naming the ADR.
        ["grafana/grafana"] =
            "AGPL-3.0, allowed by ADR-011's Grafana row as a managed instance: we distribute, we do not "
            + "modify. Upstream's image, pinned by digest, configured through its documented surface; no "
            + "Grafana code is linked into any platform assembly or into the portal, which embeds rendered "
            + "dashboards by URL. Rendered by charts/managed/grafana for CyberCloud.Dashboard/grafanas."
    };

    /// <summary>One line of the report: an artefact, where its licence was read from, and the verdict.</summary>
    /// <param name="Section">Which table it goes in.</param>
    /// <param name="Artefact">The component, image, chart or package.</param>
    /// <param name="Evidence">The URL, label, annotation or SBOM the licence came from.</param>
    /// <param name="Licence">What the evidence says, or what it failed to say.</param>
    /// <param name="Verdict">✔ allowed, ✘ refused, or ○ not inspected.</param>
    /// <param name="Detail">The sentence that goes with the verdict.</param>
    sealed record LicenceRow(string Section, string Artefact, string Evidence, string Licence, string Verdict, string Detail);

    const string Allowed = "✔";
    const string Refused = "✘";
    const string NotInspected = "○";

    void ScanLicences() {
        var components = ReadBundleComponents(out var manifestViolations);
        var rows = new List<LicenceRow>();
        var violations = new List<string>();

        foreach (var violation in manifestViolations.Where(x => x.Contains("licenceEvidence", StringComparison.Ordinal))) {
            violations.Add(violation);
        }

        var syft = ResolveOptionalTool(
            "syft",
            "the image-contents half of the scan",
            "install Syft — `brew install syft`, or anchore/sbom-action/download-syft on CI, which "
            + "weekly.yml and release.yml already do",
            violations
        );

        using var registry = new OciRegistry();

        Log.Information(
            "Licence: {Components} bundle component(s) recording {Images} image(s); syft {Syft}; "
            + "SBOMs from Images: {Sboms}",
            components.Count,
            components.Sum(x => ReadBundleSequence(x.File, "images").Count),
            syft is null ? "absent" : "present",
            SbomDirectory.DirectoryExists() ? SbomDirectory.GlobFiles("*.spdx.json").Count : 0
        );

        foreach (var component in components) {
            ScanComponent(component, registry, syft, rows, violations);
        }

        ScanPlatformImages(rows, violations);

        WriteLicenceReport(rows, violations);

        var refused = rows.Count(x => x.Verdict == Refused);
        var uninspected = rows.Count(x => x.Verdict == NotInspected);

        Log.Information(
            "Licence: {Rows} artefact(s) — {Allowed} ✔, {Refused} ✘, {Uninspected} ○. Report: {Report}",
            rows.Count,
            rows.Count - refused - uninspected,
            refused,
            uninspected,
            RootDirectory.GetRelativePathTo(LicenceReportFile)
        );

        if (violations.Count == 0) {
            return;
        }

        foreach (var violation in violations) {
            Log.Error("  ✘ {Violation}", violation);
        }

        Assert.Fail(
            $"Licence: {violations.Count} finding(s), listed above and in "
            + $"{RootDirectory.GetRelativePathTo(LicenceReportFile)}. docs/plan/02 § ADR-011: "
            + "\"Offering software as a service is exactly the use that several 2023-2025 licence "
            + "changes exist to prevent. This is a product-blocking category of mistake.\" A "
            + "component.yaml that disagrees with its artefacts is corrected on the side the "
            + "artefact contradicts; a service-restricted package is a refusal with the alternative "
            + "written down, or an entry in LicenceExceptions with the argument beside it."
        );
    }

    // ── One component ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The artefact-level scan of one component: its LICENSE at the pinned release, its chart's
    ///     annotation, its images' labels — and, with Syft, what each image links.
    /// </summary>
    void ScanComponent(
        BundleComponent component,
        OciRegistry registry,
        Tool? syft,
        List<LicenceRow> rows,
        List<string> violations
    ) {
        var relative = RootDirectory.GetRelativePathTo(component.File).ToString();
        var declared = component.Scalars.GetValueOrDefault("licence", "(none)");

        // ⚠ A first-party component (`install: file`, issue #15) is a document this repository
        // wrote. It pulls nothing from anyone, the Bundle gate forbids `licence:` and
        // `licenceEvidence:` on it, and so there is no upstream to witness — it is "not inspected"
        // rather than "declared and unwitnessed", which is the refusal for a PIN with no evidence.
        if (component.Scalars.GetValueOrDefault("install", string.Empty) == "file") {
            rows.Add(new("Components", component.Name, "(first-party file)", "(none)", NotInspected, "install: file pulls no upstream artefact"));

            return;
        }

        // 1. The LICENSE file at the pinned release, classified by its text.
        if (component.Scalars.TryGetValue("licenceEvidence", out var evidenceUrl)) {
            try {
                var text = registry.GetText(evidenceUrl);
                var classified = ClassifyLicenceText(text);

                if (classified is null) {
                    rows.Add(new("Components", component.Name, evidenceUrl, "unrecognised", Refused, "the licence text matches no family this scan knows"));
                    violations.Add(
                        $"{relative}: `licenceEvidence: {evidenceUrl}` fetched, and its text matches none of "
                        + "the licence families Build.Licence.cs § ClassifyLicenceText recognises. Either "
                        + "the URL is not a licence file, or it is a licence this scan has never seen — and "
                        + "the second case is worth a person reading it before the classifier learns it"
                    );
                } else if (!string.Equals(classified, declared, StringComparison.Ordinal)) {
                    rows.Add(new("Components", component.Name, evidenceUrl, classified, Refused, $"component.yaml declares `licence: {declared}`"));
                    violations.Add(
                        $"{relative} declares `licence: {declared}`, and the licence file it names — "
                        + $"{evidenceUrl} — reads as {classified}. The declaration is a claim about the "
                        + "artefact and the artefact disagrees; correct whichever is wrong, and if it is "
                        + "the artefact, the pin has moved to a release under different terms"
                    );
                } else {
                    var verdict = IsOnBundleAllowList(classified) ? Allowed : Refused;
                    rows.Add(new("Components", component.Name, evidenceUrl, classified, verdict, verdict == Allowed ? "agrees with component.yaml; on the allow-list" : "not on ADR-011's allow-list"));

                    if (verdict == Refused) {
                        violations.Add($"{relative}: {classified}, read from {evidenceUrl}, is not on ADR-011's allow-list ({string.Join(", ", BundleLicenceAllowList)})");
                    }
                }
            } catch (HttpRequestException exception) {
                rows.Add(new("Components", component.Name, evidenceUrl, "unread", Refused, exception.Message));
                violations.Add($"{relative}: `licenceEvidence: {evidenceUrl}` could not be fetched ({exception.Message}). A licence nobody can read is a licence nobody has checked");
            }
        } else {
            rows.Add(new("Components", component.Name, "(no licenceEvidence:)", declared, Refused, "declared and unwitnessed"));
        }

        // 2. The pinned chart's own metadata, where the pin is a chart.
        ScanChartMetadata(component, registry, declared, relative, rows, violations);

        // 3. Every recorded image: its label, and with Syft, its contents.
        foreach (var image in ReadBundleSequence(component.File, "images")) {
            ScanImage(image, component.Name, declared, registry, syft, rows, violations);
        }
    }

    /// <summary>
    ///     Reads the pinned chart's <c>Chart.yaml</c> — from the repository index for
    ///     <c>install: helm</c>, from inside the archive for <c>install: helm-archive</c> — and checks
    ///     its <c>artifacthub.io/license</c> annotation when it has one. Its dependencies are listed.
    /// </summary>
    /// <remarks>
    ///     ⚠ Dependencies are listed and not scanned, and the report says so per row. A Helm
    ///     dependency is pinned by a version RANGE (<c>0.3.*</c>), often lives at an <c>oci://</c>
    ///     repository that has no <c>index.yaml</c>, and for a <c>repository: ""</c> is a subchart
    ///     inside the same archive. What its licence is, is a question about the rendered images —
    ///     which <c>images:</c> already records and step 3 scans. The listing is here so the report
    ///     shows what the chart pulls in, and so a dependency that appears between two runs is a
    ///     visible diff.
    /// </remarks>
    void ScanChartMetadata(
        BundleComponent component,
        OciRegistry registry,
        string declared,
        string relative,
        List<LicenceRow> rows,
        List<string> violations
    ) {
        var install = component.Scalars.GetValueOrDefault("install", string.Empty);
        UpstreamChart? chart;
        string evidence;

        try {
            switch (install) {
                case "helm":
                    evidence = $"{component.Scalars["repo"]}/index.yaml";
                    chart = ReadIndexEntry(registry.GetText(evidence), component.Scalars["chart"], component.Scalars["version"]);
                    break;
                case "helm-archive":
                    evidence = component.Scalars["archive"];
                    chart = ReadArchivedChart(registry.GetBytes(evidence));
                    break;
                default:
                    return;
            }
        } catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException) {
            rows.Add(new("Charts", component.Name, install, "unread", Refused, exception.Message));
            violations.Add($"{relative}: the pinned chart could not be read ({exception.Message}), so its annotations were not checked");

            return;
        }

        if (chart is null) {
            rows.Add(new("Charts", component.Name, evidence, "absent", Refused, $"no entry for {component.Scalars["chart"]} {component.Scalars["version"]}"));
            violations.Add($"{relative}: {evidence} has no entry for chart {component.Scalars["chart"]} version {component.Scalars["version"]}. The pin does not resolve, and install.sh --verify would say the same");

            return;
        }

        if (chart.Licence is null) {
            rows.Add(new("Charts", $"{component.Name} ({chart.Name} {chart.Version})", evidence, "no annotation", NotInspected, "the chart carries no artifacthub.io/license; the LICENSE file above is the evidence"));
        } else if (!string.Equals(chart.Licence, declared, StringComparison.Ordinal)) {
            rows.Add(new("Charts", $"{component.Name} ({chart.Name} {chart.Version})", evidence, chart.Licence, Refused, $"component.yaml declares `licence: {declared}`"));
            violations.Add($"{relative} declares `licence: {declared}` and the pinned chart's artifacthub.io/license annotation says {chart.Licence}");
        } else {
            rows.Add(new("Charts", $"{component.Name} ({chart.Name} {chart.Version})", evidence, chart.Licence, Allowed, "artifacthub.io/license agrees with component.yaml"));
        }

        foreach (var (name, version, repository) in chart.Dependencies) {
            rows.Add(new(
                "Chart dependencies",
                $"{component.Name} → {name} {version}",
                repository.Length == 0 ? "(subchart inside the archive)" : repository,
                "listed",
                NotInspected,
                "a dependency's images are in this component's images: block, which is where they are scanned"
            ));
        }
    }

    /// <summary>
    ///     One image: the licence label on its config, and with Syft, every package it links.
    /// </summary>
    void ScanImage(
        string image,
        string componentName,
        string declared,
        OciRegistry registry,
        Tool? syft,
        List<LicenceRow> rows,
        List<string> violations
    ) {
        var reference = image.Split('@')[0];
        OciImage? inspected;

        try {
            inspected = registry.Inspect(image);
        } catch (HttpRequestException exception) {
            rows.Add(new("Images", reference, "registry", "unread", Refused, exception.Message));
            violations.Add($"{componentName}: {image} could not be read from its registry ({exception.Message}). A recorded digest the registry does not serve is a pin to nothing");

            return;
        }

        if (inspected.Licences is null) {
            rows.Add(new("Images", reference, "config label", "no label", NotInspected, $"no {OciImage.LicencesLabel}; the component's LICENSE evidence stands for it"));
        } else {
            var offending = LicenceIdentifiers(inspected.Licences).Where(id => !IsOnBundleAllowList(id)).ToList();

            if (offending.Count == 0) {
                var note = string.Equals(inspected.Licences, declared, StringComparison.Ordinal)
                    ? "agrees with component.yaml"
                    : $"a helper image under its own terms; the component declares {declared}";
                rows.Add(new("Images", reference, OciImage.LicencesLabel, inspected.Licences, Allowed, note));
            } else {
                rows.Add(new("Images", reference, OciImage.LicencesLabel, inspected.Licences, Refused, "not on ADR-011's allow-list"));
                violations.Add(
                    $"{componentName}: {reference} is labelled `{OciImage.LicencesLabel}={inspected.Licences}`, and "
                    + $"{string.Join(", ", offending)} is not on ADR-011's allow-list ({string.Join(", ", BundleLicenceAllowList)}). "
                    + "Widen the list in build/Build.Bundle.cs with the reason beside it, or drop the image"
                );
            }
        }

        if (syft is null) {
            rows.Add(new("Image contents", reference, "syft", "not inspected", NotInspected, "syft is not on PATH"));

            return;
        }

        ScanSbom(reference, $"{componentName}: {reference}", () => RunSyft(syft, image), rows, violations);
    }

    /// <summary>Runs Syft over a registry reference and returns the SBOM it wrote.</summary>
    /// <remarks>
    ///     ⚠ By digest, exactly as recorded. <c>registry:</c> makes Syft speak to the registry
    ///     directly, with no daemon and no pull into one; the same source Build.Images uses.
    /// </remarks>
    JsonNode RunSyft(Tool syft, string image) {
        LicenceReportDirectory.CreateDirectory();

        var file = LicenceReportDirectory / (Regex.Replace(image.Split('@')[0], "[^A-Za-z0-9.-]", "_") + ".spdx.json");

        syft($"scan registry:{image} --output spdx-json={file} --quiet", workingDirectory: RootDirectory, logOutput: false);

        Assert.FileExists(file, $"syft reported success for {image} and wrote no SBOM to {file}");

        return JsonNode.Parse(file.ReadAllText()) ?? throw new InvalidDataException($"{file} is not JSON");
    }

    // ── Our own images ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The SBOMs Build.Images wrote for the platform's own images, scanned for service-restricted
    ///     packages. Absent locally and reported as such; absent on CI and a failure, since
    ///     <c>Licence</c> depends on <c>Images</c> and a CI run without them scanned nothing of ours.
    /// </summary>
    void ScanPlatformImages(List<LicenceRow> rows, List<string> violations) {
        var sboms = SbomDirectory.DirectoryExists()
            ? SbomDirectory.GlobFiles("*.spdx.json").OrderBy(x => x.Name, StringComparer.Ordinal).ToList()
            : [];

        if (sboms.Count == 0) {
            rows.Add(new("Platform images", "(every host under src/Hosts)", RootDirectory.GetRelativePathTo(SbomDirectory).ToString(), "no SBOM", NotInspected, "Images has not run in this checkout"));

            // ⚠ `--skip Images` ON CI IS A ○, NOT A ✘, AND ONLY WHEN IT WAS ASKED FOR IN SO MANY WORDS.
            //
            // Issue #25: weekly.yml has no registry to push to until docs/plan/23 § CI secrets is
            // acted on, so `Images` cannot run there, and a `Licence` that failed on the missing SBOMs
            // every Sunday was a scan of every bundle component and every upstream image (twenty and
            // thirty-three on 2026-09-18) reported as red for a reason that had nothing to do with any
            // of them. The workflow now
            // says `--skip Images` when the registry secret is absent, and this branch is how the
            // target tells "Images was skipped, on purpose, by name" from "Images ran and wrote
            // nothing" — the second is still the failure below, because a target that depends on
            // Images and finds no SBOM after Images ran has found a broken Images.
            //
            // ⚠ It stays a ○ in the report and a warning in the log, never a ✔. The platform's own
            // images were not scanned, and weekly.yml prints a `skipped:` step naming the secret that
            // would change that. What this must never become is a green row over an unscanned host.
            var imagesSkipped = SkippedTargets.Any(x => x.Name == nameof(Images));

            if (IsServerBuild && !imagesSkipped) {
                violations.Add(
                    $"no SBOM under {RootDirectory.GetRelativePathTo(SbomDirectory)}/ on a CI build. Licence depends on "
                    + "Images, which writes one per host, so this run scanned none of the platform's own images"
                );
            } else if (imagesSkipped) {
                Log.Warning(
                    "Licence: Images was skipped (--skip Images), so no SBOM exists under {Directory}/ and "
                    + "the platform's own images were NOT scanned — ○, not ✔. On CI that is the "
                    + "no-registry shape docs/plan/23 § CI secrets describes; the bundle's components and "
                    + "their upstream images above were scanned in full.",
                    RootDirectory.GetRelativePathTo(SbomDirectory)
                );
            } else {
                Log.Warning(
                    "Licence: no SBOM under {Directory}/ — Images has not run here, so the platform's own "
                    + "images were not scanned. That is a warning locally and a failure on CI.",
                    RootDirectory.GetRelativePathTo(SbomDirectory)
                );
            }

            return;
        }

        foreach (var sbom in sboms) {
            var host = sbom.Name[..^".spdx.json".Length];

            ScanSbom(host, host, () => JsonNode.Parse(sbom.ReadAllText()) ?? throw new InvalidDataException($"{sbom} is not JSON"), rows, violations);
        }
    }

    // ── SBOM contents ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every package in an SPDX document against the deny-list, and the licence tally for the
    ///     report.
    /// </summary>
    void ScanSbom(string artefact, string owner, Func<JsonNode> load, List<LicenceRow> rows, List<string> violations) {
        JsonNode document;

        try {
            document = load();
        } catch (Exception exception) when (exception is IOException or InvalidDataException or ProcessException) {
            rows.Add(new("Image contents", artefact, "syft", "unread", Refused, exception.Message));
            violations.Add($"{owner}: the SBOM could not be produced or read ({exception.Message})");

            return;
        }

        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var restricted = new List<string>();
        var packages = 0;

        foreach (var package in document["packages"] as JsonArray ?? []) {
            packages++;

            var name = package?["name"]?.GetValue<string>() ?? "(unnamed)";
            var version = package?["versionInfo"]?.GetValue<string>() ?? string.Empty;
            var expression = Spdx(package?["licenseConcluded"]) ?? Spdx(package?["licenseDeclared"]);

            if (expression is null) {
                tally["(none declared)"] = tally.GetValueOrDefault("(none declared)") + 1;

                continue;
            }

            foreach (var id in LicenceIdentifiers(expression)) {
                tally[id] = tally.GetValueOrDefault(id) + 1;

                if (IsServiceRestricted(id) && !LicenceExceptions.ContainsKey($"{name}@{version}")) {
                    restricted.Add($"{name}@{version} ({id})");
                }
            }
        }

        var summary = string.Join(", ", tally.Select(x => $"{x.Key} ×{x.Value}"));

        if (restricted.Count > 0) {
            rows.Add(new("Image contents", artefact, "syft SBOM", summary, Refused, $"service-restricted: {string.Join("; ", restricted)}"));
            violations.Add(
                $"{owner} links {restricted.Count} package(s) under a licence ADR-011 refuses — {string.Join("; ", restricted)}. "
                + "docs/plan/02 § ADR-011: the alternative is written down (Valkey not Redis, OpenBao not "
                + "Vault, OpenSearch not Elasticsearch) or the exception is, in Build.Licence.cs § LicenceExceptions"
            );
        } else {
            rows.Add(new("Image contents", artefact, "syft SBOM", $"{packages} package(s)", Allowed, summary.Length == 0 ? "no package declares a licence" : summary));
        }
    }

    static string? Spdx(JsonNode? node) {
        var value = node?.GetValue<string>();

        return value is null or "NOASSERTION" or "NONE" || value.Length == 0 ? null : value;
    }

    // ── Licence identifiers ───────────────────────────────────────────────────────────────────

    static bool IsOnBundleAllowList(string id) => Array.IndexOf(BundleLicenceAllowList, id) >= 0;

    static bool IsServiceRestricted(string id) =>
        ServiceRestrictedLicences.Any(family => id.Contains(family, StringComparison.OrdinalIgnoreCase));

    /// <summary>The identifiers in an SPDX expression — <c>MIT AND (Apache-2.0 OR GPL-2.0)</c> is three.</summary>
    static IEnumerable<string> LicenceIdentifiers(string expression) =>
        ExpressionOperator.Split(expression)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);

    static readonly Regex ExpressionOperator = new(@"\s+(?:AND|OR|WITH|and|or|with)\s+|[()]", RegexOptions.Compiled);

    /// <summary>
    ///     The SPDX identifier a licence file's text is, or <see langword="null" /> when it is none
    ///     this scan knows.
    /// </summary>
    /// <remarks>
    ///     ⚠ Phrases rather than a full-text match, because every upstream reflows its LICENSE
    ///     differently and two of the nineteen (mariadb-operator, rabbitmq/cluster-operator) do not
    ///     start with the licence's canonical first line. Each family is identified by the sentence
    ///     no other family contains, and the BSD split is the third clause: <c>BSD-3-Clause</c> is the
    ///     one that forbids using the names of the contributors to endorse. The families here are
    ///     the ones on the allow-list, the ones ADR-011's table names, and the ones its Enforcement
    ///     paragraph refuses; a text outside them is reported as unrecognised, which is a failure
    ///     and not a pass, for the reason every ○ in this file is not a ✔.
    /// </remarks>
    static string? ClassifyLicenceText(string text) {
        var flat = Regex.Replace(text, @"\s+", " ").ToLowerInvariant();

        if (flat.Contains("apache license", StringComparison.Ordinal) && flat.Contains("version 2.0", StringComparison.Ordinal)) {
            return "Apache-2.0";
        }

        if (flat.Contains("mozilla public license", StringComparison.Ordinal) && flat.Contains("version 2.0", StringComparison.Ordinal)) {
            return "MPL-2.0";
        }

        if (flat.Contains("server side public license", StringComparison.Ordinal)) {
            return "SSPL-1.0";
        }

        if (flat.Contains("business source license", StringComparison.Ordinal)) {
            return "BUSL-1.1";
        }

        if (flat.Contains("elastic license", StringComparison.Ordinal)) {
            return "Elastic-2.0";
        }

        if (flat.Contains("gnu affero general public license", StringComparison.Ordinal)) {
            return "AGPL-3.0";
        }

        if (flat.Contains("gnu lesser general public license", StringComparison.Ordinal)
            || flat.Contains("gnu library general public license", StringComparison.Ordinal)) {
            return flat.Contains("version 3", StringComparison.Ordinal) ? "LGPL-3.0" : "LGPL-2.1";
        }

        if (flat.Contains("gnu general public license", StringComparison.Ordinal)) {
            return flat.Contains("version 3", StringComparison.Ordinal) ? "GPL-3.0" : "GPL-2.0";
        }

        if (flat.Contains("permission is hereby granted, free of charge", StringComparison.Ordinal)) {
            return "MIT";
        }

        if (flat.Contains("redistribution and use in source and binary forms", StringComparison.Ordinal)) {
            return flat.Contains("neither the name", StringComparison.Ordinal) || flat.Contains("may not be used to endorse", StringComparison.Ordinal)
                ? "BSD-3-Clause"
                : "BSD-2-Clause";
        }

        if (flat.Contains("isc license", StringComparison.Ordinal)) {
            return "ISC";
        }

        return null;
    }

    // ── Chart metadata ────────────────────────────────────────────────────────────────────────

    /// <summary>A pinned upstream chart as its <c>Chart.yaml</c> describes it.</summary>
    /// <param name="Name">The chart's name.</param>
    /// <param name="Version">The chart's version.</param>
    /// <param name="Licence">Its <c>artifacthub.io/license</c> annotation, or <see langword="null" />.</param>
    /// <param name="Dependencies">Its <c>dependencies:</c> — name, version constraint, repository.</param>
    sealed record UpstreamChart(string Name, string Version, string? Licence, IReadOnlyList<(string Name, string Version, string Repository)> Dependencies);

    /// <summary>
    ///     The entry for one chart version in a Helm repository's <c>index.yaml</c>, or
    ///     <see langword="null" /> when the index has no such version.
    /// </summary>
    /// <remarks>
    ///     ⚠ A hand-written reader over the one shape Helm writes, for the reason Build.Bundle.cs
    ///     gives for its own: <c>entries:</c>, then <c>  &lt;chart&gt;:</c>, then one <c>  - </c>
    ///     item per version with its fields at four spaces and its annotations at six. An index is
    ///     several megabytes for the larger repositories (prometheus-community is 6 MB), so the read
    ///     walks lines once and keeps only the entry it wants.
    /// </remarks>
    static UpstreamChart? ReadIndexEntry(string index, string chart, string version) {
        var lines = index.Split('\n');
        var inChart = false;
        var entry = new List<string>();
        var entries = new List<List<string>>();

        foreach (var raw in lines) {
            var line = raw.TrimEnd('\r');

            // A top-level key — `entries:`, `generated:` — closes whatever chart block was open.
            if (line.Length > 0 && line[0] != ' ') {
                if (entry.Count > 0) {
                    entries.Add(entry);
                    entry = [];
                }

                inChart = false;

                continue;
            }

            if (line.StartsWith("  ", StringComparison.Ordinal) && line.Length > 2 && line[2] != ' ' && line[2] != '-') {
                if (entry.Count > 0) {
                    entries.Add(entry);
                    entry = [];
                }

                inChart = line == $"  {chart}:";

                continue;
            }

            if (!inChart) {
                continue;
            }

            if (line.StartsWith("  - ", StringComparison.Ordinal)) {
                if (entry.Count > 0) {
                    entries.Add(entry);
                }

                entry = ["    " + line[4..]];
            } else if (entry.Count > 0) {
                entry.Add(line);
            }
        }

        if (entry.Count > 0) {
            entries.Add(entry);
        }

        var wanted = entries.FirstOrDefault(x => x.Any(line => line == $"    version: {version}" || line == $"    version: \"{version}\""));

        return wanted is null ? null : ReadChartDocument(wanted, 4);
    }

    /// <summary>The <c>Chart.yaml</c> inside a packaged chart.</summary>
    static UpstreamChart? ReadArchivedChart(byte[] archive) {
        using var compressed = new MemoryStream(archive);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (tar.GetNextEntry() is { } entry) {
            var name = entry.Name.TrimStart('.', '/');

            if (name.Count(x => x == '/') != 1 || !name.EndsWith("/Chart.yaml", StringComparison.Ordinal) || entry.DataStream is null) {
                continue;
            }

            using var reader = new StreamReader(entry.DataStream, Encoding.UTF8);

            return ReadChartDocument(reader.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).ToList(), 0);
        }

        return null;
    }

    /// <summary>
    ///     A <c>Chart.yaml</c>-shaped block whose scalars sit at <paramref name="indent" /> spaces:
    ///     the name and version, the <c>annotations:</c> licence, and each <c>dependencies:</c> item.
    /// </summary>
    static UpstreamChart ReadChartDocument(List<string> lines, int indent) {
        var pad = new string(' ', indent);
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var licence = (string?)null;
        var dependencies = new List<(string, string, string)>();
        var block = string.Empty;
        Dictionary<string, string>? dependency = null;

        void Flush() {
            if (dependency is not null) {
                dependencies.Add((
                    dependency.GetValueOrDefault("name", "(unnamed)"),
                    dependency.GetValueOrDefault("version", "(unversioned)"),
                    dependency.GetValueOrDefault("repository", string.Empty)
                ));
                dependency = null;
            }
        }

        foreach (var line in lines) {
            if (line.Length == 0 || line.Trim().StartsWith('#')) {
                continue;
            }

            if (line.StartsWith(pad, StringComparison.Ordinal) && line.Length > indent && line[indent] != ' ' && line[indent] != '-') {
                Flush();

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                var key = line[indent..colon];
                var value = Unquote(line[(colon + 1)..].Trim());

                block = value.Length == 0 ? key : string.Empty;

                if (value.Length > 0) {
                    scalars[key] = value;
                }

                continue;
            }

            switch (block) {
                case "annotations": {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("artifacthub.io/license:", StringComparison.Ordinal)) {
                        licence = Unquote(trimmed["artifacthub.io/license:".Length..].Trim());
                    }

                    break;
                }
                case "dependencies": {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("- ", StringComparison.Ordinal)) {
                        Flush();
                        dependency = new(StringComparer.Ordinal);
                        trimmed = trimmed[2..];
                    }

                    var colon = trimmed.IndexOf(':', StringComparison.Ordinal);

                    if (dependency is not null && colon > 0) {
                        dependency[trimmed[..colon]] = Unquote(trimmed[(colon + 1)..].Trim());
                    }

                    break;
                }
            }
        }

        Flush();

        return new(
            scalars.GetValueOrDefault("name", "(unnamed)"),
            scalars.GetValueOrDefault("version", "(unversioned)"),
            licence,
            dependencies
        );
    }

    // ── Tools ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     A tool this target can run without locally and cannot run without on CI — the asymmetry
    ///     Build.Test.cs § the coverage floor argues for.
    /// </summary>
    static Tool? ResolveOptionalTool(string executable, string what, string unblock, List<string> violations) {
        try {
            return ToolResolver.GetPathTool(executable);
        } catch (Exception exception) {
            var message = $"`{executable}` is not on PATH ({exception.Message.TrimEnd('.')}), so {what} was not run. To unblock: {unblock}.";

            if (IsServerBuild) {
                violations.Add(message + " On CI this is a failure, not a warning — a licence scan that skips the SBOM half is green over the row with a legal consequence");
            } else {
                Log.Warning("Licence: {Message} Locally that is a warning; on CI it fails the target.", message);
            }

            return null;
        }
    }

    // ── The report ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Writes every artefact and its evidence — <see cref="LicenceReportFile" /> for a person,
    ///     <see cref="LicenceReportJson" /> for anything else.
    /// </summary>
    void WriteLicenceReport(List<LicenceRow> rows, List<string> violations) {
        LicenceReportDirectory.CreateDirectory();

        var markdown = new StringBuilder();

        markdown.AppendLine("# Licence scan — ADR-011 § Enforcement");
        markdown.AppendLine();
        markdown.AppendLine($"Run at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC over {rows.Count} artefact row(s); {violations.Count} finding(s).");
        markdown.AppendLine();
        markdown.AppendLine("Allow-list (offering): " + string.Join(", ", BundleLicenceAllowList) + ". Refused families (linked): " + string.Join(", ", ServiceRestrictedLicences) + ".");
        markdown.AppendLine();

        foreach (var section in rows.GroupBy(x => x.Section)) {
            markdown.AppendLine($"## {section.Key}");
            markdown.AppendLine();
            markdown.AppendLine("| | Artefact | Evidence | Licence | Detail |");
            markdown.AppendLine("|---|---|---|---|---|");

            foreach (var row in section) {
                markdown.AppendLine($"| {row.Verdict} | {Cell(row.Artefact)} | {Cell(row.Evidence)} | {Cell(row.Licence)} | {Cell(row.Detail)} |");
            }

            markdown.AppendLine();
        }

        if (violations.Count > 0) {
            markdown.AppendLine("## Findings");
            markdown.AppendLine();

            foreach (var violation in violations) {
                markdown.AppendLine($"* {violation}");
            }

            markdown.AppendLine();
        }

        LicenceReportFile.WriteAllText(markdown.ToString());

        var json = new JsonObject {
            ["rows"] = new JsonArray(
                rows.Select(x => (JsonNode)new JsonObject {
                        ["section"] = x.Section,
                        ["artefact"] = x.Artefact,
                        ["evidence"] = x.Evidence,
                        ["licence"] = x.Licence,
                        ["verdict"] = x.Verdict,
                        ["detail"] = x.Detail
                    }
                ).ToArray()
            ),
            ["findings"] = new JsonArray(violations.Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
        };

        LicenceReportJson.WriteAllText(json.ToString());
    }

    static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
