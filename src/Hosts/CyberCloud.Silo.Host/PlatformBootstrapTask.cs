using CyberCloud.Authorization.Contracts;
using CyberCloud.Communication.Contracts;
using CyberCloud.Communication.Providers.Smtp;
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
///     What a fresh cluster needs written before the first tenant can exist: the shard map, —
///     when self-serve sign-up is on — the platform-operator grant sign-up creates tenants under,
///     and — when a relay is configured — the platform's own communication service (#93).
///     docs/plan/05 § The shard map, docs/plan/06 § Platform administration, docs/plan/17.
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

    /// <summary>Runs every half. What <c>SiloComposition</c> registers as the silo's startup task.</summary>
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

        cancellationToken.ThrowIfCancellationRequested();

        var relay = new SmtpRelayOptions();
        configuration.GetSection(SmtpRelayOptions.SectionName).Bind(relay);

        if (relay.IsConfigured) {
            await ConfigurePlatformCommunicationServiceAsync();
        } else {
            logger.LogInformation(
                "The platform's communication service was not configured: no relay is named under {Section}, so "
                + "there is no email carrier for it to send through. The platform's codes go to the console in "
                + "Development and are refused elsewhere.",
                SmtpRelayOptions.SectionName
            );
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

    /// <summary>
    ///     The third half (#93): the platform's own communication service, with its email channel on
    ///     the smtp carrier — <see cref="PlatformCommunicationService" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Idempotent on the same terms as the other two: <c>CreateAsync</c> on a created service
    ///         answers the snapshot and writes nothing, and <c>ConfigureChannelAsync</c> with the same
    ///         configuration is a rewrite of the same bytes. A second silo, or a restart, finds the
    ///         service there and re-asserts the channel — which is also what picks up a changed
    ///         <see cref="PlatformCommunicationServiceOptions.MaxEmailsPerDay" /> on the next start.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only when a relay is configured.</b> A service with an email channel on a carrier
    ///         nobody registered would refuse every send with the seam's sentence, which is the same
    ///         outcome as no service at all with one more grain to be confused by. So the channel
    ///         exists exactly when <c>SmtpRelayOptions.IsConfigured</c> — the same switch that
    ///         registers the carrier in <c>SiloComposition</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Thrown rather than logged, like the shard map.</b> A silo whose platform service
    ///         could not be written is a silo on which every OTP refuses later, one sign-up at a time.
    ///     </para>
    /// </remarks>
    async Task ConfigurePlatformCommunicationServiceAsync() {
        var options = new PlatformCommunicationServiceOptions();
        configuration.GetSection(PlatformCommunicationServiceOptions.SectionName).Bind(options);

        var service = grains
            .ForTenant(N(PlatformTenant, "D"))
            .GetGrain<ICommunicationServiceGrain>(CommunicationGrainKeys.Service(PlatformCommunicationService.ServiceId));

        var created = await service.CreateAsync(PlatformTenant, PlatformCommunicationService.Name);

        if (created.TryGetError(out var notCreated)) {
            throw new InvalidOperationException(
                $"The platform's communication service {PlatformCommunicationService.ServiceId:D} could not be "
                + $"created: {notCreated.Message}. Without it the platform's own codes have nothing to send through."
            );
        }

        var configured = await service.ConfigureChannelAsync(PlatformCommunicationService.EmailChannel(options));

        if (configured.TryGetError(out var notConfigured)) {
            throw new InvalidOperationException(
                $"The platform's communication service {PlatformCommunicationService.ServiceId:D} refused its email "
                + $"channel: {notConfigured.Message}"
            );
        }

        logger.LogInformation(
            "The platform's communication service {ServiceId} ({Path}) has its email channel on the {Provider} carrier, "
            + "{MaxEmailsPerDay} message(s) per UTC day. Point {OtpSection} at it, or run in Development, to send the "
            + "platform's codes through it.",
            PlatformCommunicationService.ServiceId,
            PlatformCommunicationService.Address.Path,
            SmtpChannelProvider.ProviderName,
            options.MaxEmailsPerDay,
            SiloIdentityOptions.SectionName
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
