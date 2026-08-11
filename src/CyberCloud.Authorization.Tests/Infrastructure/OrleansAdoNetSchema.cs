using System.Reflection;
using Npgsql;

namespace CyberCloud.Authorization.Tests.Infrastructure;

/// <summary>
///     Creates the Orleans ADO.NET grain-storage schema on a PostgreSQL shard.
/// </summary>
/// <remarks>
///     ⚠ <b>NOTHING IN PRODUCTION DOES THIS, AND IT IS STILL A REAL GAP.</b> The full account is in
///     <c>CyberCloud.ServiceDefaults.Tests.Storage.OrleansAdoNetSchema</c>: the provider does not
///     create its own schema, the package ships no SQL, the DDL is two files from
///     <c>dotnet/orleans</c> that must run in order, and no document in <c>docs/plan</c> names an
///     owner for running them against a shard. docs/plan/05 § Durable's "a shard is added by starting
///     a server and putting it in the map" skips the step that makes the server usable.
///     <para>
///         This is a second copy because a test project referencing another test project to share a
///         helper would drag that project's containers and fixtures in with it.
///     </para>
/// </remarks>
static class OrleansAdoNetSchema
{
    static readonly string[] Scripts =
    [
        "CyberCloud.Authorization.Tests.Infrastructure.PostgreSQL-Main.sql",
        "CyberCloud.Authorization.Tests.Infrastructure.PostgreSQL-Persistence.sql",
    ];

    /// <summary>Applies both scripts to the database the connection string points at.</summary>
    /// <param name="connectionString">An Npgsql connection string for one shard.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    public static async Task CreateAsync(string connectionString, CancellationToken cancellationToken)
    {
        // Pooling off: a pooled connection opened here would stay as an idle backend under the same
        // application_name for the rest of the run and pollute any per-shard connection count.
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        foreach (var name in Scripts)
        {
            await using var command = new NpgsqlCommand(Read(name), connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    static string Read(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' is missing. Available: "
                + string.Join(", ", Assembly.GetExecutingAssembly().GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
