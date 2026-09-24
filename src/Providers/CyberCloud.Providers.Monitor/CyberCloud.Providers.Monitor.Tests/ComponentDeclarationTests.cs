using CyberCloud.Providers.Monitor.Telemetry;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Registry;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The component declaration, checked the way a silo checks it at start; the connection string
///     against the chart that spells it a second time; and the view handler's refusals, which run before
///     any store is asked.
/// </summary>
/// <remarks>
///     What the views <i>answer</i> is asserted against a real ClickHouse fed by the real collector, in
///     <c>ComponentViewsAgainstClickHouseTests</c>; nothing here pretends a double of the store can say
///     that.
/// </remarks>
public sealed class ComponentDeclarationTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void TheComponentBuildsIntoARegistryTheSiloWouldAccept() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);

        registry.TryGetType(MonitorComponents.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.Chart.ShouldBe(MonitorComponents.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(MonitorComponentReconciler));
        registration.SoftDeleteDays.ShouldBe(0, "the telemetry is the workspace's, and the workspace has the window");
    }

    [Fact]
    public void EveryActionIsSynchronousReadableNotSecretAndHandled() {
        var registry = ProviderRegistry.Build([new MonitorProvider()]);
        registry.TryGetType(MonitorComponents.Type, out var registration).ShouldBeTrue();

        registration.Actions.Select(static x => x.Name)
            .ShouldBe([MonitorComponents.ListConnectionStringAction, .. MonitorComponents.Views], ignoreOrder: true);

        foreach (var action in registration.Actions) {
            action.LongRunning.ShouldBeFalse(action.Name);
            action.Secret.ShouldBeFalse(action.Name);
            action.Permission.ShouldBe(registration.ReadPermission, action.Name);
            action.Response.ShouldNotBeNull(action.Name);
            action.HandlerType.ShouldBe(
                action.Name == MonitorComponents.ListConnectionStringAction
                    ? typeof(MonitorComponentConnectionStringHandler)
                    : typeof(MonitorComponentViewHandler),
                action.Name
            );
        }

        // Every view declares what it takes, so the CLI has flags and the write path refuses a
        // window past the cap before anything reaches the store.
        foreach (var view in MonitorComponents.Views) {
            registration.Actions.Single(x => x.Name == view).Request.ShouldNotBeNull(view);
        }
    }

    [Fact]
    public void EveryDeclaredPropertyIsCoherent() {
        foreach (var schema in new[] {
                     MonitorComponents.Schema2026, MonitorComponents.ListConnectionStringResponse,
                     MonitorComponents.ViewRequest, MonitorComponents.TransactionRequest,
                     MonitorComponents.RequestsResponse, MonitorComponents.DependenciesResponse,
                     MonitorComponents.ExceptionsResponse, MonitorComponents.ApplicationMapResponse,
                     MonitorComponents.TransactionResponse
                 }) {
            foreach (var property in schema.Properties) {
                property.Incoherences().ShouldBeEmpty(property.JsonPointer);
            }
        }
    }

    [Theory]
    [InlineData("""{"timespanMinutes":1441}""")]
    [InlineData("""{"timespanMinutes":0}""")]
    [InlineData("""{"top":101}""")]
    public void AViewRequestPastItsLimitsIsRefusedByTheSchema(string body) {
        using var document = JsonDocument.Parse(body);
        MonitorComponents.ViewRequest.Validate(document.RootElement, allowTags: false).IsFailure.ShouldBeTrue(body);
    }

    [Theory]
    [InlineData("""{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736' OR 1=1 --"}""")]
    [InlineData("""{"traceId":"4BF92F3577B34DA6A3CE929D0E0E4736"}""")]
    [InlineData("""{}""")]
    public void ATraceIdThatIsNotThirtyTwoHexDigitsIsRefusedByTheSchema(string body) {
        using var document = JsonDocument.Parse(body);
        MonitorComponents.TransactionRequest.Validate(document.RootElement, allowTags: false).IsFailure.ShouldBeTrue(body);
    }

    // ── The connection string ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MonitorComponents.HttpProtobuf, 4318)]
    [InlineData(MonitorComponents.Grpc, 4317)]
    public void TheConnectionStringNamesTheCollectorsServiceAndTheComponentsNamespace(string protocol, int port) {
        var id = Address("shop", "prod", TenantA);
        var ns = ReconcileDriver.NamespaceFor(id);
        using var body = JsonDocument.Parse(MonitorComponents.Body(ClusterId, "gateway", protocol));

        MonitorComponents.Endpoint(ns, id, body.RootElement).ShouldBe($"http://collector-prod-gateway.{ns}.svc:{port}");
        MonitorComponents.ResourceAttributes(id).ShouldBe("service.namespace=shop");
        MonitorComponents.ConnectionString(ns, id, body.RootElement)
            .ShouldBe(
                $"OTEL_EXPORTER_OTLP_ENDPOINT=http://collector-prod-gateway.{ns}.svc:{port};"
                + $"OTEL_EXPORTER_OTLP_PROTOCOL={protocol};OTEL_RESOURCE_ATTRIBUTES=service.namespace=shop"
            );

        // ⚠ The collector's address is ITS OWN spelling, not a second copy of it: a component names a
        // collector, and the collector's Service is what that name resolves to.
        var collector = new ResourceId(TenantA, Subscription, "prod", MonitorCollectors.Type, "gateway", Guid.Empty, "prod");
        MonitorComponents.Endpoint(ns, id, body.RootElement).ShouldContain(MonitorCollectors.ServiceHost(ns, collector));
    }

    [Fact]
    public void TheChartSpellsTheEndpointAndTheObjectNameTheWayTheContractDoes() {
        // ⚠ A chart cannot include another chart's helper, so the collector's Service name is spelled
        // again in charts/managed/monitor-component. A drift is a connection string pointing at a
        // Service that does not exist — an SDK that exports into nothing, forever, with no error.
        var helpers = Embedded("monitor-component.helpers.tpl");

        helpers.ShouldContain("""printf "http://collector-%s-%s.%s.svc:%s" .Values.platform.workspace .Values.collector .Values.platform.namespace $port""");
        helpers.ShouldContain("""ternary "4317" "4318" (eq .Values.protocol "grpc")""");
        helpers.ShouldContain("""printf "component-%s-%s" .Values.platform.workspace""");

        MonitorCollectors.ObjectNameOf(
                new(TenantA, Subscription, "prod", MonitorCollectors.Type, "c", Guid.Empty, "w")
            )
            .ShouldBe("collector-w-c");
        MonitorComponents.ObjectNameOf(Address("a", "w", TenantA)).ShouldBe("component-w-a");
    }

    [Fact]
    public async Task ListConnectionStringReachesNothingAndMatchesItsSchema() {
        var id = Address("shop", "prod", TenantA);
        var ns = ReconcileDriver.NamespaceFor(id);
        using var body = JsonDocument.Parse(MonitorComponents.Body(ClusterId));

        var response = JsonNode.Parse(
            (await new MonitorComponentConnectionStringHandler().InvokeAsync(
                Action(id, MonitorComponents.ListConnectionStringAction, body.RootElement, null),
                TestContext.Current.CancellationToken
            )).GetValueOrThrow()
        )!;

        response["configMap"]!.GetValue<string>().ShouldBe("component-prod-shop");
        response["otlpEndpoint"]!.GetValue<string>().ShouldBe($"http://collector-prod-gateway.{ns}.svc:4318");
        MonitorComponents.ListConnectionStringResponse.Validate(response.Deserialize<JsonElement>(), allowTags: false)
            .IsSuccess.ShouldBeTrue();
    }

    // ── The reconciler ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new MonitorComponentReconciler(new FixedClock())).ShouldBeEmpty();

    [Fact]
    public async Task APassAppliesTheConnectionStringAndReadsItBack() {
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MonitorComponents.Body(ClusterId));
        var context = Context(connection, body.RootElement);

        var outcome = await new MonitorComponentReconciler(new FixedClock()).ReconcileAsync(
            context,
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Reason);
        connection.Applied.Select(static x => x.Target.Kind.Kind).ShouldBe(["ConfigMap"]);
        connection.Read.Select(static x => x.Kind.Kind).ShouldBe(["ConfigMap"]);

        foreach (var label in KubeLabels.Mandatory) {
            connection.Applied[0].Labels.ContainsKey(label).ShouldBeTrue(label);
        }

        var stored = JsonNode.Parse(
            connection.Objects[RecordingConnection.Key(MonitorComponents.ConfigMapRef(context.Namespace, context.Id))]
        )!;

        stored["data"]!["OTEL_RESOURCE_ATTRIBUTES"]!.GetValue<string>().ShouldBe("service.namespace=gateway-app");
    }

    [Fact]
    public async Task ASwallowedApplyIsInProgressAndNeverConverged() {
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(MonitorComponents.Body(ClusterId));

        var outcome = await new MonitorComponentReconciler(new FixedClock()).ReconcileAsync(
            Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public void AnEditedEndpointIsDriftAndAnAddedKeyIsNot() {
        var id = Address("shop", "prod", TenantA);
        var ns = ReconcileDriver.NamespaceFor(id);
        using var body = JsonDocument.Parse(MonitorComponents.Body(ClusterId));
        var document = JsonNode.Parse(MonitorComponents.ConfigMapJson(ns, id, body.RootElement))!.AsObject();

        MonitorComponents.Matches(document.ToJsonString(), ns, id, body.RootElement).ShouldBeTrue();

        document["data"]!["TENANTS_OWN"] = "kept";
        MonitorComponents.Matches(document.ToJsonString(), ns, id, body.RootElement)
            .ShouldBeTrue("a key the tenant added beside the three is theirs");

        document["data"]![MonitorComponents.EnvEndpoint] = "http://elsewhere:4318";
        MonitorComponents.Matches(document.ToJsonString(), ns, id, body.RootElement).ShouldBeFalse();
    }

    // ── The view handler's refusals, before any store is asked ────────────────────────────────

    [Fact]
    public async Task AViewWithNoResolvedWorkspaceNeverReachesTheStore() {
        var store = new CountingStore();
        var id = Address("shop", "prod", TenantA);
        using var body = JsonDocument.Parse("{}");

        var refused = await new MonitorComponentViewHandler(store).InvokeAsync(
            Action(id, MonitorComponents.RequestsAction, body.RootElement, null),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        store.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AViewHandedAnotherTenantsWorkspaceNeverReachesTheStore() {
        // ⚠ The manager never does this — step 1 refuses the caller first and the parent is read from
        // the caller's own tenant index. The handler checks anyway, because the database it would read
        // is the one boundary between two tenants' telemetry.
        var store = new CountingStore();
        var id = Address("shop", "prod", TenantA);
        var foreign = new ResourceId(TenantB, Subscription, "prod", MonitorWorkspaces.Type, "prod", Guid.NewGuid());
        using var body = JsonDocument.Parse("{}");

        var refused = await new MonitorComponentViewHandler(store).InvokeAsync(
            Action(id, MonitorComponents.RequestsAction, body.RootElement, foreign),
            TestContext.Current.CancellationToken
        );

        refused.IsFailure.ShouldBeTrue();
        store.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task AViewAsksTheStoreForItsOwnWorkspaceAndBindsEveryValue() {
        var store = new CountingStore();
        var id = Address("shop", "prod", TenantA);
        var workspace = new ResourceId(TenantA, Subscription, "prod", MonitorWorkspaces.Type, "prod", Guid.NewGuid());
        using var body = JsonDocument.Parse("""{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","timespanMinutes":30}""");

        (await new MonitorComponentViewHandler(store).InvokeAsync(
            Action(id, MonitorComponents.TransactionAction, body.RootElement, workspace),
            TestContext.Current.CancellationToken
        )).IsSuccess.ShouldBeTrue();

        store.Queries.Count.ShouldBe(2, "a transaction reads its spans and then its logs");

        foreach (var query in store.Queries) {
            query.Workspace.ShouldBe(workspace);
            query.Parameters["trace"].ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
            query.Parameters["namespace"].ShouldBe("shop");
            query.Parameters["minutes"].ShouldBe("30");

            // ⚠ The statement is the platform's constant: nothing the caller sent is in its text.
            query.Sql.ShouldNotContain("4bf92f3577b34da6a3ce929d0e0e4736");
            query.Sql.ShouldNotContain("ws_");
        }
    }

    [Fact]
    public async Task TheRefusingStoreNamesTheSectionToSet() {
        var workspace = new ResourceId(TenantA, Subscription, "prod", MonitorWorkspaces.Type, "prod", Guid.NewGuid());

        var refused = await new UnavailableTelemetryStore().QueryAsync(
            new(workspace, ComponentViews.RequestsSql, new Dictionary<string, string>()),
            TestContext.Current.CancellationToken
        );

        refused.Error!.Message.ShouldContain(MonitorTelemetryOptions.SectionName);
    }

    [Fact]
    public void ThePlainHttpStoreIsRefusedUnlessTheSectionOptsIn() {
        Should.Throw<ArgumentException>(() => ClickHouseTelemetryStore.ValidatedEndpoint(
                new() { ClickHouseEndpoint = "http://telemetry:8123" }
            )
        );

        ClickHouseTelemetryStore.ValidatedEndpoint(
                new() { ClickHouseEndpoint = "http://telemetry:8123", AllowInsecureTransport = true }
            )
            .Port.ShouldBe(8123);
    }

    [Theory]
    [InlineData("ws_0123456789abcdef0123456789abcdef", true)]
    [InlineData("ws_0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("ws_0123456789abcdef0123456789abcdef; DROP DATABASE x", false)]
    [InlineData("system", false)]
    public void OnlyAWorkspaceDatabasesNameIsAnIdentifierTheSchemaWillWrite(string database, bool accepted) =>
        MonitorTelemetrySchema.IsWorkspaceDatabase(database).ShouldBe(accepted);

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static ResourceId Address(string name, string workspace, Guid tenant) =>
        new(tenant, Subscription, "prod", MonitorComponents.Type, name, Guid.NewGuid(), workspace);

    static ActionContext Action(ResourceId id, string action, JsonElement body, ResourceId? parent) {
        using var desired = JsonDocument.Parse(MonitorComponents.Body(ClusterId));

        return new(
            id,
            MonitorWorkspaces.V2026,
            action,
            body,
            desired.RootElement.Clone(),
            ReconcileDriver.NamespaceFor(id),
            null,
            new InMemorySecretVault()
        ) { Parent = parent };
    }

    static ReconcileContext Context(IKubeClusterConnection connection, JsonElement desired) {
        var id = Address("gateway-app", "prod", TenantA);
        var vault = new InMemorySecretVault();

        return new(
            id,
            MonitorWorkspaces.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(id),
            connection,
            vault,
            new NullLog()
        ) { SecretWriter = vault };
    }

    static string Embedded(string logicalName) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"{logicalName} is not embedded. See the EmbeddedResource items in this project's .csproj."
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Records what it was asked and answers no rows — for the refusals above, which are about whether it is asked.</summary>
    sealed class CountingStore : ITelemetryStore {
        public List<TelemetryQuery> Queries { get; } = [];

        public Task<Result<ImmutableArray<JsonObject>>> QueryAsync(
            TelemetryQuery query,
            CancellationToken cancellationToken = default
        ) {
            Queries.Add(query);
            return Task.FromResult(Result<ImmutableArray<JsonObject>>.Success([]));
        }
    }
}
