// Bundle — the gate over charts/bundle/, reported by Build.Architecture.cs's `Architecture` target
// beside the other fourteen rows.
//
// ⚠ WHY THIS ROW IS NOT IN Build.Charts.cs, WHICH IS THE OBVIOUS PLACE FOR IT.
// `Build.Charts` globs `charts/**/Chart.yaml`, and for each hit it regenerates a values.schema.json
// from an annotated values.yaml, lints and packages. That pipeline describes a RESOURCE TYPE's
// configuration surface — charts/README.md calls the annotated values file "the single description
// of a managed service's configuration surface". A bundle component has no resource type and no
// tenant-facing surface, so it has no Chart.yaml and `Build.Charts` never sees it. Putting the check
// there would mean either giving eighteen components a Chart.yaml they do not want, or writing a
// file-shape check inside a target that resolves `helm` before it does anything. This gate reads
// files and nothing else.
//
// ⚠ THE COVERAGE CHECK IS THE ONE THAT MATTERS AND IT IS NOT A FILE-SHAPE CHECK.
// The rest of this file asserts that a component.yaml is complete, which catches a careless author.
// `CoverageViolations` asserts something a careless author cannot see: that every group/version any
// managed chart renders is served by exactly one component's pin. That is the machine-checkable half
// of charts/managed/opensearch/conformance.yaml § owed, api-group-is-deprecated — "a bundle that
// moved first would strand every existing service" — and it was written after finding a live
// instance. Strimzi 1.0.0 dropped `kafka.strimzi.io/v1beta2`, which charts/managed/kafka renders; a
// bundle pinned at the newest Strimzi passes every check in this file except that one.
//
// WHAT WOULD HAVE TO BREAK FOR EACH CHECK TO GO RED, because a gate that cannot answer that is the
// failure class this repository has shipped roughly ten times:
//
//   * a component.yaml missing a key, or naming a directory it is not in                  → Manifest
//   * a component declaring neither `serves:` nor a written `servesNoDefinitions:`         → Manifest
//   * a component declaring a licence outside ADR-011's allow-list                        → Licence
//   * bundle.yaml and the directories disagreeing about which components exist            → Roster
//   * a managed chart rendering a group no component serves, OR a pin that stopped
//     serving one, OR two components claiming the same group/version                      → Coverage
//   * one commit that both bumps a pin and edits a managed template                       → Ordering

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Git;

partial class Build
{
    /// <summary>
    ///     The operator layer a platform cluster needs before any provider can converge —
    ///     charts/bundle/README.md.
    /// </summary>
    AbsolutePath BundleDirectory => ChartsDirectory / "bundle";

    /// <summary>The roster. Named here because two checks read it and one of them compares it.</summary>
    AbsolutePath BundleRosterFile => BundleDirectory / "bundle.yaml";

    const string ComponentFileName = "component.yaml";

    /// <summary>
    ///     ADR-011 § The licence audit's allow-list, as SPDX identifiers.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is a list rather than "must be Apache-2.0", and two components are why.</b>
    ///         <c>mariadb-operator</c> is MIT and <c>rabbitmq-cluster-operator</c> is MPL-2.0. Both
    ///         are permissive with no service clause, which is the question ADR-011 is asking: SSPL
    ///         and BUSL exist precisely to prevent offering the software as a service, and a
    ///         file-level copyleft on the operator does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>AGPL-3.0 is absent and that is deliberate, not an oversight.</b> ADR-011 marks
    ///         Grafana "⚠ Offerable as a managed instance (we distribute, we do not modify)" — a
    ///         conditional the row's own text has to carry. A conditional that a build gate turns
    ///         into an unconditional yes is a gate that retires the condition, so an AGPL component
    ///         fails here and the failure is where the argument gets written down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>GPL-2.0 and GPL-3.0 are absent, and "the gate fails on SSPL/BUSL/AGPL" is not
    ///         what this list does.</b> ADR-011 § Enforcement is worded as a deny-list — <i>"fails on
    ///         any SSPL/BUSL/AGPL image outside an allow-list"</i> — and reading it that way gives the
    ///         wrong answer for every licence in neither set. This is an ALLOW-list: a component
    ///         declaring GPL-3.0 fails here, exactly as an SSPL one does. ADR-011's table marks
    ///         LINSTOR (GPL-3.0), DRBD (GPL-2.0) and ClamAV (GPL-2.0) ✓ on their own terms, so the
    ///         two documents disagree the moment a GPL component is added — and the moment is
    ///         `charts/bundle/bundle.yaml` § owed, <c>the-replicated-stage-is-not-installed</c>.
    ///         Widening the list is the sanctioned move and it is deliberately not done in advance:
    ///         an allowance with no component behind it is a permission nobody argued for, and the
    ///         argument is the artifact this list exists to produce.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this checks is a DECLARATION, and the distance from what ADR-011
    ///         § Enforcement asks for is the distance from an attestation to a scan.</b> That clause
    ///         wants "a licence scan over the chart set and the container images in the platform
    ///         bundle"; <c>build/Build.Licence.cs</c> is still <c>NotImplementedYet</c>. This catches
    ///         a component added under SSPL or BUSL by an author who wrote the licence down honestly,
    ///         and catches nothing else. charts/bundle/bundle.yaml § owed says so in its own words.
    ///     </para>
    /// </remarks>
    static readonly string[] BundleLicenceAllowList =
    [
        "Apache-2.0",
        "BSD-3-Clause",
        "MIT",
        "MPL-2.0",
    ];

    /// <summary>
    ///     Kubernetes' own API groups. An <c>apiVersion</c> in one of these needs no operator, so the
    ///     coverage check drops it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Enumerated rather than inferred from the name, and the reason is one character.</b>
    ///     The tempting rule — "a group ending <c>.k8s.io</c> is built in" — accepts
    ///     <c>cluster.x-k8s.io</c>, which is Cluster API's and is the single most operator-dependent
    ///     group in this tree. It would have silently excused the four charts whose objects need the
    ///     most installing. A list is longer and cannot make that mistake.
    /// </remarks>
    static readonly HashSet<string> BuiltInApiGroups = new(StringComparer.Ordinal)
    {
        "",
        "admissionregistration.k8s.io",
        "apiextensions.k8s.io",
        "apiregistration.k8s.io",
        "apps",
        "authentication.k8s.io",
        "authorization.k8s.io",
        "autoscaling",
        "batch",
        "certificates.k8s.io",
        "coordination.k8s.io",
        "discovery.k8s.io",
        "events.k8s.io",
        "flowcontrol.apiserver.k8s.io",
        "networking.k8s.io",
        "node.k8s.io",
        "policy",
        "rbac.authorization.k8s.io",
        "resource.k8s.io",
        "scheduling.k8s.io",
        "storage.k8s.io",
        "storagemigration.k8s.io",
    };

    /// <summary>One component, as its <c>component.yaml</c> declares it.</summary>
    /// <param name="Name">The directory name, which the file must also declare.</param>
    /// <param name="File">The manifest, for a violation message that names a file.</param>
    /// <param name="Scalars">Top-level <c>key: value</c> pairs.</param>
    /// <param name="Serves">The <c>group/version</c> pairs under <c>serves:</c>.</param>
    sealed record BundleComponent(
        string Name,
        AbsolutePath File,
        Dictionary<string, string> Scalars,
        IReadOnlyList<string> Serves);

    /// <summary>
    ///     Everything charts/bundle/ owes, in one row of the <c>Architecture</c> report.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The candidate count is components PLUS charts, and both halves have to be non-zero
    ///     for this row to be anything but <see cref="GateStatus.Vacuous" />.</b> Components alone
    ///     would let a bundle with eighteen perfect manifests and nothing to cover report ✔; charts
    ///     alone would do the same for a tree with twenty charts and no bundle — which is exactly the
    ///     state this directory was created out of, and the state a gate counting one of the two
    ///     would have called clean.
    /// </remarks>
    GateOutcome BundleGate()
    {
        if (!BundleDirectory.DirectoryExists())
        {
            return new(
                "Bundle",
                GateStatus.Vacuous,
                $"{RootDirectory.GetRelativePathTo(BundleDirectory)}/ does not exist, so no component "
                + "manifest was read and no chart's rendered api-groups were covered. Every chart "
                + "under charts/managed/ renders a custom resource and installs no operator — three "
                + "say so verbatim — so a tree in this state converges nothing",
                []);
        }

        var components = ReadBundleComponents(out var manifestViolations);
        var rendered = ReadRenderedApiGroups(out var chartsInspected);

        var violations = new List<string>(manifestViolations);

        violations.AddRange(RosterViolations(components));
        violations.AddRange(CoverageViolations(components, rendered));
        violations.AddRange(OrderingViolations(out var orderingDetail));

        return new(
            "Bundle",
            violations.Count > 0 ? GateStatus.Failed
            : components.Count == 0 || chartsInspected == 0 ? GateStatus.Vacuous
            : GateStatus.Enforced,
            $"{components.Count} component(s) serving {components.Sum(x => x.Serves.Count)} "
            + $"group/version(s) and recording "
            + $"{components.Sum(x => ReadBundleSequence(x.File, "images").Count)} image digest(s); "
            + $"{chartsInspected} chart(s) rendering {rendered.Count} "
            + $"non-built-in group/version(s); {orderingDetail}",
            violations);
    }

    // ── The manifests ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Reads every component and reports what a <c>component.yaml</c> owes and has not paid.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A component directory is one that contains a <c>component.yaml</c>, and a
    ///         directory that does not is a violation rather than a skip.</b> That is the whole
    ///         lesson of the failure Task #109 names: <c>Build.Charts</c> requires <c>SOURCE</c> and
    ///         <c>conformance.yaml</c> only under <c>charts/managed/</c>, so a chart one directory
    ///         over "quietly owes no conformance manifest" — and one of docs/plan/12's eight pieces
    ///         was dropped by a directory name. A silent skip here would rebuild that hole.
    ///     </para>
    ///     <para>
    ///         Every problem is reported, not the first. A contributor fixing one key per build is a
    ///         contributor who stops writing manifests — the same reason <c>Build.Charts</c> collects
    ///         rather than throws.
    ///     </para>
    /// </remarks>
    List<BundleComponent> ReadBundleComponents(out List<string> violations)
    {
        violations = [];

        var components = new List<BundleComponent>();

        foreach (var directory in BundleDirectory.GlobDirectories("*").OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var file = directory / ComponentFileName;
            var relative = RootDirectory.GetRelativePathTo(file);

            if (!file.FileExists())
            {
                violations.Add(
                    $"{RootDirectory.GetRelativePathTo(directory)}/ has no {ComponentFileName}. Every "
                    + "directory under charts/bundle/ is a component and every component declares its "
                    + "pin, its licence and the api-groups it serves — charts/bundle/README.md § What "
                    + "a component owes. A directory that owes nothing is how a rule gets dropped by a "
                    + "directory name");

                continue;
            }

            var scalars = ReadFlatKeys(file);
            var serves = ReadBundleSequence(file, "serves");

            components.Add(new(directory.Name, file, scalars, serves));

            foreach (var required in new[] { "component", "phase", "licence", "install", "source", "checked" })
            {
                if (!scalars.ContainsKey(required))
                    violations.Add($"{relative} declares no `{required}:`.");
            }

            if (scalars.TryGetValue("component", out var declaredName)
                && !string.Equals(declaredName, directory.Name, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{relative} declares `component: {declaredName}` and sits in a directory called "
                    + $"'{directory.Name}'. install.sh resolves a component's directory from the "
                    + "roster's name, so the two disagreeing means the file you edit and the "
                    + "component that gets installed are different things");
            }

            violations.AddRange(ServesViolations(relative, file, serves));

            foreach (var entry in serves.Where(entry => !GroupVersion.IsMatch(entry)))
            {
                violations.Add(
                    $"{relative} lists `{entry}` under `serves:`, which is not a `group/version` "
                    + "pair. The coverage check compares these against the apiVersion strings charts "
                    + "render, so an entry in another shape matches nothing and silently covers "
                    + "nothing");
            }

            violations.AddRange(ImagesViolations(relative, file));
            violations.AddRange(LicenceViolations(relative, scalars));
            violations.AddRange(PinViolations(relative, scalars));
            violations.AddRange(CheckedDateViolations(relative, scalars));
        }

        return components;
    }

    /// <summary>
    ///     A component declares the definitions it serves, or declares in writing that it installs
    ///     none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The escape exists because the rule "every component installs a
    ///         CustomResourceDefinition" was true of eighteen operators and is false of the
    ///         nineteenth.</b> <c>charts/bundle/openebs-localpv</c> installs a Deployment, a
    ///         ClusterRole and a <c>StorageClass</c>. Every kind in it is a Kubernetes built-in, so
    ///         there is nothing honest to write on a <c>serves:</c> line — and the dishonest thing
    ///         was available and tempting: <c>storage.k8s.io/v1</c> matches
    ///         <see cref="GroupVersion" />, would have satisfied the old check, and would have been a
    ///         claim that a component *serves* a group the API server has served since 1.6.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A written reason rather than a boolean, and the length floor is the point.</b>
    ///         <c>servesNoDefinitions: true</c> is a checkbox, and a checkbox is what turns an
    ///         exception into the default. The floor is deliberately low enough that one real
    ///         sentence clears it and high enough that no word does — a reviewer reading the diff
    ///         sees an argument, which is the only thing that can be wrong in a way somebody
    ///         notices.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Declaring both is a violation rather than a precedence rule.</b> A component
    ///         that lists a group/version AND says it installs no definitions has one of the two
    ///         wrong, and picking a winner would silently discard whichever half was the truth.
    ///     </para>
    /// </remarks>
    static IEnumerable<string> ServesViolations(
        string relative,
        AbsolutePath file,
        // List rather than IReadOnlyList: CA1859 is an error here and this is a private helper — the
        // same reason ShippingAssemblyPaths and ShippingProjectFiles do it in Build.Architecture.cs.
        List<string> serves)
    {
        var reason = ReadBundleReason(file, "servesNoDefinitions");

        if (serves.Count > 0)
        {
            if (reason is not null)
            {
                yield return
                    $"{relative} declares {serves.Count} `serves:` entr(y/ies) AND "
                    + "`servesNoDefinitions:`. One of the two is wrong, and this gate will not choose "
                    + "which: the escape is for a component that installs no CustomResourceDefinition "
                    + "at all, and a component that installs one owes the coverage check a line";
            }

            yield break;
        }

        if (reason is null)
        {
            yield return
                $"{relative} declares no `serves:` entries. A component that serves no "
                + "group/version covers no chart, and the coverage check is the only thing "
                + "standing between a bundle bump and every tenant's create failing at the API "
                + "server — charts/bundle/README.md § `serves:` is the load-bearing key. ⚠ If this "
                + "component genuinely installs no CustomResourceDefinition — a CSI or a storage "
                + "class installs Kubernetes built-ins and nothing else — say so in "
                + $"`servesNoDefinitions:`, in at least {ServesNoDefinitionsMinimumReason} characters "
                + "of prose. Do NOT reach for a built-in group such as `storage.k8s.io/v1` to satisfy "
                + "the line: it parses, it passes, and it claims something no component in this "
                + "directory does";

            yield break;
        }

        if (reason.Length < ServesNoDefinitionsMinimumReason)
        {
            yield return
                $"{relative} declares `servesNoDefinitions:` in {reason.Length} character(s) and the "
                + $"floor is {ServesNoDefinitionsMinimumReason}. This is the one check in this file "
                + "that a component can turn off, so what it costs is a sentence saying which kinds "
                + "the component does install and why none of them is a definition. A one-word reason "
                + "is a checkbox, and a checkbox is how an exception becomes the default";
        }
    }

    /// <summary>
    ///     The shortest <c>servesNoDefinitions:</c> this gate will accept, in characters.
    /// </summary>
    /// <remarks>
    ///     ⚠ Chosen against the two strings it has to separate rather than picked round.
    ///     <c>"true"</c>, <c>"no CRDs"</c> and <c>"it is a CSI"</c> are 4, 7 and 11; the shortest
    ///     honest reason names the kinds the component installs and is a clause longer than any of
    ///     them. Sixty is above the first group and below anything a reviewer would call an argument.
    /// </remarks>
    const int ServesNoDefinitionsMinimumReason = 60;

    /// <summary>
    ///     A component records every image its pinned artefact renders, with the digest each tag
    ///     served — or argues, in prose, that it renders none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What this closes is one level below the pin, and the two decay differently.</b>
    ///         A chart version is immutable once published, which is why <c>install.sh --verify</c>
    ///         can answer "does the pin resolve" with an HTTP HEAD. The image tag inside that chart
    ///         is mutable, so a component whose every pin resolves can be running bytes that were
    ///         rebuilt last night by somebody else, and nothing in this directory would say so.
    ///         <c>charts/bundle/images.sh</c> is what re-resolves these; this gate is what stops the
    ///         record from being unreadable, absent, or quietly emptied.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It does NOT resolve anything, and the reason is the same one
    ///         <see cref="CheckedDateViolations" /> gives for having no maximum age.</b> An
    ///         architecture gate that made thirty registry calls would be a gate that goes red when
    ///         a network is slow, and a gate that goes red for reasons unrelated to the tree is a
    ///         gate somebody switches off. The network half is a script a person runs, exactly as
    ///         <c>--verify</c> is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The prose escape is <c>servesNoDefinitions:</c>'s shape and exists for one real
    ///         component.</b> <c>prometheus-operator-crds</c> renders CustomResourceDefinition
    ///         documents and no container at all. An empty <c>images:</c> list and a list nobody has
    ///         filled in look identical, which is exactly the checkbox failure the sixty-character
    ///         floor exists to prevent.
    ///     </para>
    /// </remarks>
    static IEnumerable<string> ImagesViolations(string relative, AbsolutePath file)
    {
        var images = ReadBundleSequence(file, "images");
        var reason = ReadBundleReason(file, "rendersNoWorkloadImages");

        if (images.Count > 0 && reason is not null)
        {
            yield return
                $"{relative} declares {images.Count} `images:` entr(y/ies) AND "
                + "`rendersNoWorkloadImages:`. One of the two is wrong, and this gate will not choose "
                + "which: the escape is for a component that renders no container, and a component "
                + "that renders one owes charts/bundle/images.sh a line to compare against";

            yield break;
        }

        if (images.Count == 0)
        {
            if (reason is null)
            {
                yield return
                    $"{relative} records no `images:`. Every image this bundle pulls is a tag inside "
                    + "somebody else's chart, and a tag is mutable — charts/bundle/bundle.yaml § owed, "
                    + "`images-are-not-pinned-by-digest`. Generate the block with "
                    + "`./charts/bundle/images.sh --component <name> --resolve`, review what it found, "
                    + "and paste it. ⚠ If this component genuinely renders no container — a chart of "
                    + "CustomResourceDefinitions does — say so in `rendersNoWorkloadImages:`, in at "
                    + $"least {ServesNoDefinitionsMinimumReason} characters of prose. Do NOT write an "
                    + "empty `images:` list: it is indistinguishable from one nobody filled in";
            }
            else if (reason.Length < ServesNoDefinitionsMinimumReason)
            {
                yield return
                    $"{relative} declares `rendersNoWorkloadImages:` in {reason.Length} character(s) "
                    + $"and the floor is {ServesNoDefinitionsMinimumReason}, for the same reason "
                    + "`servesNoDefinitions:` has one: a one-word reason is a checkbox, and a checkbox "
                    + "is how an exception becomes the default";
            }

            yield break;
        }

        foreach (var image in images.Where(image => !ImageReference.IsMatch(image)))
        {
            yield return
                $"{relative} lists `{image}` under `images:`, which is not a "
                + "`repository:tag@sha256:<64 hex>` reference. charts/bundle/images.sh compares its "
                + "resolved digest against these strings verbatim, so an entry in another shape "
                + "matches nothing it finds and is reported as an image nobody recorded — which reads "
                + "as a supply-chain change rather than as a typo";
        }

        // ⚠ Duplicates are a violation rather than a set union, because the two entries would carry
        // two different digests for one reference and the comparison would accept whichever came
        // first. A record that can hold two answers is a record that has none.
        foreach (var duplicate in images
            .GroupBy(image => image.Split('@')[0], StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            yield return
                $"{relative} lists `{duplicate.Key}` under `images:` {duplicate.Count()} times. One "
                + "reference has one digest; two rows for it means one of them is stale and nothing "
                + "can say which";
        }
    }

    static IEnumerable<string> LicenceViolations(string relative, Dictionary<string, string> scalars)
    {
        if (!scalars.TryGetValue("licence", out var licence))
            yield break;

        if (Array.IndexOf(BundleLicenceAllowList, licence) >= 0)
            yield break;

        yield return
            $"{relative} declares `licence: {licence}`, which is not on ADR-011's allow-list "
            + $"({string.Join(", ", BundleLicenceAllowList)}). docs/plan/02 § ADR-011: \"Offering "
            + "software as a service is exactly the use that several 2023-2025 licence changes exist "
            + "to prevent. This is a product-blocking category of mistake.\" An SSPL or BUSL operator "
            + "is a refusal with the alternative written down, not a dependency; anything else needs "
            + "the allow-list widened in build/Build.Bundle.cs with the reason next to it";
    }

    /// <summary>
    ///     The keys each <c>install:</c> kind needs, so that a pin is complete and is in one place.
    /// </summary>
    /// <remarks>
    ///     ⚠ Three kinds rather than one, because upstream projects publish three ways and pretending
    ///     otherwise would mean a URL assembled in <c>install.sh</c> from parts — which is a pin the
    ///     gate cannot read. <c>helm</c> is a chart repository, <c>helm-archive</c> is a packaged
    ///     chart published as a release asset (Altinity and SeaweedFS both do this), <c>manifest</c>
    ///     is a plain document applied with <c>kubectl</c>.
    /// </remarks>
    static IEnumerable<string> PinViolations(string relative, Dictionary<string, string> scalars)
    {
        if (!scalars.TryGetValue("install", out var install))
            yield break;

        var required = install switch
        {
            "helm" => new[] { "repo", "chart", "version" },
            "helm-archive" => ["archive", "chart", "version"],
            "manifest" => ["manifest", "release"],
            _ => [],
        };

        if (required.Length == 0)
        {
            yield return
                $"{relative} declares `install: {install}`, which is not one of helm, helm-archive or "
                + "manifest. install.sh switches on this value and does nothing for a kind it does "
                + "not know, so an unknown kind is a component that is silently never installed";

            yield break;
        }

        foreach (var key in required.Where(key => !scalars.ContainsKey(key)))
        {
            yield return
                $"{relative} declares `install: {install}` and no `{key}:`. install.sh reads the pin "
                + "out of this file and hard-codes no version, so a missing key is not a default — it "
                + "is an install command with an empty argument in it";
        }
    }

    static IEnumerable<string> CheckedDateViolations(string relative, Dictionary<string, string> scalars)
    {
        if (!scalars.TryGetValue("checked", out var checkedText))
            yield break;

        if (!DateOnly.TryParseExact(checkedText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkedOn))
        {
            yield return
                $"{relative} declares `checked: {checkedText}`, which is not an ISO date. It is the "
                + "one field that says how old the claim \"this pin resolves\" is, and a date nothing "
                + "can parse is a date nobody can weigh";

            yield break;
        }

        // ⚠ A future date is the specific lie this catches: a pin recorded as verified on a day that
        // has not happened is a pin nobody resolved. There is deliberately no MAXIMUM AGE — a gate
        // that goes red on a Tuesday because a correct pin got old is a gate somebody switches off,
        // and `install.sh --verify` is the thing that answers staleness.
        if (checkedOn > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            yield return
                $"{relative} declares `checked: {checkedText}`, which is in the future. That date is a "
                + "claim that somebody resolved this pin against its registry on that day";
        }
    }

    // ── The roster ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>bundle.yaml</c> and the directories name the same set, with the same phases.
    /// </summary>
    /// <remarks>
    ///     ⚠ Both directions, and the second one is the one that bites. A component on disk and off
    ///     the roster is a component <c>install.sh</c> never installs — it reads the roster, because
    ///     the filesystem's order is alphabetical and alphabetical puts <c>cert-manager</c> before
    ///     <c>kube-ovn</c>, which installs a webhook onto a cluster with no CNI.
    /// </remarks>
    IEnumerable<string> RosterViolations(List<BundleComponent> components)
    {
        if (!BundleRosterFile.FileExists())
        {
            yield return
                $"{RootDirectory.GetRelativePathTo(BundleRosterFile)} is missing. It is what install.sh "
                + "reads to get the install ORDER, which the filesystem does not carry";

            yield break;
        }

        var roster = ReadRoster();
        var onDisk = components.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var name in roster.Keys.Where(name => !onDisk.Contains(name)).OrderBy(x => x, StringComparer.Ordinal))
        {
            yield return
                $"charts/bundle/bundle.yaml lists `{name}` and charts/bundle/{name}/ does not exist. "
                + "install.sh would report it missing at install time, on a cluster, rather than here";
        }

        foreach (var component in components.Where(x => !roster.ContainsKey(x.Name)))
        {
            yield return
                $"charts/bundle/{component.Name}/ is a component and charts/bundle/bundle.yaml does "
                + "not list it, so install.sh never installs it. Adding a component is meant to be a "
                + "visible diff in the roster rather than a directory somebody has to notice";
        }

        foreach (var component in components)
        {
            if (!roster.TryGetValue(component.Name, out var rosterPhase))
                continue;

            if (!component.Scalars.TryGetValue("phase", out var componentPhase))
                continue;

            if (!string.Equals(rosterPhase, componentPhase, StringComparison.Ordinal))
            {
                yield return
                    $"charts/bundle/{component.Name}/{ComponentFileName} declares `phase: "
                    + $"{componentPhase}` and charts/bundle/bundle.yaml puts it in phase "
                    + $"{rosterPhase}. install.sh obeys the roster, so the manifest's number is the "
                    + "one a reader would trust and the wrong one";
            }
        }
    }

    /// <summary>Component name to phase, from the roster's <c>components:</c> block.</summary>
    Dictionary<string, string> ReadRoster()
    {
        var roster = new Dictionary<string, string>(StringComparer.Ordinal);
        var inside = false;
        string? name = null;

        foreach (var line in BundleRosterFile.ReadAllLines())
        {
            if (line.StartsWith("components:", StringComparison.Ordinal))
            {
                inside = true;

                continue;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#')
                inside = false;

            if (!inside)
                continue;

            var match = RosterEntry.Match(line);

            if (!match.Success)
                continue;

            if (match.Groups["key"].Value is "name")
                name = match.Groups["value"].Value;
            else if (name is not null)
                roster[name] = match.Groups["value"].Value;
        }

        return roster;
    }

    /// <summary><c>  - name: x</c> or <c>    phase: 10</c>.</summary>
    static readonly Regex RosterEntry = new(
        @"^\s+-?\s*(?<key>name|phase):\s*(?<value>\S+)\s*$",
        RegexOptions.Compiled);

    // ── Coverage ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every group/version any managed chart renders is served by exactly one component.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the check the directory exists for, and it goes red on three distinct
    ///         mistakes rather than one.</b> A chart rendering a group nothing installs — the state
    ///         the whole tree was in. A pin moved to a release that stopped serving a group some
    ///         chart still renders — Strimzi 1.0.0 against <c>charts/managed/kafka</c>, which is a
    ///         live example and not a hypothetical. And two components claiming one group/version,
    ///         which is two operators owning one definition and a fight nobody wins.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Group AND version, never group alone.</b> <c>charts/bundle/cluster-api</c> serves
    ///         <c>controlplane.cluster.x-k8s.io/v1beta2</c> and
    ///         <c>charts/bundle/kamaji-control-plane-provider</c> serves
    ///         <c>controlplane.cluster.x-k8s.io/v1alpha2</c> — the same group, two definitions, two
    ///         owners, both correct. A duplicate check over groups would call that a conflict, and a
    ///         coverage check over groups would call an operator that dropped a version "still
    ///         covered", which is the exact failure being prevented.
    ///     </para>
    /// </remarks>
    static IEnumerable<string> CoverageViolations(
        List<BundleComponent> components,
        IReadOnlyDictionary<string, List<string>> rendered)
    {
        var servedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var component in components)
        {
            foreach (var entry in component.Serves)
            {
                if (!servedBy.TryGetValue(entry, out var owners))
                    servedBy[entry] = owners = [];

                owners.Add(component.Name);
            }
        }

        foreach (var (entry, owners) in servedBy.Where(x => x.Value.Count > 1).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            yield return
                $"{entry} is declared under `serves:` by {owners.Count} components — "
                + $"{string.Join(", ", owners.OrderBy(x => x, StringComparer.Ordinal))}. One "
                + "definition has one owner: two components installing it means whichever applies "
                + "last wins, and an upgrade of either silently rewrites the other's schema";
        }

        foreach (var (entry, sources) in rendered.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (servedBy.ContainsKey(entry))
                continue;

            yield return
                $"{entry} is rendered by {string.Join(", ", sources)} and no component under "
                + "charts/bundle/ declares it under `serves:`. An object applied against a "
                + "group/version the cluster does not serve is refused outright; an object applied "
                + "against one no controller watches is accepted and reconciled by NOTHING, which is "
                + "the one failure mode with no error anywhere in it — "
                + "charts/managed/opensearch/SOURCE. Either add the component that installs it or, if "
                + "a component was just bumped past it, that bump is the half of the ordering rule "
                + "that must not land first";
        }
    }

    /// <summary>
    ///     Every non-built-in <c>group/version</c> any chart under <c>charts/managed/</c> renders,
    ///     with the templates that render it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A text scan of <c>templates/</c>, not a <c>helm template</c> render, and the
    ///         difference is a deliberate trade.</b> Rendering would need values that satisfy every
    ///         chart's schema and a working <c>helm</c>, and would then miss any object behind a
    ///         conditional the default values switch off. Reading the literal <c>apiVersion:</c>
    ///         lines sees every object in the file including the conditional ones, which is the set
    ///         the bundle has to cover.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only <c>templates/</c>.</b> A <c>Chart.yaml</c>'s <c>apiVersion: v2</c> is Helm's
    ///         own schema version and a <c>conformance.yaml</c>'s is a resource api-version; both
    ///         would be read as Kubernetes groups by a scan that walked the whole chart, and the
    ///         second would produce a violation naming a date.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A templated value is skipped rather than guessed.</b> No chart in the tree writes
    ///         <c>apiVersion: {{ … }}</c> today; if one ever does, the honest answer is that this
    ///         scan cannot see it, and a regex that guessed would cover a chart it had not read.
    ///     </para>
    /// </remarks>
    Dictionary<string, List<string>> ReadRenderedApiGroups(out int chartsInspected)
    {
        var rendered = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var charts = 0;

        if (!ManagedChartsDirectory.DirectoryExists())
        {
            chartsInspected = 0;

            return rendered;
        }

        foreach (var chart in ManagedChartsDirectory.GlobDirectories("*").OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var templates = chart / "templates";

            if (!templates.DirectoryExists())
                continue;

            charts++;

            foreach (var template in templates.GlobFiles("**/*.yaml", "**/*.yml", "**/*.tpl")
                         .OrderBy(x => x.ToString(), StringComparer.Ordinal))
            {
                foreach (var line in template.ReadAllLines())
                {
                    var match = RenderedApiVersion.Match(line);

                    if (!match.Success)
                        continue;

                    var value = match.Groups["value"].Value;

                    if (value.Contains("{{", StringComparison.Ordinal))
                        continue;

                    var slash = value.LastIndexOf('/');
                    var group = slash < 0 ? string.Empty : value[..slash];

                    if (BuiltInApiGroups.Contains(group))
                        continue;

                    if (!rendered.TryGetValue(value, out var sources))
                        rendered[value] = sources = [];

                    var source = RootDirectory.GetRelativePathTo(template).ToString();

                    if (!sources.Contains(source, StringComparer.Ordinal))
                        sources.Add(source);
                }
            }
        }

        chartsInspected = charts;

        return rendered;
    }

    /// <summary>An <c>apiVersion:</c> line at any indent, with its value.</summary>
    static readonly Regex RenderedApiVersion = new(
        @"^\s*-?\s*apiVersion:\s*(?<value>[^\s#]+)\s*$",
        RegexOptions.Compiled);

    /// <summary>A <c>group/version</c> pair, as <c>serves:</c> must spell one.</summary>
    static readonly Regex GroupVersion = new(
        @"^[a-z0-9]([a-z0-9.-]*[a-z0-9])?/v[0-9]+((alpha|beta)[0-9]+)?$",
        RegexOptions.Compiled);

    /// <summary>
    ///     An <c>images:</c> entry: a reference, a tag, and the digest that tag served when somebody
    ///     looked.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The tag half is required and is not redundant with the digest.</b> A digest alone
    ///     would be unreviewable — nobody can tell <c>sha256:a2701eb9…</c> from
    ///     <c>sha256:b2701eb9…</c> in a diff, and nobody can tell which upstream release either is.
    ///     The pair is what makes a moved tag a readable failure: <c>charts/bundle/images.sh</c>
    ///     re-renders the chart, resolves the tag, and prints the two digests side by side.
    ///     ⚠ <b><c>sha256</c> only, and lower-case hex only.</b> An entry with a truncated or
    ///     upper-cased digest would never equal what a registry returns, so the comparison would
    ///     always be red and the natural response would be to delete the check.
    /// </remarks>
    static readonly Regex ImageReference = new(
        @"^[a-z0-9][a-z0-9._/-]*:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$",
        RegexOptions.Compiled);

    // ── Ordering ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One commit must not both bump a component's pin and edit a managed chart's templates.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         charts/managed/opensearch/conformance.yaml § owed, <c>api-group-is-deprecated</c>:
    ///         <i>"Closing it is a new api-version on the resource type plus a charts/bundle/ bump,
    ///         in that order, and the two must not be done in one commit: a bundle that moved first
    ///         would strand every existing service."</i>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A CHANGED pin, not a touched file, and the difference is what makes this
    ///         usable.</b> Adding a component adds pin lines and removes none, so the commit that
    ///         created this directory does not trip it. What trips it is a <c>version:</c> or
    ///         <c>release:</c> line that was replaced — a bump — landing beside a template edit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tip commit only, and it says so rather than implying more.</b> "Not in one
    ///         commit" is a statement about one commit; walking a branch would need a merge base this
    ///         gate cannot know in every checkout. A repository with no reachable parent — a shallow
    ///         CI clone, or a first commit — is reported as not inspected in the row's detail. That
    ///         cannot make the row <see cref="GateStatus.Vacuous" /> on its own, because the coverage
    ///         and manifest halves are what supply the candidate count; it is a shortfall named in
    ///         the detail rather than a silence.
    ///     </para>
    /// </remarks>
    IEnumerable<string> OrderingViolations(out string detail)
    {
        var diff = GitTasks
            .Git("diff --unified=0 HEAD~1 HEAD -- charts", RootDirectory, logOutput: false, logInvocation: false, exitHandler: _ => 0)
            .Where(x => x.Type == OutputType.Std)
            .Select(x => x.Text)
            .ToList();

        if (diff.Count == 0)
        {
            detail = "the ordering rule was not inspected (no reachable parent commit, or no change under charts/)";

            return [];
        }

        detail = "the ordering rule was checked over HEAD";

        var bumped = new List<string>();
        var templates = new List<string>();
        var file = string.Empty;

        foreach (var line in diff)
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                file = line[6..];

                if (file.Contains("/templates/", StringComparison.Ordinal)
                    && file.StartsWith("charts/managed/", StringComparison.Ordinal)
                    && !templates.Contains(file, StringComparer.Ordinal))
                {
                    templates.Add(file);
                }

                continue;
            }

            // A removed pin line. `-` and not `+`: a version that was replaced is a bump, and a
            // version that was only added is a new component.
            if (line.StartsWith("--", StringComparison.Ordinal) || !line.StartsWith('-'))
                continue;

            if (!file.StartsWith("charts/bundle/", StringComparison.Ordinal))
                continue;

            if (PinLine.IsMatch(line[1..]) && !bumped.Contains(file, StringComparer.Ordinal))
                bumped.Add(file);
        }

        if (bumped.Count == 0 || templates.Count == 0)
            return [];

        return
        [
            $"HEAD changes a version pin in {string.Join(", ", bumped)} and edits "
            + $"{string.Join(", ", templates)} in the same commit. "
            + "charts/managed/opensearch/conformance.yaml § owed, api-group-is-deprecated: \"the two "
            + "must not be done in one commit: a bundle that moved first would strand every existing "
            + "service\". Split it — the chart's new api-version first, the bundle bump after, so "
            + "that at no commit does a rendered group/version go unserved",
        ];
    }

    /// <summary>A pin, as a <c>component.yaml</c> spells one.</summary>
    static readonly Regex PinLine = new(
        @"^\s*(version|release|versionCrds|archive|manifest|manifestExtra):",
        RegexOptions.Compiled);

    // ── The one reader this file adds ─────────────────────────────────────────────────────────

    /// <summary>
    ///     A top-level key's prose, following a <c>&gt;</c> or <c>|</c> folding indicator into the
    ///     block it introduces, or <see langword="null" /> when the key is absent.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="ReadFlatKeys" /> reads the same key as the literal string <c>"&gt;"</c>,
    ///     which is a folding indicator and not a reason — so a gate that measured that value would
    ///     accept every folded block in the directory as one character of prose and reject it. Every
    ///     multi-line field a <c>component.yaml</c> carries today (<c>notes:</c>, and now
    ///     <c>servesNoDefinitions:</c>) is written folded, so reading the block is the normal case
    ///     rather than a tolerance.
    /// </remarks>
    static string? ReadBundleReason(AbsolutePath file, string key)
    {
        var lines = file.ReadAllLines();

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith(key + ":", StringComparison.Ordinal))
                continue;

            var inline = lines[index][(key.Length + 1)..].Trim();

            if (inline.Length > 0 && inline is not (">" or "|" or ">-" or "|-" or ">+" or "|+"))
                return Unquote(inline);

            var block = new List<string>();

            for (var next = index + 1; next < lines.Length; next++)
            {
                var line = lines[next];

                if (line.Trim().Length == 0)
                    continue;

                if (!char.IsWhiteSpace(line[0]))
                    break;

                var trimmed = line.Trim();

                if (trimmed[0] != '#')
                    block.Add(trimmed);
            }

            return string.Join(' ', block);
        }

        return null;
    }

    /// <summary>
    ///     The entries of a top-level block sequence — <c>serves:</c> and nothing else today.
    /// </summary>
    /// <remarks>
    ///     ⚠ <see cref="ReadFlatKeys" /> in <c>Build.Charts.cs</c> reads top-level scalars and
    ///     deliberately ignores everything indented, which is right for a <c>SOURCE</c> and blind to
    ///     the one key here that the coverage check depends on. This is the narrowest possible
    ///     addition rather than a second YAML reader: one nesting level, one shape, comments skipped.
    /// </remarks>
    static List<string> ReadBundleSequence(AbsolutePath file, string key)
    {
        var entries = new List<string>();
        var inside = false;

        foreach (var line in file.ReadAllLines())
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                inside = true;

                continue;
            }

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                inside = false;

            if (!inside)
                continue;

            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            if (trimmed[0] != '-')
            {
                inside = false;

                continue;
            }

            entries.Add(Unquote(trimmed[1..].Trim()));
        }

        return entries;
    }
}
