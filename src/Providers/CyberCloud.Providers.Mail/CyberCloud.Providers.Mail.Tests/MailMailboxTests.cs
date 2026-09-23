using CyberCloud.Core.Contracts;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Mail.Tests;

/// <summary>
///     A mailbox: the slice it writes, what it refuses before writing anything, and whose vault it
///     may read a password from.
/// </summary>
/// <remarks>
///     ⚠ The co-owned apply itself — the merge, the claim refusal, the withdrawal — is the shared
///     suite's (<c>MailMailboxConformance</c>, over the fake cluster that implements server-side
///     apply's field ownership) and the cluster-backed suite's, against a real API server. What is
///     here is what only this type can get wrong.
/// </remarks>
public sealed class MailMailboxTests {
    static readonly Guid Mailbox = Guid.Parse("44444444-4444-4444-8444-444444444444");

    [Fact]
    public void TheFragmentIsAPasswordFileAliasLinesAndAClaimPerAddress() {
        var fragment = JsonNode.Parse(
            MailMailboxes.FragmentJson(
                Mailbox,
                "alice",
                MailMailboxes.PasswdLine("alice@example.com", "$6$s$h", string.Empty),
                MailMailboxes.VirtualLines("example.com", "alice", ["info", "sales"], [], true),
                ["info", "sales"]
            )
        )!["data"]!.AsObject();

        fragment.Select(static x => x.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["alice.claim", "alice.passwd", "alice.virtual", "info.claim", "sales.claim"]);

        Decode(fragment["alice.passwd"]).ShouldBe("alice@example.com:{SHA512-CRYPT}$6$s$h::::::\n");
        Decode(fragment["alice.virtual"]).ShouldBe(
            "alice@example.com alice@example.com\ninfo@example.com alice@example.com\nsales@example.com alice@example.com\n"
        );

        // ⚠ Every claim holds the mailbox's id — two mailboxes naming one address write one key
        // with two values, which the co-writer's merge refuses.
        Decode(fragment["info.claim"]).ShouldBe(Mailbox.ToString("D"));
        Decode(fragment["alice.claim"]).ShouldBe(Mailbox.ToString("D"));
    }

    [Theory]
    [InlineData(true, "alice@example.com alice@example.com, bob@elsewhere.example\n")]
    [InlineData(false, "alice@example.com bob@elsewhere.example\n")]
    public void ForwardingKeepsACopyOnlyWhenAskedTo(bool keepCopy, string expected) =>
        MailMailboxes.VirtualLines("example.com", "alice", [], ["bob@elsewhere.example"], keepCopy).ShouldBe(expected);

    [Theory]
    [InlineData("""{"properties":{"localPart":"alice","aliases":["Not Valid"]}}""", "/properties/aliases/0")]
    [InlineData("""{"properties":{"localPart":"alice","aliases":["ok","alice"]}}""", "/properties/aliases/1")]
    [InlineData("""{"properties":{"localPart":"alice","forwardTo":["bob@example.net\nevil@x.example root@example.net"]}}""", "/properties/forwardTo/0")]
    [InlineData("""{"properties":{"localPart":"alice","forwardTo":["a@example.net, b@example.net"]}}""", "/properties/forwardTo/0")]
    [InlineData("""{"properties":{"localPart":"alice","forwardTo":["not-an-address"]}}""", "/properties/forwardTo/0")]
    public void AnAliasOrForwardTheSchemaCouldNotCheckIsRefusedByPointer(string body, string target) {
        // ⚠ The newline and the comma are the security half: an address lands in a Postfix alias
        // map, where a comma separates targets and a newline starts a new entry — a forward that
        // could carry either could add a rule for somebody else's address.
        using var document = JsonDocument.Parse(body);

        var problem = MailMailboxes.Problem(document.RootElement);

        problem.ShouldNotBeNull();
        problem.Value.Target.ShouldBe(target);
    }

    [Fact]
    public void AnOrdinaryBodyHasNoProblem() {
        using var document = JsonDocument.Parse(
            MailMailboxes.Body(MailHarness.ClusterId, aliases: ["info", "sales"], forwardTo: ["bob@elsewhere.example"])
        );

        MailMailboxes.Problem(document.RootElement).ShouldBeNull();
    }

    [Theory]
    [InlineData("tenants/11111111-1111-4111-8111-222222222222/x#password")]
    [InlineData("tenants/11111111-1111-4111-8111-111111111111#password")]
    [InlineData("platform/root#token")]
    public void APasswordHandleOutsideTheTenantsOwnVaultIsRefused(string handle) {
        // ⚠ A handle into another tenant's vault would hash THEIR secret into a password file THIS
        // tenant can log in against — a password oracle for a value they cannot read.
        var parsed = MailMailboxes.ParsePasswordRef(handle, MailHarness.TenantA);

        parsed.IsFailure.ShouldBeTrue();
        parsed.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        parsed.Error.Message.ShouldContain("tenants/" + MailHarness.TenantA.ToString("D") + "/");
    }

    [Fact]
    public void APasswordHandleInsideTheTenantsVaultParses() {
        var parsed = MailMailboxes.ParsePasswordRef(
            "tenants/" + MailHarness.TenantA.ToString("D") + "/mail#alice@3",
            MailHarness.TenantA
        );

        var handle = parsed.GetValueOrThrow();
        handle.Path.ShouldBe("tenants/" + MailHarness.TenantA.ToString("D") + "/mail");
        handle.Field.ShouldBe("alice");
        handle.Version.ShouldBe("3");
    }

    [Fact]
    public async Task AForeignHandleIsRefusedBeforeTheVaultIsAskedAndNothingIsWritten() {
        var vault = new CountingVault();
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(
            MailMailboxes.Body(MailHarness.ClusterId, passwordRef: "tenants/11111111-1111-4111-8111-222222222222/x#pw")
        );

        var outcome = await new MailMailboxReconciler(new FixedClock()).ReconcileAsync(
            MailboxContext(connection, body.RootElement, vault),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.Failed);
        outcome.Error!.Code.ShouldBe(ErrorCode.AuthorizationFailed);
        vault.Reads.ShouldBe(0, "the vault was asked for another tenant's path");
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMailboxWaitsForItsDomainRatherThanCreatingItsObject() {
        // ⚠ A co-writer never creates the owner's object: it would be created under the domain's name
        // without the seven labels. No domain Secret, no write, and InProgress rather than Failed —
        // a domain still Creating and one that does not exist look the same from here.
        var connection = new RecordingConnection();
        using var body = JsonDocument.Parse(MailMailboxes.Body(MailHarness.ClusterId));

        var outcome = await new MailMailboxReconciler(new FixedClock()).ReconcileAsync(
            MailboxContext(connection, body.RootElement, new CountingVault()),
            TestContext.Current.CancellationToken
        );

        outcome.Kind.ShouldBe(ReconcileOutcomeKind.InProgress);
        connection.Applied.ShouldBeEmpty();
    }

    [Fact]
    public void TheDomainTellsItsMailboxesTheDomainOnTheObjectTheyWriteOnto() {
        using var body = JsonDocument.Parse(MailDomains.Body(MailHarness.ClusterId, domain: "Example.COM"));

        MailMailboxes.DomainOf(MailDomains.UsersSecretJson("example-com", body.RootElement)).ShouldBe("example.com");
    }

    static ReconcileContext MailboxContext(IKubeClusterConnection connection, JsonElement desired, CountingVault vault) {
        var id = new ResourceId(
            MailHarness.TenantA,
            MailHarness.SubscriptionA,
            "prod",
            MailMailboxes.Type,
            "alice",
            Mailbox,
            "example-com"
        );

        return new(
            id,
            MailMailboxes.V2026,
            desired,
            null,
            ResourceManager.Reconcile.ReconcileDriver.NamespaceFor(id),
            connection,
            vault,
            new NullLog()
        );
    }

    /// <summary>A vault that counts what it was asked, and holds nothing.</summary>
    sealed class CountingVault : ISecretResolver {
        public int Reads { get; private set; }

        public Task<Result<string>> ResolveAsync(SecretRef reference, CancellationToken cancellationToken = default) {
            Reads++;
            return Task.FromResult(Result<string>.Failure(ErrorCode.ResourceNotFound, "nothing here"));
        }
    }

    static string Decode(JsonNode? value) => Encoding.UTF8.GetString(Convert.FromBase64String(value!.GetValue<string>()));
}
