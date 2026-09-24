using CyberCloud.ResourceManager.Conformance;
using System.Text.Json;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The two properties of a reconciler that the shared suite — which drives one tenant, once —
///     cannot see, plus the apply order this type's five objects depend on.
/// </summary>
public sealed class MailReconcilerTests {
    [Fact]
    public void TheReconcilerHoldsNoStateAcrossTenants() {
        // ⚠ A reconciler is registered AS A SINGLETON, BY CONCRETE TYPE, so one instance serves every
        // tenant in the process. CheckNoHiddenState reads a field's declared type — including the
        // readonly-field-holding-a-mutable-dictionary shape that used to slip past it.
        //
        // ⚠ The cross-tenant half of this claim is asserted separately, in
        // MailDkimTests.TwoTenantsWithTheSameDomainNameGetDifferentKeys, because a structural check
        // cannot see mixing and only the two together cover it.
        ReconcilerConformance.CheckNoHiddenState(MailHarness.Reconciler())
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task ASecondPassWithTheSameBodyChangesNothing() {
        // Clause 1. ⚠ On this type the interesting half is the Secret, and it is asserted at length
        // in MailDkimTests. What this adds is the other four documents: a pass that re-rendered the
        // ConfigMap differently — because a relay list or a plugin set was ordered by chance — would
        // fight itself forever.
        var vault = new InMemorySecretVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var reconciler = MailHarness.Reconciler();
        var context = MailHarness.Context(connection, body.RootElement, vault);

        await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);

        var first = connection.Applied.Select(static x => x.Target.Name + "\n" + x.Body).ToArray();

        connection.Applied.Clear();

        await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);

        connection.Applied.Select(static x => x.Target.Name + "\n" + x.Body).ShouldBe(first);
    }

    [Fact]
    public async Task TheSecretAndTheConfigMapAreAppliedBeforeTheStatefulSet() {
        // ⚠ THE ORDER IS LOAD-BEARING. The pod mounts both, and a container whose mount is missing
        // sits in CreateContainerConfigError — which this loop never sees, because the StatefulSet
        // itself applies and reads back perfectly. So the symptom of getting this wrong is a
        // converged resource whose pod never starts.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        await MailHarness.Reconciler().ReconcileAsync(
            MailHarness.Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        var order = connection.Applied.Select(static x => x.Target.Kind.Kind).ToList();

        order.IndexOf("Secret").ShouldBeLessThan(order.IndexOf("StatefulSet"));
        order.IndexOf("ConfigMap").ShouldBeLessThan(order.IndexOf("StatefulSet"));

        // The set names the Service as its serviceName, so the Service precedes it too.
        order.IndexOf("Service").ShouldBeLessThan(order.IndexOf("StatefulSet"));
    }

    [Fact]
    public async Task EveryAppliedObjectIsAlsoReadBack() {
        // Clause 4. ⚠ ON A FIVE-OBJECT TYPE THIS IS NOT A FORMALITY: an object applied and never read
        // back is one the loop reports Converged without having observed, and four right ones make
        // the fifth invisible.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        await MailHarness.Reconciler().ReconcileAsync(
            MailHarness.Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        var applied = connection.Applied.Select(static x => RecordingConnection.Key(x.Target))
            .ToHashSet(StringComparer.Ordinal);
        var read = connection.Read.Select(RecordingConnection.Key).ToHashSet(StringComparer.Ordinal);

        applied.ShouldBe(read, true);
    }

    [Fact]
    public async Task NothingReachesTheClusterWhenTheVaultRefusesTheMint() {
        // ⚠ THE ORDERING OF THE TWO FAILURE WINDOWS. A mint that fails must leave the cluster
        // untouched, because the other order gives a StatefulSet referencing a Secret that does not
        // exist — three containers in CreateContainerConfigError. The reverse window is harmless: a
        // vault document with no cluster behind it is inert and the next pass reuses it.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault { RefuseMint = true };
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var outcome = await MailHarness.Reconciler().ReconcileAsync(
            MailHarness.Context(connection, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldNotBe(ReconcileOutcomeKind.Converged);
        connection.Applied.ShouldBeEmpty("the cluster was touched before the vault answered");
    }

    [Fact]
    public async Task ADeleteRemovesTheSecretLastAndLeavesTheMailStoreBehind() {
        // ⚠ REVERSE ORDER, AND THE REASON IS NOT SYMMETRY. Taking the DKIM key from a running signer
        // is the one removal that would make the domain send UNSIGNED mail rather than no mail —
        // which a receiver scores as a forgery, because the domain still publishes a DKIM record.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));
        var context = MailHarness.Context(connection, body.RootElement);
        var reconciler = MailHarness.Reconciler();

        await reconciler.ReconcileAsync(context, TestContext.Current.CancellationToken);
        await reconciler.DeleteAsync(context, TestContext.Current.CancellationToken);

        // ⚠ By name, since the domain has two Secrets: the one holding the key goes last.
        connection.Deleted[^1].Name.ShouldBe(
            MailDomains.CredentialsSecretName("example-com"),
            "the credentials Secret was not removed last"
        );

        // ⚠ And the claim is NOT among them. A PersistentVolumeClaim made from a volumeClaimTemplate
        // has no owner reference to the set, so it survives; RetainedVolumesAsync is what makes that
        // deliberate rather than incidental.
        connection.Deleted.ShouldNotContain(x => x.Kind.Kind == "PersistentVolumeClaim");

        var retained = await reconciler.RetainedVolumesAsync(context, TestContext.Current.CancellationToken);

        retained.GetValueOrThrow().Length.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections. A tenant whose cluster is down has a resource that is
        // still coming, not one that broke.
        var connection = new RecordingConnection { Suspend = true };
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var outcome = await MailHarness.Reconciler().ReconcileAsync(
            MailHarness.Context(connection, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
    }

    [Fact]
    public async Task ADeleteWithNoClusterConvergesRatherThanFailing() {
        // ⚠ The asymmetry with ReconcileAsync is deliberate: a teardown with no cluster to reach has
        // nothing left to remove, and failing would park the resource in Deleting — visible, billed
        // and permanent, for a wiring reason.
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId));

        var outcome = await MailHarness.Reconciler().DeleteAsync(
            MailHarness.Context(null, body.RootElement),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Converged);
    }

    [Fact]
    public async Task TheRenderedServiceIsNeverExternallyAddressable() {
        // ⚠ NO BODY THE SCHEMA ACCEPTS PRODUCES ONE. docs/plan/17 § Topology puts the front doors in
        // shared pools; a tenant-addressable listener here is a second, unauthenticated way into the
        // mail store that bypasses the pool doing the rate limiting and the reputation management,
        // which is the entire product.
        foreach (var sieve in new[] { true, false }) {
            var connection = new RecordingConnection();
            using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId, sieve: sieve));

            await MailHarness.Reconciler().ReconcileAsync(
                MailHarness.Context(connection, body.RootElement),
                TestContext.Current.CancellationToken
            );

            foreach (var command in connection.Applied.Where(static x => x.Target.Kind.Kind == "Service")) {
                command.Body.ShouldNotContain("LoadBalancer", Case.Sensitive);
                command.Body.ShouldNotContain("NodePort", Case.Sensitive);
            }
        }
    }
}
