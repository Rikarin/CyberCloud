using Npgsql;
using System.Globalization;

namespace CyberCloud.ServiceDefaults.Storage;

/// <summary>
///     Creates the Orleans ADO.NET grain-storage schema on a PostgreSQL shard of the durable tier.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The durable tier does not create its own schema, and until this type landed nothing
///             in the repository outside two test projects did either.
///         </b>
///         The facts, established rather
///         than assumed:
///     </para>
///     <list type="number">
///         <item>
///             <b>The provider does not migrate.</b> <c>AdoNetGrainStorage.Init</c> runs
///             <c>SELECT QueryKey, QueryText FROM OrleansQuery WHERE …</c> and expects four rows.
///             Against an empty database that is <c>relation "orleansquery" does not exist</c>, at
///             silo start for the bootstrap shard and at first grain write for every other one.
///         </item>
///         <item>
///             <b>Microsoft.Orleans.Persistence.AdoNet ships no SQL.</b> Its
///             <c>GetManifestResourceNames()</c> is empty. The DDL exists only in the
///             <c>dotnet/orleans</c> git repository.
///         </item>
///         <item>
///             <b>It is two files and the order matters.</b>
///             <c>src/AdoNet/Shared/PostgreSQL-Main.sql</c> creates <c>OrleansQuery</c>;
///             <c>src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql</c> creates
///             <c>OrleansStorage</c>, the <c>writetostorage</c> function and the four rows that go
///             into <c>OrleansQuery</c>. The second alone fails on its <c>INSERT</c>s.
///         </item>
///         <item>
///             <b><c>PostgreSQL-Clustering.sql</c> is deliberately not here.</b> It belongs to the
///             ADO.NET <i>membership</i> provider, and ADR-004 says there is no clustering database.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Neither script is idempotent</b> — they are bare <c>CREATE TABLE</c>, not
///         <c>CREATE TABLE IF NOT EXISTS</c>, and they are copied verbatim from <c>dotnet/orleans</c>
///         so that editing them never becomes ours to maintain. Idempotence is therefore this type's
///         job, and it is built from three things rather than from a probe:
///     </para>
///     <list type="number">
///         <item>
///             <b>A probe over every object, not one.</b> <see cref="DurableSchemaState" /> asks
///             about all five things the two files create. The single
///             <c>to_regclass('orleansquery')</c> check this replaces answered "already applied" for
///             a shard that had the first script and not the second — permanently, on every later
///             run. <c>deploy/README.md § Idempotence</c> named that hole and said the close belongs
///             here.
///         </item>
///         <item>
///             <b>One transaction around the whole apply.</b> PostgreSQL DDL is transactional, so
///             both scripts commit together or neither does. That is what makes the half-schema state
///             unreachable going forward instead of merely detectable.
///         </item>
///         <item>
///             <b>A transaction-scoped advisory lock.</b> Two appliers on one shard serialise; the
///             loser wakes up, re-probes inside the lock, finds a complete schema and returns
///             <see langword="false" />. Not "the loser gets a clean <c>42P07</c>" — the loser gets a
///             clean no-op.
///         </item>
///     </list>
///     <para>
///         <b>Production reuse.</b> This lives in <c>CyberCloud.ServiceDefaults</c> — the assembly
///         that wires the durable tier — precisely so that it is not local-development-only. It has
///         no Aspire, Testcontainers or host dependency: it takes a connection string and a
///         cancellation token. <c>CyberCloud.AppHost</c> drives it through
///         <c>CyberCloud.Silo.Host --apply-durable-schema</c>, and <c>deploy/bootstrap</c> drives the
///         same entry point from the same image as a Helm <c>pre-install</c> hook Job. The Helm hook
///         guarantees a single applier; the AppHost does not, and neither does an operator running
///         the job twice, which is why the guarantee is in the program rather than in the manifest.
///     </para>
/// </remarks>
public static class OrleansAdoNetSchema {
    /// <summary>
    ///     The four <c>QueryKey</c> values <c>AdoNetGrainStorage.Init</c> reads back, and refuses to
    ///     start without.
    /// </summary>
    public static readonly IReadOnlyList<string> QueryKeys = [
        "WriteToStorageKey", "ReadFromStorageKey", "ClearStorageKey", "DeleteStorageKey"
    ];

    /// <summary>
    ///     The two scripts, in the order they must run. Embedded copies of <c>dotnet/orleans</c> at
    ///     tag <c>v10.2.2</c> — see the <c>EmbeddedResource</c> items in the project file.
    /// </summary>
    const string MainScript = "CyberCloud.ServiceDefaults.Storage.PostgreSQL-Main.sql";

    const string PersistenceScript = "CyberCloud.ServiceDefaults.Storage.PostgreSQL-Persistence.sql";

    /// <summary>
    ///     The advisory-lock coordinates two appliers on one shard agree on.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>pg_advisory_xact_lock</c>, not <c>pg_advisory_lock</c>.</b> The session form
    ///         is held until an explicit unlock or disconnect, which under the transaction-mode
    ///         PgBouncer that docs/plan/05 § Storage provider wiring calls non-negotiable means the
    ///         lock outlives the pooled server connection it was taken on and lands on some later,
    ///         unrelated transaction. The transaction form is released by <c>COMMIT</c>, which is the
    ///         boundary PgBouncer already respects.
    ///     </para>
    ///     <para>
    ///         The two integers are arbitrary and only have to be unique within the database:
    ///         <c>0x4343</c> is <c>CC</c>, and 5 is docs/plan/05, the durable tier.
    ///     </para>
    /// </remarks>
    const int AdvisoryLockClass = 0x4343;

    const int AdvisoryLockKey = 5;

    /// <summary>
    ///     Applies the schema to one shard, completing a half-applied one and skipping a complete one.
    /// </summary>
    /// <param name="connectionString">An Npgsql connection string for one shard.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    ///     <see langword="true" /> if this call ran DDL, <see langword="false" /> if the schema was
    ///     already complete — including when a concurrent applier completed it while this one waited
    ///     on the lock.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     The shard holds a mixture of objects no script and no interrupted script can produce, so
    ///     an operator has edited it. The message inventories what is present, because the recovery
    ///     in <c>deploy/README.md § Idempotence</c> drops tables and needs to be aimed.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>What the happy path costs.</b> A complete shard is one connection, one catalog
    ///         <c>SELECT</c>, and one four-row <c>SELECT</c> over a primary key — no transaction, no
    ///         lock, no DDL. Two round trips rather than the one this replaces, and the second is what
    ///         buys the four-rows check that <c>Init</c> itself performs. Cheap enough for a silo
    ///         start, which is the bar, even though no silo runs it today.
    ///     </para>
    /// </remarks>
    public static async Task<bool> ApplyAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // ⚠ Pooling off, deliberately. A pooled connection opened here would stay as an idle
        // Postgres backend under the same application_name for the rest of the process, and the
        // durable tier's whole connection budget (docs/plan/05 § Storage provider wiring) is counted
        // per shard by application_name. A one-shot job must not leave a connection behind in the
        // pool that the silo then inherits.
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString
        );

        await connection.OpenAsync(cancellationToken);

        // The cheap path, taken by every run after the first: no lock, no transaction. Taking the
        // advisory lock first would be correct and would also make every silo start on a healthy
        // fleet queue behind every other one.
        if ((await ProbeAsync(connection, null, cancellationToken)).IsComplete) {
            return false;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // ⚠ A lock wait is unbounded by default, and an applier that hangs is worse than one that
        // fails: the AppHost's WaitForCompletion and Helm's hook wait both block on it, so the
        // symptom is "the deployment is stuck" with nothing naming the shard. SET LOCAL is scoped to
        // this transaction and needs no reset.
        await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '30s';", connection, transaction)) {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var gate = new NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock({AdvisoryLockClass}, {AdvisoryLockKey});",
            connection,
            transaction
        )) {
            await gate.ExecuteNonQueryAsync(cancellationToken);
        }

        // Re-probed inside the lock, and this is the whole concurrency story: the second applier's
        // first probe saw an empty shard, and by the time it holds the lock the first one has
        // committed. Without this it would run CREATE TABLE against a table that now exists.
        var state = await ProbeAsync(connection, transaction, cancellationToken);

        switch (state.Plan) {
            case DurableSchemaPlan.AlreadyComplete:
                await transaction.RollbackAsync(cancellationToken);
                return false;

            case DurableSchemaPlan.ApplyBothScripts:
                await RunAsync(connection, transaction, MainScript, cancellationToken);
                await RunAsync(connection, transaction, PersistenceScript, cancellationToken);
                break;

            case DurableSchemaPlan.ApplyPersistenceScript:
                // The half-schema hole, closed. Script one already landed; running it again would be
                // 42P07 and running neither is what left the shard unusable in the first place.
                await RunAsync(connection, transaction, PersistenceScript, cancellationToken);
                break;

            default:
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"This shard holds a partial Orleans grain-storage schema that no script and "
                        + $"no interrupted script produces, so it has been edited by hand: "
                        + $"{state.Describe()}. Applying the scripts over it would fail halfway, and "
                        + $"dropping what is in the way would drop orleansstorage, which is every "
                        + $"tenant's durable state on this shard. Recover it deliberately — see "
                        + $"deploy/README.md § Idempotence — then re-run."
                    )
                );
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    ///     Applies the schema to every shard in a bound <see cref="DurableTierOptions" />.
    /// </summary>
    /// <param name="durable">The <c>CyberCloud:Storage:Durable</c> section, bound.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Shard id → whether this call ran DDL, in shard-id order.</returns>
    /// <remarks>
    ///     Sequential on purpose. Parallelism buys nothing here — the whole run is a handful of
    ///     <c>CREATE</c>s — and a failure that names one shard is worth more than a faster
    ///     <c>AggregateException</c>.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, bool>> ApplyAsync(
        DurableTierOptions durable,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(durable);

        var applied = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var (shard, connectionString) in durable.Shards.OrderBy(x => x.Key, StringComparer.Ordinal)) {
            try {
                applied[shard] = await ApplyAsync(connectionString, cancellationToken);
            } catch (Exception failure) when (failure is NpgsqlException or InvalidOperationException) {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Could not complete the Orleans grain-storage schema on durable shard "
                        + $"'{shard}'. Every tenant this shard holds will fail its grain writes until "
                        + $"this is fixed; the silos themselves will start and look healthy."
                    ),
                    failure
                );
            }
        }

        return applied;
    }

    /// <summary>
    ///     Reads which of the five schema objects a shard has.
    /// </summary>
    /// <param name="connection">An open connection to one shard.</param>
    /// <param name="transaction">The enclosing transaction, or <see langword="null" /> outside one.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The objects found, ready for <see cref="DurableSchemaState.Plan" />.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <c>::text</c> is not cosmetic.</b> <c>to_regclass</c> returns PostgreSQL's
    ///         <c>regclass</c> OID-alias type, which Npgsql 10 has no <c>object</c> reader for:
    ///         reading it throws
    ///         <c>
    ///             InvalidCastException: Reading as 'System.Object' is not supported for fields
    ///             having DataTypeName 'regclass'
    ///         </c>
    ///         . Observed — it took down the schema job with exit code 134 and left both silos
    ///         waiting on it forever, which presents as "the AppHost hangs" rather than as a cast
    ///         error. The comparison to <c>NULL</c> keeps a <c>boolean</c> on the wire and the lesson
    ///         out of the reader.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two round trips, and the second one cannot be folded into the first.</b>
    ///         PostgreSQL parses a whole statement before executing any of it, so
    ///         <c>SELECT count(*) FROM orleansquery</c> is a parse error — not a <c>NULL</c> — on a
    ///         shard that has no such table. The row count is therefore a separate command, sent only
    ///         when the first one says the table is there.
    ///     </para>
    ///     <para>
    ///         The function is read out of <c>pg_proc</c> rather than through <c>to_regproc</c>,
    ///         which raises <c>more than one function named</c> rather than answering, the day
    ///         somebody adds an overload.
    ///     </para>
    /// </remarks>
    static async Task<DurableSchemaState> ProbeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken
    ) {
        bool queryTable, storageTable, storageIndex, writeFunction;

        await using (var objects = new NpgsqlCommand(
            """
            SELECT to_regclass('orleansquery')::text     IS NOT NULL,
                   to_regclass('orleansstorage')::text   IS NOT NULL,
                   to_regclass('ix_orleansstorage')::text IS NOT NULL,
                   EXISTS (SELECT 1
                           FROM pg_proc p
                           JOIN pg_namespace n ON n.oid = p.pronamespace
                           WHERE p.proname = 'writetostorage'
                             AND n.nspname = ANY (current_schemas(false)));
            """,
            connection,
            transaction
        )) {
            await using var reader = await objects.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            queryTable = reader.GetBoolean(0);
            storageTable = reader.GetBoolean(1);
            storageIndex = reader.GetBoolean(2);
            writeFunction = reader.GetBoolean(3);
        }

        if (!queryTable) {
            return new(false, storageTable, storageIndex, writeFunction, 0);
        }

        await using var rows = new NpgsqlCommand(
            "SELECT count(*) FROM orleansquery WHERE querykey = ANY (@keys);",
            connection,
            transaction
        );

        rows.Parameters.AddWithValue("keys", QueryKeys.ToArray());

        var count = (long)(await rows.ExecuteScalarAsync(cancellationToken))!;

        return new(true, storageTable, storageIndex, writeFunction, (int)count);
    }

    static async Task RunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string resourceName,
        CancellationToken cancellationToken
    ) {
        await using var command = new NpgsqlCommand(Read(resourceName), connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    static string Read(string resourceName) {
        var assembly = typeof(OrleansAdoNetSchema).Assembly;

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing. Available: "
                + string.Join(", ", assembly.GetManifestResourceNames())
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
