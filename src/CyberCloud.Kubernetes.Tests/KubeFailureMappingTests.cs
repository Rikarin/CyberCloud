using CyberCloud.Core.Resources;
using CyberCloud.Kubernetes.Apply;
using CyberCloud.Kubernetes.Tests.Infrastructure;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Text;
using System.Text.Json;

namespace CyberCloud.Kubernetes.Tests;

/// <summary>
///     What the API server refuses, and what the platform says about it — against a real k3s.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect this suite pins.</b> <c>KubeApiClient</c> mapped a <c>409</c> and whatever
///         <c>IsTransport</c> recognised, and nothing else. A <c>403</c> from Pod Security, a
///         <c>422</c> from an admission policy, a <c>400</c>, or a <c>404</c> for a kind the cluster
///         does not serve escaped as a raw <c>k8s.Autorest.HttpOperationException</c> — a type Orleans
///         cannot serialise, so it crossed the connection grain's boundary and reached the caller as
///         <c>CodecNotFoundException</c>, naming no status code and no cause. A provider's
///         cluster-backed suite against a bare k3s went red exactly that way.
///     </para>
///     <para>
///         <b>Real k3s and not a fake, for the same reason <c>ServerSideApplyTests</c> is.</b> Every
///         shape asserted below is the API server's, not ours: which status an admission decision
///         arrives under, whether the body is a <c>v1.Status</c> at all, and what <c>details</c>
///         carries. Two of those turned out to contradict what the API conventions imply — an
///         unserved group answers with the bare text <c>404 page not found</c>, and a body that will
///         not type-check answers <c>500</c> — and a fake would have asserted the belief instead of
///         the behaviour.
///     </para>
///     <para>
///         The suite is also the leak check. docs/plan/10 sends a failed <c>Result</c>'s message
///         straight to the tenant (<c>OperationStatus.Error</c>, <c>ResourceSnapshot.LastFailure</c>)
///         with no redaction on the way, and an API server's message can name the platform's service
///         account, its namespaces and its hosts. So every refusal is asserted twice: once for what
///         the operator's log has to contain, and once for what the tenant's message must not.
///     </para>
/// </remarks>
[Collection(K3sSuite.Name)]
public sealed class KubeFailureMappingTests(K3sFixture k3s) : IAsyncLifetime {
    /// <summary>A namespace whose Pod Security level refuses an ordinary pod — docs/plan/09's "restrictive PSA".</summary>
    const string RestrictedNamespace = "cc-restricted";

    /// <summary>A kind no cluster serves until its CRD is installed.</summary>
    static readonly GroupVersionKind Widgets =
        new() { Group = "widgets.example.io", Version = "v1", Kind = "Widget", Plural = "widgets" };

    /// <summary>A kind that stays unserved for the whole suite.</summary>
    static readonly GroupVersionKind Gadgets =
        new() { Group = "gadgets.example.io", Version = "v1", Kind = "Gadget", Plural = "gadgets" };

    static readonly GroupVersionKind Deployments =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    static readonly GroupVersionKind Pods =
        new() { Group = "", Version = "v1", Kind = "Pod", Plural = "pods" };

    readonly CapturingLogger log = new();

    KubeApiClient api = null!;
    KubeApiClient unprivileged = null!;
    k8s.Kubernetes unprivilegedClient = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        // The client under test, with somewhere for the operator's half of a refusal to go.
        api = new KubeApiClient(k3s.Raw, K3sFixture.ClusterId, new TestClock(), false, log);

        await EnsureRestrictedNamespaceAsync(token);

        // ⚠ A service account with no role binding at all — the shape a BYO cluster has when the
        // kubeconfig it handed us was scoped too narrowly, which is the common way this goes wrong.
        await EnsureServiceAccountAsync(token);

        var minted = await k3s.Raw.CoreV1.CreateNamespacedServiceAccountTokenAsync(
            new() { Spec = new() { ExpirationSeconds = 3600 } },
            "cc-nobody",
            "default",
            cancellationToken: token
        );

        using var yaml = new MemoryStream(Encoding.UTF8.GetBytes(k3s.Kubeconfig));
        var config = await KubernetesClientConfiguration.BuildConfigFromConfigFileAsync(yaml);
        config.ClientCertificateData = null;
        config.ClientCertificateKeyData = null;
        config.ClientCertificateFilePath = null;
        config.ClientKeyFilePath = null;
        config.AccessToken = minted.Status.Token;

        unprivilegedClient = new k8s.Kubernetes(config);
        unprivileged = new KubeApiClient(unprivilegedClient, K3sFixture.ClusterId, new TestClock(), false, log);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() {
        api.Dispose();
        unprivileged.Dispose();
        unprivilegedClient.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── (c) One status, two meanings ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnApplyOfAKindTheClusterDoesNotServeNamesTheKindRatherThanThrowing() {
        // ⚠ THE REPRODUCTION. This is the call that used to leave a raw HttpOperationException in
        // flight, and the one that reached a provider's suite as CodecNotFoundException.
        var token = TestContext.Current.CancellationToken;

        var outcome = await api.ApplyAsync(
            Command("no-such-kind", Gadgets, """{ "metadata": { "name": "no-such-kind" } }"""),
            token
        );

        outcome.IsFailure.ShouldBeTrue("an unserved kind must be a Result, not an exception.");
        outcome.Error!.Code.ShouldBe(
            ErrorCode.InvalidResourceType,
            "\"this cluster does not serve this kind\" is a permanent misconfiguration, not a "
            + "missing object."
        );

        // docs/plan/09 § The platform's own cluster: a failure here has to name "a missing custom
        // resource rather than a missing CRD, which is the version of this bug that costs an
        // afternoon". The kind is what the operator has to go and install.
        outcome.Error.Message.ShouldContain("gadgets.example.io/v1");
        outcome.Error.Message.ShouldContain("Gadget");
        outcome.Error.Message.ShouldContain("gadgets");
    }

    [Fact]
    public async Task AReadOfAKindTheClusterDoesNotServeIsNotReportedAsAMissingObject() {
        // ⚠ The read half, and the reason the apply above could not simply be retried. ApplyAsync
        // reads before it writes and treats ResourceNotFound as "go ahead and create". An unserved
        // kind answering ResourceNotFound therefore told the writer to create an object of a kind
        // that cannot exist — the 404 laundered into a create.
        var outcome = await api.GetAsync(
            new() { Kind = Gadgets, Namespace = K3sFixture.Namespace, Name = "no-such-kind" },
            TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
        outcome.Error.Code.ShouldNotBe(ErrorCode.ResourceNotFound);
    }

    [Fact]
    public async Task ADeleteOfAKindTheClusterDoesNotServeIsNotAConvergedTeardown() {
        // ⚠ The worst of the three, because it was silent. DeleteAsync accepted every 404 as "the
        // object is already gone" (docs/plan/06 § Two-phase create's idempotent delete), so a
        // teardown against a cluster missing the CRD reported Success and the resource left
        // `Deleting` as converged — a delete that never happened, recorded as one that did.
        var outcome = await api.DeleteAsync(
            new() { Kind = Gadgets, Namespace = K3sFixture.Namespace, Name = "no-such-kind" },
            CascadePolicy.Background,
            TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue(
            "a delete that could not have happened must not report the desired end state."
        );
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
    }

    [Fact]
    public async Task AnAbsentObjectOfAServedKindIsStillResourceNotFound() {
        // The other side of the discriminator, and the control that keeps the three tests above from
        // passing by classifying every 404 as a misconfiguration. Both a built-in kind and a custom
        // one, because the interesting case is a CRD that IS served — the shape a provider hits on
        // every create.
        var token = TestContext.Current.CancellationToken;

        (await api.GetAsync(
            new() { Kind = Deployments, Namespace = K3sFixture.Namespace, Name = "no-such-thing" },
            token
        )).Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        await EnsureWidgetCrdAsync(token);

        (await api.GetAsync(
            new() { Kind = Widgets, Namespace = K3sFixture.Namespace, Name = "no-such-thing" },
            token
        )).Error!.Code.ShouldBe(
            ErrorCode.ResourceNotFound,
            "a served CRD whose object is absent is an ordinary missing object."
        );

        // And the delete of an absent object still converges, which is the behaviour
        // ADeleteOfAKindTheClusterDoesNotServeIsNotAConvergedTeardown must not have broken.
        (await api.DeleteAsync(
            new() { Kind = Widgets, Namespace = K3sFixture.Namespace, Name = "no-such-thing" },
            CascadePolicy.Background,
            token
        )).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task AnUnservedVersionOfAServedGroupIsAlsoAMisconfiguration() {
        // ⚠ Measured, and it is a THIRD 404 shape: apps/v9 answers with a v1.Status whose `details`
        // is empty and whose message is the generic "the server could not find the requested
        // resource", where an unserved GROUP answers with the bare text "404 page not found" and no
        // JSON at all. Reading details.kind to tell these apart — the obvious approach — reads a
        // field that is absent in both. details.name is the only signal that survives all three.
        var outcome = await api.GetAsync(
            new() {
                Kind = Deployments with { Version = "v9" },
                Namespace = K3sFixture.Namespace,
                Name = "anything"
            },
            TestContext.Current.CancellationToken
        );

        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
    }

    // ── (a) The reason survives ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAdmissionPolicysRejectionCarriesItsOwnMessageToTheTenant() {
        // ⚠ THE CASE docs/plan/09's "Hostile BYO" LAYER EXISTS FOR, and the single most likely
        // failure on a customer's own cluster. The policy's message names which object and which
        // rule; a mapping that produced a bare code and dropped the text would have moved the
        // problem rather than fixed it.
        var token = TestContext.Current.CancellationToken;
        await EnsureRejectingPolicyAsync(token);

        log.Clear();

        var outcome = await api.ApplyAsync(
            Command("cc-refused-x", Deployments, DeploymentJson("cc-refused-x")),
            token
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);

        // The API server's own words, verbatim, because they are the whole diagnostic.
        outcome.Error.Message.ShouldContain("cc-refused-*");
        outcome.Error.Message.ShouldContain("refused by the cluster policy for tenant workloads");
        outcome.Error.Message.ShouldContain("ValidatingAdmissionPolicy");

        // …and it says whose decision it was, so a tenant does not read it as a platform bug.
        outcome.Error.Message.ShouldContain("not a fault in the platform");

        // The operator gets the status and the causes too, which the tenant's copy leaves out.
        log.OnlyLine().ShouldContain("HTTP 422");
        log.OnlyLine().ShouldContain("Causes:");
    }

    [Fact]
    public async Task APodSecurityRefusalCarriesItsOwnMessageToTheTenant() {
        // The 403 shape of the same thing. docs/plan/09 § Testing the fabric names "a restrictive
        // PSA" in the hostile-BYO layer, and Pod Security is an admission plugin, so its refusal is
        // the tenant's cluster policy talking about the tenant's object.
        var token = TestContext.Current.CancellationToken;
        log.Clear();

        var outcome = await api.ApplyAsync(
            Command(
                "cc-psa",
                Pods,
                """
                { "metadata": { "name": "cc-psa" },
                  "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] } }
                """,
                RestrictedNamespace
            ),
            token
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(
            ErrorCode.PolicyViolation,
            "an admission refusal is the cluster's policy, not the platform's credentials."
        );

        outcome.Error.Message.ShouldContain("violates PodSecurity");
        outcome.Error.Message.ShouldContain("runAsNonRoot");
        log.OnlyLine().ShouldContain("HTTP 403");
    }

    // ── (d) What must not cross to a tenant ────────────────────────────────────────────────────

    [Fact]
    public async Task AnRbacRefusalNamesNothingInternalToTheTenantAndEverythingToTheOperator() {
        // ⚠ THE ASYMMETRY. Both of these are 403 with reason "Forbidden" and identical `details`,
        // and only the prose separates them:
        //
        //   admission → "… is forbidden: violates PodSecurity \"restricted:latest\": …"
        //   RBAC      → "… is forbidden: User \"system:serviceaccount:default:cc-nobody\" cannot …"
        //
        // The second names the PLATFORM's identity, in the platform's own namespace. docs/plan/08
        // § Errors — "No exception details, ever … the details go to the trace" — is the licence for
        // sending that one to the log and giving the tenant a fixed sentence instead.
        var token = TestContext.Current.CancellationToken;
        log.Clear();

        var outcome = await unprivileged.ApplyAsync(
            Command("cc-403", Deployments, DeploymentJson("cc-403")),
            token
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);

        // The operator's copy is complete — it is the only place this text exists.
        var operatorLine = log.OnlyLine();
        operatorLine.ShouldContain("system:serviceaccount:default:cc-nobody");
        operatorLine.ShouldContain("HTTP 403");

        // ⚠ And the tenant's copy names nothing internal. Same shape as
        // ManagedIdentityTests.ATokenFromAnUntrustedIssuerIsRefusedAndTheRefusalNamesNothing.
        foreach (var leak in new[] {
                     "system:serviceaccount", "cc-nobody", "serviceaccount", "User \"",
                     "cannot get", "cannot patch", "RBAC", "clusterrole", "rolebinding"
                 }) {
            outcome.Error.Message.ShouldNotContain(leak, Case.Insensitive);
        }

        // It still tells the tenant enough to raise a ticket about the right thing.
        outcome.Error.Message.ShouldContain("not permitted");
        outcome.Error.Message.ShouldContain("operator");
    }

    [Fact]
    public async Task AMalformedBodyIsOurFaultAndSaysSoWithoutQuotingOurSerializer() {
        // ⚠ MEASURED, AND IT REFUTES THE OBVIOUS READING. An apply whose body will not type-check
        // answers 500 — "failed to create typed patch object (…): .spec.replicas: expected numeric
        // (int or float), got string" — not 400. Under a blanket "5xx is transport" rule that
        // reported as "Cluster … did not answer", which is precisely what IsTransport's own remarks
        // promise cannot happen to a malformed object.
        var token = TestContext.Current.CancellationToken;
        log.Clear();

        var outcome = await api.ApplyAsync(
            Command(
                "cc-bad-body",
                Deployments,
                """
                { "metadata": { "name": "cc-bad-body" },
                  "spec": { "replicas": "lots",
                    "selector": { "matchLabels": { "app": "cc-bad-body" } },
                    "template": { "metadata": { "labels": { "app": "cc-bad-body" } },
                      "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] } } } }
                """
            ),
            token
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Message.ShouldNotContain("did not answer");
        outcome.Error.Message.ShouldContain("fault in the platform");

        // Our serializer's complaint is an implementation detail; the operator gets it, not the
        // tenant.
        outcome.Error.Message.ShouldNotContain("typed patch", Case.Insensitive);
        log.OnlyLine().ShouldContain(KubeFailures.TypedPatchFailurePrefix);
    }

    [Fact]
    public async Task ASchemaInvalidBodyIsAlsoTheTenantsToFixAndKeepsTheFieldPath() {
        // 422 with reason "Invalid" — the API server's own validation rather than a policy. The
        // causes name the field, which is the part a tenant can act on.
        var token = TestContext.Current.CancellationToken;

        var outcome = await api.ApplyAsync(
            Command(
                "cc-invalid",
                Deployments,
                """
                { "metadata": { "name": "cc-invalid" },
                  "spec": { "replicas": 1,
                    "selector": { "matchLabels": { "app": "cc-invalid" } },
                    "template": { "metadata": { "labels": { "app": "somethingelse" } },
                      "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] } } } }
                """
            ),
            token
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        outcome.Error.Message.ShouldContain("selector` does not match template");
    }

    // ── (b) None of it looks like an unreachable cluster ───────────────────────────────────────

    [Fact]
    public async Task NoRefusalIsReportedAsAnUnreachableCluster() {
        // ⚠ THE RETRY DISPOSITION, asserted where it is decided. "Cluster … did not answer" is the
        // one message that means Degraded, and Degraded means suspended-and-rescheduled forever
        // (docs/plan/09 § Cluster connections). Any refusal misfiled as unreachable is a hot loop
        // against somebody's API server, so every one of them is checked here in one place.
        var token = TestContext.Current.CancellationToken;
        await EnsureRejectingPolicyAsync(token);

        Result[] refusals = [
            (await api.ApplyAsync(Command("cc-refused-y", Deployments, DeploymentJson("cc-refused-y")), token))
            .ToResult(),
            (await api.ApplyAsync(
                Command(
                    "cc-psa2",
                    Pods,
                    """
                    { "metadata": { "name": "cc-psa2" },
                      "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] } }
                    """,
                    RestrictedNamespace
                ),
                token
            )).ToResult(),
            (await unprivileged.GetAsync(
                new() { Kind = Deployments, Namespace = K3sFixture.Namespace, Name = "cc-403" },
                token
            )).ToResult(),
            (await api.GetAsync(
                new() { Kind = Gadgets, Namespace = K3sFixture.Namespace, Name = "x" },
                token
            )).ToResult(),
            await api.DeleteAsync(
                new() { Kind = Gadgets, Namespace = K3sFixture.Namespace, Name = "x" },
                CascadePolicy.Background,
                token
            )
        ];

        foreach (var refusal in refusals) {
            refusal.IsFailure.ShouldBeTrue();
            refusal.Error!.Code.ShouldNotBe(
                ErrorCode.InternalError,
                $"'{refusal.Error.Message}' is the cluster answering, not the cluster going quiet."
            );

            KubeFailures.MeansTheClusterAnswered(refusal.Error.Code).ShouldBeTrue(
                $"{refusal.Error.Code} must keep the cluster healthy — see "
                + "ClusterConnectionGrain.Answered."
            );

            refusal.Error.Message.ShouldNotContain("did not answer");
        }
    }

    [Fact]
    public async Task ARateLimitStaysTransportAndIsNotTurnedIntoATerminalFailure() {
        // The mirror-image mistake, guarded at the mapping rather than observed — 429 and 408 are
        // 4xx that IsTransport already calls transport, and classifying them would make a rate limit
        // terminal. Asserted through the predicate because provoking a real 429 out of k3s is not
        // something a test can do reliably.
        await Task.CompletedTask;

        KubeFailures.MeansTheClusterAnswered(ErrorCode.InternalError).ShouldBeFalse(
            "InternalError is what Unreachable carries, and it is the one code that degrades a "
            + "cluster."
        );
    }

    // ── The sweep ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingTheApiServerRefusesEscapesAsAnException() {
        // ⚠ The property the whole suite is about, stated once. Every call below threw a raw
        // k8s.Autorest.HttpOperationException before this change; an exception escaping any of them
        // is a CodecNotFoundException at the far side of a grain call.
        var token = TestContext.Current.CancellationToken;
        await EnsureRejectingPolicyAsync(token);

        Func<Task>[] calls = [
            () => api.ApplyAsync(Command("cc-sweep-1", Gadgets, """{ "metadata": { "name": "cc-sweep-1" } }"""), token),
            () => api.ApplyAsync(Command("cc-refused-z", Deployments, DeploymentJson("cc-refused-z")), token),
            () => api.GetAsync(new() { Kind = Gadgets, Namespace = K3sFixture.Namespace, Name = "x" }, token),
            () => api.DeleteAsync(
                new() { Kind = Gadgets, Namespace = K3sFixture.Namespace, Name = "x" },
                CascadePolicy.Background,
                token
            ),
            () => api.ListAsync(Gadgets, K3sFixture.Namespace, string.Empty, cancellationToken: token),
            () => unprivileged.ApplyAsync(Command("cc-sweep-2", Deployments, DeploymentJson("cc-sweep-2")), token),
            () => unprivileged.ListAsync(Deployments, K3sFixture.Namespace, string.Empty, cancellationToken: token)
        ];

        foreach (var call in calls) {
            await Should.NotThrowAsync(call);
        }
    }

    [Fact]
    public async Task AListOfAnUnservedKindNamesTheKindRatherThanThrowing() {
        // The informer's version of the same defect: SharedInformer.EstablishAsync goes through
        // ListAsync, so an unserved kind used to throw out of a grain call there too.
        var outcome = await api.ListAsync(
            Gadgets,
            K3sFixture.Namespace,
            string.Empty,
            cancellationToken: TestContext.Current.CancellationToken
        );

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Code.ShouldBe(ErrorCode.InvalidResourceType);
        outcome.Error.Message.ShouldContain("gadgets.example.io/v1");
    }

    // ── Fixture plumbing ───────────────────────────────────────────────────────────────────────

    async Task EnsureRestrictedNamespaceAsync(CancellationToken token) {
        var metadata = new V1ObjectMeta {
            Name = RestrictedNamespace,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal) {
                ["pod-security.kubernetes.io/enforce"] = "restricted",
                ["pod-security.kubernetes.io/enforce-version"] = "latest"
            }
        };

        try {
            await k3s.Raw.CoreV1.CreateNamespaceAsync(new() { Metadata = metadata }, cancellationToken: token);
        } catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict) {
            // Already there from an earlier test in the collection.
        }
    }

    async Task EnsureServiceAccountAsync(CancellationToken token) {
        try {
            await k3s.Raw.CoreV1.CreateNamespacedServiceAccountAsync(
                new() { Metadata = new() { Name = "cc-nobody" } },
                "default",
                cancellationToken: token
            );
        } catch (k8s.Autorest.HttpOperationException ex)
            when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict) {
            // Already there.
        }
    }

    async Task EnsureWidgetCrdAsync(CancellationToken token) {
        await ApplyAsClusterAdminAsync(
            "apiextensions.k8s.io",
            "v1",
            "customresourcedefinitions",
            "widgets.widgets.example.io",
            """
            { "apiVersion": "apiextensions.k8s.io/v1", "kind": "CustomResourceDefinition",
              "metadata": { "name": "widgets.widgets.example.io" },
              "spec": { "group": "widgets.example.io", "scope": "Namespaced",
                "names": { "plural": "widgets", "singular": "widget", "kind": "Widget" },
                "versions": [ { "name": "v1", "served": true, "storage": true,
                  "schema": { "openAPIV3Schema": { "type": "object",
                    "properties": { "spec": { "type": "object",
                      "properties": { "size": { "type": "integer" } } } } } } } ] } }
            """,
            token
        );

        // ⚠ Polled rather than slept. A CRD is not served the instant it is accepted — the discovery
        // cache has to pick it up — and a fixed sleep is either flaky or slow. The wait ends when
        // the API server answers the way a SERVED kind does: a missing object rather than a missing
        // kind.
        await WaitUntilAsync(
            async () => (await api.GetAsync(
                new() { Kind = Widgets, Namespace = K3sFixture.Namespace, Name = "probe" },
                token
            )).Error!.Code == ErrorCode.ResourceNotFound,
            "the widgets CRD never became served",
            token
        );
    }

    async Task EnsureRejectingPolicyAsync(CancellationToken token) {
        // ⚠ A ValidatingAdmissionPolicy rather than a webhook, and the difference does not matter to
        // the code under test. A webhook needs a TLS-serving pod inside the cluster; the in-tree
        // policy produces the same admission refusal through the same admission chain, and its
        // 422/Invalid shape is asserted rather than assumed. A webhook's own denial arrives under
        // whatever status the webhook sets — 403 by default — which is the arm
        // APodSecurityRefusalCarriesItsOwnMessageToTheTenant covers.
        await ApplyAsClusterAdminAsync(
            "admissionregistration.k8s.io",
            "v1",
            "validatingadmissionpolicies",
            "cc-refuse-policy",
            """
            { "apiVersion": "admissionregistration.k8s.io/v1", "kind": "ValidatingAdmissionPolicy",
              "metadata": { "name": "cc-refuse-policy" },
              "spec": {
                "failurePolicy": "Fail",
                "matchConstraints": { "resourceRules": [ {
                  "apiGroups": ["apps"], "apiVersions": ["v1"],
                  "operations": ["CREATE","UPDATE"], "resources": ["deployments"] } ] },
                "validations": [ {
                  "expression": "!has(object.metadata.name) || !object.metadata.name.startsWith('cc-refused-')",
                  "message": "deployments named cc-refused-* are refused by the cluster policy for tenant workloads" } ] } }
            """,
            token
        );

        await ApplyAsClusterAdminAsync(
            "admissionregistration.k8s.io",
            "v1",
            "validatingadmissionpolicybindings",
            "cc-refuse-policy-binding",
            """
            { "apiVersion": "admissionregistration.k8s.io/v1", "kind": "ValidatingAdmissionPolicyBinding",
              "metadata": { "name": "cc-refuse-policy-binding" },
              "spec": { "policyName": "cc-refuse-policy", "validationActions": ["Deny"] } }
            """,
            token
        );

        // Same reasoning as the CRD: polled until the policy actually refuses.
        await WaitUntilAsync(
            async () => (await api.ApplyAsync(
                Command("cc-refused-probe", Deployments, DeploymentJson("cc-refused-probe")),
                token
            )).IsFailure,
            "the admission policy never took effect",
            token
        );
    }

    async Task ApplyAsClusterAdminAsync(
        string group,
        string version,
        string plural,
        string name,
        string json,
        CancellationToken token
    ) {
        using var response = await k3s.Raw.CustomObjects.PatchClusterCustomObjectWithHttpMessagesAsync(
            new V1Patch(JsonSerializer.Deserialize<JsonElement>(json), V1Patch.PatchType.ApplyPatch),
            group,
            version,
            plural,
            name,
            fieldManager: "cc-test",
            force: true,
            cancellationToken: token
        );
    }

    static async Task WaitUntilAsync(Func<Task<bool>> condition, string whatFailed, CancellationToken token) {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);

        while (DateTimeOffset.UtcNow < deadline) {
            if (await condition()) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }

        throw new InvalidOperationException(
            $"{whatFailed} within 60 s, so this test cannot say anything about the mapping."
        );
    }

    static ResourceId Resource(string name) =>
        new(
            Guid.Parse("9f2c1b7e-3d4a-4f21-9c6b-0a1e2d3c4b5a"),
            Guid.Parse("77de4a10-1b2c-4d3e-8f90-a1b2c3d4e5f6"),
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            name,
            K3sFixture.ClusterId
        );

    static string DeploymentJson(string name) =>
        $$"""
          { "metadata": { "name": "{{name}}" },
            "spec": { "replicas": 1,
              "selector": { "matchLabels": { "app": "{{name}}" } },
              "template": { "metadata": { "labels": { "app": "{{name}}" } },
                "spec": { "containers": [ { "name": "c", "image": "registry.k8s.io/pause:3.10" } ] } } } }
          """;

    static KubeCommand Command(string name, GroupVersionKind kind, string json, string? ns = null) {
        var id = Resource(name);

        return KubeCommand.For(new UnusedConnection())
            .WithTenantId(id.TenantId)
            .WithResourceId(id)
            .WithKind(kind)
            .InNamespace(ns ?? K3sFixture.Namespace)
            .WithFieldManager("cybercloud/cybercloud.dbforpostgresql")
            .ObjectJson(json)
            .Build();
    }
}

/// <summary>
///     An <see cref="ILogger" /> that keeps what it was told, so a test can assert on the operator's
///     half of a refusal.
/// </summary>
/// <remarks>
///     ⚠ Instance state rather than <c>LogCapture</c>'s static bag: these tests assert that
///     <b>exactly one</b> line was written per refusal, which a bag shared with a whole silo cannot
///     support.
/// </remarks>
public sealed class CapturingLogger : ILogger {
    readonly List<string> lines = [];

    /// <summary>The one line written since the last <see cref="Clear" />.</summary>
    /// <exception cref="InvalidOperationException">None, or more than one, was written.</exception>
    public string OnlyLine() {
        lock (lines) {
            return lines.Count == 1
                ? lines[0]
                : throw new InvalidOperationException(
                    $"Expected exactly one log line, got {lines.Count}: {string.Join(" | ", lines)}"
                );
        }
    }

    /// <summary>Forgets everything written so far.</summary>
    public void Clear() {
        lock (lines) {
            lines.Clear();
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (lines) {
            lines.Add(formatter(state, exception));
        }
    }
}
