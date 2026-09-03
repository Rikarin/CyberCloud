using CyberCloud.Core.Time;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ResourceManager.Reconcile;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Cache.Tests;

/// <summary>
///     The reconciler against a connection that misbehaves in the ways a real cluster does.
/// </summary>
public sealed class ValkeyReconcilerTests {
    [Fact]
    public void TheReconcilerHoldsNoMutableState() {
        // Clause 2, checked structurally, in the cheap place. The conformance run checks it too; this
        // is the one that catches the field somebody adds in a hurry, before a suite has to boot.
        //
        // ⚠ AND IT IS NOT ENOUGH ON ITS OWN. ReconcilerConformance.CheckNoHiddenState skips `readonly`
        // fields, because an injected dependency assigned once in a constructor is the normal shape —
        // so a `readonly Dictionary<,>` used as a per-pass cache passes this check and is a
        // cross-tenant bug. The test below is the one that catches that, and neither replaces the
        // other: this one runs in microseconds and names the field, that one needs two tenants and a
        // world.
        ReconcilerConformance.CheckNoHiddenState(new ValkeyCacheReconciler(new FixedClock())).ShouldBeEmpty();
    }

    [Fact]
    public async Task OneReconcilerInstanceServesTwoTenantsWithoutMixingThem() {
        // ⚠ THE TEST A SINGLE-TENANT TEST CANNOT BE, AND THE ONE THAT COVERS THE STRUCTURAL CHECK'S
        // BLIND SPOT. AddCyberCloudProvider registers a reconciler as a SINGLETON BY CONCRETE TYPE —
        // its own remarks say a transient registration "would hide a field long enough for it to reach
        // production" — so in a real silo ONE instance serves every tenant in the process. A field
        // caching, say, the last rendered spec would pass every test that drives one tenant and would
        // hand tenant B tenant A's eviction policy in production.
        //
        // So: one instance, two tenants, two different bodies, interleaved, and both worlds checked.
        var reconciler = new ValkeyCacheReconciler(new FixedClock());

        // ⚠ THE SAME RESOURCE NAME IN BOTH TENANTS. Two tenants naming a cache `sessions` is the
        // ordinary case, not an edge one — the namespaces differ and nothing else does, so a
        // reconciler that keyed anything on the name alone would serve one of them the other's spec.
        // ⚠ Each tenant brings its OWN subscription, and it has to. ReconcileDriver.NamespaceFor is
        // `{subscriptionId:N}-{resourceGroup}` — the TENANT ID IS NOT IN IT — so two tenants sharing a
        // subscription id would share a namespace and this test would fail for the harness's reason
        // rather than the reconciler's. docs/plan/06 § The hierarchy puts a subscription inside exactly
        // one tenant, so the two can never really collide; it is worth knowing that the isolation here
        // rests on that invariant rather than on the label.
        var alice = Address("sessions", TenantA, SubscriptionA);
        var bob = Address("sessions", TenantB, SubscriptionB);

        var world = new RecordingConnection();

        // ⚠ ONE VAULT ACROSS BOTH TENANTS, because in a silo there is one. Two doubles would isolate
        // the tenants for the harness's reason rather than the reconciler's, and the mint-once rule
        // would never be exercised across the interleave — which is exactly where a path built from
        // the resource NAME rather than its GUID would show up, both caches being called `sessions`.
        var vault = new InMemorySecretVault();

        // ⚠ The two bodies differ in THREE independently rendered fields — replicas, the persistence
        // mode (which changes the storage block AND two customConfig lines) and the volume size. A
        // cache that kept one of them would be caught; a cache that kept a whole rendered document
        // would be caught three times over.
        using var aliceBody = JsonDocument.Parse(ValkeyCaches.Body(ClusterId, replicas: 3, persistence: "AOF", size: "8Gi"));
        using var bobBody = JsonDocument.Parse(ValkeyCaches.Body(ClusterId, replicas: 5, persistence: "None", size: "64Gi"));

        // Interleaved on purpose: A, B, A. A reconciler that remembered anything from its first pass
        // would answer the third pass with B's values.
        await Pass(reconciler, world, alice, aliceBody.RootElement, vault);
        await Pass(reconciler, world, bob, bobBody.RootElement, vault);
        var third = await Pass(reconciler, world, alice, aliceBody.RootElement, vault);

        third.IsConverged.ShouldBeTrue(third.ToString());

        // ⚠ Two objects per pass now — the credential Secret and then the RedisFailover — so the
        // failovers are the odd indices. The pairing is asserted rather than assumed, because an
        // off-by-one here would compare a Secret's body to a spec and fail for the wrong reason.
        var applied = world.Applied;
        applied.Count.ShouldBe(6);

        foreach (var (index, kind) in new[] {
                     (0, "Secret"), (1, "RedisFailover"),
                     (2, "Secret"), (3, "RedisFailover"),
                     (4, "Secret"), (5, "RedisFailover")
                 }) {
            applied[index].Target.Kind.Kind.ShouldBe(kind);
        }

        Redis(applied[1].Body)["replicas"]!.GetValue<int>().ShouldBe(3);
        Redis(applied[3].Body)["replicas"]!.GetValue<int>().ShouldBe(5);
        Redis(applied[5].Body)["replicas"]!.GetValue<int>().ShouldBe(3);

        Redis(applied[1].Body)["storage"]!["persistentVolumeClaim"]!["spec"]!["resources"]!["requests"]!["storage"]!
            .GetValue<string>().ShouldBe("8Gi");
        Redis(applied[3].Body)["storage"]!.AsObject().ContainsKey("emptyDir").ShouldBeTrue(
            "tenant B asked for no persistence and got tenant A's volume claim"
        );
        Redis(applied[5].Body)["storage"]!["persistentVolumeClaim"]!["spec"]!["resources"]!["requests"]!["storage"]!
            .GetValue<string>().ShouldBe("8Gi");

        applied[1].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));
        applied[3].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantB));
        applied[5].Labels[KubeLabels.TenantId].ShouldBe(KubeLabels.GuidValue(TenantA));

        // ⚠ And the two tenants' objects are in different namespaces, so the third pass read back
        // Alice's RedisFailover rather than Bob's. Without this the assertions above would hold for a
        // reconciler that wrote both tenants into one namespace and let the second overwrite the first.
        applied[1].Target.Namespace.ShouldNotBe(applied[3].Target.Namespace);

        // ⚠ AND THE TWO TENANTS GOT TWO PASSWORDS, which no assertion above could see. Both caches
        // are called `sessions`, so a vault path built from the name would have handed Bob whatever
        // Alice minted — mint-once makes the collision permanent rather than transient — and every
        // structural check in this test would still pass.
        var alicePassword = Password(applied[0].Body);
        var bobPassword = Password(applied[2].Body);

        alicePassword.ShouldNotBeNullOrEmpty();
        bobPassword.ShouldNotBe(alicePassword, "two tenants' caches share one requirepass");

        // ⚠ And Alice's third pass rendered the password her FIRST pass minted, not a fresh one. This
        // is the property that keeps a reminder from rotating the credential out from under a running
        // server; see ASecondPassRendersTheSamePasswordAndMintsNothing for the isolated form.
        Password(applied[4].Body).ShouldBe(alicePassword);
    }

    [Fact]
    public async Task AnUnreachableClusterSuspendsRatherThanFails() {
        // docs/plan/09 § Cluster connections: an unreachable cluster suspends reconciles rather than
        // failing them. A tenant whose cluster is down has a resource that is still coming, not one
        // that broke — a Failed here would end the operation and strand a half-built cache.
        var connection = new RecordingConnection { Suspend = true };
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain("cannot reach");
    }

    [Fact]
    public async Task AConflictBecomesProgressRatherThanAFailureOrAForcedOverwrite() {
        // ADR-013: a conflict is "a drift event with a name", not an error — and never a forced apply.
        // ⚠ On this type the other manager is plausibly the operator itself: spotahome's Validate()
        // prepends `replica-priority 100` to spec.redis.customConfig and fills in images and ports.
        // Forcing would take a list field back from the controller that maintains it, once per
        // reminder, forever.
        var connection = new RecordingConnection { ConflictField = ".spec.redis.customConfig" };
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.Reason.ShouldContain(".spec.redis.customConfig");
        connection.Applied[0].Force.ShouldBeFalse("forcing would take a field another manager owns");
    }

    [Fact]
    public async Task ConvergedFollowsTheReadAndNotTheApply() {
        // ⚠ CLAUSE 4, isolated. The apply succeeds and the read finds nothing — a reconciler that
        // believed its own apply would say Converged here.
        var connection = new RecordingConnection { SwallowApplies = true };
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement);

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        outcome.IsConverged.ShouldBeFalse();
    }

    [Fact]
    public async Task TheRedisFailoverCarriesTheSevenLabelsAndBothAnnotations() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        connection.Applied.Count.ShouldBe(2);
        connection.Applied[0].Target.Kind.Kind.ShouldBe("Secret");
        connection.Applied[1].Target.Kind.Kind.ShouldBe("RedisFailover");

        // ⚠ ADR-013's seven, on a CUSTOM RESOURCE whose CRD nothing in this repository installs. The
        // rendered object goes through the same KubeCommand injection as a core-group ConfigMap, and
        // that is the point being pinned — a provider cannot opt out by rendering something exotic.
        foreach (var command in connection.Applied) {
            foreach (var label in KubeLabels.Mandatory) {
                command.Labels.ShouldContainKey(label, command.Target.ToString());
                command.Labels[label].ShouldNotBeNullOrEmpty();
            }

            command.Labels[KubeLabels.ResourceType].ShouldBe("cybercloud.cache_redis");

            foreach (var annotation in KubeLabels.MandatoryAnnotations) {
                command.Annotations.ShouldContainKey(annotation);
            }
        }
    }

    [Fact]
    public async Task ASecondPassOnAConvergedCacheWritesNothingNew() {
        // Clause 1, in the shape that matters for this type: the apply is server-side, so a repeat is
        // an Unchanged, and nothing here counts, appends or timestamps. A rendering that embedded a
        // clock or a counter would produce a different body on every pass and re-apply forever.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement, vault)).IsConverged.ShouldBeTrue();
        var first = connection.Applied[1].Body;

        (await Reconcile(connection, desired.RootElement, vault)).IsConverged.ShouldBeTrue();

        connection.Applied[3].Body.ShouldBe(first, "the rendered object changed between two identical passes");
    }

    [Fact]
    public async Task ASecondPassRendersTheSamePasswordAndMintsNothing() {
        // ⚠⚠ THE PROPERTY THIS TYPE IS MOST EASILY WRONG ABOUT, AND THE ONE WITH THE QUIETEST
        // FAILURE. ValkeyCaches.GeneratePassword answers differently on every call, and a reconciler
        // that rendered ITS OWN candidate rather than what the vault returned would converge on every
        // pass, apply cleanly, read back cleanly, and rewrite the Secret a RUNNING Valkey read its
        // requirepass from at start-up. The server keeps the password it was started with; every
        // already-connected client keeps working; `listKeys` starts handing out a value the server has
        // never accepted; and nothing anywhere reports a failure. So: drive the reconciler twice
        // against ONE vault and assert both halves.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement, vault)).IsConverged.ShouldBeTrue();
        (await Reconcile(connection, desired.RootElement, vault)).IsConverged.ShouldBeTrue();

        connection.Applied.Count.ShouldBe(4);

        // ⚠ BYTE-IDENTICAL, not "both non-empty". A comparison of the decoded values would pass for a
        // render that changed the key name, the type or the base64 padding — and a Secret the operator
        // cannot read is the failure this whole change exists to end.
        connection.Applied[2].Body.ShouldBe(
            connection.Applied[0].Body,
            "the second pass rendered a different Secret, which rotates the requirepass out from under "
            + "a running server while every surface reports success"
        );

        // ⚠ THE VAULT SIDE, AND IT IS NOT REDUNDANT WITH THE LINE ABOVE. A reconciler that minted a
        // fresh candidate on every pass and then resolved would ALSO render an identical Secret, if
        // the writer happened to honour mint-once — so identical bodies alone cannot tell a correct
        // reconciler from one that is relying on the vault to save it. `Writes` counts mints that
        // actually WROTE: the second pass must find the credential already there.
        vault.Writes.ShouldBe(1, "the second pass wrote to the vault");
        vault.Holds(ValkeyCaches.SecretPath(Address("observed", TenantA, SubscriptionA))).ShouldBeTrue();
    }

    [Fact]
    public async Task TheRenderedSecretCarriesThePasswordUnderTheKeyTheOperatorReads() {
        // ⚠ THE KEY NAME IS THE ASSERTION. spotahome's service/k8s/util.go:
        //
        //     if password, ok := secret.Data["password"]; ok { return string(password), nil }
        //     return "", fmt.Errorf("secret %q does not have a password field", ...)
        //
        // A Secret with the right NAME and any other key applies, reads back and converges — the API
        // server has no opinion about the keys in an Opaque document — and the cache still never
        // starts. Nothing inside this repository can tell the two apart, so the constant is pinned
        // here against the upstream read.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement, vault)).IsConverged.ShouldBeTrue();

        var secret = JsonNode.Parse(connection.Applied[0].Body)!.AsObject();

        secret["type"]!.GetValue<string>().ShouldBe("Opaque");
        secret["data"]!.AsObject().Select(x => x.Key).ShouldBe(["password"]);

        // ⚠ `data` and not `stringData`. The API server folds stringData into data and never returns
        // it, so a Secret written that way reads back with no `password` at all — and
        // GetRedisPassword reads secret.Data, which on a fetched object would then be empty.
        secret.ContainsKey("stringData").ShouldBeFalse();

        // ⚠ And the name is the one the CR points at. Two spellings of one object's name is a
        // credential the operator looks for and never finds.
        connection.Applied[0].Target.Name.ShouldBe(
            JsonNode.Parse(connection.Applied[1].Body)!["spec"]!["auth"]!["secretPath"]!.GetValue<string>()
        );

        // And the value is the one the vault holds, rather than a candidate this pass invented.
        Password(connection.Applied[0].Body).ShouldBe(
            vault.Peek(
                ValkeyCaches.SecretPath(Address("observed", TenantA, SubscriptionA)),
                ValkeyCaches.PasswordField
            )
        );
    }

    [Fact]
    public async Task AVaultThatRefusesLeavesNoRedisFailoverBehind() {
        // ⚠ THE ORDER, AS A CONSEQUENCE RATHER THAN AS A COMMENT. A RedisFailover applied before its
        // Secret exists is a cache whose pods fail from the first loop the operator runs, and it would
        // still be applied — and billed — if the credential step were best-effort. The mint is first
        // and it fails the pass, so nothing reaches the cluster at all.
        var connection = new RecordingConnection();
        var vault = new InMemorySecretVault { RefuseMint = true };
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var outcome = await Reconcile(connection, desired.RootElement, vault);

        outcome.IsConverged.ShouldBeFalse();
        connection.Applied.ShouldBeEmpty("the cache was applied without a credential to start it with");
    }

    [Fact]
    public async Task ATeardownIsConvergedOnlyOnceTheObjectIsUnreadableAndItCascadesInForeground() {
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        (await Reconcile(connection, desired.RootElement)).IsConverged.ShouldBeTrue();

        var torn = await new ValkeyCacheReconciler(new FixedClock())
            .DeleteAsync(Context(connection, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
        connection.Objects.ShouldBeEmpty();

        // ⚠ FOREGROUND on the RedisFailover, and this is the one platform default this provider
        // departs from. The StatefulSet, Deployment, Services and ConfigMaps a RedisFailover expands
        // into are its OWNED children. A background cascade returns as soon as the CR is gone, so the
        // read-back would report "not found" while the pods were still serving — and a resource that
        // stops being billed while it is still serving traffic is the failure the read-back exists to
        // prevent. docs/plan/06 § Two-phase create: "never silently gone while its pods still run and
        // its meter still ticks".
        //
        // ⚠ BACKGROUND on the Secret, because a Secret owns nothing and Foreground would block on a
        // dependent set that is always empty.
        connection.Cascades.ShouldBe([CascadePolicy.Foreground, CascadePolicy.Background]);

        // ⚠ THE CR GOES FIRST, WHICH IS THE REVERSE OF THE APPLY ORDER AND IS THE SAME ARGUMENT.
        // spotahome's checker calls GetRedisPassword on every loop, so a Secret removed underneath a
        // live RedisFailover makes the controller error out mid-teardown against the object it is
        // trying to remove.
        connection.Deleted.Select(x => x.Kind.Kind).ShouldBe(["RedisFailover", "Secret"]);
    }

    [Fact]
    public async Task ATeardownWithNoClusterIsConvergedRatherThanStuck() {
        // ⚠ The asymmetry with the create path, asserted so it is not read as an oversight. Failing
        // here would park the resource in Deleting — visible, billed and permanent — for a wiring
        // reason rather than a cluster one.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var torn = await new ValkeyCacheReconciler(new FixedClock())
            .DeleteAsync(Context(null, desired.RootElement), TestContext.Current.CancellationToken);

        torn.IsConverged.ShouldBeTrue();
    }

    [Fact]
    public async Task ObservingReportsWhatIsThereAndNeverApplies() {
        // docs/plan/08 § The reconcile loop: ObserveAsync "must not apply anything — this runs on the
        // drift path too, where a write would turn a diff into a change."
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var reconciler = new ValkeyCacheReconciler(new FixedClock());
        var address = Address("observed", TenantA, SubscriptionA);
        var ns = ReconcileDriver.NamespaceFor(address);

        var absent = await reconciler.ObserveAsync(
            new(address, ValkeyCaches.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        absent.Exists.ShouldBeFalse();
        connection.Applied.ShouldBeEmpty("observing applied something");

        await Reconcile(connection, desired.RootElement);
        var appliedByReconciling = connection.Applied.Count;

        var present = await reconciler.ObserveAsync(
            new(address, ValkeyCaches.V2026, desired.RootElement, ns, connection),
            TestContext.Current.CancellationToken
        );

        present.Exists.ShouldBeTrue();
        present.Summary.ShouldContain("desired");
        connection.Applied.Count.ShouldBe(appliedByReconciling, "observing applied something");
    }

    [Fact]
    public async Task TheRenderedFailoverCarriesThePresetsQuantitiesAndAMaxmemoryDerivedFromThem() {
        // ⚠ THE TWO PLACES WHERE "THE BODY SAID IT AND THE CR DID NOT GET IT" WOULD BE INVISIBLE. A
        // preset that renders no `resources` block bills a tenant for a size they did not get; and a
        // `maxmemory-policy` with no `maxmemory` behind it is a setting Valkey never consults, because
        // an eviction policy applies only when a ceiling is set. The second is the one nothing else
        // would catch: the CR is valid, the operator is happy, the config file has the tenant's chosen
        // policy in it, and the pod is OOM killed instead of evicting.
        var connection = new RecordingConnection();

        var body = JsonNode.Parse(ValkeyCaches.Body(ClusterId))!.AsObject();
        body["properties"]!.AsObject()["sizing"] = new JsonObject { ["preset"] = "m1.large" };
        body["properties"]!.AsObject()["maxmemoryPolicy"] = "allkeys-lru";

        using var desired = JsonDocument.Parse(body.ToJsonString());
        await Reconcile(connection, desired.RootElement);

        var redis = Redis(connection.Applied[1].Body);

        redis["resources"]!["requests"]!["cpu"]!.GetValue<string>().ShouldBe("2");
        redis["resources"]!["limits"]!["memory"]!.GetValue<string>().ShouldBe("16Gi");

        var config = redis["customConfig"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

        // 16Gi × 0.75, in bytes.
        config.ShouldContain("maxmemory 12884901888");
        config.ShouldContain("maxmemory-policy allkeys-lru");
        config.IndexOf("maxmemory 12884901888").ShouldBeLessThan(
            config.IndexOf("maxmemory-policy allkeys-lru"),
            "the ceiling is set before the policy that reads it, which is how redis.conf is read"
        );
    }

    [Theory]
    [InlineData("None", "appendonly no", "save \"\"")]
    [InlineData("RDB", "appendonly no", "save 900 1 300 10 60 10000")]
    [InlineData("AOF", "appendonly yes", "appendfsync everysec")]
    public async Task EveryPersistenceModeTurnsBothSwitchesOffOrOn(string mode, string first, string second) {
        // ⚠ BOTH LINES, AND `None` IS WHY. `appendonly no` alone leaves Valkey's default RDB save
        // points in place, so a cache the tenant asked to keep nothing would still snapshot — to a disk
        // the pod does not have, because `None` also renders an emptyDir. The failure is a background
        // save that fills the node's ephemeral storage, which nothing in this platform would attribute
        // to a persistence setting.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId, persistence: mode));

        await Reconcile(connection, desired.RootElement);

        var config = Redis(connection.Applied[1].Body)["customConfig"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();

        config.ShouldContain(first);
        config.ShouldContain(second);
    }

    [Fact]
    public async Task TheImageIsAValkeyTagAndNeverTheOperatorsRedisDefault() {
        // ⚠ ADR-011 IS A LICENCE DECISION AND THIS IS THE LINE THAT KEEPS IT. spotahome's
        // `defaultImage` is `redis:6.2.6-alpine` — the RSALv2 / SSPL product this platform decided
        // against — so an unset `spec.redis.image` would ship it from a default, silently, with nothing
        // in the build or the API to notice. Build.Licence scans images in the platform bundle; a tag
        // an operator supplies at runtime is not in that scan.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        await Reconcile(connection, desired.RootElement);

        var image = Redis(connection.Applied[1].Body)["image"]!.GetValue<string>();

        image.ShouldStartWith("valkey/valkey:");
        image.ShouldNotContain("redis", Case.Insensitive);
    }

    [Fact]
    public void MatchesIsAContainmentTestSoTheOperatorsOwnDefaultingIsNotDrift() {
        // ⚠ THIS TYPE'S OPERATOR EDITS THE SPEC IT IS GIVEN, which the first provider's did not.
        // spotahome's Validate() fills in images, ports and exporter images, and PREPENDS its own
        // `replica-priority 100` to spec.redis.customConfig before de-duplicating. An equality test on
        // that list would report drift on the pass immediately after the operator first saw the object,
        // and on every pass after that, forever.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId));

        var rendered = JsonNode.Parse(ValkeyCaches.RedisFailoverJson("subset", desired.RootElement))!.AsObject();
        rendered["kind"] = "RedisFailover";
        rendered["status"] = new JsonObject { ["master"] = "10.0.0.1" };
        rendered["spec"]!["redis"]!.AsObject()["port"] = 6379;
        rendered["spec"]!["redis"]!["customConfig"]!.AsArray().Insert(0, "replica-priority 100");

        ValkeyCaches.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeTrue();

        // And a line the operator dropped IS drift, which is the half that makes the containment test
        // worth more than no test.
        rendered["spec"]!["redis"]!["customConfig"]!.AsArray().Clear();
        ValkeyCaches.Matches(rendered.ToJsonString(), desired.RootElement).ShouldBeFalse();

        ValkeyCaches.Matches("not json at all", desired.RootElement).ShouldBeFalse();
    }

    [Fact]
    public void TheKeptClaimsAreNamedFromTheRenderedTemplateAndTheOperatorsSetName() {
        // ⚠ THE AGREEMENT ASSERTION, AND THE REASON IS THAT ONE HALF OF THE NAME IS NOT OURS.
        // `keepAfterDeletion: true` makes spotahome withhold the owner reference that would let the
        // collector take the claim — generator.go's `if !rf.Spec.Redis.Storage.KeepAfterDeletion {
        // pvc.OwnerReferences = ownerRefs }` — so the claim outlives everything, and this type has no
        // recovery window for it to outlive INTO. What removes it is RetainedClaims, which has to
        // predict a name Kubernetes composed from a template WE render and a StatefulSet the OPERATOR
        // renders. The template half is read back out of the applied document here rather than
        // restated, because a rename in Storage() that did not reach RetainedClaims is a purge that
        // silently reclaims nothing.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId, replicas: 2));

        var storage = Redis(ValkeyCaches.RedisFailoverJson("observed", desired.RootElement))["storage"]!.AsObject();

        // The flag and the reclaim are one decision. If the flag ever goes back to false the operator
        // takes the claims and this list must go with it.
        storage["keepAfterDeletion"]!.GetValue<bool>().ShouldBeTrue();

        var template = (storage["persistentVolumeClaim"]!["metadata"] as JsonObject)!["name"]!.GetValue<string>();

        var claims = ValkeyCaches.RetainedClaims("ns", "observed", desired.RootElement);

        claims.Length.ShouldBe(2);
        claims.Select(x => x.Claim.Name).ShouldBe(
            [
                RetainedVolume.NameFor(template, "rfr-observed", 0),
                RetainedVolume.NameFor(template, "rfr-observed", 1)
            ]
        );

        // ⚠ `rfr-` is names.go's GetRedisName and not a guess — see OperatorSetName's remarks for why
        // reading an archived project's convention is safe here and would not be elsewhere.
        ValkeyCaches.OperatorSetName("observed").ShouldBe("rfr-observed");

        foreach (var claim in claims) {
            claim.Claim.Kind.ShouldBe(RetainedVolume.ClaimKind);
            claim.Claim.Namespace.ShouldBe("ns");
            claim.OwnedBy["app.kubernetes.io/name"].ShouldBe("observed");
            claim.OwnedBy["app.kubernetes.io/component"].ShouldBe("redis");
            claim.OwnedBy["app.kubernetes.io/part-of"].ShouldBe("redis-failover");
        }
    }

    [Fact]
    public void ACacheThatPersistsNothingKeepsNothing() {
        // ⚠ `None` renders an emptyDir and no `persistentVolumeClaim` at all, so there is no template,
        // no claim and nothing for a reclaim to remove. Naming one anyway would be a purge acting on a
        // claim that never existed — which VolumeReclaimer treats as absence and converges on, so the
        // damage would be silent rather than loud.
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId, persistence: "None"));

        Redis(ValkeyCaches.RedisFailoverJson("observed", desired.RootElement))["storage"]!
            .AsObject()
            .ContainsKey("persistentVolumeClaim")
            .ShouldBeFalse();

        ValkeyCaches.RetainedClaims("ns", "observed", desired.RootElement).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFinalTeardownRemovesTheDataDirectoriesTheOperatorKept() {
        // ⚠ THE ONE THAT WOULD HAVE CAUGHT THE LEAK. Before this the reconciler took
        // IResourceReconciler's default — an empty array, meaning "this type keeps nothing" — while
        // rendering a claim the operator was explicitly told to keep. Every delete of a
        // CyberCloud.Cache/redis therefore returned the tenant's quota, freed the name, and left the
        // disk allocated with nothing referencing it.
        //
        // The claims are PLANTED, because this connection runs no StatefulSet controller: the name is
        // Kubernetes' {volume}-{set}-{ordinal} and the labels are the operator's own selector, which
        // the controller copies onto every claim its template makes.
        var connection = new RecordingConnection();
        using var desired = JsonDocument.Parse(ValkeyCaches.Body(ClusterId, replicas: 2));
        var context = Context(connection, desired.RootElement);

        var planted = ValkeyCaches.RetainedClaims(context.Namespace, "observed", desired.RootElement);
        planted.Length.ShouldBe(2);

        foreach (var claim in planted) {
            connection.Objects[RecordingConnection.Key(claim.Claim)] = new JsonObject {
                ["metadata"] = new JsonObject {
                    ["name"] = claim.Claim.Name,
                    ["namespace"] = claim.Claim.Namespace,
                    ["labels"] = new JsonObject(
                        claim.OwnedBy.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)JsonValue.Create(x.Value)))
                    )
                }
            }.ToJsonString();
        }

        var outcome = await VolumeReclaimer.ReclaimAsync(
            new ValkeyCacheReconciler(new FixedClock()),
            context,
            TestContext.Current.CancellationToken
        );

        outcome.IsConverged.ShouldBeTrue(outcome.ToString());

        foreach (var claim in planted) {
            connection.Objects.ContainsKey(RecordingConnection.Key(claim.Claim)).ShouldBeFalse(
                $"'{claim.Claim}' survived the final teardown. `keepAfterDeletion: true` asks spotahome "
                + "to leave it, and CyberCloud.Cache/redis declares no recovery window, so nothing "
                + "else is ever coming back for it."
            );
        }
    }

    // ── Harness ───────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");
    static readonly Guid TenantA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid TenantB = Guid.Parse("44444444-4444-4444-8444-444444444444");
    static readonly Guid SubscriptionA = Guid.Parse("22222222-2222-4222-8222-222222222222");
    static readonly Guid SubscriptionB = Guid.Parse("55555555-5555-4555-8555-555555555555");

    static async Task<ReconcileOutcome> Reconcile(
        RecordingConnection connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) =>
        await new ValkeyCacheReconciler(new FixedClock())
            .ReconcileAsync(Context(connection, desired, vault), TestContext.Current.CancellationToken);

    static async Task<ReconcileOutcome> Pass(
        ValkeyCacheReconciler reconciler,
        RecordingConnection connection,
        ResourceId address,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) {
        var store = vault ?? new InMemorySecretVault();

        return await reconciler.ReconcileAsync(
            new(
                address,
                ValkeyCaches.V2026,
                desired,
                null,
                ReconcileDriver.NamespaceFor(address),
                connection,
                store,
                new NullLog()
            ) {
                SecretWriter = store
            },
            TestContext.Current.CancellationToken
        );
    }

    /// <summary>
    ///     A pass's context, with a vault that mints once and resolves what it minted.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><see cref="InMemorySecretVault" /> rather than a stub that always answers, and the
    ///     difference is the whole point of the idempotence assertions below.</b> A double that
    ///     overwrote on every call would make "the rendered Secret is byte-identical across two
    ///     passes" pass against a writer with no mint-once property — the test would be measuring
    ///     itself. This one implements <c>cas=0</c> for real, with <c>TryAdd</c>.
    ///     <para>
    ///         ⚠ One instance serves both <see cref="ReconcileContext.Secrets" /> and
    ///         <see cref="ReconcileContext.SecretWriter" />, because in production they are two views
    ///         of one vault. Two separate doubles would let a reconciler render a value nothing ever
    ///         wrote and still go green.
    ///     </para>
    /// </remarks>
    static ReconcileContext Context(
        IKubeClusterConnection? connection,
        JsonElement desired,
        InMemorySecretVault? vault = null
    ) {
        var address = Address("observed", TenantA, SubscriptionA);
        var store = vault ?? new InMemorySecretVault();

        return new(
            address,
            ValkeyCaches.V2026,
            desired,
            null,
            ReconcileDriver.NamespaceFor(address),
            connection,
            store,
            new NullLog()
        ) {
            SecretWriter = store
        };
    }

    /// <summary>An address in a named tenant and its own subscription.</summary>
    static ResourceId Address(string name, Guid tenant, Guid subscription) =>
        new(
            tenant,
            subscription,
            "prod",
            ValkeyCaches.Type,
            name,
            Guid.Parse("33333333-3333-4333-8333-333333333333")
        );

    static JsonObject Redis(string objectJson) => JsonNode.Parse(objectJson)!["spec"]!["redis"]!.AsObject();

    /// <summary>The <c>requirepass</c> out of a rendered <c>Secret</c>, decoded.</summary>
    /// <remarks>
    ///     ⚠ Decodes rather than comparing base64, because the assertions that use it are about which
    ///     VALUE the vault handed back. The one assertion about the rendered document's bytes compares
    ///     the bodies directly and does not come through here.
    /// </remarks>
    static string Password(string objectJson) =>
        Encoding.UTF8.GetString(
            Convert.FromBase64String(
                JsonNode.Parse(objectJson)!["data"]![ValkeyCaches.PasswordField]!.GetValue<string>()
            )
        );
}

/// <summary>A connection that records what it was asked to do and can be made to misbehave.</summary>
sealed class RecordingConnection : IKubeClusterConnection {
    /// <summary>What is in the "cluster", keyed by kind, namespace and name.</summary>
    public ConcurrentDictionary<string, string> Objects { get; } = new(StringComparer.Ordinal);

    /// <summary>Every command applied, in order.</summary>
    public List<KubeCommand> Applied { get; } = [];

    /// <summary>Every object deleted, in order.</summary>
    public List<ObjectRef> Deleted { get; } = [];

    /// <summary>
    ///     The cascade policy of every delete, in order.
    /// </summary>
    /// <remarks>
    ///     ⚠ Recorded because it is a CHOICE this provider makes and the first provider makes
    ///     differently, and because it is invisible in the resulting object. A delete that used the
    ///     default would look identical in <see cref="Deleted" />.
    /// </remarks>
    public List<CascadePolicy> Cascades { get; } = [];

    /// <summary>Whether every apply answers <c>Suspended</c>.</summary>
    public bool Suspend { get; init; }

    /// <summary>The field another manager owns, or empty.</summary>
    public string ConflictField { get; init; } = string.Empty;

    /// <summary>Whether an apply reports success and stores nothing — the clause-4 trap.</summary>
    public bool SwallowApplies { get; init; }

    public Guid ClusterId => Guid.Parse("eeeeeeee-0000-4000-8000-000000000005");

    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);
        Applied.Add(command);

        if (Suspend) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Suspended,
                        Target = command.Target,
                        Message = "We cannot reach your cluster; this will resume automatically."
                    }
                )
            );
        }

        if (ConflictField.Length > 0) {
            return Task.FromResult(
                Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Conflict,
                        Target = command.Target,
                        Drift = new() {
                            Target = command.Target,
                            FieldManager = command.FieldManager,
                            Conflicts = [new() { Field = ConflictField, OwnedBy = "redis-operator" }]
                        }
                    }
                )
            );
        }

        if (!SwallowApplies) {
            Objects[Key(command.Target)] = command.Body;
        }

        return Task.FromResult(
            Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(target);

        return Task.FromResult(
            Objects.TryGetValue(Key(target), out var json)
                ? Result<KubeObject>.Success(new() { Ref = target, Json = json })
                : Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, $"'{target}' is not here.")
        );
    }

    public Task<Result> DeleteAsync(
        KubeCommand command,
        CascadePolicy policy = CascadePolicy.Background,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(command);

        var removed = Objects.TryRemove(Key(command.Target), out _);

        if (removed) {
            Deleted.Add(command.Target);
            Cascades.Add(policy);
        }

        return Task.FromResult(
            removed
                ? Result.Success
                : Result.Failure(ErrorCode.ResourceNotFound, $"'{command.Target}' is not here.")
        );
    }

    /// <summary>
    ///     ⚠ Keyed by kind, namespace AND name. The namespace is in the key because the cross-tenant
    ///     test puts the same resource name in two tenants, which is the only shape in which one
    ///     singleton reconciler serving both can be caught mixing them.
    /// </summary>
    internal static string Key(ObjectRef target) =>
        target.Kind.Kind + "/" + target.Namespace + "/" + target.Name;
}

/// <summary>A clock that does not move. Nothing here depends on time passing.</summary>
sealed class FixedClock : IClock {
    public DateTimeOffset UtcNow => new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>A log that drops everything. These tests assert outcomes, not progress.</summary>
sealed class NullLog : IReconcileLog {
    public void Report(string phase, string detail) { }

    public void Report(string phase, string detail, int percentComplete) { }
}
