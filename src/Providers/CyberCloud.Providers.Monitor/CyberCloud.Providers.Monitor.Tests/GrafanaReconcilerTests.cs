using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Tests;

/// <summary>
///     The managed Grafana reconciler against a connection that misbehaves in the ways a real cluster
///     does, and a vault that mints once.
/// </summary>
public sealed class GrafanaReconcilerTests {
    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid OtherTenant = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid Subscription = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void TheReconcilerHoldsNoMutableState() =>
        ReconcilerConformance.CheckNoHiddenState(new GrafanaReconciler(new FixedClock())).ShouldBeEmpty();

    [Fact]
    public async Task APassMintsOnceAppliesFourObjectsInOrderAndReadsAllFourBack() {
        var reconciler = new GrafanaReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry")));
        var context = Context(connection, body.RootElement, vault);

        var first = await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);
        first.Kind.ShouldBe(ReconcileOutcomeKind.Converged, first.Reason);

        // Credential, provisioning, Grafana, address — the pod mounts the first two.
        connection.Applied.Select(static x => x.Target.Kind.Kind)
            .ShouldBe(["Secret", "ConfigMap", "Deployment", "Service"]);
        connection.Read.Select(static x => x.Kind.Kind).ShouldBe(["Secret", "ConfigMap", "Deployment", "Service"]);

        // ⚠ MINT-ONCE, OBSERVED ON THE RENDERED SECRET. A second pass renders the same document
        // byte for byte because what reaches it is what the vault returns, not what the generator
        // produced — clause 1 over a generator that is not idempotent.
        var second = await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);
        second.Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var secrets = connection.Applied.Where(static x => x.Target.Kind.Kind == "Secret")
            .Select(static x => x.Body)
            .ToList();
        secrets.Count.ShouldBe(2);
        secrets[0].ShouldBe(secrets[1]);

        var password = (await vault.ResolveAsync(
                Grafanas.AdminPasswordRef(context.Id),
                TestContext.Current.CancellationToken
            )).GetValueOrThrow();
        password.Length.ShouldBe(32);
        JsonNode.Parse(secrets[0])!["data"]![Grafanas.AdminPasswordField]!
            .GetValue<string>()
            .ShouldBe(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password)));

        // And the url action hands back that same password, the in-cluster URL, and the workspace.
        var handler = new GrafanaUrlHandler();
        var action = new ActionContext(
            context.Id,
            Grafanas.V2026,
            Grafanas.UrlAction,
            body.RootElement,
            body.RootElement,
            context.Namespace,
            null,
            vault
        );
        var response = JsonNode.Parse(
            (await handler.InvokeAsync(action, TestContext.Current.CancellationToken)).GetValueOrThrow()
        )!;

        response["adminPassword"]!.GetValue<string>().ShouldBe(password);
        response["adminUser"]!.GetValue<string>().ShouldBe(Grafanas.AdminUser);
        response["url"]!.GetValue<string>().ShouldBe($"http://grafana-team.{context.Namespace}.svc:3000");
        Grafanas.UrlResponse.Validate(response.Deserialize<JsonElement>(), allowTags: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AWorkspaceInAnotherTenantIsRefusedBeforeAnythingIsMintedOrApplied() {
        var reconciler = new GrafanaReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry", OtherTenant)));

        var outcome = await reconciler.ReconcileAsync(
            Context(connection, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        // ⚠ Failed, terminal: a Grafana pointed at another tenant's workspace does not start working
        // later, and a retry every ten seconds for an hour is what the default would do.
        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Target.ShouldBe(Grafanas.WorkspacePointer);
        outcome.Error.Message.ShouldContain("belongs to tenant");

        connection.Applied.ShouldBeEmpty();
        (await vault.ResolveAsync(
                Grafanas.AdminPasswordRef(Context(connection, body.RootElement, vault).Id),
                TestContext.Current.CancellationToken
            ))
            .IsFailure.ShouldBeTrue("a refused body minted a credential");
    }

    [Fact]
    public async Task AWorkspaceInAnotherResourceGroupIsRefusedByName() {
        var reconciler = new GrafanaReconciler(new FixedClock());
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry", resourceGroup: "other")));

        var outcome = await reconciler.ReconcileAsync(
            Context(new RecordingConnection(), body.RootElement, new InMemorySecretVault()),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Message.ShouldContain("share a resource group");
    }

    [Fact]
    public async Task ASwallowedApplyIsInProgressAndNeverConverged() {
        var reconciler = new GrafanaReconciler(new FixedClock());
        var connection = new RecordingConnection { SwallowApplies = true };
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry")));

        var outcome = await reconciler.ReconcileAsync(
            Context(connection, body.RootElement, new InMemorySecretVault()),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("not readable back yet");
    }

    [Fact]
    public async Task ADeleteRemovesTheAddressFirstAndLeavesTheVaultEntry() {
        var reconciler = new GrafanaReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var body = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry")));
        var context = Context(connection, body.RootElement, vault);

        (await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken)).Kind.ShouldBe(
            ReconcileOutcomeKind.Converged
        );
        connection.Objects.Count.ShouldBe(4);

        var outcome = await reconciler.DeleteAsync(context, TestContext.Current.CancellationToken);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged, outcome.Reason);
        connection.Objects.ShouldBeEmpty();
        connection.Deleted.Select(static x => x.Kind.Kind).ShouldBe(["Service", "Deployment", "ConfigMap", "Secret"]);

        // ⚠ ISecretWriter mints and does not delete: a teardown that failed halfway and is retried
        // must not hand the next pass a different credential from the one already rendered.
        (await vault.ResolveAsync(
                Grafanas.AdminPasswordRef(context.Id),
                TestContext.Current.CancellationToken
            )).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task FlippingAnonymousViewersRollsTheInstanceAndMatchesJudgesIt() {
        var reconciler = new GrafanaReconciler(new FixedClock());
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();

        using var closed = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry")));
        using var open = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry"), true));

        (await reconciler.ReconcileAsync(
                Context(connection, closed.RootElement, vault),
                TestContext.Current.CancellationToken
            )).Kind.ShouldBe(ReconcileOutcomeKind.Converged);

        var deployment = connection.Objects[RecordingConnection.Key(
            Grafanas.DeploymentRef(ReconcileDriver.NamespaceFor(Address()), "team")
        )];
        Grafanas.Matches(deployment, "telemetry", closed.RootElement).ShouldBeTrue();
        Grafanas.Matches(deployment, "telemetry", open.RootElement)
            .ShouldBeFalse("a Deployment with anonymous access off matched a body asking for it on");

        // ⚠ A changed preset is a drift too: the limits are what the vCPU and memory meters bill for,
        // and the review of #32 noted the observer could not see them move.
        using var large = JsonDocument.Parse(Grafanas.Body(ClusterId, Workspace("telemetry"), preset: "c1.large"));
        Grafanas.Matches(deployment, "telemetry", large.RootElement)
            .ShouldBeFalse("a c1.small Deployment matched a body asking for c1.large");

        (await reconciler.ReconcileAsync(
                Context(connection, open.RootElement, vault),
                TestContext.Current.CancellationToken
            )).Kind.ShouldBe(ReconcileOutcomeKind.Converged);
        Grafanas.Matches(
            connection.Objects[RecordingConnection.Key(
                Grafanas.DeploymentRef(ReconcileDriver.NamespaceFor(Address()), "team")
            )],
            "telemetry",
            open.RootElement
        )
            .ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid InstanceId = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static ResourceId Address() => new(Tenant, Subscription, "prod", Grafanas.Type, "team", InstanceId);

    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        InMemorySecretVault vault
    ) {
        var id = Address();
        return new(
            id,
            Grafanas.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(id),
            connection,
            vault,
            new NullLog()
        ) { SecretWriter = vault };
    }

    static string Workspace(string name, Guid? tenant = null, string resourceGroup = "prod") =>
        new ResourceId(tenant ?? Tenant, Subscription, resourceGroup, MonitorWorkspaces.Type, name, Guid.Empty).Path;
}
