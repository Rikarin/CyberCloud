// Licence — docs/plan/23 § Build, row `Licence`: "ADR-011 scan over charts and images".
//
// ── ⚠ THE SCAN THIS ROW ASKS FOR CANNOT BE WRITTEN AGAINST THE ALLOW-LIST IT NAMES ───────────────
//
// ADR-011 § Enforcement: "a build gate runs a licence scan over the chart set and the container
// images in the platform bundle [and] fails on any SSPL/BUSL/AGPL image outside an allow-list". The
// only allow-list in this repository is `BundleLicenceAllowList` in build/Build.Bundle.cs, and it
// holds Apache-2.0, BSD-3-Clause, MIT and MPL-2.0.
//
// Measured on 2026-09-03 rather than argued, against `mcr.microsoft.com/dotnet/aspnet:10.0` — the
// base image Build.Images publishes EVERY platform host onto:
//
//   * /usr/share/doc holds 99 packaged components;
//   * 76 of them declare a GPL or LGPL licence in their Debian copyright file;
//   * coreutils, to name the least avoidable one, is `License: GPL-3+`.
//
// So a scan over the container images that failed on anything outside that allow-list would fail on
// our own runtime image, on its first run, before it ever reached a bundle component. It is not a
// gate that is missing — it is a gate that as specified cannot be green.
//
// ⚠ THE MISTAKE IS NOT THE ALLOW-LIST AND IT IS NOT THE ADR. It is that ONE list is being asked two
// questions. `BundleLicenceAllowList` answers "may this platform OFFER this software as a managed
// service", which is what ADR-011 is about: SSPL and BUSL exist precisely to prevent that, and a
// file-level copyleft on a program we run unmodified does not. A scan over image LAYERS answers
// "what is linked into this artefact", where GPL and LGPL are the normal state of every Linux base
// image and say nothing about offering anything. Answering the second question with the first list
// is what produces the contradiction charts/bundle/bundle.yaml § owed,
// `the-replicated-stage-is-not-installed`, already records — ADR-011's own table marks LINSTOR
// (GPL-3.0), DRBD (GPL-2.0) and ClamAV (GPL-2.0) ✓ on their own terms, while the gate would fail
// them.
//
// ⚠ THAT DECISION IS ISSUE #18's AND IT IS DELIBERATELY NOT TAKEN HERE. What #18 owns is which
// question each list answers and what the second list contains. Widening the existing list to make
// this target implementable would answer #18 by making the first list stop answering its own
// question, which is the worst of the available outcomes: an allowance nobody argued for, attached
// to the wrong question, that also retires the SSPL/BUSL refusal it exists for.
//
// ── WHAT EXISTS INSTEAD, SO THIS FILE IS NOT READ AS "NOTHING IS CHECKED" ─────────────────────────
//
//   * The DECLARATION is checked per bundle component, on every build, by Build.Bundle.cs's Bundle
//     gate: each component.yaml states an SPDX id and it must be on the allow-list. That catches a
//     component added under SSPL or BUSL by an author who wrote the licence down honestly, and it
//     catches nothing else. It reads no LICENSE file and opens no image.
//   * The ARTEFACTS the bundle pulls are now enumerated and pinned to a digest — every component
//     records every image its pinned chart or manifest renders, and charts/bundle/images.sh
//     re-resolves each tag against its registry and compares. That is the input a real scan needs
//     and did not have: before it, nothing in this repository knew which thirty-two images the
//     bundle installs.
//   * SBOMs, signing and attestation for OUR images are Build.Images and are done. Verification at
//     admission is #15.
//
// So what is owed here is a SECOND list with a different question written above it, and a scanner
// (Syft is already a Build.Images dependency) pointed at the images charts/bundle/ now names. The
// first half of that is a decision and the second is an afternoon.

partial class Build
{
    void ScanLicences()
        => NotImplementedYet(
            nameof(Licence),
            "scan the declared licence of every packaged chart and every image charts/bundle/ "
            + "records, against a list that answers 'what may be linked into an artefact we ship' — "
            + "which is NOT build/Build.Bundle.cs' BundleLicenceAllowList, whose question is 'what "
            + "may this platform offer as a managed service'",
            "issue #18, which owns that split. Measured on 2026-09-03: 76 of the 99 packaged "
            + "components in mcr.microsoft.com/dotnet/aspnet:10.0 declare GPL or LGPL, so a scan "
            + "over image contents against the offering allow-list fails on our own base image "
            + "before it reaches any bundle component. See this file's header, ADR-011 § Enforcement "
            + "and charts/bundle/bundle.yaml § owed, `licence-scan-is-a-declaration-not-a-scan`");
}
