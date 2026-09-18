using CyberCloud.ResourceManager.Registry;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The managed Grafana declaration, checked the way a silo checks it at start, plus the facts
///     ADR-011 makes load-bearing and nothing else reads: the image pin, the licence gate's exception,
///     and the portal's freedom from Grafana code.
/// </summary>
public sealed partial class GrafanaDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        var registry = ProviderRegistry.Build([new DashboardProvider()]);

        registry.TryGetType(Grafanas.Type, out var registration).ShouldBeTrue();
        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(Grafanas.ClusterIdPointer);
        registration.Chart.ShouldBe(Grafanas.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(GrafanaReconciler));
        registration.SupportsTags.ShouldBeTrue();
        registration.SoftDeleteDays.ShouldBe(0, "an instance's state is an emptyDir, and a window over nothing is a promise nobody can keep");
    }

    [Fact]
    public void BothProvidersOfThisAssemblyBuildIntoOneRegistryAndDiscoveryFindsBoth() {
        // ⚠ THE FIRST ASSEMBLY WITH TWO IResourceProviders. ProviderRegistry.Build refuses a duplicate
        // namespace; these are two. ProviderDiscovery.FromAssembly is what the generation step runs,
        // and "every provider an assembly declares" has to mean two here or the OpenAPI document
        // publishes one namespace of two.
        var registry = ProviderRegistry.Build([new MonitorProvider(), new DashboardProvider()]);

        registry.Namespaces.Order(StringComparer.Ordinal).ShouldBe(["CyberCloud.Dashboard", "CyberCloud.Monitor"]);

        ProviderDiscovery.FromAssembly(typeof(MonitorProvider).Assembly)
            .Select(x => x.ProviderNamespace)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["CyberCloud.Dashboard", "CyberCloud.Monitor"]);
    }

    [Fact]
    public void UrlIsSynchronousSecretAndHasAHandler() {
        var registry = ProviderRegistry.Build([new DashboardProvider()]);
        registry.TryGetType(Grafanas.Type, out var registration).ShouldBeTrue();

        var action = registration.Actions.Single(x => x.Name == Grafanas.UrlAction);

        // ⚠ Secret, because the response carries the admin password; its own permission rather than
        // read, for the reason listKeys gives — a viewer of the resource is not a party who may sign
        // in as its administrator.
        action.Secret.ShouldBeTrue();
        action.LongRunning.ShouldBeFalse();
        action.HandlerType.ShouldBe(typeof(GrafanaUrlHandler));
        action.Permission.ShouldNotBe(registration.ReadPermission);

        Grafanas.UrlResponse.Properties.Where(x => x.Secret).Select(x => x.JsonPointer).ShouldBe(["/adminPassword"]);

        var handler = new GrafanaUrlHandler();
        handler.Type.ShouldBe(Grafanas.Type);
        handler.Action.ShouldBe(Grafanas.UrlAction);
    }

    [Fact]
    public void EveryDeclaredPropertyIsCoherentAndTheDefaultBodyIsAccepted() {
        foreach (var property in Grafanas.Schema2026.Properties.Concat(Grafanas.UrlResponse.Properties)) {
            property.Incoherences().ShouldBeEmpty(property.JsonPointer);
        }

        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, WorkspacePath("prod")));
        Grafanas.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue();
        Grafanas.AnonymousViewers(body.RootElement).ShouldBeFalse("anonymous viewing is off unless asked for");

        // ⚠ The second SchemaFormat.ResourceId in the catalogue; a bare name is refused at the API.
        using var bare = JsonDocument.Parse(Grafanas.Body(ClusterId, "prod"));
        Grafanas.Schema2026.Validate(bare.RootElement, allowTags: true).IsSuccess.ShouldBeFalse();
    }

    // ── ADR-011 ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheImageIsUpstreamsUnmodifiedPinnedByDigestInTheBundlesShape() {
        // ⚠ "We distribute, we do not modify" — the reference is upstream's repository and a digest
        // Docker Hub served, not an image this platform built. A `cybercloud/` repository here would
        // be a modified Grafana and a different licence question.
        Grafanas.ImageRepository.ShouldBe("grafana/grafana");
        BundlePin().IsMatch(Grafanas.Image).ShouldBeTrue(Grafanas.Image);
        Grafanas.Image.ShouldNotContain("0000000000000000", customMessage: "a placeholder digest is a reference nothing can pull");
    }

    [Fact]
    public void TheChartCarriesTheSameImagePinAndPluginAsTheContract() {
        var values = Embedded("grafana.values.yaml").Split('\n');

        values.Single(x => x.StartsWith("image: ", StringComparison.Ordinal))["image: ".Length..].Trim().ShouldBe(Grafanas.Image);
        values.Single(x => x.StartsWith("preinstall: ", StringComparison.Ordinal))["preinstall: ".Length..].Trim()
            .ShouldBe(Grafanas.ClickHousePreinstall);
    }

    [Fact]
    public void TheChartsDeploymentCarriesTheSamePluginSettingsAsTheContract() {
        // ⚠ The chart is a Helm template and generation does not reach its env block, so this is the
        // only comparison of the two plugin settings between the chart and Grafanas.DeploymentJson.
        // A chart that drifted back to GF_INSTALL_PLUGINS, or lost the auto-update switch, renders a
        // pod that goes Ready with its default datasource dead — the shape the review of #32 found.
        var env = LiteralEnvironment(Embedded("grafana.deployment.yaml"));

        env["GF_PLUGINS_PREINSTALL_SYNC"].ShouldBe("{{ .Values.preinstall | quote }}");
        env["GF_PLUGINS_PREINSTALL_AUTO_UPDATE"].ShouldBe("\"false\"");
        env.ShouldNotContainKey("GF_INSTALL_PLUGINS");
        env.ShouldNotContainKey("GF_PLUGINS_PREINSTALL_DISABLED");
    }

    [Fact]
    public void TheLicenceGateCarriesTheGrafanaExceptionBesideTheImage() {
        // ⚠ ADR-011 § Enforcement: "fails on any SSPL/BUSL/AGPL image outside an allow-list with a
        // written reason". Build.Licence.cs § LicenceExceptions is that allow-list and it was empty
        // until this type — "an allowance with no artefact behind it is a permission nobody argued
        // for". The artefact exists now, so the entry does, keyed the way the scan names an image
        // (repository, no digest) and carrying the deployed-component reading. The scan does not
        // reach a workload image yet (charts/managed/grafana/conformance.yaml § owed), so this test
        // is the only reader of the entry — which is exactly why it exists.
        var gate = Embedded("build.licence.cs");

        gate.ShouldContain($"[\"{Grafanas.ImageRepository}\"]");
        gate.ShouldContain("we distribute, we do not modify");
    }

    [Fact]
    public void ThePortalTakesNoGrafanaPackage() {
        // ⚠ ADR-011's other half: "Our portal must not embed or link Grafana code — it embeds rendered
        // dashboards by URL." A dependency named grafana in portal/package.json is the rule broken at
        // the one place no build gate looks; the url action is the integration, and it is a string.
        var manifest = JsonNode.Parse(Embedded("portal.package.json"))!.AsObject();

        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" }) {
            if (manifest[section] is JsonObject dependencies) {
                dependencies.Select(x => x.Key)
                    .Where(x => x.Contains("grafana", StringComparison.OrdinalIgnoreCase))
                    .ShouldBeEmpty($"portal/package.json § {section} names a Grafana package");
            }
        }
    }

    [Fact]
    public void TheDeploymentAllowsEmbeddingAndRunsUpstreamsImageReadOnly() {
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, WorkspacePath("prod")));
        var deployment = JsonNode.Parse(Grafanas.DeploymentJson("team", "prod", body.RootElement))!;
        var container = deployment["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Single()!;
        var env = container["env"]!.AsArray().Select(x => x!.AsObject()).ToDictionary(x => x["name"]!.GetValue<string>(), x => x);

        // ⚠ The one integration ADR-011 permits, spelled as Grafana spells it.
        env["GF_SECURITY_ALLOW_EMBEDDING"]["value"]!.GetValue<string>().ShouldBe("true");
        env["GF_AUTH_ANONYMOUS_ENABLED"]["value"]!.GetValue<string>().ShouldBe("false");

        // ⚠ THE TWO PLUGIN SETTINGS, AND WHAT EACH ONE IS FOR. The synchronous preinstall names the
        // ClickHouse plugin with its version — an entry without one means "latest, kept updated",
        // which is a different plugin on every start under an image pinned by digest. The auto-update
        // switch is off because at 13.2.2 the installer otherwise tries to update the BUNDLED
        // Prometheus plugin in place, stops its process, and fails on the read-only root — leaving a
        // Ready pod whose default datasource answers "Plugin not registered". Measured on the image;
        // Grafanas.DeploymentJson's remarks carry the transcript, and the cluster-backed suite asks
        // each datasource's health so the assertion is not on this env set alone. GF_INSTALL_PLUGINS
        // is not here: the image's run.sh logs it as deprecated and ignores it without a FORCE flag.
        env["GF_PLUGINS_PREINSTALL_SYNC"]["value"]!.GetValue<string>().ShouldBe(Grafanas.ClickHousePreinstall);
        Grafanas.ClickHousePreinstall.ShouldBe(Grafanas.ClickHousePlugin + "@" + Grafanas.ClickHousePluginVersion);
        env["GF_PLUGINS_PREINSTALL_AUTO_UPDATE"]["value"]!.GetValue<string>().ShouldBe("false");
        env.ShouldNotContainKey("GF_INSTALL_PLUGINS");
        env.ShouldNotContainKey("GF_PLUGINS_PREINSTALL_DISABLED", "preinstall_disabled also disables preinstall_sync — measured — so the ClickHouse plugin would never install");

        // The workspace's three, by reference, plus the admin credential's two.
        env[MonitorWorkspaces.EnvAccountId]["valueFrom"]!["configMapKeyRef"]!["name"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.RowName("prod"));
        env[MonitorWorkspaces.EnvIngestKey]["valueFrom"]!["secretKeyRef"]!["name"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.KeySecretName("prod"));
        env["GF_SECURITY_ADMIN_PASSWORD"]["valueFrom"]!["secretKeyRef"]!["name"]!.GetValue<string>().ShouldBe(Grafanas.AdminSecretName("team"));

        container["image"]!.GetValue<string>().ShouldBe(Grafanas.Image);
        container["securityContext"]!["readOnlyRootFilesystem"]!.GetValue<bool>().ShouldBeTrue();
        deployment["spec"]!["template"]!["spec"]!["securityContext"]!["runAsUser"]!.GetValue<int>().ShouldBe(Grafanas.GrafanaUid);
        deployment["spec"]!["replicas"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void TheDatasourcesPointAtTheWorkspaceAndNothingElseAndCarryNoCoordinate() {
        var yaml = Grafanas.DatasourcesYaml("prod");

        // Two, both locked, both authenticated as the workspace's VMUser.
        Regex.Count(yaml, "- name: ").ShouldBe(2);
        Regex.Count(yaml, "editable: false").ShouldBe(2);
        Regex.Count(yaml, MonitorWorkspaces.VmUserName("prod")).ShouldBe(2);

        // The PromQL endpoint the workspace's listKeys publishes, with the accountID left to Grafana's
        // own interpolation at start.
        yaml.ShouldContain(MonitorWorkspaces.PromqlEndpoint("$" + MonitorWorkspaces.EnvAccountId));
        yaml.ShouldContain("$" + MonitorWorkspaces.EnvIngestKey);
        yaml.ShouldContain("defaultDatabase: $" + MonitorWorkspaces.EnvDatabase);
        yaml.ShouldContain("type: " + Grafanas.ClickHousePlugin);

        Regex.IsMatch(yaml, @"/select/\d+/").ShouldBeFalse("a literal accountID reached the provisioning file");
        yaml.ShouldNotContain("ws_");
    }

    [Fact]
    public void TheChartsProvisioningFileIsTheContracts() {
        // ⚠ Line for line, with the chart's one include resolved — a drifted uid here is a dashboard
        // exported from one instance that imports into another with a broken datasource.
        var helpers = Embedded("grafana.helpers.tpl");
        const string open = "{{- define \"grafana.datasources\" -}}";
        var define = helpers[(helpers.IndexOf(open, StringComparison.Ordinal) + open.Length)..];
        define = define[..define.IndexOf("{{- end -}}", StringComparison.Ordinal)];

        var chartLines = define
            .Replace("{{ include \"grafana.vmUser\" . }}", MonitorWorkspaces.VmUserName("prod"), StringComparison.Ordinal)
            .Split('\n')
            .Select(x => x.TrimEnd())
            .Where(x => x.Length > 0)
            .ToArray();

        var contractLines = Grafanas.DatasourcesYaml("prod").Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0).ToArray();

        chartLines.ShouldBe(contractLines);
    }

    [Fact]
    public void TheChartsSizingTableIsTheContractsRowForRow() {
        var helpers = Embedded("grafana.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in Grafanas.Presets) {
            var row = PresetRow(preset).Match(helpers);
            row.Success.ShouldBeTrue($"charts/managed/grafana/templates/_helpers.tpl carries no row for {preset}");
            (row.Groups["cpu"].Value, row.Groups["memory"].Value).ShouldBe((cpu, memory));
        }
    }

    // ── The workspace pointer ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWorkspacePointerIsCheckedForTenantTypeAndGroup() {
        var id = new ResourceId(Tenant, Subscription, "prod", Grafanas.Type, "team", Guid.NewGuid());

        Refusal(id, WorkspacePath("telemetry")).ShouldBeNull();

        Refusal(id, "not-a-path")!.ShouldContain("not a resource id path");
        Refusal(id, WorkspacePath("telemetry", tenant: Guid.Parse("22222222-2222-4222-8222-222222222222")))!.ShouldContain("belongs to tenant");
        Refusal(id, new ResourceId(Tenant, Subscription, "prod", MonitorAlertRules.Type, "rule", Guid.Empty, "telemetry").Path)!.ShouldContain("must be a CyberCloud.Monitor/workspaces");
        Refusal(id, new ResourceId(Tenant, Subscription, "prod", new("CyberCloud.Communication", "services"), "alerts", Guid.Empty).Path)!.ShouldContain("must be a CyberCloud.Monitor/workspaces");
        Refusal(id, WorkspacePath("telemetry", resourceGroup: "other"))!.ShouldContain("share a resource group");
        Refusal(id, WorkspacePath("telemetry", subscription: Guid.Parse("44444444-4444-4444-8444-444444444444")))!.ShouldContain("share a resource group");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static string? Refusal(ResourceId id, string workspace) {
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, workspace));
        var result = Grafanas.WorkspaceOf(id, body.RootElement);

        if (result.IsSuccess) {
            return null;
        }

        result.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        result.Error.Target.ShouldBe(Grafanas.WorkspacePointer);
        return result.Error.Message;
    }

    static string WorkspacePath(string name, Guid? tenant = null, Guid? subscription = null, string resourceGroup = "prod") =>
        new ResourceId(tenant ?? Tenant, subscription ?? Subscription, resourceGroup, MonitorWorkspaces.Type, name, Guid.Empty).Path;

    /// <summary>
    ///     The template's <c>env</c> entries that carry a literal <c>value</c> line, by name — the
    ///     <c>valueFrom</c> references are not in it.
    /// </summary>
    static Dictionary<string, string> LiteralEnvironment(string template) {
        var lines = template.Split('\n').Select(x => x.Trim()).ToArray();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i + 1 < lines.Length; i++) {
            if (lines[i].StartsWith("- name: ", StringComparison.Ordinal) && lines[i + 1].StartsWith("value: ", StringComparison.Ordinal)) {
                env[lines[i]["- name: ".Length..]] = lines[i + 1]["value: ".Length..];
            }
        }

        return env;
    }

    static string Embedded(string logicalName) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} is not embedded. See the EmbeddedResource items in this project's .csproj.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    static Regex PresetRow(string preset) =>
        new($"\"{Regex.Escape(preset)}\"\\s+\\(dict \"cpu\" \"(?<cpu>[^\"]+)\"\\s+\"memory\" \"(?<memory>[^\"]+)\"\\)", RegexOptions.None, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"^[a-z0-9./_-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$")]
    private static partial Regex BundlePin();
}
