using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ServiceDefaults.Storage;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Silo.Host;

/// <summary>
///     What a fresh cluster needs written before the first tenant can exist: the shard map, and —
///     when self-serve sign-up is on — the platform-operator grant sign-up creates tenants under.
///     docs/plan/05 § The shard map, docs/plan/06 § Platform administration.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Without this, <c>IScopeManager.CreateTenantAsync</c> cannot succeed on a fresh run at
///             all, and both halves fail in a way that names nothing useful.
///         </b> <c>IShardMapGrain</c> starts with no shards and refuses the assignment with "Call
///         ConfigureShardsAsync before assigning a tenant" — true, and said to a person who has never
///         heard of the shard map. And nobody holds <c>platform:root#operator</c>, so the operator
///         check in front of the assignment fails first, and the enforcement seam renders that as a
///         refusal that looks like a permissions bug. <c>TenantOverHttpTests</c> bootstraps its
///         tenant by grain for exactly these two reasons; a person signing up cannot.
///     </para>
///     <para>
///         <b>Both silos run it, and both halves are idempotent.</b> <c>ConfigureShardsAsync</c> adds
///         shards it has not seen and changes nothing for ones it has; a tuple write for a tuple
///         that exists is a success. The second silo to start finds the first's work done and does
///         nothing, and a restart finds its own. <c>PlatformBootstrapTaskTests.ASecondRunChangesNothing</c>
///         pins the version number across two runs.
///     </para>
///     <para>
///         ⚠ <b>The platform shard is not in the map.</b> <c>CyberCloud:Storage:Durable:Shards</c>
///         names every PostgreSQL server including the one <c>NullTenantShard</c> points at, which
///         carries the directory, the shard map and the provider registry and nothing else. A tenant
///         placed on it would put a customer's rows beside the platform's, on the one server whose
///         loss is every tenant's loss at once. So the map is the configured shards minus that one.
///     </para>
///     <para>
///         ⚠ <b>Skipped entirely when no durable shard is configured.</b> A silo with no
///         <c>CyberCloud:Storage</c> section has no grain storage, so the map grain cannot activate
///         — and such a silo exists: <c>HostCompositionTests.BothHostsStartAndNotOnlyCompose</c>
///         starts the real composition with no storage at all. There is nothing to configure a map
///         from, and the log line says so.
///     </para>
///     <para>
///         ⚠ <b>The grant is a tuple and nothing else.</b> <c>IdentityBootstrap</c> says why the
///         sign-up operator has no principal record and no credential; what is written here is
///         <c>platform:root#operator@servicePrincipal:{SignUpOperator}</c> in the platform tenant's
///         store, which is the single relation <c>ReBacScopeAuthorizer.AuthorizePlatformAsync</c>
///         evaluates. It is written only when <c>CyberCloud:Identity:SelfServeSignUp</c> is
///         <c>true</c>: a deployment that has not opened sign-up has no reason to hold a standing
///         operator grant for it.
///     </para>
/// </remarks>
public sealed class PlatformBootstrapTask(
    IGrainFactory grains,
    IConfiguration configuration,
    ILogger<PlatformBootstrapTask> logger
) {
    /// <summary>The configuration key that opens self-serve sign-up. Read by the silo and the identity host.</summary>
    public const string SelfServeSignUpKey = "CyberCloud:Identity:SelfServeSignUp";

    /// <summary>Runs both halves. What <c>SiloComposition</c> registers as the silo's startup task.</summary>
    /// <param name="cancellationToken">Cancels the start-up.</param>
    public async Task ExecuteAsync(CancellationToken cancellationToken) {
        var storage = new CyberCloudStorageOptions();
        configuration.GetSection(CyberCloudStorageOptions.SectionName).Bind(storage);

        var shards = TenantShards(storage);

        if (shards.Count == 0) {
            logger.LogInformation(
                "Platform bootstrap skipped: no durable shard is configured under {Section}, so there is no "
                + "shard map to configure and no store to hold a grant.",
                CyberCloudStorageOptions.SectionName
            );

            return;
        }

        await ConfigureShardMapAsync(shards);

        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.GetValue<bool>(SelfServeSignUpKey)) {
            await GrantSignUpOperatorAsync();
        }
    }

    /// <summary>The shards a tenant may be placed on: every configured durable shard but the platform's.</summary>
    /// <param name="storage">The bound <c>CyberCloud:Storage</c> section.</param>
    public static IReadOnlyList<string> TenantShards(CyberCloudStorageOptions storage) {
        ArgumentNullException.ThrowIfNull(storage);

        return [
            .. storage.Durable.Shards.Keys
                .Where(x => !string.Equals(x, storage.Durable.NullTenantShard, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
        ];
    }

    async Task ConfigureShardMapAsync(IReadOnlyList<string> shards) {
        var configured = await grains
            .GetGrain<IShardMapGrain>(GrainKeys.ShardMap())
            .ConfigureShardsAsync(shards);

        if (configured.TryGetError(out var error)) {
            // ⚠ Thrown rather than logged. A silo that starts with a map it could not configure is a
            // silo on which every tenant creation fails later, one at a time, in front of a person;
            // failing the start puts the sentence in front of the operator once.
            throw new InvalidOperationException(
                $"The shard map could not be configured from [{string.Join(", ", shards)}]: {error.Message}"
            );
        }

        logger.LogInformation(
            "Shard map configured with {ShardCount} tenant shard(s) at version {Version}: {Shards}.",
            shards.Count,
            configured.GetValueOrThrow().Version,
            string.Join(", ", shards)
        );
    }

    async Task GrantSignUpOperatorAsync() {
        var tuple = RelationTuple.Create(
            ObjectRef.Create(ObjectTypes.Platform, PlatformObjectId).GetValueOrThrow(),
            Relations.Operator,
            SubjectRef.Create(SubjectTypes.ServicePrincipal, N(IdentityBootstrap.SignUpOperator)).GetValueOrThrow()
        )
            .GetValueOrThrow();

        var platform = grains.ForTenant(N(PlatformTenant, "D"));

        var written = await platform
            .GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(PlatformTenant))
            .WriteAsync(tuple);

        if (written.TryGetError(out var error)) {
            throw new InvalidOperationException(
                $"The sign-up operator grant {tuple} could not be written to the platform tenant's tuple "
                + $"store: {error.Message}. Without it self-serve sign-up cannot create a tenant."
            );
        }

        logger.LogInformation(
            "Self-serve sign-up is open: {Tuple} is in the platform tenant's tuple store.",
            tuple
        );
    }

    // ⚠ Both spellings are ReBacScopeAuthorizer's, deliberately. Its PlatformObjectId remarks say a
    // second declaration of `root` "belongs here instead", and the check this grant has to satisfy
    // is that class's — a tuple written against any other object id evaluates false with nothing
    // in any log to say why. This host already composes the resource manager, so naming the
    // constant costs no reference.
    static Guid PlatformTenant => ReBacScopeAuthorizer.PlatformTenant;

    const string PlatformObjectId = ReBacScopeAuthorizer.PlatformObjectId;

    static string N(Guid value, string format = "N") => value.ToString(format, CultureInfo.InvariantCulture);
}
