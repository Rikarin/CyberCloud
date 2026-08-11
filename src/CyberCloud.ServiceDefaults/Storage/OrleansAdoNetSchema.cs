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
///         <c>CREATE TABLE IF NOT EXISTS</c>. Applying them twice fails with <c>42P07</c>. That is
///         why <see cref="ApplyAsync(string, CancellationToken)" /> probes for <c>orleansquery</c>
///         first and returns <see langword="false" /> rather than throwing, and it is why this is a
///         <b>one-shot job</b> and not something a silo does on start: N silos racing the same
///         <c>CREATE TABLE</c> is N−1 failures.
///     </para>
///     <para>
///         <b>Production reuse.</b> This lives in <c>CyberCloud.ServiceDefaults</c> — the assembly
///         that wires the durable tier — precisely so that it is not local-development-only. It has
///         no Aspire, Testcontainers or host dependency: it takes a connection string and a
///         cancellation token. <c>CyberCloud.AppHost</c> drives it through
///         <c>CyberCloud.Silo.Host --apply-durable-schema</c>; a chart under <c>charts/platform</c>
///         can drive the same entry point from the same image as a <c>pre-install</c>/
///         <c>pre-upgrade</c> Helm hook Job, which is the shape docs/plan/05 § Durable's "a shard is
///         added by starting a server and putting it in the map" leaves out. Nothing in
///         <c>deploy/</c> or <c>charts/</c> does it yet; that gap is now one call wide instead of a
///         missing component.
///     </para>
/// </remarks>
public static class OrleansAdoNetSchema {
    /// <summary>
    ///     The two scripts, in the order they must run. Embedded copies of <c>dotnet/orleans</c> at
    ///     tag <c>v10.2.2</c> — see the <c>EmbeddedResource</c> items in the project file.
    /// </summary>
    static readonly string[] Scripts = [
        "CyberCloud.ServiceDefaults.Storage.PostgreSQL-Main.sql",
        "CyberCloud.ServiceDefaults.Storage.PostgreSQL-Persistence.sql"
    ];

    /// <summary>
    ///     Applies the schema to one shard, unless it is already there.
    /// </summary>
    /// <param name="connectionString">An Npgsql connection string for one shard.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    ///     <see langword="true" /> if the schema was created, <see langword="false" /> if
    ///     <c>orleansquery</c> already existed and nothing was run.
    /// </returns>
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

        if (await AlreadyAppliedAsync(connection, cancellationToken)) {
            return false;
        }

        foreach (var name in Scripts) {
            await using var command = new NpgsqlCommand(Read(name), connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    ///     Applies the schema to every shard in a bound <see cref="DurableTierOptions" />.
    /// </summary>
    /// <param name="durable">The <c>CyberCloud:Storage:Durable</c> section, bound.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Shard id → whether this call created the schema, in shard-id order.</returns>
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
            } catch (NpgsqlException failure) {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Could not create the Orleans grain-storage schema on durable shard "
                        + $"'{shard}'. A silo bound to this shard will fail on its first grain write "
                        + $"with 'relation \"orleansquery\" does not exist'."
                    ),
                    failure
                );
            }
        }

        return applied;
    }

    /// <summary>
    ///     Whether <c>orleansquery</c> is already present. <c>to_regclass</c> returns
    ///     <c>NULL</c> rather than raising when the relation is absent, so this is one round trip and
    ///     no exception handling.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The <c>::text</c> is not cosmetic.</b> <c>to_regclass</c> returns PostgreSQL's
    ///     <c>regclass</c> OID-alias type, which Npgsql 10 has no <c>object</c> reader for:
    ///     <c>ExecuteScalarAsync</c> throws
    ///     <c>
    ///         InvalidCastException: Reading as 'System.Object' is
    ///         not supported for fields having DataTypeName 'regclass'
    ///     </c>
    ///     . Observed — it took down the
    ///     schema job with exit code 134 and left both silos waiting on it forever, which presents as
    ///     "the AppHost hangs" rather than as a cast error.
    /// </remarks>
    static async Task<bool> AlreadyAppliedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken
    ) {
        await using var probe = new NpgsqlCommand(
            "SELECT to_regclass('orleansquery')::text;",
            connection
        );

        var result = await probe.ExecuteScalarAsync(cancellationToken);

        return result is not null && result != DBNull.Value;
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
