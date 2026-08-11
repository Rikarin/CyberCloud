using System.Globalization;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     What <see cref="OrleansAdoNetSchema" /> should do about the objects it found on a shard.
/// </summary>
public enum DurableSchemaPlan {
    /// <summary>Everything the two scripts create is there. Run nothing.</summary>
    AlreadyComplete,

    /// <summary>The shard is empty of Orleans objects. Run both scripts, in order.</summary>
    ApplyBothScripts,

    /// <summary>
    ///     <c>orleansquery</c> is there and the persistence half is entirely absent — the half-schema
    ///     hole. Run <c>PostgreSQL-Persistence.sql</c> only.
    /// </summary>
    ApplyPersistenceScript,

    /// <summary>
    ///     A mixture no script and no interrupted script can produce, so an operator has been in
    ///     here. Refuse, and say exactly what is present.
    /// </summary>
    NeedsManualRecovery
}

/// <summary>
///     Which of the Orleans grain-storage objects exist on one shard right now.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Five facts, not one.</b> The probe this replaces asked
///         <c>SELECT to_regclass('orleansquery')</c> and skipped the shard when it answered. That is
///         the first object the <i>first</i> of two scripts creates, so a run that died between the
///         scripts left a shard with <c>orleansquery</c>, no <c>orleansstorage</c>, and a probe that
///         would answer "already applied" forever. <c>deploy/README.md § Idempotence</c> named that
///         hole, carried the <c>DROP TABLE</c> recovery for it, and said the close belongs here.
///     </para>
///     <para>
///         The five are everything the two files create between them:
///         <c>PostgreSQL-Main.sql</c> creates <see cref="QueryTable" />;
///         <c>PostgreSQL-Persistence.sql</c> creates <see cref="StorageTable" />,
///         <see cref="StorageIndex" />, <see cref="WriteFunction" />, and the four
///         <see cref="QueryRows" /> that <c>AdoNetGrainStorage.Init</c> reads back. Miss any one and
///         a silo bound to the shard fails — on <c>relation "orleansquery" does not exist</c>, on
///         <c>relation "orleansstorage" does not exist</c>, or on the four-rows read that returns
///         fewer than four.
///     </para>
///     <para>
///         <b>This type is deliberately free of Npgsql.</b> Deciding what to do about a set of
///         observed facts is the part worth testing exhaustively, and it needs no database to test —
///         see <c>DurableSchemaPlanTests</c>, which walks all 80 combinations.
///     </para>
/// </remarks>
/// <param name="QueryTable">Whether the <c>orleansquery</c> table is present.</param>
/// <param name="StorageTable">Whether the <c>orleansstorage</c> table is present.</param>
/// <param name="StorageIndex">Whether the <c>ix_orleansstorage</c> index is present.</param>
/// <param name="WriteFunction">Whether the <c>writetostorage</c> function is present.</param>
/// <param name="QueryRows">
///     How many of the four query rows <c>AdoNetGrainStorage.Init</c> reads are in
///     <c>orleansquery</c>, from 0 to 4. Always 0 when <paramref name="QueryTable" /> is
///     <see langword="false" />, because there is nowhere for them to be.
/// </param>
public readonly record struct DurableSchemaState(
    bool QueryTable,
    bool StorageTable,
    bool StorageIndex,
    bool WriteFunction,
    int QueryRows
) {
    /// <summary>How many query rows a complete schema has — the number <c>Init</c> expects.</summary>
    public const int ExpectedQueryRows = 4;

    /// <summary>Whether every object the two scripts create is present.</summary>
    public bool IsComplete =>
        QueryTable && StorageTable && StorageIndex && WriteFunction && QueryRows == ExpectedQueryRows;

    /// <summary>Whether nothing the two scripts create is present.</summary>
    public bool IsEmpty =>
        !QueryTable && !StorageTable && !StorageIndex && !WriteFunction && QueryRows == 0;

    /// <summary>
    ///     What to run against a shard in this state.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only two partial states are repairable, and that is a claim about transactions
    ///         rather than caution.</b> Each script runs as one statement batch inside one explicit
    ///         transaction (<see cref="OrleansAdoNetSchema.ApplyAsync(string, CancellationToken)" />),
    ///         and PostgreSQL DDL is transactional — so an interrupted apply rolls back whole scripts,
    ///         never half of one. The only states an interruption can leave are therefore
    ///         <i>nothing</i> and <i>script one only</i>, which are exactly the two this repairs.
    ///     </para>
    ///     <para>
    ///         Everything else — <c>orleansstorage</c> without <c>orleansquery</c>, the four rows
    ///         without the table they describe, the index dropped by hand — means someone edited the
    ///         schema. Re-running the scripts there is not a repair: the bare
    ///         <c>CREATE TABLE</c>/<c>CREATE INDEX</c>/<c>INSERT</c> would fail on <c>42P07</c> or
    ///         <c>23505</c> halfway, and the alternative — dropping what is in the way — would drop
    ///         <c>orleansstorage</c>, which is every tenant's durable state on this shard.
    ///         <see cref="DurableSchemaPlan.NeedsManualRecovery" /> is the honest answer, and it is
    ///         loud on every start rather than silent forever.
    ///     </para>
    /// </remarks>
    public DurableSchemaPlan Plan {
        get {
            if (IsComplete) {
                return DurableSchemaPlan.AlreadyComplete;
            }

            if (IsEmpty) {
                return DurableSchemaPlan.ApplyBothScripts;
            }

            // Script one landed, script two did not — the half-schema hole, and the whole reason this
            // type exists. Nothing of the persistence half may be present, or the INSERTs and the
            // CREATEs in that script collide with what is already there.
            return QueryTable && !StorageTable && !StorageIndex && !WriteFunction && QueryRows == 0
                ? DurableSchemaPlan.ApplyPersistenceScript
                : DurableSchemaPlan.NeedsManualRecovery;
        }
    }

    /// <summary>
    ///     Spells the state out object by object, for a failure message an operator can act on.
    /// </summary>
    /// <returns>
    ///     A one-line inventory — every object, present or missing, and the query-row count.
    /// </returns>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"orleansquery={Word(QueryTable)}, orleansstorage={Word(StorageTable)}, "
            + $"ix_orleansstorage={Word(StorageIndex)}, writetostorage={Word(WriteFunction)}, "
            + $"query rows={QueryRows} of {ExpectedQueryRows}"
        );

    static string Word(bool present) => present ? "present" : "MISSING";
}
