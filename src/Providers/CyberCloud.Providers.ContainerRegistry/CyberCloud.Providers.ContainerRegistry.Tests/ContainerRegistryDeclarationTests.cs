using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     What the managed-container-registry provider declares, checked the way a silo checks it at
///     start.
/// </summary>
public sealed class ContainerRegistryDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build is what AddCyberCloudProvider runs, and it throws on a duplicate short
        // name, a reconciler naming an undeclared type, a RequiresCluster pointer no schema declares, a
        // purge-protection pointer that is not a declared boolean, and half a dozen other declaration
        // mistakes. Building it here is the cheapest possible version of "would this silo start".
        var registration = Registration();

        registration.ReconcilerType.ShouldBe(typeof(ContainerRegistryReconciler));
        registration.ReadPermission.ShouldBe("read");
        registration.ClusterIdPointer.ShouldBe(ContainerRegistries.ClusterIdPointer);
    }

    // ── The recovery window that was declared, measured, and taken back ─────────────────────────

    [Fact]
    public void TheRecoveryWindowIsWithheldAndTheReasonIsAPlatformDefect() {
        // ⚠ THIS ASSERTS A NEGATIVE, WHICH IS UNUSUAL AND IS THE POINT: it goes red the day somebody
        // adds `.SupportsSoftDelete(...)` back, which is the day the platform defect below has to have
        // been closed.
        //
        // This row DID declare it. Every argument for the window holds: docs/plan/06 § Tags, locks
        // gives seven days to "types carrying data", and a registry's images, metadata database and
        // job queue all live on PersistentVolumeClaims that a StatefulSet's deletion leaves behind, so
        // there is genuinely something to hand back.
        //
        // ⚠ AND THE CLUSTER-BACKED SUITE REPORTED THAT A SOFT-DELETED REGISTRY REBUILDS ITS WHOLE DATA
        // PLANE AFTER A CONVERGED TEARDOWN. ClusterConformanceTests.TheLifecycleRunsAgainstARealApiServer
        // failed with "is still in the real cluster after a converged teardown"; reordering the case's
        // object list showed it was EVERY object rather than one. Removing this single call and
        // changing nothing else made the test pass, and putting it back made it fail again.
        //
        // A delete that does not delete is worse than no recovery window: the resource stops being
        // addressable, the workload keeps running, the quota stays held, and the tenant cannot see it
        // in order to delete it again. See charts/managed/harbor/conformance.yaml § owed,
        // `a-soft-deleted-resource-undeletes-itself`.
        Registration().SoftDeleteDays.ShouldBe(
            0,
            "this type declares a recovery window again. That is the right end state and it is only "
            + "safe once a soft-deleted resource stops being re-reconciled — charts/managed/harbor/"
            + "conformance.yaml § owed, `a-soft-deleted-resource-undeletes-itself`. If that is closed, "
            + "delete this test and say so there."
        );
    }

    [Fact]
    public void TheThreeArgumentsTheWindowWouldNeedAreWrittenDownAndAgreeWithDocsPlan08() {
        // ⚠ Kept as constants rather than deleted, so that re-declaring the window is one line rather
        // than three decisions taken again from scratch. Each is asserted here so they cannot rot
        // while nothing calls them.
        //
        //   • SEVEN DAYS — docs/plan/06 § Tags, locks, "types carrying data". Declared on the TYPE and
        //     therefore immutable by construction, which is the stronger form of docs/plan/08's
        //     "retention is set at creation and immutable afterwards": there is no per-resource
        //     property for a caller to shorten.
        //
        //   • `purge` AND NOT `delete` — docs/plan/08 follows Azure in keeping the purge right out of
        //     the contributor role, so "may delete" and "may destroy permanently" stay separable.
        //     Sharing the delete permission would make the window worth nothing against exactly the
        //     caller it protects against.
        //
        //   • A PURGE-PROTECTION POINTER, because a registry is the type somebody wants one on: its
        //     images are what a tenant's production deployments pull, so an accidental purge is an
        //     outage that starts at the next pod restart rather than at the moment of the mistake.
        ContainerRegistries.SoftDeleteDays.ShouldBe(7);
        ContainerRegistries.PurgePermission.ShouldBe("purge");
        ContainerRegistries.PurgePermission.ShouldNotBe(Registration().DeletePermission);
        ContainerRegistries.PurgeProtectionPointer.ShouldBe("/properties/purgeProtection");
    }

    [Fact]
    public void ThePurgeProtectionFlagIsNotADeclaredPropertyWhileThereIsNoWindow() {
        // ⚠ ProviderBuilder refuses a purge-protection pointer on a type with no window — "the flag
        // would be a property callers can set and nothing reads" — and that refusal is right. This is
        // the other half: the property is not in the schema either, so no generated surface offers a
        // caller a protection that engages against nothing.
        ContainerRegistries.Schema2026.Properties
            .ShouldNotContain(x => x.JsonPointer == ContainerRegistries.PurgeProtectionPointer);

        Registration().PurgeProtectionPointer.ShouldBeEmpty();
        Registration().PurgePermission.ShouldBeEmpty();
    }

    // ── Failure class (d): a shortName collision ─────────────────────────────────────────────────

    [Fact]
    public void TheShortNameIsNoneOfTheTwelveCliGroupKeysInTheTree() {
        // ⚠ CliEmitter derives the CLI GROUP key from the provider namespace's LAST SEGMENT,
        // lower-cased — so this namespace is already the group `containerregistry`. System.CommandLine's
        // ValidTokens builds ONE dictionary of every command token and every alias in the whole tree,
        // so a group and an alias that share a string throw `ArgumentException: An item with the same
        // key has already been added` ON THE FIRST PARSE OF ANY COMMAND LINE — not only for the
        // colliding command. Three agents have hit that.
        //
        // ⚠ THE LIST IS TYPED OUT. Deriving it from the providers in the tree would compare a constant
        // to itself and would also require referencing another Providers.* assembly, which
        // src/Providers/README.md § Hard rule forbids.
        string[] groups = [
            "sample",
            "dbforpostgresql",
            "dbformysql",
            "cache",
            "messaging",
            "search",
            "storage",
            "documentdb",
            "analytics",
            "containerservice",
            "containerregistry",
            "network"
        ];

        groups.ShouldContain(
            "containerregistry",
            "this provider's own group key is missing from the list it is being checked against"
        );

        groups.ShouldNotContain(ContainerRegistryProvider.ShortName);
    }

    [Fact]
    public void TheShortNameIsNoneOfTheSixteenAlreadyDeclared() {
        // ⚠ ProviderRegistry.Build refuses a duplicate short name WITHIN one provider and never
        // compares one across providers or against a group name — see
        // charts/managed/seaweedfs/conformance.yaml § owed, `short-name-collides-with-the-group`. So
        // this is checked by hand, against literals, for the sixth time.
        string[] declared = [
            "widget",
            "postgres",
            "mariadb",
            "valkey",
            "kafka",
            "nats",
            "rabbitmq",
            "objectstore",
            "bucket",
            "docdb",
            "opensearch",
            "clickhouse",
            "aks",
            "nodepool",
            "vnet",
            "subnet"
        ];

        declared.ShouldNotContain(ContainerRegistryProvider.ShortName);
    }

    [Fact]
    public void TheShortNameIsNoneOfTheCliSReservedGroups() {
        // ⚠ A THIRD DICTIONARY. `CommandTree.ReservedGroups` takes down `cyc --help` entirely at
        // start-up when a generated group name collides — and an ALIAS lands in the same ValidTokens
        // dictionary a group name does, so the reserved list binds both.
        string[] reserved = [
            "login",
            "logout",
            "account",
            "rest",
            "config",
            "completion",
            "complete",
            "extension",
            "version"
        ];

        reserved.ShouldNotContain(ContainerRegistryProvider.ShortName);
    }

    [Fact]
    public void TheShortNameIsTheWordAPersonWouldReachFor() {
        // ⚠ Not `acr`. docs/plan/21 § Grammar's two examples are `aks` and `postgres` — a vendor
        // acronym and a product name — and this platform already spends `aks` on the Kubernetes row.
        // A second borrowed acronym for a service whose own name is a word people type would be worse
        // than the word.
        ContainerRegistryProvider.ShortName.ShouldBe("registry");
    }

    // ── The meters ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDerivedMeterSaysWhatItReadsAndTheSizedOnesSayTheReplicaCount() {
        // ⚠ MeterDerivation.Reads is what the generated document publishes as a meter's inputs. A
        // derivation whose amount depends on `replicas` and whose declared reads did not mention it
        // would publish a lie that nothing in the platform compares — the derivation is a delegate, so
        // no gate can infer its inputs.
        var derived = Derived();

        derived.Count.ShouldBe(3);

        foreach (var meter in derived) {
            meter.Expression.ShouldNotBeNullOrWhiteSpace(meter.Meter.ToString());
        }

        foreach (var meter in derived.Where(x => x.Meter != QuotaMeter.StorageGb)) {
            meter.Reads.ShouldContain("/properties/replicas", meter.Meter.ToString());
            meter.Reads.ShouldContain("/properties/sizing/preset", meter.Meter.ToString());
        }

        // ⚠ The storage meter deliberately does NOT read `replicas`, and that is the finding rather
        // than an omission: the three components a replica count moves own no volume, and the three
        // that own a volume run one replica each. A storage derivation that multiplied by `replicas`
        // would reserve three times the disk on the default body.
        derived.Single(x => x.Meter == QuotaMeter.StorageGb)
            .Reads
            .ShouldNotContain("/properties/replicas");
    }

    [Fact]
    public void TheTypeDrawsTheResourceCountAndNotTheClustersMeter() {
        var drawn = Registration().Meters.Select(x => x.Meter).ToList();

        drawn.ShouldContain(QuotaMeter.Resources);
        drawn.ShouldContain(QuotaMeter.Vcpu);
        drawn.ShouldContain(QuotaMeter.MemoryGb);
        drawn.ShouldContain(QuotaMeter.StorageGb);

        // A registry is not a Kubernetes cluster. QuotaMeter.Clusters bills BillingMeter.ClusterHours,
        // and a family that drew it would double every tenant's cluster count.
        drawn.ShouldNotContain(QuotaMeter.Clusters);
    }

    // ── The schema ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        // A DefaultJson the schema itself refuses is a chart whose own values.yaml fails validation —
        // and `helm lint --strict` runs a chart against its own defaults, so it would be found by the
        // build rather than by a tenant. Found here first is cheaper.
        using var document = JsonDocument.Parse(ContainerRegistries.Body(ClusterId));

        var validated = ContainerRegistries.Schema2026.Validate(document.RootElement, allowTags: true);

        validated.IsSuccess.ShouldBeTrue(validated.Error?.Message);
    }

    [Fact]
    public void NoBodyPropertyIsDeclaredSecretAndTheCredentialResponseIs() {
        // ⚠ A `Secret` property in a resource BODY is a value the platform would have to store in
        // durable grain state, which docs/plan/00 § Non-negotiables forbids. What leaves through a
        // secret ACTION is a different path with different auditing.
        ContainerRegistries.Schema2026.Properties.ShouldAllBe(x => !x.Secret);

        ContainerRegistries.ListCredentialsResponse.Properties
            .ShouldContain(x => x.JsonPointer == "/password" && x.Secret);

        // ⚠ And the username is NOT secret. It is `admin` on every registry in the platform; marking it
        // secret would tell every generated surface to redact a constant.
        ContainerRegistries.ListCredentialsResponse.Properties
            .ShouldContain(x => x.JsonPointer == "/username" && !x.Secret);
    }

    [Fact]
    public void ThePresetTableHoldsTheOneToFourRatioTheFamilyNameClaims() {
        // ⚠ docs/plan/12 § Sizing vocabulary: "s1.* · 1:4 · General". Three shipped spellings of s1
        // already differ by a rung, and PostgresServers.Presets["s1.nano"] is 5 GiB per core. This
        // asserts the RATIO rather than the copy, so a future table that fixed the outlier still passes
        // and one that copied it does not.
        foreach (var (preset, (cpu, memory)) in ContainerRegistries.Presets) {
            KubeQuantity.TryParse(cpu, out var cores).ShouldBeTrue(preset);
            KubeQuantity.TryGibibytes(memory, out var gibibytes).ShouldBeTrue(preset);

            (gibibytes / cores).ShouldBe(4m, preset + " is not 1:4");
        }
    }

    [Fact]
    public void ThePresetEnumAndThePresetTableAreTheSameSet() {
        var declared = ContainerRegistries.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/sizing/preset")
            .AllowedValues;

        declared.Order(StringComparer.Ordinal)
            .ShouldBe(ContainerRegistries.Presets.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheQuantityGrammarIsThePlatformsRatherThanACopyOfIt() {
        ContainerRegistries.QuantityPattern.ShouldBeSameAs(KubeQuantity.Pattern);
    }

    // ── The version rules ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryOfferedMinorHasAPinnedPatchAndThePatchBelongsToThatMinor() {
        // ⚠ THE API TAKES A MINOR AND A CONTAINER IMAGE TAKES A FULL TAG. Harbor publishes v2.15.2 and
        // no v2.15 tag at all, so a bare minor as an image reference is an ImagePullBackOff per pod,
        // after the caller was told 202 — and nothing in the platform would report it as anything but
        // a resource that never converges.
        var offered = ContainerRegistries.Schema2026.Properties
            .Single(x => x.JsonPointer == "/properties/version")
            .AllowedValues;

        offered.Order(StringComparer.Ordinal)
            .ShouldBe(ContainerRegistries.Versions.Order(StringComparer.Ordinal));

        foreach (var minor in ContainerRegistries.Versions) {
            ContainerRegistries.PinnedPatch.ShouldContainKey(minor);

            var pinned = ContainerRegistries.PinnedPatch[minor];

            pinned.ShouldStartWith("v" + minor + ".", Case.Sensitive);
            pinned.Split('.').Length.ShouldBe(3, pinned + " is not a three-component version");
        }

        ContainerRegistries.PinnedPatch.Count.ShouldBe(ContainerRegistries.Versions.Length);
        ContainerRegistries.Versions.ShouldContain(ContainerRegistries.DefaultVersion);
    }

    [Fact]
    public void AnUnrecognisedMinorFallsBackToTheDefaultsTagRatherThanToItself() {
        // ⚠ Reachable only through a body that was never validated — the enum makes it unreachable from
        // the write path — and the fallback still matters, because the alternative is composing an
        // image reference out of whatever string arrived.
        using var body = JsonDocument.Parse(
            ContainerRegistries.Body(ClusterId).Replace("\"2.15\"", "\"9.99\"", StringComparison.Ordinal)
        );

        ContainerRegistries.ImageTag(body.RootElement)
            .ShouldBe(ContainerRegistries.PinnedPatch[ContainerRegistries.DefaultVersion]);
    }

    // ── The action ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheActionIsSynchronousWithAHandlerAndItsPermissionIsNotRead() {
        Registration().TryGetAction(ContainerRegistries.ListCredentialsAction, out var action)
            .ShouldBeTrue();

        // ⚠ A SYNCHRONOUS ACTION WITH NO HANDLER IS NOW REFUSED BY NAME, which is the honest answer for
        // eleven declarations across the catalogue and would be the wrong one here: this handler is one
        // vault read. And a LONG-RUNNING action would answer 202 with an operation record anyone
        // holding `read` can poll, which is exactly where a secret result must not travel.
        action.LongRunning.ShouldBeFalse();
        action.HandlerType.ShouldBe(typeof(ContainerRegistryListCredentialsHandler));
        action.Secret.ShouldBeTrue();

        action.Permission.ShouldBe("listCredentials");
        action.Permission.ShouldNotBe(Registration().ReadPermission);
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────

    static readonly Guid ClusterId = Guid.Parse("eeeeeeee-0000-4000-8000-00000000000c");

    static ResourceTypeRegistration Registration() {
        ProviderRegistry.Build([new ContainerRegistryProvider()])
            .TryGetType(ContainerRegistries.Type, out var registration)
            .ShouldBeTrue();

        return registration;
    }

    static List<MeterRegistration> Derived() =>
        [.. Registration().Meters.Where(x => x.Derivation is not null)];
}
