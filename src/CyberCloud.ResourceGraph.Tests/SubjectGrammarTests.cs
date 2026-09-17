namespace CyberCloud.ResourceGraph.Tests;

/// <summary>
///     docs/plan/04 § Streams: <c>cc.{tenant}.res.{provider}.{type}.{id}</c>, as the sink spells it.
/// </summary>
public sealed class SubjectGrammarTests {
    static readonly Guid Tenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    static readonly Guid Resource = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");

    static ResourceChangedEvent Event(string provider, string type) =>
        new() { TenantId = Tenant, ResourceId = Resource, Provider = provider, Type = type };

    [Fact]
    public void TheProviderDotAndTheChildTypeSlashFoldSoEveryTokenKeepsItsPosition() {
        // ⚠ `CyberCloud.Storage` holds a dot. Copied into a dot-delimited subject it would be two
        // tokens, and the type and the id would each sit one position to the right of where a
        // filter expects them. `accounts/fileShares` holds a slash NATS allows but no wildcard can
        // address as one token.
        var subject = Event("CyberCloud.Storage", "accounts/fileShares").Subject;

        subject.ShouldBe($"cc.{Tenant:N}.res.cybercloud_storage.accounts_fileshares.{Resource:N}");
        subject.Split('.').Length.ShouldBe(6, "six tokens, whatever the provider and the type are called");
    }

    [Fact]
    public void TwoSpellingsOfOneTypeAreOneSubject() {
        // The provider namespace is case-preserving and case-insensitive (docs/plan/08 § The provider
        // registry); a subject that differed by case would put one resource under two subjects.
        Event("CyberCloud.Testing", "Widgets").Subject.ShouldBe(Event("cybercloud.testing", "widgets").Subject);
    }

    [Theory]
    [InlineData("a*b", "a_b")]
    [InlineData("a>b", "a_b")]
    [InlineData("a b", "a_b")]
    [InlineData("", "_")]
    public void NoSegmentCanWidenAFilter(string segment, string expected) {
        // A `*` or `>` inside a token would make a subject match a wildcard it did not intend, and
        // an empty token is not a legal NATS subject at all.
        ResourceChangedEvent.SubjectToken(segment).ShouldBe(expected);
    }

    [Fact]
    public void TheTenantReadsBackFromTheSecondToken() {
        ResourceChangedLog.TryTenantOf(Event("CyberCloud.Testing", "widgets").Subject, out var tenant).ShouldBeTrue();
        tenant.ShouldBe(Tenant);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cc")]
    [InlineData("cc.not-a-guid.res.x.y.z")]
    [InlineData("cc.11111111111141118111111111111111.op.z")]
    [InlineData("xx.11111111111141118111111111111111.res.x.y.z")]
    public void AnythingElseIsNotAResourceChangedSubject(string subject) {
        ResourceChangedLog.TryTenantOf(subject, out _).ShouldBeFalse();
    }

    [Fact]
    public void TheStreamFilterCapturesEveryTenantAndNothingOutsideRes() {
        // The filter is what the stream captures; a subject the sink builds must fall under it, or
        // the publish is acknowledged by nothing and the event is gone.
        ResourceChangedLog.SubjectFilter.ShouldBe("cc.*.res.>");
        Event("CyberCloud.Testing", "widgets").Subject.ShouldStartWith($"cc.{Tenant:N}.res.");
    }

    [Fact]
    public void TheMessageIdIsTheResourceAndTheVersion() {
        // JetStream de-duplicates on it inside the stream's window, so a retried publish is one
        // message; the projector's own check is the other half.
        var change = Event("CyberCloud.Testing", "widgets") with { Version = 7 };
        ResourceChangedLog.MessageId(change).ShouldBe($"{Resource:N}.7");
    }
}
