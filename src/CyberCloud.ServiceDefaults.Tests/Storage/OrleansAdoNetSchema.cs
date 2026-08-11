using Npgsql;
using System.Reflection;

namespace CyberCloud.ServiceDefaults.Tests.Storage;

/// <summary>
///     Creates the Orleans ADO.NET grain-storage schema on a PostgreSQL shard.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NOTHING IN PRODUCTION DOES THIS, AND THAT IS A REAL GAP.</b> docs/plan/05 § Durable
///         quotes the <c>OrleansStorage</c> column list and stops; no document in <c>docs/plan</c>
///         says who runs the DDL, when, or how a new shard gets it. The facts, established rather
///         than assumed:
///     </para>
///     <list type="number">
///         <item>
///             <b>The provider does not create its own schema.</b> There is no auto-migrate.
///             <c>AdoNetGrainStorage.Init</c> runs
///             <c>
///                 SELECT QueryKey, QueryText FROM OrleansQuery WHERE QueryKey = 'WriteToStorageKey'
///                 OR …
///             </c>
///             and expects four rows. Against an empty database that is a
///             <c>relation "orleansquery" does not exist</c> at silo start.
///         </item>
///         <item>
///             <b>The package ships no SQL.</b> Microsoft.Orleans.Persistence.AdoNet 10.2.2 contains
///             two <c>.dll</c>s, two <c>.xml</c>s, a logo and a README —
///             <c>Assembly.GetManifestResourceNames()</c> returns nothing. The scripts live only in
///             the <c>dotnet/orleans</c> git repository.
///         </item>
///         <item>
///             <b>The schema is two files, not one, and order matters.</b>
///             <c>src/AdoNet/Shared/PostgreSQL-Main.sql</c> creates <c>OrleansQuery</c>;
///             <c>src/AdoNet/Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql</c> creates
///             <c>OrleansStorage</c>, the <c>writetostorage</c> function, and the four rows that go
///             into <c>OrleansQuery</c>. Running the second without the first fails on the
///             <c>INSERT</c>s.
///         </item>
///         <item>
///             <b>There is a third file for clustering that is deliberately not here.</b>
///             <c>PostgreSQL-Clustering.sql</c> belongs to the ADO.NET membership provider, and
///             ADR-004 says there is no clustering database — membership is Kubernetes.
///         </item>
///     </list>
///     <para>
///         So a real deployment needs an owner for this: a migration job per shard, run before the
///         silos that use it start, and re-run when a shard is added (docs/plan/05 § Durable:
///         "a shard is added by starting a server and putting it in the map" — which as written skips
///         the step that makes the server usable). It is not in <c>deploy/</c>, not in
///         <c>charts/platform</c>, and not in any plan document. Naming it here rather than hiding
///         it behind a test helper is the point of this comment.
///     </para>
/// </remarks>
static class OrleansAdoNetSchema {
    /// <summary>The two scripts, in the order they must run.</summary>
    static readonly string[] Scripts = [
        "CyberCloud.ServiceDefaults.Tests.Storage.PostgreSQL-Main.sql",
        "CyberCloud.ServiceDefaults.Tests.Storage.PostgreSQL-Persistence.sql"
    ];

    /// <summary>Applies both scripts to the database the connection string points at.</summary>
    /// <param name="connectionString">An Npgsql connection string for one shard.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task CreateAsync(string connectionString, CancellationToken cancellationToken) {
        // ⚠ Pooling off, deliberately. A pooled connection opened here stays as an idle Postgres
        // backend under the same application_name for the rest of the run, and
        // TheDurableConnectionCountPerShardStaysWithinMaxPoolSizeUnderLoad counts backends by
        // application_name — observed as a peak of 6 against a MaxPoolSize of 5, entirely from this
        // one connection. A measurement that includes the measuring apparatus is worse than no
        // measurement, because it fails at 5 and looks like a real breach.
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString
        );

        await connection.OpenAsync(cancellationToken);

        foreach (var name in Scripts) {
            await using var command = new NpgsqlCommand(Read(name), connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    static string Read(string resourceName) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing. Available: "
                + string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames())
            );

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
