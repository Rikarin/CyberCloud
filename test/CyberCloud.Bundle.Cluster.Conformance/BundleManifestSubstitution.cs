using Shouldly;

namespace CyberCloud.Bundle.Cluster.Conformance;

/// <summary>
///     The <c>${VAR:=default}</c> pass <c>charts/bundle/substitute.sh</c> performs on a
///     <c>manifest:</c> document before <c>install.sh</c> applies it — run over fixture documents,
///     with no cluster and no network.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This exists because four of the six manifest components crashlooped on the first
///             real run of the roster, and nothing short of a kubelet could have said so.
///         </b> The Cluster API family's release documents are clusterctl templates:
///         <c>--insecure-diagnostics=${CAPI_INSECURE_DIAGNOSTICS:=false}</c> reached the container
///         verbatim and <c>strconv.ParseBool</c> refused it (issue #2, 2026-09-15). The fix is the
///         substitution clusterctl performs, done in awk from the document's own defaults; what this
///         class pins is the three forms, the environment override, the refusal of a variable with
///         neither, and — the half that is easy to get wrong — the <c>$</c> forms the same documents
///         carry that must NOT be touched.
///     </para>
///     <para>
///         ⚠ <b>Fixtures and not the pinned documents, on purpose.</b> The real
///         <c>cluster-api-components.yaml</c> is 2.5 MB behind a GitHub CDN, and a test that
///         downloaded it would be red on every machine with no network — which is every machine
///         <c>--dry-run</c> promises to work on. The fixtures carry the exact spellings those
///         documents use, copied on 2026-09-15: a default with a colon in it
///         (<c>${CAPI_DIAGNOSTICS_ADDRESS:=:8443}</c>), a default that is a single space
///         (<c>${CACPPK_INFRASTRUCTURE_CLUSTERS:= }</c>), a feature-gate list with five variables
///         on one line, and the <c>$(VAR_NAME)</c>, <c>$$(VAR_NAME)</c> and regex-terminal
///         <c>$</c> the CustomResourceDefinition descriptions are full of. That the real documents
///         come through the same pass clean was measured the same day on the cluster —
///         <c>BundleInstallSelection.EveryManifestComponentIsFollowedByAnEstablishmentWait</c>
///         asserts the pass is in the recipe; this asserts what the pass does.
///     </para>
/// </remarks>
public sealed class BundleManifestSubstitution : IDisposable {
    readonly string root = Path.Combine(Path.GetTempPath(), "cybercloud-substitute-" + Guid.NewGuid().ToString("N"));

    static string Script => Path.Combine(BundleInstaller.RepositoryRoot, "charts", "bundle", "substitute.sh");

    /// <summary>
    ///     The arg block of <c>cluster-api-components.yaml</c> v1.14.0's core controller and the
    ///     kamaji provider's, spellings verbatim, plus the <c>$</c> forms a definition carries.
    /// </summary>
    const string Fixture =
        "        - --diagnostics-address=${CAPI_DIAGNOSTICS_ADDRESS:=:8443}\n"
        + "        - --insecure-diagnostics=${CAPI_INSECURE_DIAGNOSTICS:=false}\n"
        + "        - --feature-gates=MachinePool=${EXP_MACHINE_POOL:=true},ClusterTopology=${CLUSTER_TOPOLOGY:=false},RuntimeSDK=${EXP_RUNTIME_SDK:-false}\n"
        + "        - --dynamic-infrastructure-clusters=${CACPPK_INFRASTRUCTURE_CLUSTERS:= }\n"
        + "          description: 'Variable references $(VAR_NAME) are expanded using the container''s\n"
        + "            environment. Escaped references will never be expanded, regardless of whether $$(VAR_NAME)\n"
        + "            exists or not.'\n"
        + "          pattern: ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$\n";

    /// <summary>
    ///     With nothing in the environment, every variable takes the default the document carries,
    ///     and every other <c>$</c> is left alone.
    /// </summary>
    [Fact]
    public async Task TheDocumentsOwnDefaultsAreUsedWhenTheEnvironmentSaysNothing() {
        var run = await SubstituteAsync(Fixture);

        run.ExitCode.ShouldBe(0, "substitute.sh refused a document whose every variable has a default:\n" + run.Output);

        run.Output.ShouldContain(
            "--diagnostics-address=:8443\n",
            Case.Sensitive,
            "a default containing a colon was not taken whole — the parser split on the wrong colon. Output:\n"
            + run.Output
        );
        run.Output.ShouldContain("--insecure-diagnostics=false\n", Case.Sensitive, run.Output);
        run.Output.ShouldContain(
            "--feature-gates=MachinePool=true,ClusterTopology=false,RuntimeSDK=false\n",
            Case.Sensitive,
            "three variables on one line, `:=` and `:-` mixed, did not all substitute. Output:\n" + run.Output
        );
        run.Output.ShouldContain(
            "--dynamic-infrastructure-clusters= \n",
            Case.Sensitive,
            "a default that is one space was not preserved as one space, which is what clusterctl "
            + "produces for the kamaji provider's `${CACPPK_INFRASTRUCTURE_CLUSTERS:= }`. Output:\n"
            + run.Output
        );

        // ⚠ The three forms the same documents carry that are NOT variables. A pass that touched
        // any of them would corrupt every CustomResourceDefinition description in the document,
        // and a regex pattern ending in `$` is the one whose corruption an API server refuses.
        run.Output.ShouldContain(
            "$(VAR_NAME) are expanded",
            Case.Sensitive,
            "`$(VAR_NAME)` — Kubernetes' container-env syntax, not a variable — was altered. Output:\n" + run.Output
        );
        run.Output.ShouldContain(
            "whether $$(VAR_NAME)",
            Case.Sensitive,
            "`$$(VAR_NAME)` — the escape of the above — was altered. Output:\n" + run.Output
        );
        run.Output.ShouldContain(
            "?$\n",
            Case.Sensitive,
            "a validation pattern's terminal `$` was altered. Output:\n" + run.Output
        );
        run.Output.ShouldNotContain(
            "${",
            Case.Sensitive,
            "a `${…}` survived the pass, so it would reach a container as the literal string that "
            + "crashlooped four controllers on 2026-09-15. Output:\n"
            + run.Output
        );
    }

    /// <summary>
    ///     A variable set in the environment wins over the document's default — the same knob
    ///     <c>CLUSTER_TOPOLOGY=true clusterctl init</c> offers — and an EMPTY one does not, for the
    ///     two default forms.
    /// </summary>
    [Fact]
    public async Task TheEnvironmentOverridesADefaultUnlessItIsEmpty() {
        var run = await SubstituteAsync(
            Fixture,
            new Dictionary<string, string> {
                ["CLUSTER_TOPOLOGY"] = "true", ["EXP_RUNTIME_SDK"] = "true", ["CAPI_INSECURE_DIAGNOSTICS"] = ""
            }
        );

        run.ExitCode.ShouldBe(0, run.Output);
        run.Output.ShouldContain(
            "ClusterTopology=true,RuntimeSDK=true",
            Case.Sensitive,
            "`:=` or `:-` did not take the environment's value over the document's default. Output:\n" + run.Output
        );
        run.Output.ShouldContain(
            "--insecure-diagnostics=false\n",
            Case.Sensitive,
            "a variable set to the empty string replaced a `:=` default with nothing. drone/envsubst, "
            + "which clusterctl uses, treats empty as unset for the two default forms. Output:\n"
            + run.Output
        );
    }

    /// <summary>
    ///     A <c>${NAME}</c> with no default and no value is refused, naming <c>NAME</c>, and nothing
    ///     is silently emptied. So is a <c>${…}</c> form the pass does not implement.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The refusal is the point, not the substitution.</b> GNU <c>envsubst</c> would
    ///     expand an unset <c>${NAME}</c> to the empty string and exit 0, which for a feature-gate
    ///     list is <c>--feature-gates=MachinePool=</c> — a different crashloop from the one being
    ///     fixed. clusterctl refuses with <i>"value for variables [NAME] is not set"</i>, and so
    ///     does this, before anything reaches a cluster.
    /// </remarks>
    [Fact]
    public async Task AVariableWithNeitherAValueNorADefaultIsRefusedByName() {
        var run = await SubstituteAsync(
            "        - --cloud=${CLOUD_PROVIDER}\n        - --shout=${NAME^^}\n        - --fine=${PRESENT:=yes}\n",
            new Dictionary<string, string> { ["PRESENT"] = "yes" }
        );

        run.ExitCode.ShouldBe(
            1,
            "substitute.sh accepted a document naming `${CLOUD_PROVIDER}` with no default and no value. "
            + "The container would have got the literal string, or an empty one. Output:\n"
            + run.Output
        );
        run.Output.ShouldContain(
            "CLOUD_PROVIDER",
            Case.Sensitive,
            "the refusal did not name the variable, which is the one thing the reader needs. Output:\n" + run.Output
        );
        run.Output.ShouldContain(
            "${NAME^^}",
            Case.Sensitive,
            "a `${…}` form the pass does not implement was passed through rather than refused. Output:\n" + run.Output
        );
        run.Output.ShouldNotContain(
            "--cloud=\n",
            Case.Sensitive,
            "the unset variable was expanded to the empty string, which is envsubst's behaviour and "
            + "the one this pass exists to avoid. Output:\n"
            + run.Output
        );
    }

    /// <summary>
    ///     A document with no variable at all comes back byte for byte — the case four of the six
    ///     pinned manifests are.
    /// </summary>
    [Fact]
    public async Task ADocumentWithNoVariableIsTheIdentity() {
        const string document = "apiVersion: v1\nkind: Namespace\nmetadata:\n  name: cdi\n  labels:\n    cost: $5\n";
        var run = await SubstituteAsync(document);

        run.ExitCode.ShouldBe(0, run.Output);
        run.Output.ShouldBe(document, "a variable-free document was altered by the pass:\n" + run.Output);
    }

    async Task<BundleInstaller.Run> SubstituteAsync(
        string document,
        IReadOnlyDictionary<string, string>? environment = null
    ) {
        Assert.SkipUnless(
            BundleInstaller.OnPath("bash"),
            BundleInstaller.SkipWithoutBash(
                "substitute.sh",
                "the `${VAR:=default}` pass install.sh runs over every manifest component."
            )
        );

        Directory.CreateDirectory(root);
        var input = Path.Combine(root, Guid.NewGuid().ToString("N") + ".yaml");
        await File.WriteAllTextAsync(input, document, TestContext.Current.CancellationToken);

        // ⚠ Forward slashes for the same reason BundleInstaller.RunAsync gives for the script path:
        // Git's bash on Windows resolves `C:/…` and mangles `C:\…`.
        return await BundleInstaller.RunAsync(
            Script,
            input.Replace('\\', '/') + " -",
            null,
            TestContext.Current.CancellationToken,
            environment
        );
    }

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }
    }
}
