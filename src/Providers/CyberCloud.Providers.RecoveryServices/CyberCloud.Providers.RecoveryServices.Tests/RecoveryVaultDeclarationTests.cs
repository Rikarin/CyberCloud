using CyberCloud.Providers.DBforPostgreSQL.Contracts;
using CyberCloud.ResourceManager.Registry;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.RecoveryServices.Tests;

/// <summary>
///     The vault's declaration, checked the way a silo checks it at start — plus the facts that only
///     exist because it reads another provider's contract by hand.
/// </summary>
public sealed class RecoveryVaultDeclarationTests {
    /// <summary>The spelling, written out. ⚠ <b>A literal, and it has to be</b> — see <c>StorageBucketDeclarationTests</c>.</summary>
    const string QualifiedType = "CyberCloud.RecoveryServices/vaults";

    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        var registry = ProviderRegistry.Build([new RecoveryServicesProvider()]);

        registry.TryGetType(RecoveryVaults.Type, out var registration).ShouldBeTrue();

        RecoveryVaults.Type.ToString().ShouldBe(QualifiedType);
        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe("/properties/clusterId");
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe("managed/recovery-vault");
        registration.ReconcilerType.ShouldBe(typeof(RecoveryVaultReconciler));
        registration.ReadPermission.ShouldBe("read");

        var list = registration.Actions.Single(static x => x.Name == "listRecoveryPoints");
        list.Secret.ShouldBeFalse("a recovery point is a name, a phase and two timestamps");
        list.Permission.ShouldBe("read");
        list.HandlerType.ShouldBe(typeof(RecoveryVaultListRecoveryPointsHandler));
        list.LongRunning.ShouldBeFalse();
        list.Request.ShouldBeNull();

        var recover = registration.Actions.Single(static x => x.Name == "recover");
        recover.Permission.ShouldBe("write", "a recover writes a Cluster into the tenant's namespace");
        recover.HandlerType.ShouldBe(typeof(RecoveryVaultRecoverHandler));
        recover.Request.ShouldNotBeNull();
        recover.LongRunning.ShouldBeFalse();

        // ⚠ Not `restore`: the builder reserves it for soft delete's own dispatch, and this type's first
        // conformance run found the refusal. Pinned so nobody renames it back.
        registration.Actions.Select(static x => x.Name).ShouldNotContain("restore");
    }

    [Fact]
    public void TheOnlyMeterIsTheResourceCount() {
        var registry = ProviderRegistry.Build([new RecoveryServicesProvider()]);
        registry.TryGetType(RecoveryVaults.Type, out var registration).ShouldBeTrue();

        // ⚠ A ScheduledBackup is a controller's intent, not a pod. The pods that run a backup are the
        // protected server's and are reserved against the server's meters; a vault that reserved
        // vCPU or memory would count them twice.
        registration.Meters.Select(static x => x.Meter).ShouldBe([QuotaMeter.Resources]);
    }

    [Fact]
    public void TheShortNameIsNotVaultBecauseKeyVaultIsOwedThatWord() {
        var registry = ProviderRegistry.Build([new RecoveryServicesProvider()]);
        registry.TryGetType(RecoveryVaults.Type, out var registration).ShouldBeTrue();

        registration.Display.Alias.ShouldBe("backupvault");
        registration.Display.Alias.ShouldNotBe(
            "recoveryservices",
            "a short name equal to its own group key is the collision CliTokens refuses"
        );
    }

    [Fact]
    public void TheScheduleIsFiveFieldsAndTheObjectGetsSix() {
        var schedule = RecoveryVaults.Schema2026.Properties.Single(static x => x.JsonPointer
            == "/properties/policy/schedule"
        );

        schedule.Required.ShouldBeTrue();
        schedule.DefaultJson.ShouldBe("\"0 2 * * *\"");

        var pattern = new Regex(schedule.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));

        pattern.IsMatch("0 2 * * *").ShouldBeTrue();
        pattern.IsMatch("*/15 * * * *").ShouldBeTrue();
        pattern.IsMatch("0 0,12 1-15 * 1-5").ShouldBeTrue();

        // ⚠ Six fields, names and descriptors are refused at the API: the only parser that would
        // accept them is the operator's, after the caller was told 202.
        pattern.IsMatch("0 0 2 * * *").ShouldBeFalse("a six-field schedule would render as seven");
        pattern.IsMatch("0 2 * * MON").ShouldBeFalse();
        pattern.IsMatch("@daily").ShouldBeFalse();
        pattern.IsMatch("0 2 * *").ShouldBeFalse();

        RecoveryVaults.SixFieldSchedule("0 2 * * *").ShouldBe("0 0 2 * * *");
    }

    [Fact]
    public void ProtectedItemsIsAnArrayOfTextWhoseShapeTheReconcilerChecks() {
        var items = RecoveryVaults.Schema2026.Properties.Single(static x => x.JsonPointer
            == "/properties/protectedItems"
        );

        items.Kind.ShouldBe(SchemaKind.Array);
        items.ElementKind.ShouldBe(SchemaKind.Text);
        items.Required.ShouldBeTrue();

        // ⚠ No Format on the element: ./build.sh Charts refuses `@format` on a `{array}` @param
        // (charts/managed/kafka's cidr-shape-is-unenforced, second sighting), so a path that does not
        // parse passes the API and is refused by ItemOf at the element's pointer instead.
        items.Format.ShouldBe(SchemaFormat.None);
        items.MaxLength.ShouldBeNull();

        using var bad = JsonDocument.Parse(RecoveryVaults.Body(Guid.NewGuid(), ["not-a-path"]));
        RecoveryVaults.Schema2026.Validate(bad.RootElement)
            .IsSuccess.ShouldBeTrue("the API admits it; the reconciler is where it is refused");

        var refused = RecoveryVaults.ItemOf(Ids.Vault("v"), 0, "not-a-path");
        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Target.ShouldBe("/properties/protectedItems/0");

        // And an empty list is a vault that protects nothing, which is legal.
        using var empty = JsonDocument.Parse(RecoveryVaults.Body(Guid.NewGuid(), []));
        RecoveryVaults.Schema2026.Validate(empty.RootElement).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void EveryDeclaredDefaultIsAValueTheApiWouldAccept() {
        using var body = JsonDocument.Parse(RecoveryVaults.Body(Guid.NewGuid(), []));
        RecoveryVaults.Schema2026.Validate(body.RootElement).IsSuccess.ShouldBeTrue();

        // The defaults the readers fall back to are the ones the schema publishes.
        using var bare = JsonDocument.Parse(
            """{"location":"eu-central","properties":{"clusterId":"eeeeeeee-0000-4000-8000-000000000005","protectedItems":[]}}"""
        );
        RecoveryVaults.Schedule(bare.RootElement).ShouldBe("0 2 * * *");
        RecoveryVaults.RetentionDays(bare.RootElement).ShouldBe(14);

        RecoveryVaults.Schema2026.Properties.Single(static x => x.JsonPointer == "/properties/policy/retentionDays")
            .DefaultJson.ShouldBe("14");
    }

    [Fact]
    public void TheServerContractDefaultsAreTheOnesThePublishedDocumentGives() {
        // ⚠ THE TWO POINTERS AND TWO DEFAULTS THE VAULT SPELLS BY HAND, HELD AGAINST THE OTHER FAMILY'S
        // OWN SCHEMA. The shipping vault assembly may not reference PostgresServers (rule 2); this
        // test project may, and it is the one reader that compares the two spellings. A change to the
        // server's backup block turns this red rather than quietly refusing every server.
        var server = PostgresServers.Schema2026.Properties.ToDictionary(
            static x => x.JsonPointer,
            StringComparer.Ordinal
        );

        foreach (var pointer in RecoveryVaults.ServerContractPointers) {
            server.ShouldContainKey(
                pointer,
                $"the vault reads '{pointer}' off a PostgreSQL server's body and that type no longer declares it"
            );
        }

        server["/properties/backup/enabled"].DefaultJson.ShouldBe(
            RecoveryVaults.ServerBackupEnabledDefault ? "true" : "false"
        );
        server["/properties/backup/retentionDays"].DefaultJson.ShouldBe(
            RecoveryVaults.ServerRetentionDaysDefault.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );

        // And the type name the vault dispatches on is that family's.
        RecoveryVaults.PostgresServerType.ShouldBe(PostgresServers.Type);
        RecoveryVaults.ClusterKind.ShouldBe(PostgresServers.ClusterKind);
    }

    [Fact]
    public void TheServerContractReaderTakesAbsenceAsThePublishedDefaultAndPresenceAsWritten() {
        RecoveryVaults.ServerBackupContract("""{"properties":{}}""").ShouldBe((true, 14));
        RecoveryVaults.ServerBackupContract("""{"properties":{"backup":{"enabled":false}}}""").ShouldBe((false, 14));
        RecoveryVaults.ServerBackupContract("""{"properties":{"backup":{"retentionDays":30}}}""").ShouldBe((true, 30));
        RecoveryVaults.ServerBackupContract("not json").ShouldBe((true, 14));
    }

    [Fact]
    public void TheScheduledBackupNameFitsAndStaysUniqueWhenTheTwoNamesDoNot() {
        // ⚠ A literal digest, not one computed here: the value is the first twelve hex digits of
        // SHA-256 over `nightly/main`, and a test that recomputed it would compare the function to
        // itself.
        RecoveryVaults.ScheduledBackupNameOf("nightly", "main").ShouldBe("nightly-main-19eac1a54fcd");

        var longVault = new string('v', 40);
        var longItem = new string('i', 40);
        var folded = RecoveryVaults.ScheduledBackupNameOf(longVault, longItem);

        folded.Length.ShouldBeLessThanOrEqualTo(63);
        folded.ShouldMatch("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$");
        folded.ShouldStartWith(new string('v', 24) + "-" + new string('i', 24) + "-");
        folded.ShouldNotBe(
            RecoveryVaults.ScheduledBackupNameOf(longVault, new string('i', 39) + "j"),
            "two items that share a 24-character stem folded to one name"
        );
    }

    [Fact]
    public void TwoVaultsWhoseNamesJoinToOneSpellingOwnTwoSchedules() {
        // ⚠ THE PAIR THE REVIEW OF THE FIRST CUT FOUND. Vault `a` protecting `b-c` and vault `a-b`
        // protecting `c` both spelled `a-b-c` when the digest was only added past the cap — and under
        // one field manager the API server would not have conflicted, so the two vaults would have
        // silently taken the object from each other every pass and pruned each other's points.
        var first = RecoveryVaults.ScheduledBackupNameOf("a", "b-c");
        var second = RecoveryVaults.ScheduledBackupNameOf("a-b", "c");

        first.ShouldNotBe(second, "a hyphen join is not unique, and the digest is what makes it so");
        first.ShouldStartWith("a-b-c-");
        second.ShouldStartWith("a-b-c-");
    }

    [Fact]
    public void MatchesComparesTheThreeFieldsTheVaultOwnsAndNothingTheOperatorWrites() {
        using var desired = JsonDocument.Parse(RecoveryVaults.Body(Guid.NewGuid(), [], "0 2 * * *"));

        var rendered = RecoveryVaults.ScheduledBackupJson("nightly", "main", "main", desired.RootElement);
        RecoveryVaults.Matches(rendered, "main", desired.RootElement)
            .ShouldBeTrue("the object as rendered, before apply, must match");

        // As the API server hands it back: kind, a defaulted target, a status.
        var readBack =
            """{"apiVersion":"postgresql.cnpg.io/v1","kind":"ScheduledBackup","metadata":{"name":"nightly-main"},"spec":{"schedule":"0 0 2 * * *","cluster":{"name":"main"},"backupOwnerReference":"self","method":"barmanObjectStore","immediate":true,"target":"prefer-standby","online":true},"status":{"lastCheckTime":"2026-09-18T02:00:00Z"}}""";
        RecoveryVaults.Matches(readBack, "main", desired.RootElement).ShouldBeTrue();

        RecoveryVaults.Matches(
            readBack.Replace("0 0 2 * * *", "0 0 3 * * *", StringComparison.Ordinal),
            "main",
            desired.RootElement
        )
            .ShouldBeFalse("a schedule rewritten by hand is a policy the tenant did not set");
        RecoveryVaults.Matches(readBack, "other", desired.RootElement).ShouldBeFalse();
        RecoveryVaults.Matches(
            readBack.Replace("\"self\"", "\"none\"", StringComparison.Ordinal),
            "main",
            desired.RootElement
        )
            .ShouldBeFalse();
        RecoveryVaults.Matches(
            readBack.Replace("ScheduledBackup", "Backup", StringComparison.Ordinal),
            "main",
            desired.RootElement
        )
            .ShouldBeFalse();
    }

    [Fact]
    public void ARecoveryPointLineCarriesEverythingTheResponseShapePromises() {
        var point = RecoveryVaults.RecoveryPointOf(
            "main",
            RecoveryVaults.OperatorBackupJson(
                "ns",
                "nightly-main",
                "main",
                "nightly-main-20260918020000",
                "failed",
                new DateTimeOffset(2026, 9, 18, 2, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 18, 2, 0, 9, TimeSpan.Zero),
                error: "invalid destination"
            )
        );

        point.ShouldNotBeNull();
        point.Value.IsCompleted.ShouldBeFalse();
        RecoveryVaults.RecoveryPointLine(point.Value)
            .ShouldBe(
                "main nightly-main-20260918020000 failed started 2026-09-18T02:00:00.0000000+00:00 stopped 2026-09-18T02:00:09.0000000+00:00 method barmanObjectStore: invalid destination"
            );

        var completed = RecoveryVaults.RecoveryPointOf(
            "main",
            RecoveryVaults.OperatorBackupJson(
                "ns",
                "nightly-main",
                "main",
                "x",
                "completed",
                DateTimeOffset.UnixEpoch,
                null
            )
        );
        completed!.Value.IsCompleted.ShouldBeTrue();
        RecoveryVaults.RecoveryPointLine(completed.Value).ShouldEndWith("stopped - method barmanObjectStore");
    }

    [Fact]
    public void TheOwedFileNamesEveryDebtTheCodeCites() {
        // ⚠ The contracts and the reconciler cite owed rows by id. A row renamed in conformance.yaml
        // and not in the code sends a reader to a debt that is not there.
        var manifest = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "charts", "managed", "recovery-vault", "conformance.yaml")
        );

        foreach (var id in new[] {
                     "file-shares-have-no-snapshot-story", "the-store-is-the-servers",
                     "retention-is-enforced-on-passes", "items-in-other-resource-groups",
                     "a-restore-is-not-yet-a-resource", "deleting-the-vault-deletes-its-recovery-points"
                 }) {
            manifest.ShouldContain("- id: " + id);
        }
    }

    static string RepositoryRoot() {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CyberCloud.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("CyberCloud.slnx was not found above the test assembly.");
    }
}
