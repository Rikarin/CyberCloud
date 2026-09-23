using CyberCloud.Conformance;
using CyberCloud.Core.Time;
using CyberCloud.Providers.Mail.Contracts;
using CyberCloud.Providers.Mail.Dns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Conformance;

/// <summary>
///     The world the Docker-free suite gives the mail provider: platform hosts that are configured,
///     and a DNS in which nothing has been published.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A DNS WITH NOTHING IN IT, AND THAT IS NOT A DOUBLE OF THE THING UNDER TEST.</b> The
///         shared suite asserts the write path, the verb grammar and the reconciler's clauses; the
///         sending gate is not among them, and the resolver is proven against a real DNS server in
///         <c>MailDnsResolverTests</c> and on a real cluster in <c>MailDeliveryOnK3sTests</c>. What
///         the suite needs from the DNS is an answer, and "no such record" is the one a domain nobody
///         has published gets — so every domain here converges <see cref="MailSending.Held" />, which
///         is the state a new domain is in.
///     </para>
///     <para>
///         ⚠ <b>The hosts are under <c>.example</c></b>, the RFC 2606 TLD that cannot belong to
///         anybody, so a record copied out of a test run and published would point at nothing.
///     </para>
/// </remarks>
public static class MailConformanceWorld {
    /// <summary>The platform's hosts, as a region would configure them.</summary>
    public static MailPlatformOptions Platform { get; } = new() {
        InboundHost = "mx.cybercloud.example",
        SpfInclude = "_spf.cybercloud.example",
        MtaStsHost = "mta-sts.cybercloud.example",
        TlsReportAddress = "tls-reports@cybercloud.example"
    };

    /// <summary>A resolver that answers "no such record" for every question.</summary>
    public static IMailDnsResolver Dns { get; } = new NothingPublished();

    /// <summary>What a host's <c>MailApplicationModule</c> registers, for the harness silo.</summary>
    /// <param name="services">The silo's container.</param>
    public static void Register(IServiceCollection services) {
        services.TryAddSingleton(Platform);
        services.TryAddSingleton(Dns);
    }

    /// <summary>The domain's reconciler over this world.</summary>
    /// <param name="clock">The harness clock.</param>
    public static MailDomainReconciler DomainReconciler(IClock clock) => new(clock, Dns, Platform);

    sealed class NothingPublished : IMailDnsResolver {
        public Task<MailDnsAnswer> QueryAsync(string name, string kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailDnsAnswer(true, [], string.Empty));
    }
}

/// <summary>
///     <c>CyberCloud.Mail/domains</c>, registered into the shared provider suite.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST PROVIDER WHOSE OBJECTS THE CASE CANNOT PREDICT THE BYTES OF.</b> The
///         credentials <c>Secret</c>'s contents are read back out of the vault rather than computed
///         from the body, so <see cref="MailDomains.Matches" /> answers <see langword="true" /> for a
///         <c>Secret</c> — honest rather than lazy, because for an object no controller rewrites an
///         apply that landed and read back <i>is</i> the whole claim.
///     </para>
///     <para>
///         ⚠ <b>WHAT A GREEN RUN HERE PROVES AND WHAT IT DOES NOT.</b> The twelve-step write path,
///         the verb grammar, <c>dnsRecords</c> through the dispatcher, the four reconciler clauses,
///         the cross-tenant 404, the seven labels and the delete-read-back, over six objects. Whether
///         mail is accepted, signed, delivered and read is <c>MailDeliveryOnK3sTests</c>', and that
///         the DKIM key never changes between passes is <c>MailDkimTests</c>' — this suite drives one
///         create.
///     </para>
/// </remarks>
public sealed class MailDomainCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Mail/domains",
            CreateProvider = static () => new MailProvider(),
            ReconcilerType = typeof(MailDomainReconciler),
            CreateReconciler = static clock => MailConformanceWorld.DomainReconciler(clock),
            Type = MailDomains.Type,
            ApiVersion = MailDomains.V2026,
            Body = static cluster => MailDomains.Body(cluster),
            // ⚠ Changes `storage.size`, which the reconciler renders into the claim template and
            // MailDomains.Matches reads back off the StatefulSet. Two other candidates were wrong for
            // two different reasons, and both look right:
            //
            //   * `filtering.rejectThreshold` reaches only the ConfigMap, whose Matches is `true` by
            //     construction — so the update assertion would pass over a reconciler that ignored
            //     the change entirely.
            //   * `sieve` DOES change the Service's port count and would work here, but it changes
            //     the ConfigMap too, so a failure would not say which document was wrong.
            ChangedBody = static cluster => MailDomains.Body(cluster, storageSize: "40Gi"),
            // Drops the required `/properties/storage/size`.
            // ⚠ Built from a valid body with one required property removed rather than hand-written: a
            // hand-written invalid body drifts out of date the day the schema gains a property and
            // then tests "invalid for the wrong reason" while still going green.
            InvalidBody = static cluster => WithoutStorageSize(MailDomains.Body(cluster)),
            InvalidBodyTarget = "/properties/storage/size",
            // ⚠ `dnsRecords` and not `verify`, because the suite's POST assertions ask whether the
            // verb grammar routes an action to its handler and back, and `dnsRecords` answers the
            // same thing on every call. `verify` would answer from the DNS and could move the gate.
            ActionName = MailDomains.DnsRecordsAction,
            Objects = static (id, ns) => MailDomains.Objects(ns, id.Name),
            // A cluster data plane, which the harness breaks and reads itself — see ProviderConformanceCase.DataPlane.
            DataPlane = null,
            StoragePrefix = null,
            // ⚠ NOTHING. There is no operator for any of the three components, so no controller
            // writes an object this provider reads back. Stated rather than defaulted — see
            // ProviderConformanceCase.OperatorWritten.
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);

                return MailDomains.Matches(match.ObjectJson, desired.RootElement);
            }
        };

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The reconciler and both handlers take the platform's hosts and a resolver, which a host's
    ///     <c>MailApplicationModule</c> registers and the harness has no module for.
    /// </remarks>
    public static void ConfigureSilo(ISiloBuilder silo) =>
        silo.ConfigureServices(static services => MailConformanceWorld.Register(services));

    /// <inheritdoc />
    public static void ConfigureHandlers(IServiceCollection services) => MailConformanceWorld.Register(services);

    /// <summary>A valid body with the required mail volume size removed.</summary>
    /// <param name="body">A valid body.</param>
    static string WithoutStorageSize(string body) {
        var node = JsonNode.Parse(body)!.AsObject();

        node["properties"]!.AsObject()["storage"]!.AsObject().Remove("size");

        return node.ToJsonString();
    }
}

/// <summary>
///     <c>CyberCloud.Mail/domains/mailboxes</c>, registered into the shared provider suite — the
///     second co-writer case in the tree.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE OBJECT IS THE DOMAIN'S, AND THE SUITE TELLS THAT FROM AN OWNED ONE BY ITS
///         LABELS.</b> <see cref="ProviderConformanceCase.Objects" /> names the ancestor domain's
///         mailbox <c>Secret</c>, which carries the domain's resource id; the suite's co-writer branch
///         asserts the seven survive, that the id is not the mailbox's, and that the mailbox's three
///         fragment annotations sit beside them — the branch <c>virtualNetworks/peerings</c> built.
///     </para>
///     <para>
///         ⚠ <b>No password, so no vault read and no hash.</b> A mailbox with an empty
///         <c>passwordRef</c> is a real, supported shape — it receives and nobody signs in — and it is
///         the one whose fragment the case can predict without a secret. The password path is
///         <c>MailMailboxTests</c>' and the cluster-backed suite's.
///     </para>
/// </remarks>
public sealed class MailMailboxCase : IProviderCaseSource {
    /// <inheritdoc />
    public static ProviderConformanceCase ProviderCase { get; } =
        new() {
            DisplayName = "CyberCloud.Mail/domains/mailboxes",
            CreateProvider = static () => new MailProvider(),
            ReconcilerType = typeof(MailMailboxReconciler),
            CreateReconciler = static clock => new MailMailboxReconciler(clock),
            Type = MailMailboxes.Type,
            ApiVersion = MailMailboxes.V2026,
            Body = static cluster => MailMailboxes.Body(cluster),
            // ⚠ An alias: it changes the fragment's claim keys AND the alias-map lines, so a
            // reconciler that ignored the update would leave a key the matcher looks for missing.
            ChangedBody = static cluster => MailMailboxes.Body(cluster, aliases: ["info"]),
            InvalidBody = static cluster => MailMailboxes.Body(cluster, localPart: "Not A Local Part"),
            InvalidBodyTarget = "/properties/localPart",
            // ⚠ EXPLICITLY EMPTY. A mailbox declares no action; everything a tenant asks of the DNS
            // is on the domain. Written out rather than defaulted — see ProviderConformanceCase.ActionName.
            ActionName = string.Empty,
            // ⚠ The PARENT's object, off the address — the harness names the ancestor.
            Objects = static (id, ns) => [MailDomains.UsersSecretRef(ns, id.Parent!.Value.Name)],
            DataPlane = null,
            StoragePrefix = null,
            OperatorWritten = static (_, _) => [],
            ObjectMatchesDesired = static match => {
                using var desired = JsonDocument.Parse(match.DesiredJson);

                var domain = MailMailboxes.DomainOf(match.ObjectJson);
                var local = MailMailboxes.LocalPart(desired.RootElement);
                var aliases = MailMailboxes.Aliases(desired.RootElement);
                var fragment = MailMailboxes.FragmentJson(
                    match.Id.Id,
                    local,
                    MailMailboxes.PasswdLine(local + "@" + domain, null, MailMailboxes.Quota(desired.RootElement)),
                    MailMailboxes.VirtualLines(
                        domain,
                        local,
                        aliases,
                        MailMailboxes.ForwardTo(desired.RootElement),
                        MailMailboxes.KeepCopy(desired.RootElement)
                    ),
                    aliases
                );

                return domain.Length > 0 && MailMailboxes.Carries(match.ObjectJson, fragment);
            }
        };

    /// <inheritdoc />
    public static ImmutableArray<ProviderConformanceCase> Ancestors { get; } = [MailDomainCase.ProviderCase];

    /// <inheritdoc />
    public static void ConfigureSilo(ISiloBuilder silo) =>
        silo.ConfigureServices(static services => MailConformanceWorld.Register(services));

    /// <inheritdoc />
    public static void ConfigureHandlers(IServiceCollection services) => MailConformanceWorld.Register(services);
}

/// <summary>The shared suite, run against the managed-mail provider.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MailDomainConformance(ProviderTestCluster<MailDomainCase> cluster)
    : ProviderConformanceTests<MailDomainCase>(cluster), IClassFixture<ProviderTestCluster<MailDomainCase>>;

/// <summary>The same suite, run against the mailbox child type.</summary>
/// <param name="cluster">The harness.</param>
public sealed class MailMailboxConformance(ProviderTestCluster<MailMailboxCase> cluster)
    : ProviderConformanceTests<MailMailboxCase>(cluster), IClassFixture<ProviderTestCluster<MailMailboxCase>>;

/// <summary>The container-backed half, skipped loudly, against the managed-mail provider.</summary>
/// <remarks>
///     ⚠
///     <b>
///         Still declared, even though this provider has a real <c>*.Cluster.Conformance</c> project
///         that makes the same assertions against a real API server.
///     </b> The skips are not redundant with
///     it: they run on a machine with no Docker and say, by name, which criteria were not checked.
///     Deleting them here would make "conformance: green" readable as "the cluster-backed criteria
///     were met" on exactly the machines where they were not.
/// </remarks>
public sealed class MailDomainClusterBackedConformance() : ClusterBackedConformanceTests(MailDomainCase.ProviderCase);

/// <summary>The container-backed half, skipped loudly, against the mailbox child type.</summary>
public sealed class MailMailboxClusterBackedConformance() : ClusterBackedConformanceTests(MailMailboxCase.ProviderCase);
