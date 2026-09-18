using CyberCloud.ResourceManager.Contracts.Generation;
using CyberCloud.ResourceManager.Registry;
using System.Text.Json;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     The mail declaration, checked the way a silo checks it at start, plus the facts that live in
///     more than one file and have to agree.
/// </summary>
public sealed class MailDeclarationTests {
    [Fact]
    public void TheProviderBuildsIntoARegistryTheSiloWouldAccept() {
        // ProviderRegistry.Build throws on a provider that declared nothing, on a duplicate
        // namespace, on a duplicate type, on a type with no api-version, on a duplicate SHORT NAME,
        // and on a RequiresCluster type whose schema does not declare the pointer as a required
        // string.
        var registry = ProviderRegistry.Build([new MailProvider()]);

        registry.TryGetType(MailDomains.Type, out var registration).ShouldBeTrue();

        registration.RequiresCluster.ShouldBeTrue();
        registration.ClusterIdPointer.ShouldBe(MailDomains.ClusterIdPointer);
        registration.SupportsTags.ShouldBeTrue();
        registration.Chart.ShouldBe(MailDomains.ChartName);
        registration.ReconcilerType.ShouldBe(typeof(MailDomainReconciler));
    }

    [Fact]
    public void ItDeclaresNoActionAtAll() {
        // ⚠ THE ONLY TYPE IN THE CATALOGUE THAT DECLARES NONE, AND THE ASSERTION IS HERE SO THAT
        // ADDING ONE IS A DELIBERATE ACT. docs/plan/17 names verify, sendTest and exportMailbox, and
        // MailProvider carries the argument for why none is declared: actions-without-handlers.txt
        // permits a handler-less action ONLY on an already-published api-version, and 2026-08-01 of
        // this type was published by the change that would have declared them.
        //
        // ⚠ If this test goes red because somebody added an action, the question to answer is not
        // "may I add a line to actions-without-handlers.txt" — it is "does this action have a
        // handler". For verify the honest answer is still no: it needs a DNS resolution seam that
        // this repository does not have.
        var registry = ProviderRegistry.Build([new MailProvider()]);

        registry.TryGetType(MailDomains.Type, out var registration).ShouldBeTrue();

        registration.Actions.ShouldBeEmpty(
            "an action was declared. If it has a handler, delete this assertion and say so; if it "
            + "does not, it must not be declared — see MailProvider."
        );
    }

    [Fact]
    public void TheShortNameIsNotItsOwnCliGroupKey() {
        // ⚠ `mail` IS THE ONE WORD THIS TYPE COULD NOT HAVE. CliEmitter.GroupOf derives a group from
        // the provider namespace's last segment lower-cased, so this type's own group key is `mail`,
        // and a short name equal to it gives `cyc mail mail` two meanings. Measured against
        // System.CommandLine, the token dictionary is per PARENT command, so a short name equal to
        // ANOTHER group's key parses cleanly — this is the only collision that could bite.
        //
        // ⚠ Derived rather than listed. Ten hand-maintained literal lists of group keys went stale
        // twice before CliTokens replaced them; asking the derived question is what keeps this
        // assertion true as the catalogue grows.
        var registry = ProviderRegistry.Build([new MailProvider()]);

        registry.TryGetType(MailDomains.Type, out var registration).ShouldBeTrue();
        registration.Display.Alias.ShouldBe("domain");

        CliTokens.Collisions(
            registry.Types.Select(static x => new CliDeclaration(x.Type.Namespace, x.Type.Type, x.Display.Alias))
        )
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheApiVersionMatchesTheChartsAnnotation() {
        // ⚠ Two files, one fact. Chart.yaml's cybercloud.io/api-version is what Build.Charts writes
        // into values.schema.json as x-cybercloud-api-version, and a drift makes the generated schema
        // describe a body shape no api-version serves.
        MailDomains.V2026.ShouldBe("2026-08-01");
        MailDomains.ChartName.ShouldBe("managed/mail");
    }

    [Fact]
    public void TheSchemaDeclaresEveryPointerTheAccessorsRead() {
        // ⚠ THE DRIFT THIS CATCHES IS AN ACCESSOR READING A POINTER THE SCHEMA DOES NOT DECLARE,
        // which is a property a tenant can never set and whose fallback is therefore the ONLY value
        // it ever has. That reads as a working default rather than as a bug.
        var pointers = MailDomains.Schema2026.Properties.Select(static x => x.JsonPointer)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[] {
                     "/properties/domain", "/properties/version", "/properties/sizing/preset", "/properties/sizing/cpu",
                     "/properties/sizing/memory", "/properties/storage/size", "/properties/storage/mailboxQuota",
                     "/properties/catchAll", "/properties/relayHosts", "/properties/dedicatedIp",
                     "/properties/filtering/rejectThreshold", "/properties/filtering/antivirus", "/properties/sieve"
                 }) {
            pointers.ShouldContain(expected);
        }
    }

    [Fact]
    public void AnEmptyBodyFallsBackToTheSchemasOwnDefaults() {
        // ⚠ THE WRITE PATH STORES THE BODY AS SENT — SchemaProperty.DefaultJson is not applied by the
        // validator — so every accessor's fallback IS the default a tenant gets. A fallback that
        // disagreed with DefaultJson would make the portal show one value and the cluster run
        // another.
        using var empty = JsonDocument.Parse("{}");
        var body = empty.RootElement;

        MailDomains.Version(body).ShouldBe(MailDomains.DefaultVersion);
        MailDomains.StorageSize(body).ShouldBe(MailDomains.DefaultStorageSize);
        MailDomains.MailboxQuota(body).ShouldBe(MailDomains.DefaultMailboxQuota);
        MailDomains.RejectThreshold(body).ShouldBe(MailDomains.DefaultRejectThreshold);
        MailDomains.CatchAll(body).ShouldBeEmpty();
        MailDomains.RelayHosts(body).ShouldBeEmpty();
        MailDomains.DedicatedIpRequested(body).ShouldBeFalse();

        // ⚠ Both of these default to TRUE, which is the direction worth asserting: a fallback of
        // false would silently turn off virus scanning and server-side rules for every body that
        // did not name them.
        MailDomains.AntivirusEnabled(body).ShouldBeTrue();
        MailDomains.SieveEnabled(body).ShouldBeTrue();
    }

    [Fact]
    public void RelayHostsAreSortedAndDeduplicatedSoTwoOrderingsRenderTheSameFile() {
        // ⚠ CLAUSE 1. Two bodies naming the same relays in different orders must render identical
        // Postfix configuration, or every pass reports drift against the last one and the reconcile
        // loop never settles — the failure RabbitmqClusters.Plugins found once already.
        using var first = JsonDocument.Parse("""{"properties":{"relayHosts":["b.example","a.example","B.EXAMPLE"]}}""");

        using var second = JsonDocument.Parse("""{"properties":{"relayHosts":["a.example","b.example"]}}""");

        MailDomains.RelayHosts(first.RootElement).ShouldBe(MailDomains.RelayHosts(second.RootElement));

        MailDomains.PostfixMainCf("example-com", first.RootElement)
            .ShouldBe(MailDomains.PostfixMainCf("example-com", second.RootElement));
    }

    [Fact]
    public void TheRetainedClaimNamesTheClaimTheStatefulSetControllerWouldMake() {
        // ⚠ A claim made from a volumeClaimTemplate is named {volume}-{set}-{ordinal} by the
        // StatefulSet controller, and NOTHING ELSE CAN RECREATE THAT NAME once the set is gone. If
        // this drifts, a purge looks for a claim that does not exist and the mail store is left
        // behind forever — billed, invisible, and belonging to a resource nobody can name.
        var claims = MailDomains.RetainedClaims("ns", "example-com");

        claims.Length.ShouldBe(1);
        claims[0].Claim.Name.ShouldBe(RetainedVolume.NameFor(MailDomains.MailVolumeName, "example-com-mail", 0));
        claims[0].Claim.Kind.ShouldBe(RetainedVolume.ClaimKind);

        // ⚠ The ownership evidence, without which VolumeReclaimer refuses to act at all — a claim
        // named with no evidence behind it is a name, and this platform does not delete on a name.
        claims[0].OwnedBy.ShouldBe(MailDomains.SelectorLabels("example-com"));
    }
}
