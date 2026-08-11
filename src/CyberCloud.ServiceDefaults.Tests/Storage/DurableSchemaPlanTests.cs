using CyberCloud.ServiceDefaults.Storage;
using Shouldly;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     The half-schema hole, at the level where it is a decision rather than a query.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These run without Docker, and that is the point of splitting
///         <see cref="DurableSchemaState" /> off from <c>OrleansAdoNetSchema</c>.</b> "Which objects
///         are on the shard" needs PostgreSQL; "given these objects, what should run" does not, and
///         the second is where the bug lived. The old probe asked one question —
///         <c>to_regclass('orleansquery')</c> — and a shard that had the first script and not the
///         second answered it "yes" forever.
///     </para>
///     <para>
///         The SQL half is <c>OrleansAdoNetSchemaTests</c>, which needs a real server and says so.
///     </para>
/// </remarks>
public sealed class DurableSchemaPlanTests {
    /// <summary>The state a run that died between the two scripts leaves behind.</summary>
    static readonly DurableSchemaState HalfApplied = new(true, false, false, false, 0);

    static readonly DurableSchemaState Complete = new(true, true, true, true, 4);

    static readonly DurableSchemaState Empty = new(false, false, false, false, 0);

    [Fact]
    public void TheHalfSchemaIsCompletedRatherThanSkipped() {
        // The regression this closes, stated as directly as it can be. The old probe's question —
        // "is orleansquery there?" — is `true` in this state, so the old code returned false and the
        // shard stayed unusable through every later run of the job.
        HalfApplied.QueryTable.ShouldBeTrue();

        HalfApplied.IsComplete.ShouldBeFalse();
        HalfApplied.Plan.ShouldBe(
            DurableSchemaPlan.ApplyPersistenceScript,
            "a shard with PostgreSQL-Main.sql and not PostgreSQL-Persistence.sql needs the second "
            + "script, and only the second — the first would fail with 42P07 duplicate table."
        );
    }

    [Fact]
    public void AFullyAppliedSchemaIsANoOp() {
        Complete.IsComplete.ShouldBeTrue();
        Complete.Plan.ShouldBe(DurableSchemaPlan.AlreadyComplete);
    }

    [Fact]
    public void AnEmptyShardGetsBothScripts() {
        Empty.IsEmpty.ShouldBeTrue();
        Empty.Plan.ShouldBe(DurableSchemaPlan.ApplyBothScripts);
    }

    [Fact]
    public void MissingQueryRowsAreNotComplete() {
        // AdoNetGrainStorage.Init reads exactly these four rows and refuses to start without them,
        // so "the tables are there" is not the same claim as "the schema is there". Three rows is
        // the shape a hand-edited orleansquery leaves.
        new DurableSchemaState(true, true, true, true, 3).IsComplete.ShouldBeFalse();
        new DurableSchemaState(true, true, true, true, 3).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);

        new DurableSchemaState(true, true, true, true, 0).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);
    }

    [Fact]
    public void EachMissingPersistenceObjectOnItsOwnIsUnrecoverableRatherThanReapplied() {
        // Re-running PostgreSQL-Persistence.sql over any of these fails partway: on 42P07 for the
        // table or the index, on 23505 for the rows. Dropping what is in the way would drop
        // orleansstorage, which is the tenants' state. Refusing loudly is the only safe answer, and
        // it is loud on every run rather than silent on all of them.
        new DurableSchemaState(true, false, true, true, 4).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);
        new DurableSchemaState(true, true, false, true, 4).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);
        new DurableSchemaState(true, true, true, false, 4).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);
    }

    [Fact]
    public void PersistenceObjectsWithoutTheQueryTableAreUnrecoverable() {
        // No ordering of the two scripts produces this, so somebody dropped orleansquery by hand —
        // which is half of the recovery in deploy/README.md § Idempotence, stopped halfway.
        new DurableSchemaState(false, true, true, true, 0).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);
        new DurableSchemaState(false, false, false, true, 0).Plan.ShouldBe(DurableSchemaPlan.NeedsManualRecovery);
    }

    [Fact]
    public void ExactlyThreeStatesAreActionableOutOfEightyAndTheRestRefuse() {
        // The exhaustive version, so a future edit to Plan cannot widen "repairable" by accident.
        // Widening it is how a repair turns into a DROP TABLE on live tenant state.
        List<DurableSchemaState> repairable = [];

        foreach (var query in new[] { false, true }) {
            foreach (var storage in new[] { false, true }) {
                foreach (var index in new[] { false, true }) {
                    foreach (var function in new[] { false, true }) {
                        for (var rows = 0; rows <= DurableSchemaState.ExpectedQueryRows; rows++) {
                            var state = new DurableSchemaState(query, storage, index, function, rows);

                            if (state.Plan is not DurableSchemaPlan.NeedsManualRecovery) {
                                repairable.Add(state);
                            }
                        }
                    }
                }
            }
        }

        repairable.ShouldBe([Empty, HalfApplied, Complete], ignoreOrder: true);
    }

    [Fact]
    public void TheRefusalNamesEveryObject() {
        // "The schema is partial" sends an operator to read the DDL. This sends them to a table.
        var described = new DurableSchemaState(true, true, false, true, 2).Describe();

        described.ShouldContain("orleansquery=present");
        described.ShouldContain("orleansstorage=present");
        described.ShouldContain("ix_orleansstorage=MISSING");
        described.ShouldContain("writetostorage=present");
        described.ShouldContain("query rows=2 of 4");
    }
}
