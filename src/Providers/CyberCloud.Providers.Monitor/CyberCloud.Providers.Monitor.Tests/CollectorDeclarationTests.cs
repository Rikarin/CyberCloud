using CyberCloud.ResourceManager.Registry;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The collector declaration, checked the way a silo checks it at start, plus the facts that live
///     in more than one file and have to agree — the image pin, the sizing table and the workspace's
///     object stems, each spelled once in C# and once in the chart.
/// </summary>
public sealed partial class CollectorDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    [Fact]
    public void TheCollectorBuildsIntoARegistryTheSiloWouldAccept() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);

        registry.TryGetType(MonitorCollectors.Type, out var registration).ShouldBeTrue();

        // ⚠ RequiresCluster and Chart on a child whose sibling has neither — both presences are the
        // declaration. A collector is three objects in a cluster; an alert rule applies nothing.
        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(MonitorCollectors.ClusterIdPointer);
        registration.Chart.ShouldBe(MonitorCollectors.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(MonitorCollectorReconciler));
        registration.SupportsTags.ShouldBeTrue();
        registration.SoftDeleteDays.ShouldBe(0, "a collector holds nothing a recovery window would protect");
    }

    [Fact]
    public void ListEndpointsIsSynchronousNotSecretAndHasAHandler() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorCollectors.Type, out var registration).ShouldBeTrue();

        var action = registration.Actions.Single(x => x.Name == MonitorCollectors.ListEndpointsAction);

        // ⚠ NOT secret, and that is a statement about the collector's ingress rather than an
        // oversight: an in-cluster address is not a credential, and nothing authenticates a workload
        // to the collector — conformance.yaml § owed, collector-ingress-is-unauthenticated.
        action.Secret.ShouldBeFalse();
        action.LongRunning.ShouldBeFalse();
        action.HandlerType.ShouldBe(typeof(MonitorCollectorListEndpointsHandler));
        action.Permission.ShouldBe(registration.ReadPermission);

        var handler = new MonitorCollectorListEndpointsHandler();
        handler.Type.ShouldBe(MonitorCollectors.Type);
        handler.Action.ShouldBe(MonitorCollectors.ListEndpointsAction);
    }

    [Fact]
    public void EveryDeclaredPropertyIsCoherent() {
        foreach (var property in MonitorCollectors.Schema2026.Properties) {
            property.Incoherences().ShouldBeEmpty(property.JsonPointer);
        }

        foreach (var property in MonitorCollectors.ListEndpointsResponse.Properties) {
            property.Incoherences().ShouldBeEmpty(property.JsonPointer);
        }
    }

    [Fact]
    public void TheDefaultBodyIsAcceptedAndBothReceiversAreOnByDefault() {
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));

        MonitorCollectors.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue();
        MonitorCollectors.OtlpGrpc(body.RootElement).ShouldBeTrue();
        MonitorCollectors.OtlpHttp(body.RootElement).ShouldBeTrue();
        MonitorCollectors.Replicas(body.RootElement).ShouldBe(1);
        MonitorCollectors.Preset(body.RootElement).ShouldBe(MonitorCollectors.DefaultPreset);
    }

    [Fact]
    public void BothReceiversOffIsABodyTheSchemaAcceptsAndTheReconcilerRefuses() {
        // ⚠ The one cross-property fact ResourceSchema cannot state. A collector with nothing to
        // listen on is a pod that exports nothing; the API accepts the body and the first pass refuses
        // it by name.
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, otlpGrpc: false, otlpHttp: false));

        MonitorCollectors.Schema2026.Validate(body.RootElement, allowTags: true).IsSuccess.ShouldBeTrue();
        MonitorCollectors.ReceiverProblem(body.RootElement).ShouldNotBeNull();

        using var oneOn = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, otlpGrpc: false));
        MonitorCollectors.ReceiverProblem(oneOn.RootElement).ShouldBeNull();
    }

    // ── The image ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheImageIsPinnedByDigestInTheBundlesShape() {
        // ⚠ `repository:tag@sha256:<64 hex>` — the spelling every charts/bundle/*/component.yaml
        // § images row uses. A tag alone is what haproxy records as owed; a digest with a
        // plausible-looking placeholder is what cloud-shell records; this one was resolved against
        // Docker Hub and SOURCE carries the command and the output.
        BundlePin().IsMatch(MonitorCollectors.Image).ShouldBeTrue(MonitorCollectors.Image);
        MonitorCollectors.Image.ShouldNotContain("0000000000000000", customMessage: "a placeholder digest is a reference nothing can pull");
    }

    [Fact]
    public void TheChartCarriesTheSameImagePinAsTheContract() {
        // ⚠ THE ONE FACT ABOUT THIS CHART GENERATION CANNOT REACH. `image:` is @internal, so
        // ./build.sh Charts carries it through untouched and compares it with nothing. Two pins is a
        // chart running one digest and a reconciler another.
        var values = Embedded("monitor-collector.values.yaml");
        var line = values.Split('\n').Single(x => x.StartsWith("image: ", StringComparison.Ordinal));

        line["image: ".Length..].Trim().ShouldBe(MonitorCollectors.Image);
    }

    // ── What the chart spells a second time ───────────────────────────────────────────────────

    [Fact]
    public void TheChartsSizingTableIsTheContractsRowForRow() {
        var helpers = Embedded("monitor-collector.helpers.tpl");

        foreach (var (preset, (cpu, memory)) in MonitorCollectors.Presets) {
            helpers.ShouldContain(
                $"\"{preset}\"",
                customMessage: $"charts/managed/monitor-collector/templates/_helpers.tpl does not carry preset {preset}"
            );

            var row = PresetRow(preset).Match(helpers);
            row.Success.ShouldBeTrue($"the chart's row for {preset} does not parse as (dict \"cpu\" … \"memory\" …)");
            (row.Groups["cpu"].Value, row.Groups["memory"].Value).ShouldBe((cpu, memory));
        }
    }

    [Fact]
    public void TheChartNamesTheWorkspacesRowAndSecretTheWayTheWorkspaceSpellsThem() {
        // ⚠ Two stems the chart cannot include from the workspace's chart. A stem that drifted is a
        // pod held in CreateContainerConfigError naming a ConfigMap that will never appear.
        var helpers = Embedded("monitor-collector.helpers.tpl");

        helpers.ShouldContain("printf \"monitor-%s\" .Values.platform.workspace");
        helpers.ShouldContain("printf \"monitor-%s-ingest\" .Values.platform.workspace");

        MonitorWorkspaces.RowName("x").ShouldBe("monitor-x");
        MonitorWorkspaces.KeySecretName("x").ShouldBe("monitor-x-ingest");
        MonitorWorkspaces.VmUserName("x").ShouldBe("monitor-x");
    }

    [Fact]
    public void TheChartsConfigurationIsTheContractsForTheDefaultBody() {
        // ⚠ THE WHOLE RENDERED CONFIGURATION, COMPARED LINE FOR LINE. The chart's `define` is Helm and
        // the contract's is C#, and the two are the same file only if somebody checks — a drifted
        // exporter key here is a chart that lints clean and a collector that refuses to start.
        var helpers = Embedded("monitor-collector.helpers.tpl");
        var define = helpers[(helpers.IndexOf("{{- define \"monitorCollector.config\" -}}", StringComparison.Ordinal) + "{{- define \"monitorCollector.config\" -}}".Length)..];
        define = define[..define.IndexOf("{{- end -}}", StringComparison.Ordinal)];

        // The chart's conditionals and includes, resolved the way Helm would for the default body.
        var rendered = define
            .Replace("{{- if .Values.receivers.otlpGrpc }}", string.Empty, StringComparison.Ordinal)
            .Replace("{{- if .Values.receivers.otlpHttp }}", string.Empty, StringComparison.Ordinal)
            .Replace("{{- end }}", string.Empty, StringComparison.Ordinal)
            .Replace("{{ include \"monitorCollector.vmUser\" . }}", MonitorWorkspaces.VmUserName("prod"), StringComparison.Ordinal);

        var chartLines = rendered.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0).ToArray();

        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var contractLines = MonitorCollectors.CollectorConfig(Address("gateway", "prod"), body.RootElement)
            .Split('\n')
            .Select(x => x.TrimEnd())
            .Where(x => x.Length > 0)
            .ToArray();

        chartLines.ShouldBe(contractLines);
    }

    // ── The configuration's shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheConfigurationCarriesNoCoordinateOfTheWorkspaceAndNamesAllThreeVariables() {
        // ⚠ THE DESIGN, AS AN ASSERTION. A child's pass never learns its parent's GUID, so a
        // configuration carrying a real accountID would have had to read it from somewhere — and the
        // only somewhere is the cluster. The three `${env:…}` references are what the kubelet fills.
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var config = MonitorCollectors.CollectorConfig(Address("gateway", "prod"), body.RootElement);

        config.ShouldContain("${env:" + MonitorWorkspaces.EnvAccountId + "}");
        config.ShouldContain("${env:" + MonitorWorkspaces.EnvDatabase + "}");
        config.ShouldContain("${env:" + MonitorWorkspaces.EnvIngestKey + "}");
        config.ShouldContain(MonitorWorkspaces.RemoteWriteEndpoint("${env:" + MonitorWorkspaces.EnvAccountId + "}"));
        config.ShouldContain("username: " + MonitorWorkspaces.VmUserName("prod"));

        // No digits where an accountID would be, and no `ws_` where a database would be.
        Regex.IsMatch(config, @"/insert/\d+/").ShouldBeFalse("a literal accountID reached the rendered configuration");
        config.ShouldNotContain("ws_");

        // ⚠ The limiter FIRST in every pipeline — upstream: a limiter that is not first protects nothing.
        Regex.Count(config, @"processors: \[memory_limiter, batch\]").ShouldBe(3);
    }

    [Fact]
    public void TheConfigurationIsDeterministicAndFollowsTheReceivers() {
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var id = Address("gateway", "prod");

        MonitorCollectors.CollectorConfig(id, body.RootElement).ShouldBe(MonitorCollectors.CollectorConfig(id, body.RootElement));
        MonitorCollectors.ConfigHash(id, body.RootElement).ShouldStartWith("sha256:");

        using var httpOnly = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, otlpGrpc: false));
        var config = MonitorCollectors.CollectorConfig(id, httpOnly.RootElement);

        config.ShouldNotContain("grpc:");
        config.ShouldContain("http:");
        MonitorCollectors.ConfigHash(id, httpOnly.RootElement).ShouldNotBe(MonitorCollectors.ConfigHash(id, body.RootElement));
    }

    // ── The Deployment's wiring ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheDeploymentReadsTheWorkspacesRowAndSecretByReferenceAndNoneIsOptional() {
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var deployment = JsonNode.Parse(MonitorCollectors.DeploymentJson(Address("gateway", "prod"), body.RootElement))!;
        var container = deployment["spec"]!["template"]!["spec"]!["containers"]!.AsArray().Single()!;
        var env = container["env"]!.AsArray().Select(x => x!.AsObject()).ToList();

        env.Select(x => x["name"]!.GetValue<string>())
            .ShouldBe([MonitorWorkspaces.EnvAccountId, MonitorWorkspaces.EnvDatabase, MonitorWorkspaces.EnvIngestKey]);

        env[0]["valueFrom"]!["configMapKeyRef"]!["name"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.RowName("prod"));
        env[0]["valueFrom"]!["configMapKeyRef"]!["key"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.RowKeyAccountId);
        env[1]["valueFrom"]!["configMapKeyRef"]!["key"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.RowKeyDatabase);
        env[2]["valueFrom"]!["secretKeyRef"]!["name"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.KeySecretName("prod"));
        env[2]["valueFrom"]!["secretKeyRef"]!["key"]!.GetValue<string>().ShouldBe(MonitorWorkspaces.IngestKeyField);

        // ⚠ Not optional. An optional reference starts the pod with three empty strings and a
        // collector exporting to /insert//prometheus.
        foreach (var entry in env) {
            entry["valueFrom"]!.AsObject().Single().Value!["optional"].ShouldBeNull($"{entry["name"]} is optional");
        }

        // ⚠ The keys the row is READ under are the keys RowJson WRITES — one constant, two callers.
        var row = JsonNode.Parse(MonitorWorkspaces.RowJson(Address("prod"), body.RootElement))!["data"]!.AsObject();
        row.ContainsKey(MonitorWorkspaces.RowKeyAccountId).ShouldBeTrue();
        row.ContainsKey(MonitorWorkspaces.RowKeyDatabase).ShouldBeTrue();

        container["image"]!.GetValue<string>().ShouldBe(MonitorCollectors.Image);
        container["securityContext"]!["readOnlyRootFilesystem"]!.GetValue<bool>().ShouldBeTrue();
        deployment["spec"]!["template"]!["spec"]!["securityContext"]!["runAsUser"]!.GetValue<int>().ShouldBe(MonitorCollectors.CollectorUid);
        container["volumeMounts"]!.AsArray().Single()!["mountPath"]!.GetValue<string>().ShouldBe(MonitorCollectors.ConfigDirectory);
    }

    [Fact]
    public void TheServiceCarriesOnlyThePortsTheBodyTurnsOn() {
        var id = Address("gateway", "prod");

        using var both = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        Ports(MonitorCollectors.ServiceJson(id, both.RootElement)).ShouldBe([MonitorCollectors.OtlpGrpcPort, MonitorCollectors.OtlpHttpPort]);

        using var httpOnly = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, otlpGrpc: false));
        Ports(MonitorCollectors.ServiceJson(id, httpOnly.RootElement)).ShouldBe([MonitorCollectors.OtlpHttpPort]);

        // And Matches judges the Service on exactly that set — a receiver switched off that left its
        // port behind is a connection refused a tenant reports as an outage.
        MonitorCollectors.Matches(MonitorCollectors.ServiceJson(id, both.RootElement), id, httpOnly.RootElement).ShouldBeFalse();
        MonitorCollectors.Matches(MonitorCollectors.ServiceJson(id, httpOnly.RootElement), id, httpOnly.RootElement).ShouldBeTrue();
    }

    [Fact]
    public void TheEndpointsAreTheServicesAddressAndFollowTheReceivers() {
        var id = Address("gateway", "prod");
        const string ns = "sub-rg";

        using var both = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        MonitorCollectors.OtlpGrpcEndpoint(ns, id, both.RootElement).ShouldBe("collector-prod-gateway.sub-rg.svc:4317");
        MonitorCollectors.OtlpHttpEndpoint(ns, id, both.RootElement).ShouldBe("http://collector-prod-gateway.sub-rg.svc:4318");

        using var grpcOnly = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, otlpHttp: false));
        MonitorCollectors.OtlpHttpEndpoint(ns, id, grpcOnly.RootElement).ShouldBe(string.Empty);
    }

    [Fact]
    public void TwoWorkspacesInOneGroupMayEachHoldACollectorOfTheSameName() {
        // ⚠ AgentPools.ObjectNameOf's argument: the namespace does not distinguish two workspaces in
        // one resource group, so the parent's name is in every object name.
        MonitorCollectors.ObjectNameOf(Address("gateway", "prod")).ShouldBe("collector-prod-gateway");
        MonitorCollectors.ObjectNameOf(Address("gateway", "staging")).ShouldBe("collector-staging-gateway");

        Should.Throw<ArgumentException>(() => MonitorCollectors.ObjectNameOf(Address("orphan")));
    }

    [Fact]
    public void MatchesDispatchesOnKindAndRefusesADocumentItDoesNotKnow() {
        using var body = JsonDocument.Parse(MonitorCollectors.Body(ClusterId));
        var id = Address("gateway", "prod");

        MonitorCollectors.Matches(MonitorCollectors.ConfigMapJson(id, body.RootElement), id, body.RootElement).ShouldBeTrue();
        MonitorCollectors.Matches(MonitorCollectors.DeploymentJson(id, body.RootElement), id, body.RootElement).ShouldBeTrue();
        MonitorCollectors.Matches(MonitorCollectors.ServiceJson(id, body.RootElement), id, body.RootElement).ShouldBeTrue();
        MonitorCollectors.Matches("""{"kind":"Pod","metadata":{"name":"x"}}""", id, body.RootElement).ShouldBeFalse();
        MonitorCollectors.Matches("""{"metadata":{"name":"x"}}""", id, body.RootElement).ShouldBeFalse();

        // A changed replica count is a Deployment that no longer matches — the hash alone would not see it.
        using var two = JsonDocument.Parse(MonitorCollectors.Body(ClusterId, replicas: 2));
        MonitorCollectors.Matches(MonitorCollectors.DeploymentJson(id, body.RootElement), id, two.RootElement).ShouldBeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static ResourceId Address(string name, string? workspace = null) {
        var tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

        return workspace is null
            ? new ResourceId(tenant, subscription, "prod", MonitorWorkspaces.Type, name, Guid.NewGuid())
            : new ResourceId(tenant, subscription, "prod", MonitorCollectors.Type, name, Guid.NewGuid(), workspace);
    }

    static int[] Ports(string serviceJson) =>
        [.. JsonNode.Parse(serviceJson)!["spec"]!["ports"]!.AsArray().Select(x => x!["port"]!.GetValue<int>())];

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
